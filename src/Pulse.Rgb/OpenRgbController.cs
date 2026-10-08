using System.Net.Sockets;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;
using OpenRGB.NET;

namespace Pulse.Rgb;

/// <summary>
/// <see cref="IRgbController"/> backed by the OpenRGB SDK server through OpenRGB.NET.
/// The SDK client is synchronous, not thread-safe and blocks forever once the server stops answering, so every call
/// is serialised through one semaphore, runs on the thread pool and is bounded by a timeout; on timeout / IO error the
/// client is disposed and the next <see cref="ConnectAsync"/> creates a fresh one. A watchdog notices when OpenRGB
/// quits so the connection is dropped promptly even while the app is idle.
/// </summary>
public sealed class OpenRgbController : IRgbController
{
    private const int MaxProtocolVersion = 4;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SocketConnectTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan EnumerateTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeSlice = TimeSpan.FromMilliseconds(250);

    private static readonly RgbColor Black = new(0, 0, 0);

    private readonly ILogger<OpenRgbController> _logger;
    private readonly RgbDefaultsStore _defaults;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusLock = new();
    private readonly Timer _watchdog;

    private RgbStatus _status = new() { Host = OpenRgbInfo.DefaultHost, Port = OpenRgbInfo.DefaultPort };
    private string _host = OpenRgbInfo.DefaultHost;
    private int _port = OpenRgbInfo.DefaultPort;

    // Everything below is only touched while _gate is held.
    private OpenRgbClient? _client;
    private Device[] _rawDevices = [];
    private RgbDevice[]? _devices;
    private int _protocolVersion = MaxProtocolVersion;
    private volatile bool _disposed;

    // Stable per-device keys of the current raw list (index = OpenRGB device index); replaced atomically so
    // HasDefault can read it without the gate.
    private volatile string[] _deviceKeys = [];

    /// <param name="logger">Logger.</param>
    /// <param name="defaultsPath">Where "restore default" snapshots are kept; null = %APPDATA%\Pulse\rgb-defaults.json.</param>
    public OpenRgbController(ILogger<OpenRgbController> logger, string? defaultsPath = null)
    {
        _logger = logger;
        _defaults = new RgbDefaultsStore(defaultsPath, logger);
        _watchdog = new Timer(Watchdog, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public RgbStatus Status
    {
        get { lock (_statusLock) return _status; }
    }

    /// <summary>
    /// Raised synchronously on the thread that changed the status (a thread-pool thread for failures detected by the
    /// watchdog), never while the internal lock is held. Handlers should marshal to the UI thread themselves.
    /// </summary>
    public event EventHandler<RgbStatus>? StatusChanged;

    public void Configure(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host)) host = OpenRgbInfo.DefaultHost;
        if (port is < 1 or > ushort.MaxValue) port = OpenRgbInfo.DefaultPort;
        host = host.Trim();

        lock (_statusLock)
        {
            _host = host;
            _port = port;
        }

        Publish(Status with { Host = host, Port = port });
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return false;
        if (_client is { Connected: true }) return true;

        string host;
        int port;
        lock (_statusLock)
        {
            host = _host;
            port = _port;
        }

        Publish(Status with
        {
            State = RgbConnectionState.Connecting,
            Host = host,
            Port = port,
            ServerVersion = null,
            Message = $"正在連線到 OpenRGB SDK 伺服器（{host}:{port}）…",
        });

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled before the gate was acquired: nothing was touched, but the "Connecting" status must not stick.
            Publish(Status with { State = RgbConnectionState.Disconnected, Message = "OpenRGB 連線已取消。" });
            throw;
        }

