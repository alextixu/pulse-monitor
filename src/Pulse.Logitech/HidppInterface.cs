using Pulse.Core.Models;
using Microsoft.Extensions.Logging;

namespace Pulse.Logitech;

internal enum InterfaceRole
{
    Unknown,
    /// <summary>Unifying / LIGHTSPEED / Bolt / Nano receiver; devices live in slots 1..6.</summary>
    Receiver,
    /// <summary>A single device connected over USB or Bluetooth, addressed as 0xFF.</summary>
    DirectDevice,
    /// <summary>Vendor collection that never answered; retried occasionally.</summary>
    NotHidpp,
}

/// <summary>
/// Discovery and polling for one HID++ interface. Keeps the opened <see cref="HidppTransport"/> and the devices found
/// behind it between polls; re-runs discovery every few minutes or when the role is still unknown. The transport is
/// optional: after a fault the owner closes it (<see cref="CloseTransport"/>) and the interface lives on with its cached
/// devices, marked offline, until <see cref="Backoff"/> allows a reopen (<see cref="Attach"/>).
/// </summary>
internal sealed class HidppInterface : IDisposable
{
    private static readonly TimeSpan RediscoverInterval = TimeSpan.FromMinutes(5);
    // Each retry of a silent vendor collection costs a full request timeout, so keep it rare.
    private static readonly TimeSpan RetryNotHidppInterval = TimeSpan.FromMinutes(2);

    private readonly ILogger _logger;
    private readonly List<HidppDevice> _devices = new();
    private HidppTransport? _transport;
    private DateTimeOffset _lastDiscovery = DateTimeOffset.MinValue;
    private string? _lastReceiverSummary;

    public HidppInterface(HidppInterfaceInfo info, ILogger logger)
    {
        Info = info;
        _logger = logger;
        Family = info.IsBluetooth ? ReceiverFamily.None : ReceiverFamilies.FromProductId(info.ProductId);
    }

    public HidppInterfaceInfo Info { get; }

    /// <summary>The open transport, or null while the interface is closed (never opened yet, or closed after a fault).</summary>
    public HidppTransport? Transport => _transport;

    /// <summary>True when a transport is attached and has not faulted.</summary>
    public bool IsOpen => _transport is { IsFaulted: false };

    /// <summary>Retry schedule for opening / reopening the transport; reset by the owner after a successful poll.</summary>
    public HidppBackoff Backoff { get; } = new();

    public InterfaceRole Role { get; private set; }

    public ReceiverFamily Family { get; }

    public string ReceiverSerial { get; private set; } = string.Empty;

    public int MaxSlots { get; private set; } = Hidpp.MaxReceiverSlots;

    public IReadOnlyList<HidppDevice> Devices => _devices;

    /// <summary>Takes ownership of a freshly opened transport. Cached devices and discovery state are kept.</summary>
    public void Attach(HidppTransport transport)
    {
        var previous = _transport;
        _transport = transport;
        previous?.Dispose();
    }

    /// <summary>
    /// Disposes the transport (closes the HID handles, which stops its reader threads) and marks every cached device
    /// offline so <see cref="Snapshot"/> reports them disconnected with their last known level. Safe when already closed.
    /// </summary>
    public void CloseTransport()
    {
        var transport = _transport;
        _transport = null;
        transport?.Dispose();
        foreach (var device in _devices) device.MarkOffline();
    }

    /// <summary>Runs discovery when due, refreshes every device and returns their current snapshots.</summary>
    public async Task<List<BatteryDevice>> PollAsync(CancellationToken cancellationToken)
    {
        var transport = _transport;
        if (transport is null || transport.IsFaulted) return Snapshot();

        var now = DateTimeOffset.Now;
        var interval = Role == InterfaceRole.NotHidpp ? RetryNotHidppInterval : RediscoverInterval;
        // A receiver without paired devices is re-read every poll (three cheap register reads) so a new pairing shows up at once.
        var emptyReceiver = Role == InterfaceRole.Receiver && _devices.Count == 0;
        if (Role == InterfaceRole.Unknown || emptyReceiver || now - _lastDiscovery > interval)
        {
            await DiscoverAsync(transport, cancellationToken).ConfigureAwait(false);
        }

        var result = new List<BatteryDevice>(_devices.Count);
        foreach (var device in _devices)
        {
            if (transport.IsFaulted) break;
            cancellationToken.ThrowIfCancellationRequested();
            await device.RefreshAsync(transport, cancellationToken).ConfigureAwait(false);
            var snapshot = ToBatteryDevice(device);
            if (snapshot is not null) result.Add(snapshot);
        }
        return result;
    }