        RgbStatus? next = null;
        try
        {
            if (_client is { Connected: true })
            {
                next = Status with { State = RgbConnectionState.Connected };
                return true;
            }

            DropClient();
            var client = new OpenRgbClient(host, port, OpenRgbInfo.ClientName, autoConnect: false,
                timeoutMs: (int)SocketConnectTimeout.TotalMilliseconds, protocolVersionNumber: MaxProtocolVersion);
            _client = client;

            try
            {
                await RunAsync(() => { client.Connect(); return true; }, ConnectTimeout, cancellationToken, probePeer: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DropClient();
                next = Status with { State = RgbConnectionState.Disconnected, Message = "OpenRGB 連線已取消。" };
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogInformation("OpenRGB SDK server not reachable at {Host}:{Port}: {Error}", host, port, ex.Message);
                DropClient();
                next = Status with { State = RgbConnectionState.Disconnected, ServerVersion = null, Message = CannotConnectMessage(host, port) };
                return false;
            }

            var protocol = client.CommonProtocolVersion.Number;
            _protocolVersion = (int)protocol;
            _logger.LogInformation("Connected to OpenRGB SDK server at {Host}:{Port} (protocol v{Protocol})", host, port, protocol);

            int deviceCount;
            try
            {
                deviceCount = (await RefreshDevicesCoreAsync(client, cancellationToken).ConfigureAwait(false)).Count;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DropClient();
                next = Status with { State = RgbConnectionState.Disconnected, Message = "OpenRGB 連線已取消。" };
                throw;
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                _logger.LogWarning("OpenRGB connection established but device enumeration failed: {Error}", ex.Message);
                DropClient();
                next = Status with { State = RgbConnectionState.Disconnected, ServerVersion = null, Message = CannotConnectMessage(host, port) };
                return false;
            }
            catch (Exception ex)
            {
                // The server answered but the payload was rejected by the SDK client: stay connected, retry on refresh.
                _logger.LogWarning(ex, "OpenRGB device enumeration failed after connecting");
                _devices = null;
                deviceCount = 0;
            }

            _watchdog.Change(WatchdogInterval, WatchdogInterval);
            next = Status with
            {
                State = RgbConnectionState.Connected,
                ServerVersion = $"SDK protocol v{protocol}",
                Message = $"已連線到 OpenRGB（SDK 通訊協定 v{protocol}），偵測到 {deviceCount} 個 RGB 裝置。",
            };
            return true;
        }
        finally
        {
            _gate.Release();
            if (next is not null) Publish(next);
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        RgbStatus? next = null;
        try
        {
            var hadClient = _client is not null;
            DropClient();
            if (hadClient) _logger.LogInformation("Disconnected from OpenRGB SDK server");
            next = Status with { State = RgbConnectionState.Disconnected, ServerVersion = null, Message = "已中斷與 OpenRGB 的連線。" };
        }
        finally
        {
            _gate.Release();
            if (next is not null) Publish(next);
        }
    }

    public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(bool refresh = false, CancellationToken cancellationToken = default)
        => ExecuteAsync("GetDevices", (IReadOnlyList<RgbDevice>)Array.Empty<RgbDevice>(), async (client, ct) =>
        {
            if (!refresh && _devices is { } cached) return cached;
            return await RefreshDevicesCoreAsync(client, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task SetDeviceColorAsync(int deviceIndex, RgbColor color, CancellationToken cancellationToken = default)
        => ExecuteAsync("SetDeviceColor", false, async (client, ct) =>
        {
            var device = await RequireDeviceAsync(client, deviceIndex, ct).ConfigureAwait(false);
            await SetDeviceColorCoreAsync(client, device, color, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task SetZoneColorAsync(int deviceIndex, int zoneIndex, RgbColor color, CancellationToken cancellationToken = default)
        => ExecuteAsync("SetZoneColor", false, async (client, ct) =>
        {
            var device = await RequireDeviceAsync(client, deviceIndex, ct).ConfigureAwait(false);
            if (zoneIndex < 0 || zoneIndex >= device.Zones.Count)
                throw new BadIndexException(nameof(zoneIndex), zoneIndex, $"Device {deviceIndex} has {device.Zones.Count} zones.");

            var zone = device.Zones[zoneIndex];
            var (perLed, _) = ResolveColorMode(device);
            if (perLed is null)
            {
                // Without a per-LED mode the zone cannot be addressed on its own; paint the whole device instead.
                _logger.LogDebug("OpenRGB device {Device} has no per-LED mode; painting the whole device instead of zone {Zone}", deviceIndex, zoneIndex);
                await SetDeviceColorCoreAsync(client, device, color, ct).ConfigureAwait(false);
                return true;
            }

            device = await EnsureModeAsync(client, device, perLed, ct).ConfigureAwait(false);
            if (zone.LedCount <= 0)
            {
                _logger.LogDebug("Zone {Zone} of OpenRGB device {Device} has no LEDs; nothing to paint", zoneIndex, deviceIndex);
                return true;
            }

            var colors = Repeat(color, zone.LedCount);
            await RunAsync(() => { client.UpdateZoneLeds(device.Index, zoneIndex, colors); return true; }, RequestTimeout, ct).ConfigureAwait(false);

            var start = device.Zones.Take(zoneIndex).Sum(z => z.LedCount);
            var painted = device.Colors.ToArray();
            for (var i = start; i < Math.Min(start + zone.LedCount, painted.Length); i++) painted[i] = color;
            StoreDevice(device with { Colors = painted });
            return true;
        }, cancellationToken);

    public Task SetModeAsync(int deviceIndex, int modeIndex, RgbColor? color = null, int? speed = null, int? brightness = null, CancellationToken cancellationToken = default)
        => ExecuteAsync("SetMode", false, async (client, ct) =>
        {
            var device = await RequireDeviceAsync(client, deviceIndex, ct).ConfigureAwait(false);
            if (modeIndex < 0 || modeIndex >= device.Modes.Count)
                throw new BadIndexException(nameof(modeIndex), modeIndex, $"Device {deviceIndex} has {device.Modes.Count} modes.");

            var mode = device.Modes[modeIndex];
            if (device.Index >= _rawDevices.Length || modeIndex >= _rawDevices[device.Index].Modes.Length)
            {
                // Cached snapshot no longer matches the raw list (should not happen: both are replaced together).
                _devices = null;
                throw new InvalidOperationException("Cached OpenRGB device list is stale; refresh and retry.");
            }

            var raw = _rawDevices[device.Index].Modes[modeIndex];

            uint? sdkSpeed = null;
            if (speed is { } s)
            {
                if (mode.SupportsSpeed)
                {
                    var lo = Math.Min(raw.SpeedMin, raw.SpeedMax);
                    var hi = Math.Max(raw.SpeedMin, raw.SpeedMax);
                    sdkSpeed = (uint)Math.Clamp(s, (long)lo, hi);
                }
                else
                {
                    _logger.LogDebug("Mode {Mode} of OpenRGB device {Device} has no speed; ignoring speed={Speed}", mode.Name, deviceIndex, s);
                }
            }

            // OpenRGB.NET 3.1.1 cannot send brightness; the UPDATEMODE packet built by OpenRgbModePacket can (clamped there).
            uint? sdkBrightness = brightness is { } b && raw.SupportsBrightness ? (uint)Math.Max(0, b) : null;

            Color[]? sdkColors = null;
            var paintLeds = false;
            if (color is { } c)
            {
                if (mode.IsPerLed)
                {
                    paintLeds = device.LedCount > 0;
                }
                else if (mode.SupportsColor && raw.Colors.Length > 0)
                {
                    // The SDK client requires exactly as many colours as the mode currently reports.
                    sdkColors = Repeat(c, raw.Colors.Length);
                    if (raw.ColorMode == ColorMode.Random)
                        _logger.LogDebug("Mode {Mode} of OpenRGB device {Device} is in random-colour mode; the colour may be ignored by the device", mode.Name, deviceIndex);
                }
                else
                {
                    _logger.LogDebug("Mode {Mode} of OpenRGB device {Device} takes no colour; ignoring colour {Color}", mode.Name, deviceIndex, c);
                }
            }

            await ApplyModeAsync(client, device.Index, modeIndex, sdkSpeed, null, sdkColors, sdkBrightness, ct).ConfigureAwait(false);

            var updated = (CachedDevice(device.Index) ?? device) with { ActiveModeIndex = modeIndex };
            if (paintLeds && color is { } led)
            {
                var colors = Repeat(led, device.LedCount);
                await RunAsync(() => { client.UpdateLeds(device.Index, colors); return true; }, RequestTimeout, ct).ConfigureAwait(false);
                updated = updated with { Colors = ToModel(colors) };
            }
            else if (sdkColors is not null && color is { } whole)
            {
                updated = updated with { Colors = ToModel(Repeat(whole, device.LedCount)) };
            }

            StoreDevice(updated);
            _logger.LogDebug("OpenRGB device {Device} switched to mode {Mode} (speed={Speed}, colours={Colours})", deviceIndex, mode.Name, sdkSpeed, sdkColors?.Length);
            return true;
        }, cancellationToken);

    public Task SetAllAsync(RgbColor color, CancellationToken cancellationToken = default)
        => ExecuteAsync("SetAll", false, async (client, ct) =>
        {
            var devices = _devices ?? await RefreshDevicesCoreAsync(client, ct).ConfigureAwait(false);
            foreach (var device in devices.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await SetDeviceColorCoreAsync(client, device, color, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!IsConnectionFailure(ex) && ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Painting OpenRGB device {Index} ({Name}) failed; continuing with the next device", device.Index, device.Name);
                }
            }

            return true;
        }, cancellationToken);

    public Task TurnOffAsync(int deviceIndex, CancellationToken ct = default)
        => ExecuteAsync("TurnOff", false, async (client, token) =>
        {
            var device = await RequireDeviceAsync(client, deviceIndex, token).ConfigureAwait(false);
            await TurnOffCoreAsync(client, device, token).ConfigureAwait(false);
            return true;
        }, ct);

    public Task TurnOffAllAsync(CancellationToken ct = default)
        => ExecuteAsync("TurnOffAll", false, async (client, token) =>
        {
            await ForEachDeviceAsync(client, "Turning off", async (device, t) =>
            {
                await TurnOffCoreAsync(client, device, t).ConfigureAwait(false);
                return true;
            }, token).ConfigureAwait(false);
            return true;
        }, ct);

    public async Task RestoreDefaultAsync(int deviceIndex, CancellationToken ct = default)
    {
        var outcome = await ExecuteAsync("RestoreDefault", RestoreOutcome.None, async (client, token) =>
        {
            var device = await RequireDeviceAsync(client, deviceIndex, token).ConfigureAwait(false);
            return await RestoreCoreAsync(client, device, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (outcome == RestoreOutcome.NothingToRestore)
            Publish(Status with { Message = "這個裝置沒有可還原的預設狀態，也沒有可切換的韌體燈效，因此無法還原。" });
    }

    public async Task RestoreAllDefaultsAsync(CancellationToken ct = default)
    {
        var skipped = await ExecuteAsync("RestoreAllDefaults", (IReadOnlyList<string>)Array.Empty<string>(), async (client, token) =>
        {
            var results = await ForEachDeviceAsync(client, "Restoring", (device, t) => RestoreCoreAsync(client, device, t), token).ConfigureAwait(false);
            return (IReadOnlyList<string>)results.Where(r => r.Result == RestoreOutcome.NothingToRestore).Select(r => r.Device.Name).ToArray();
        }, ct).ConfigureAwait(false);

        if (skipped.Count > 0)
            Publish(Status with { Message = $"以下裝置沒有可還原的預設狀態，也沒有可切換的韌體燈效，已略過：{string.Join("、", skipped)}。" });
    }

    public Task SaveCurrentAsDefaultAsync(int deviceIndex, CancellationToken ct = default)
        => ExecuteAsync("SaveCurrentAsDefault", false, async (client, token) =>
        {
            var device = await RequireDeviceAsync(client, deviceIndex, token).ConfigureAwait(false);

            // Re-read the device: the raw list is not updated by LED writes, so it may not reflect what is lit now.
            var fresh = await ReadBackAsync(client, device.Index, token).ConfigureAwait(false);
            var slot = device.Index;

            var keys = _deviceKeys;
            var key = slot < keys.Length ? keys[slot] : BaseKey(fresh);
            var snapshot = Capture(fresh, key);
            _defaults.Set(snapshot);
            _logger.LogInformation("OpenRGB device {Device} ({Name}): current state saved as default (mode {Mode})", device.Index, device.Name, snapshot.ModeName);
            return true;
        }, ct);

    public bool HasDefault(int deviceIndex)
    {
        var keys = _deviceKeys;
        if (deviceIndex < 0 || deviceIndex >= keys.Length) return false;
        try
        {
            return _defaults.Contains(keys[deviceIndex]);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading RGB defaults failed");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watchdog.Dispose();

        var acquired = _gate.Wait(TimeSpan.FromSeconds(3));
        try
        {
            DropClient();
        }
        finally
        {
            if (acquired) _gate.Release();
        }

        lock (_statusLock)
        {
            _status = _status with { State = RgbConnectionState.Disconnected, ServerVersion = null };
        }
    }

    // ----- core operations (caller holds _gate) -------------------------------------------------------------

    private async Task<IReadOnlyList<RgbDevice>> RefreshDevicesCoreAsync(OpenRgbClient client, CancellationToken ct)
    {
        var raw = await RunAsync(client.GetAllControllerData, EnumerateTimeout, ct).ConfigureAwait(false);
        var keys = ComputeKeys(raw);
        _rawDevices = raw;
        _devices = raw.Select(Map).ToArray();
        _deviceKeys = keys;
        _logger.LogDebug("OpenRGB reported {Count} devices", raw.Length);
        CaptureMissingDefaults(raw, keys);
        return _devices;
    }

    /// <summary>Records the state of every device seen for the first time (keyed by <see cref="ComputeKeys"/>).</summary>
    private void CaptureMissingDefaults(Device[] raw, string[] keys)
    {
        try
        {
            var missing = new List<RgbDeviceDefault>();
            for (var i = 0; i < raw.Length && i < keys.Length; i++)
            {
                if (!_defaults.Contains(keys[i])) missing.Add(Capture(raw[i], keys[i]));
            }

            if (missing.Count == 0) return;
            _defaults.AddMissing(missing);
            foreach (var s in missing)
                _logger.LogInformation("OpenRGB default captured for {Name} (mode {Mode}, {Leds} LEDs) in {Path}", s.DeviceName, s.ModeName, s.LedColors.Count, _defaults.FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capturing OpenRGB default snapshots failed");
        }
    }

    private async Task TurnOffCoreAsync(OpenRgbClient client, RgbDevice device, CancellationToken ct)
    {
        var off = RgbModeRules.FindOffMode(device.Modes);
        if (off is null)
        {
            await SetDeviceColorCoreAsync(client, device, Black, ct).ConfigureAwait(false);
            _logger.LogDebug("OpenRGB device {Device} has no Off mode; painted black", device.Index);
            return;
        }

        try
        {
            await ApplyModeAsync(client, device.Index, off.Index, ct: ct).ConfigureAwait(false);
            StoreDevice((CachedDevice(device.Index) ?? device) with { ActiveModeIndex = off.Index, Colors = new RgbColor[device.LedCount] });
            _logger.LogDebug("OpenRGB device {Device} switched to its Off mode ({Mode})", device.Index, off.Index);
        }
        catch (InvalidOperationException ex)
        {
            // Refused Off mode: Direct + black LEDs gives the same result.
            _logger.LogInformation("OpenRGB device {Device}: Off mode refused ({Reason}); painting it black instead", device.Index, ex.Message);
            await SetDeviceColorCoreAsync(client, CachedDevice(device.Index) ?? device, Black, ct).ConfigureAwait(false);
        }
    }

    private async Task<RestoreOutcome> RestoreCoreAsync(OpenRgbClient client, RgbDevice device, CancellationToken ct)
    {
        if (device.Index >= _rawDevices.Length) throw StaleCache();
        var raw = _rawDevices[device.Index];
        var keys = _deviceKeys;
        var snapshot = device.Index < keys.Length ? _defaults.Get(keys[device.Index]) : null;

        if (snapshot is not null)
        {
            var modeIndex = ResolveSavedMode(raw, snapshot);
            if (modeIndex >= 0)
            {
                await ApplySnapshotAsync(client, device, raw, modeIndex, snapshot, ct).ConfigureAwait(false);
                return RestoreOutcome.Restored;
            }

            _logger.LogWarning("Saved default mode '{Mode}' (#{Index}) no longer exists on OpenRGB device {Device}; using the fallback", snapshot.ModeName, snapshot.ModeIndex, device.Index);
        }

        var effect = RgbModeRules.FindFirmwareEffect(device.Modes);
        if (effect is null)
        {
            _logger.LogInformation("OpenRGB device {Device} ({Name}) has no saved default and no firmware effect; nothing to restore", device.Index, device.Name);
            return RestoreOutcome.NothingToRestore;
        }

        await ApplyModeAsync(client, device.Index, effect.Index, ct: ct).ConfigureAwait(false);
        _logger.LogInformation("OpenRGB device {Device} ({Name}) has no saved default; switched to firmware effect {Mode}", device.Index, device.Name, effect.Name);
        return RestoreOutcome.FallbackEffect;
    }

    private async Task ApplySnapshotAsync(OpenRgbClient client, RgbDevice device, Device raw, int modeIndex, RgbDeviceDefault snapshot, CancellationToken ct)
    {
        var rawMode = raw.Modes[modeIndex];

        uint? speed = null;
        if (rawMode.SupportsSpeed && snapshot.Speed is { } s)
        {
            var lo = Math.Min(rawMode.SpeedMin, rawMode.SpeedMax);
            var hi = Math.Max(rawMode.SpeedMin, rawMode.SpeedMax);
            speed = Math.Clamp(s, lo, hi);
        }

        Direction? direction = rawMode.SupportsDirection && snapshot.Direction is { } d ? (Direction)d : null;

        // UpdateMode requires exactly as many colours as the mode currently reports.
        var modeColors = rawMode.Colors.Length > 0 ? Fit(ParseColors(snapshot.ModeColors), rawMode.Colors.Length) : null;

        await ApplyModeAsync(client, device.Index, modeIndex, speed, direction, modeColors, ct: ct).ConfigureAwait(false);

        var savedLeds = ParseColors(snapshot.LedColors);
        IReadOnlyList<RgbColor> cached = device.Colors;
        var isPerLed = modeIndex < device.Modes.Count && device.Modes[modeIndex].IsPerLed;
        var ledsWritten = 0;
        if (isPerLed && device.LedCount > 0 && Fit(savedLeds, device.LedCount) is { } leds)
        {
            await RunAsync(() => { client.UpdateLeds(device.Index, leds); return true; }, RequestTimeout, ct).ConfigureAwait(false);
            cached = ToModel(leds);
            ledsWritten = leds.Length;
        }
        else if (modeColors is { Length: > 0 })
        {
            cached = ToModel(Repeat(new RgbColor(modeColors[0].R, modeColors[0].G, modeColors[0].B), device.LedCount));
        }
        else if (Fit(savedLeds, device.LedCount) is { } reported)
        {
            cached = ToModel(reported);
        }

        StoreDevice(device with { ActiveModeIndex = modeIndex, Colors = cached });
        _logger.LogInformation("OpenRGB device {Device} ({Name}) restored to its default: mode {Mode} (speed={Speed}, direction={Direction}, mode colours={ModeColours}, LEDs={Leds})",
            device.Index, device.Name, rawMode.Name, speed, direction, modeColors?.Length ?? 0, ledsWritten);
    }

    /// <summary>Saved mode by name (preferring the saved index among equal names), else by index; -1 when gone.</summary>
    private static int ResolveSavedMode(Device raw, RgbDeviceDefault snapshot)
    {
        var modes = raw.Modes;
        var name = snapshot.ModeName.Trim();
        if (name.Length > 0)
        {
            if (snapshot.ModeIndex >= 0 && snapshot.ModeIndex < modes.Length && modes[snapshot.ModeIndex].Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return snapshot.ModeIndex;
            var byName = Array.FindIndex(modes, m => m.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            if (byName >= 0) return byName;
        }

        return snapshot.ModeIndex >= 0 && snapshot.ModeIndex < modes.Length ? snapshot.ModeIndex : -1;
    }

    /// <summary>Runs <paramref name="body"/> for every device; per-device failures (other than a lost connection) are logged and skipped.</summary>
    private async Task<List<(RgbDevice Device, T Result)>> ForEachDeviceAsync<T>(OpenRgbClient client, string action, Func<RgbDevice, CancellationToken, Task<T>> body, CancellationToken ct)
    {
        var devices = _devices ?? await RefreshDevicesCoreAsync(client, ct).ConfigureAwait(false);
        var results = new List<(RgbDevice, T)>();
        foreach (var listed in devices.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            var device = _devices?.FirstOrDefault(d => d.Index == listed.Index) ?? listed;
            try
            {
                results.Add((device, await body(device, ct).ConfigureAwait(false)));
            }
            catch (Exception ex) when (!IsConnectionFailure(ex) && ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "{Action} OpenRGB device {Index} ({Name}) failed; continuing with the next device", action, device.Index, device.Name);
            }
        }

        return results;
    }

    private async Task<RgbDevice> RequireDeviceAsync(OpenRgbClient client, int deviceIndex, CancellationToken ct)
    {
        var devices = _devices ?? await RefreshDevicesCoreAsync(client, ct).ConfigureAwait(false);
        if (deviceIndex < 0 || deviceIndex >= devices.Count)
            throw new BadIndexException(nameof(deviceIndex), deviceIndex, $"OpenRGB reports {devices.Count} devices.");
        return devices[deviceIndex];
    }

    private async Task SetDeviceColorCoreAsync(OpenRgbClient client, RgbDevice device, RgbColor color, CancellationToken ct)
    {
        var (perLed, staticMode) = ResolveColorMode(device);
        if (perLed is not null)
        {
            device = await EnsureModeAsync(client, device, perLed, ct).ConfigureAwait(false);
            if (device.LedCount <= 0)
            {
                _logger.LogDebug("OpenRGB device {Device} has no LEDs; nothing to paint", device.Index);
                return;
            }

            var colors = Repeat(color, device.LedCount);
            await RunAsync(() => { client.UpdateLeds(device.Index, colors); return true; }, RequestTimeout, ct).ConfigureAwait(false);
            StoreDevice(device with { Colors = ToModel(colors) });
            return;
        }

        if (staticMode is not null)
        {
            var raw = _rawDevices[device.Index].Modes[staticMode.Index];
            var colors = Repeat(color, Math.Max(1, raw.Colors.Length));
            await ApplyModeAsync(client, device.Index, staticMode.Index, colors: colors, ct: ct).ConfigureAwait(false);
            StoreDevice((CachedDevice(device.Index) ?? device) with { ActiveModeIndex = staticMode.Index, Colors = ToModel(Repeat(color, device.LedCount)) });
            return;
        }

        if (device.LedCount <= 0)
        {
            _logger.LogDebug("OpenRGB device {Device} has no LEDs; nothing to paint", device.Index);
            return;
        }

        var fallback = Repeat(color, device.LedCount);
        await RunAsync(() => { client.UpdateLeds(device.Index, fallback); return true; }, RequestTimeout, ct).ConfigureAwait(false);
        StoreDevice(device with { Colors = ToModel(fallback) });
    }

    /// <summary>
    /// Switches the device to <paramref name="mode"/> unless the server says it is already active (the cache alone is not
    /// trusted: OpenRGB or another client may have changed the mode); returns the updated cache entry.
    /// </summary>
    private async Task<RgbDevice> EnsureModeAsync(OpenRgbClient client, RgbDevice device, RgbMode mode, CancellationToken ct)
    {
        var fresh = await ReadBackAsync(client, device.Index, ct).ConfigureAwait(false);
        if (fresh.ActiveModeIndex != mode.Index)
        {
            await ApplyModeAsync(client, device.Index, mode.Index, ct: ct).ConfigureAwait(false);
            _logger.LogDebug("OpenRGB device {Device} switched to mode {Mode}", device.Index, mode.Name);
        }
        return CachedDevice(device.Index) ?? device with { ActiveModeIndex = mode.Index };
    }

    /// <summary>
    /// Switches a device to a mode in a way OpenRGB 1.0 accepts and confirms it with a read-back.
    /// Direct / Custom without parameters go through SETCUSTOMMODE; everything else through an UPDATEMODE packet with
    /// valid direction / speed / brightness / colour values (<see cref="OpenRgbModePacket"/>). OpenRGB silently ignores a
    /// mode it considers invalid, so the result is checked and an <see cref="InvalidOperationException"/> reports a refusal.
    /// </summary>
    private async Task<Device> ApplyModeAsync(OpenRgbClient client, int deviceIndex, int modeIndex,
        uint? speed = null, Direction? direction = null, Color[]? colors = null, uint? brightness = null, CancellationToken ct = default)
    {
        if (deviceIndex >= _rawDevices.Length || modeIndex < 0 || modeIndex >= _rawDevices[deviceIndex].Modes.Length) throw StaleCache();
        var mode = _rawDevices[deviceIndex].Modes[modeIndex];
        var plainDirect = speed is null && direction is null && colors is null && brightness is null
                          && mode.Flags.HasFlag(ModeFlags.HasPerLedColor)
                          && (mode.Name.Equals("Direct", StringComparison.OrdinalIgnoreCase) || mode.Name.Equals("Custom", StringComparison.OrdinalIgnoreCase));

        await RunAsync(() =>
        {
            if (plainDirect)
                client.SetCustomMode(deviceIndex);
            else if (!OpenRgbModePacket.TrySend(client, _protocolVersion, deviceIndex, modeIndex, mode, speed, direction, colors, brightness))
                client.UpdateMode(deviceIndex, modeIndex, speed, direction, colors); // socket not reachable: best effort
            return true;
        }, RequestTimeout, ct).ConfigureAwait(false);

        // UPDATEMODE is queued to the controller's own thread on the server, so the read-back may need a moment.
        Device fresh = _rawDevices[deviceIndex];
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (attempt > 0) await Task.Delay(150, ct).ConfigureAwait(false);
            fresh = await ReadBackAsync(client, deviceIndex, ct).ConfigureAwait(false);
            if (fresh.ActiveModeIndex == modeIndex) return fresh;
        }

        _logger.LogWarning("OpenRGB did not apply mode {Mode} (#{Index}) to device {Device}; it reports mode #{Active}",
            mode.Name, modeIndex, deviceIndex, fresh.ActiveModeIndex);
        throw new InvalidOperationException($"OpenRGB 沒有套用「{mode.Name}」模式（伺服器拒絕了這個設定）。");
    }

    /// <summary>Re-reads one device from the server and refreshes both caches with it.</summary>
    private async Task<Device> ReadBackAsync(OpenRgbClient client, int deviceIndex, CancellationToken ct)
    {
        var fresh = await RunAsync(() => client.GetControllerData(deviceIndex), RequestTimeout, ct).ConfigureAwait(false);
        if (deviceIndex >= _rawDevices.Length) throw StaleCache();
        var raw = _rawDevices.ToArray();
        raw[deviceIndex] = fresh;
        _rawDevices = raw;
        StoreDevice(Map(fresh) with { Index = deviceIndex });
        return fresh;
    }

    private RgbDevice? CachedDevice(int deviceIndex) => _devices?.FirstOrDefault(d => d.Index == deviceIndex);

    /// <summary>Picks the per-LED mode ("Direct" preferred) or, failing that, a "Static" mode with a mode-specific colour.</summary>
    private (RgbMode? PerLed, RgbMode? Static) ResolveColorMode(RgbDevice device)
    {
        var perLed = device.Modes.FirstOrDefault(m => m.IsPerLed && m.Name.Equals("Direct", StringComparison.OrdinalIgnoreCase))
                     ?? device.Modes.FirstOrDefault(m => m.IsPerLed && m.Name.Equals("Custom", StringComparison.OrdinalIgnoreCase))
                     ?? device.Modes.FirstOrDefault(m => m.IsPerLed);
        if (perLed is not null) return (perLed, null);

        var rawModes = device.Index < _rawDevices.Length ? _rawDevices[device.Index].Modes : [];
        var staticMode = device.Modes.FirstOrDefault(m =>
            m.Name.Equals("Static", StringComparison.OrdinalIgnoreCase)
            && m.Index < rawModes.Length
            && rawModes[m.Index].Flags.HasFlag(ModeFlags.HasModeSpecificColor)
            && rawModes[m.Index].Colors.Length > 0);
        return (null, staticMode);
    }

    /// <summary>Copy-on-write replacement so lists already handed out stay immutable snapshots.</summary>
    private void StoreDevice(RgbDevice device)
    {
        if (_devices is null) return;
        var slot = Array.FindIndex(_devices, d => d.Index == device.Index);
        if (slot < 0) return;
        var copy = _devices.ToArray();
        copy[slot] = device;
        _devices = copy;
    }

    // ----- plumbing --------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="body"/> with the gate held and a live client. Connection failures drop the client and
    /// set <see cref="RgbConnectionState.Error"/>; SDK rejections are logged and leave the connection alone.
    /// Only <see cref="ArgumentOutOfRangeException"/> (bad index) and cancellation reach the caller.
    /// </summary>
    private async Task<T> ExecuteAsync<T>(string operation, T fallback, Func<OpenRgbClient, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        if (_disposed) return fallback;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        RgbStatus? next = null;
        try
        {
            var client = _client;
            if (client is null)
            {
                var current = Status;
                if (current.State == RgbConnectionState.Disconnected && string.IsNullOrEmpty(current.Message))
                    next = current with { Message = "尚未連線到 OpenRGB SDK 伺服器。" };
                return fallback;
            }

            if (OpenRgbClientProbe.IsPeerClosed(client))
                throw new IOException("The OpenRGB SDK server closed the connection.");

            return await body(client, cancellationToken).ConfigureAwait(false);
        }
        catch (BadIndexException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // RunAsync drops the client only when a blocked SDK call had to be abandoned; a cancellation that landed
            // between calls (or after the call finished) leaves the connection intact.
            if (_client is null)
            {
                _logger.LogDebug("OpenRGB {Operation} cancelled while a request was in flight; the connection was reset", operation);
                next = Status with { State = RgbConnectionState.Disconnected, ServerVersion = null, Message = "OpenRGB 作業已取消，連線已重設。" };
            }
            else
            {
                _logger.LogDebug("OpenRGB {Operation} cancelled", operation);
            }

            throw;
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            _logger.LogWarning("OpenRGB {Operation} failed, dropping the connection: {Error}", operation, ex.Message);
            DropClient();
            next = Status with { State = RgbConnectionState.Error, ServerVersion = null, Message = ConnectionLostMessage };
            return fallback;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            _logger.LogWarning(ex, "OpenRGB {Operation} rejected by the SDK client", operation);
            _devices = null; // the server-side device list may have changed; re-query next time
            next = Status with { Message = "OpenRGB 拒絕了這次燈光操作（裝置清單可能已變更），請重新整理裝置清單後再試。" };
            return fallback;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenRGB {Operation} failed unexpectedly", operation);
            next = Status with { Message = "OpenRGB 發生未預期的錯誤，請查看記錄檔。" };
            return fallback;
        }
        finally
        {
            _gate.Release();
            if (next is not null) Publish(next);
        }
    }

    /// <summary>
    /// Runs a blocking SDK call on the thread pool bounded by <paramref name="timeout"/> / the token. If the bound is
    /// hit the client is dropped (disposing it also unblocks the SDK's internal wait).
    /// </summary>
    private async Task<T> RunAsync<T>(Func<T> call, TimeSpan timeout, CancellationToken cancellationToken, bool probePeer = true)
    {
        var task = Task.Run(call, CancellationToken.None);
        try
        {
            // Wait in slices so a server that dies mid-request is noticed by the probe long before the timeout;
            // the SDK's own wait never returns on its own once the peer is gone.
            var deadline = DateTime.UtcNow + timeout;
            while (!task.IsCompleted)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException($"OpenRGB SDK call did not complete within {timeout}.");
                await Task.WhenAny(task, Task.Delay(remaining < ProbeSlice ? remaining : ProbeSlice, cancellationToken)).ConfigureAwait(false);
                if (task.IsCompleted) break;
                cancellationToken.ThrowIfCancellationRequested();
                if (probePeer && _client is { } live && OpenRgbClientProbe.IsPeerClosed(live))
                    throw new IOException("The OpenRGB SDK server closed the connection.");
            }

            return await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
        {
            if (!task.IsCompleted)
            {
                // The abandoned call is unblocked by disposing the client; observe its (expected) failure quietly.
                _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                _logger.LogDebug("OpenRGB SDK call abandoned ({Reason}); disposing the client", ex is TimeoutException ? $"timeout {timeout}" : ex.GetType().Name);
                DropClient();
            }

            throw;
        }
    }

    /// <summary>Periodic liveness check so a closed OpenRGB is noticed (and the SDK's spinning read loop stopped) while idle.</summary>
    private void Watchdog(object? state)
    {
        // Wait(0) rather than blocking: a request in progress will notice a dead peer on its own.
        if (_disposed || !_gate.Wait(0)) return;

        RgbStatus? next = null;
        try
        {
            var client = _client;
            if (client is null || !OpenRgbClientProbe.IsPeerClosed(client)) return;

            _logger.LogWarning("OpenRGB SDK server closed the connection; dropping the client");
            DropClient();
            next = Status with { State = RgbConnectionState.Error, ServerVersion = null, Message = ConnectionLostMessage };
        }
        finally
        {
            _gate.Release();
            if (next is not null) Publish(next);
        }
    }

    private void DropClient()
    {
        if (!_disposed) _watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var client = _client;
        _client = null;
        _devices = null;
        _rawDevices = [];
        _deviceKeys = [];
        if (client is null) return;

        try
        {
            client.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing the OpenRGB client threw");
        }
    }

    private void Publish(RgbStatus next)
    {
        lock (_statusLock)
        {
            if (next == _status) return;
            _status = next;
        }

        try
        {
            StatusChanged?.Invoke(this, next);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A StatusChanged handler threw");
        }
    }

    private static bool IsConnectionFailure(Exception ex) => ex switch
    {
        BadIndexException => false,
        SocketException or IOException or ObjectDisposedException or TimeoutException => true,
        // Raised by the SDK's internal token once the client has been disposed underneath a blocked call.
        OperationCanceledException => true,
        // The SDK's span readers throw these on a truncated / stale packet, i.e. a corrupted stream.
        ArgumentOutOfRangeException or IndexOutOfRangeException => true,
        AggregateException agg => agg.InnerExceptions.Count > 0 && agg.InnerExceptions.All(IsConnectionFailure),
        _ => false,
    };

    private const string ConnectionLostMessage = "與 OpenRGB 的連線已中斷（OpenRGB 可能已關閉）。請確認 OpenRGB 正在執行且已啟用 SDK Server，然後重新連線。";

    private static string CannotConnectMessage(string host, int port)
        => $"無法連線到 OpenRGB SDK 伺服器（{host}:{port}）。請安裝 OpenRGB，並在 設定 › SDK Server 啟用伺服器（或以 --server 參數啟動）。";

    private InvalidOperationException StaleCache()
    {
        // Cached snapshot no longer matches the raw list (should not happen: both are replaced together).
        _devices = null;
        return new InvalidOperationException("Cached OpenRGB device list is stale; refresh and retry.");
    }

    private enum RestoreOutcome
    {
        None,
        Restored,
        FallbackEffect,
        NothingToRestore,
    }

    /// <summary>Bad device / zone / mode index supplied by the caller; the only failure that propagates.</summary>
    private sealed class BadIndexException(string paramName, int actualValue, string message)
        : ArgumentOutOfRangeException(paramName, actualValue, message);

    // ----- mapping ---------------------------------------------------------------------------------------------

    private static RgbDevice Map(Device device) => new()
    {
        Index = device.Index,
        Name = string.IsNullOrWhiteSpace(device.Name) ? $"OpenRGB 裝置 {device.Index}" : device.Name,
        Type = HumanizeType((int)device.Type),
        Vendor = string.IsNullOrWhiteSpace(device.Vendor) ? null : device.Vendor,
        Description = string.IsNullOrWhiteSpace(device.Description) ? null : device.Description,
        Zones = device.Zones.Select(z => new RgbZone { Index = z.Index, Name = z.Name, LedCount = (int)z.LedCount }).ToArray(),
        Modes = device.Modes.Select(MapMode).ToArray(),
        ActiveModeIndex = device.ActiveModeIndex,
        LedCount = device.Leds.Length,
        Colors = ToModel(device.Colors),
    };

    private static RgbMode MapMode(Mode mode) => new()
    {
        Index = mode.Index,
        Name = mode.Name,
        SupportsColor = mode.Flags.HasFlag(ModeFlags.HasPerLedColor) || mode.Flags.HasFlag(ModeFlags.HasModeSpecificColor),
        SupportsSpeed = mode.SupportsSpeed,
        SupportsBrightness = mode.SupportsBrightness,
        IsPerLed = mode.Flags.HasFlag(ModeFlags.HasPerLedColor),
        MinSpeed = mode.SupportsSpeed ? (int)Math.Min(mode.SpeedMin, int.MaxValue) : null,
        MaxSpeed = mode.SupportsSpeed ? (int)Math.Min(mode.SpeedMax, int.MaxValue) : null,
        MinBrightness = mode.SupportsBrightness ? (int)Math.Min(mode.BrightnessMin, int.MaxValue) : null,
        MaxBrightness = mode.SupportsBrightness ? (int)Math.Min(mode.BrightnessMax, int.MaxValue) : null,
    };

    /// <summary>Maps the raw OpenRGB device_type value (OpenRGB 1.0 enum; 3.1.1's <see cref="DeviceType"/> stops at 14).</summary>
    private static string HumanizeType(int type) => type switch
    {
        0 => "Motherboard",
        1 => "DRAM",
        2 => "GPU",
        3 => "Cooler",
        4 => "LED Strip",
        5 => "Keyboard",
        6 => "Mouse",
        7 => "Mousemat",
        8 => "Headset",
        9 => "Headset Stand",
        10 => "Gamepad",
        11 => "Light",
        12 => "Speaker",
        13 => "Virtual",
        14 => "Storage",
        15 => "Case",
        16 => "Microphone",
        17 => "Accessory",
        18 => "Keypad",
        19 => "Laptop",
        20 => "Monitor",
        _ => "Unknown",
    };

    // ----- default snapshots ----------------------------------------------------------------------------------

    /// <summary>
    /// Stable identity of each device across sessions: Name + Location + Serial when OpenRGB reports either, else
    /// Name + type + LED count; duplicates within one list get a "#n" suffix in enumeration order.
    /// </summary>
    private static string[] ComputeKeys(Device[] devices)
    {
        var keys = new string[devices.Length];
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < devices.Length; i++)
        {
            var key = BaseKey(devices[i]);
            var n = seen.GetValueOrDefault(key);
            seen[key] = n + 1;
            keys[i] = n == 0 ? key : $"{key}#{n + 1}";
        }

        return keys;
    }

    private static string BaseKey(Device device)
    {
        var name = device.Name?.Trim() ?? string.Empty;
        var location = device.Location?.Trim() ?? string.Empty;
        var serial = device.Serial?.Trim() ?? string.Empty;
        return location.Length > 0 || serial.Length > 0
            ? $"{name}|{location}|{serial}"
            : $"{name}|type{(int)device.Type}|{device.Leds.Length} LEDs";
    }

    private static RgbDeviceDefault Capture(Device device, string key)
    {
        var mode = device.ActiveModeIndex >= 0 && device.ActiveModeIndex < device.Modes.Length ? device.Modes[device.ActiveModeIndex] : null;
        return new RgbDeviceDefault
        {
            Key = key,
            DeviceName = device.Name ?? string.Empty,
            CapturedAt = DateTimeOffset.Now,
            ModeIndex = device.ActiveModeIndex,
            ModeName = mode?.Name ?? string.Empty,
            ModeIsPerLed = mode?.Flags.HasFlag(ModeFlags.HasPerLedColor) ?? false,
            Speed = mode is { SupportsSpeed: true } ? mode.Speed : null,
            Direction = mode is { SupportsDirection: true } ? (int)mode.Direction : null,
            ColorMode = mode is null ? 0 : (int)mode.ColorMode,
            ModeColors = mode?.Colors.Select(ToHex).ToList() ?? new List<string>(),
            LedColors = device.Colors.Select(ToHex).ToList(),
        };
    }

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color[] ParseColors(IEnumerable<string>? hexes)
    {
        var result = new List<Color>();
        foreach (var hex in hexes ?? [])
        {
            try
            {
                var c = RgbColor.FromHex(hex);
                result.Add(new Color(c.R, c.G, c.B));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
            {
                // Hand-edited / corrupt entry: skip it.
            }
        }

        return result.ToArray();
    }

    /// <summary>Repeats / truncates <paramref name="colors"/> to exactly <paramref name="count"/> entries; null when there is nothing to fit.</summary>
    private static Color[]? Fit(Color[] colors, int count)
    {
        if (colors.Length == 0 || count <= 0) return null;
        if (colors.Length == count) return colors;
        var result = new Color[count];
        for (var i = 0; i < count; i++) result[i] = colors[i % colors.Length];
        return result;
    }

    private static Color[] Repeat(RgbColor color, int count)
    {
        var colors = new Color[Math.Max(count, 0)];
        Array.Fill(colors, new Color(color.R, color.G, color.B));
        return colors;
    }

    private static RgbColor[] ToModel(Color[] colors)
    {
        var result = new RgbColor[colors.Length];
        for (var i = 0; i < colors.Length; i++) result[i] = new RgbColor(colors[i].R, colors[i].G, colors[i].B);
        return result;
    }
}