    /// <summary>Cached snapshots without any I/O (used when the poll budget is exhausted or the transport is closed).</summary>
    public List<BatteryDevice> Snapshot()
    {
        var result = new List<BatteryDevice>(_devices.Count);
        foreach (var device in _devices)
        {
            var snapshot = ToBatteryDevice(device);
            if (snapshot is not null) result.Add(snapshot);
        }
        return result;
    }

    public void Dispose() => CloseTransport();

    private async Task DiscoverAsync(HidppTransport transport, CancellationToken cancellationToken)
    {
        _lastDiscovery = DateTimeOffset.Now;

        var probe = new HidppDevice(_logger, Hidpp.ReceiverIndex);
        var answered = await probe.PingAsync(transport, cancellationToken).ConfigureAwait(false);
        if (transport.IsFaulted) return;

        if (answered && probe.ProtocolMajor >= 2)
        {
            Role = InterfaceRole.DirectDevice;
            KeepOrReplace(new[] { probe });
            _logger.LogInformation("HID++ {Version}.{Minor} device on {Key} (PID 0x{Pid:X4}, {Transport})",
                probe.ProtocolMajor, probe.ProtocolMinor, Info.Key, Info.ProductId, Info.IsBluetooth ? "Bluetooth" : "USB");
            return;
        }

        if (answered)
        {
            // HID++ 1.0 at 0xFF: a receiver (confirmed by its info register) or, rarely, an old direct device.
            var (serial, slots, kind) = await HidppReceiverRegisters.ReadReceiverInfoAsync(transport, cancellationToken).ConfigureAwait(false);
            if (transport.IsFaulted) return;
            if (serial is not null || Family != ReceiverFamily.None)
            {
                await EnumerateReceiverAsync(transport, serial, slots, cancellationToken).ConfigureAwait(false);
                return;
            }

            _logger.LogInformation("HID++ 1.0 device on {Key} (PID 0x{Pid:X4}); info register answered {Kind}", Info.Key, Info.ProductId, kind);
            Role = InterfaceRole.DirectDevice;
            KeepOrReplace(new[] { probe });
            return;
        }

        if (Family != ReceiverFamily.None)
        {
            _logger.LogDebug("No ping answer from receiver PID 0x{Pid:X4} on {Key}; enumerating by product id", Info.ProductId, Info.Key);
            await EnumerateReceiverAsync(transport, null, Hidpp.MaxReceiverSlots, cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogDebug("{Key} (PID 0x{Pid:X4}) did not answer a HID++ ping; ignoring for now", Info.Key, Info.ProductId);
        Role = InterfaceRole.NotHidpp;
        _devices.Clear();
    }

    private async Task EnumerateReceiverAsync(HidppTransport transport, string? serial, int slots, CancellationToken cancellationToken)
    {
        Role = InterfaceRole.Receiver;
        ReceiverSerial = string.IsNullOrEmpty(Info.SerialNumber) ? serial ?? string.Empty : Info.SerialNumber;
        MaxSlots = slots;

        var found = new List<HidppDevice>();
        var pairingRegisters = true;
        for (var slot = 1; slot <= MaxSlots; slot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (transport.IsFaulted) return;

            HidppDevice? device = null;
            if (pairingRegisters)
            {
                var (pairing, kind, error) = await HidppReceiverRegisters.ReadPairingAsync(transport, Family, slot, cancellationToken).ConfigureAwait(false);
                if (pairing is not null)
                {
                    device = Existing(slot, pairing.Wpid) ?? new HidppDevice(_logger, (byte)slot);
                    device.Wpid = pairing.Wpid;
                    if (device.Kind == DeviceKind.Unknown || !device.FeaturesDiscovered) device.Kind = pairing.Kind;
                    device.PairingSerial ??= pairing.Serial;
                    device.ReceiverName = await HidppReceiverRegisters.ReadDeviceNameAsync(transport, Family, slot, cancellationToken).ConfigureAwait(false) ?? device.ReceiverName;
                }
                else if (slot == 1 && (kind == HidppResultKind.Timeout || error == Hidpp1Error.InvalidSubId))
                {
                    _logger.LogDebug("Receiver {Key} does not expose pairing registers; falling back to ping enumeration", Info.Key);
                    pairingRegisters = false;
                }
                else if (kind == HidppResultKind.IoError)
                {
                    return;
                }
            }

            if (!pairingRegisters)
            {
                var probe = Existing(slot, null) ?? new HidppDevice(_logger, (byte)slot);
                var online = await probe.PingAsync(transport, cancellationToken).ConfigureAwait(false);
                // 0x09 = paired but asleep; 0x08 / timeout = empty slot.
                if (online || probe.LastPingError == Hidpp1Error.ResourceError) device = probe;
            }

            if (device is not null) found.Add(device);
        }

        KeepOrReplace(found);

        // The pairing table is re-read every few minutes (and on every retry of a flaky interface); only a change is news.
        var summary = string.Join(", ", found.Select(d => $"#{d.Index} '{d.Name}' {d.Kind} wpid={d.Wpid} serial={d.PairingSerial}"));
        var level = summary == _lastReceiverSummary ? LogLevel.Debug : LogLevel.Information;
        _lastReceiverSummary = summary;
        _logger.Log(level, "Receiver {Family} PID 0x{Pid:X4} serial {Serial}: {Count} paired device(s) in {Slots} slot(s): {Devices}",
            Family, Info.ProductId, ReceiverSerial, found.Count, MaxSlots, summary);
    }

    private HidppDevice? Existing(int slot, string? wpid)
        => _devices.FirstOrDefault(d => d.Index == slot && (wpid is null || d.Wpid is null || d.Wpid == wpid));

    private void KeepOrReplace(IEnumerable<HidppDevice> devices)
    {
        var list = devices.ToList();
        _devices.Clear();
        _devices.AddRange(list);
    }

    private BatteryDevice? ToBatteryDevice(HidppDevice device)
    {
        string id;
        ConnectionType connection;
        string detail;
        string? fallbackName = null;

        if (Role == InterfaceRole.Receiver)
        {
            id = !string.IsNullOrEmpty(device.PairingSerial)
                ? $"hidpp:{device.PairingSerial}"
                : $"hidpp:{Info.ProductId:X4}-{ReceiverSerial}:{device.Index}";
            connection = ConnectionType.UsbReceiver;
            detail = ReceiverFamilies.Label(Family);
        }
        else
        {
            // A wired device without any battery feature (e.g. a corded keyboard) is not a battery device.
            if (device.FeaturesDiscovered && !device.HasBatteryFeature) return null;

            id = device.UnitId != 0
                ? $"hidpp:{device.UnitId:X8}"
                : $"hidpp:{Info.ProductId:X4}-{(string.IsNullOrEmpty(Info.SerialNumber) ? Info.Key.GetHashCode(StringComparison.OrdinalIgnoreCase).ToString("X8") : Info.SerialNumber)}";
            connection = Info.IsBluetooth ? ConnectionType.Bluetooth : ConnectionType.Usb;
            detail = Info.IsBluetooth ? "Bluetooth" : "USB";
            if (!string.IsNullOrWhiteSpace(Info.ProductName)) fallbackName = Info.ProductName;
        }

        if (!device.IsOnline) detail = $"{detail} · 休眠中";

        var reading = device.LastReading;
        return new BatteryDevice
        {
            Id = id,
            Name = device.Name ?? fallbackName ?? $"Logitech {KindLabel(device.Kind)}",
            Kind = device.Kind,
            Connection = connection,
            Source = LogitechHidppBatteryProvider.ProviderName,
            Percent = reading?.Percent,
            Status = device.IsOnline ? reading?.Status ?? BatteryStatus.Unknown : BatteryStatus.Unknown,
            IsConnected = device.IsOnline,
            VoltageMillivolts = reading?.VoltageMillivolts,
            Detail = detail,
            UpdatedAt = DateTimeOffset.Now,
        };
    }

    private static string KindLabel(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "滑鼠",
        DeviceKind.Keyboard => "鍵盤",
        DeviceKind.Headset => "耳機",
        DeviceKind.Speaker => "喇叭",
        DeviceKind.Controller => "控制器",
        DeviceKind.Trackpad => "觸控板",
        DeviceKind.Presenter => "簡報器",
        _ => "裝置",
    };
}
