using Avalonia.Threading;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Pulse.Rgb;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Pulse.App.Services;

/// <summary>The device with the lowest battery among the connected, visible ones.</summary>
public sealed record BatterySummary(string DeviceId, string Name, int Percent, bool IsCharging, DeviceKind Kind);

/// <summary>Everything the tray icon / tooltip needs, recomputed after every poll.</summary>
public sealed record TraySummary
{
    public IReadOnlyList<BatteryDevice> ConnectedDevices { get; init; } = Array.Empty<BatteryDevice>();
    public BatterySummary? Lowest { get; init; }
    public double? CpuLoadPercent { get; init; }
    public double? CpuTempC { get; init; }
    public double? GpuLoadPercent { get; init; }
    public double? GpuTempC { get; init; }

    public static TraySummary Empty { get; } = new();

    /// <summary>Value equality at tooltip precision (whole percents / degrees, device id + level + status).</summary>
    public bool Equals(TraySummary? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Lowest != other.Lowest) return false;
        if (Round(CpuLoadPercent) != Round(other.CpuLoadPercent) || Round(CpuTempC) != Round(other.CpuTempC)) return false;
        if (Round(GpuLoadPercent) != Round(other.GpuLoadPercent) || Round(GpuTempC) != Round(other.GpuTempC)) return false;
        if (ConnectedDevices.Count != other.ConnectedDevices.Count) return false;
        for (var i = 0; i < ConnectedDevices.Count; i++)
        {
            var a = ConnectedDevices[i];
            var b = other.ConnectedDevices[i];
            if (a.Id != b.Id || a.Name != b.Name || a.Percent != b.Percent || a.Status != b.Status) return false;
        }
        return true;
    }

    public override int GetHashCode() => HashCode.Combine(Lowest, Round(CpuLoadPercent), Round(CpuTempC), Round(GpuLoadPercent), Round(GpuTempC), ConnectedDevices.Count);

    private static int? Round(double? value) => value is { } v ? (int)Math.Round(v) : null;
}

/// <summary>
/// Singleton orchestrator: polls the battery providers and the hardware monitor on background timers,
/// publishes results on the UI thread (all observable properties / events are raised on <see cref="Dispatcher.UIThread"/>),
/// and tracks the hardware / RGB status. Exceptions are logged per provider and never propagate to callers.
/// </summary>
public sealed partial class MonitoringService : ObservableObject, IDisposable
{
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HardwareTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PopoverRefreshThrottle = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<IBatteryProvider> _providers;
    private readonly IHardwareMonitor _hardware;
    private readonly IRgbController _rgb;
    /// <summary>Null in demo mode (and wherever the OpenRGB backend is not registered).</summary>
    private readonly OpenRgbServerLauncher? _openRgbLauncher;
    private readonly AppSettings _settings;
    private readonly ILogger<MonitoringService> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _batteryGate = new(1, 1);
    private readonly SemaphoreSlim _hardwareGate = new(1, 1);
    private readonly object _startGate = new();
    private Task? _batteryLoop;
    private Task? _hardwareLoop;
    private DateTimeOffset _lastBatteryPoll = DateTimeOffset.MinValue;
    private string? _lastBatteryLogSignature;
    private int _hardwarePolls;
    private bool _disposed;

    /// <summary>Every Nth hardware poll is summarised at Information level (the first one always is).</summary>
    private const int HardwareLogEvery = 30;

    [ObservableProperty] private HardwareSnapshot _latestHardware = HardwareSnapshot.Empty;
    [ObservableProperty] private IReadOnlyList<BatteryDevice> _latestBatteries = Array.Empty<BatteryDevice>();
    [ObservableProperty] private IReadOnlyList<BatteryDevice> _visibleBatteries = Array.Empty<BatteryDevice>();
    [ObservableProperty] private HardwareMonitorStatus _hardwareStatus;
    [ObservableProperty] private RgbStatus _rgbStatus;
    [ObservableProperty] private BatterySummary? _lowestBattery;
    [ObservableProperty] private TraySummary _summary = TraySummary.Empty;
    [ObservableProperty] private bool _isRefreshingBatteries;
    [ObservableProperty] private bool _isRefreshingHardware;
    [ObservableProperty] private DateTimeOffset? _lastBatteryUpdate;
    [ObservableProperty] private DateTimeOffset? _lastHardwareUpdate;
    /// <summary>Message of the last provider failure in the most recent battery poll; null when all providers succeeded.</summary>
    [ObservableProperty] private string? _lastBatteryError;

    public MonitoringService(
        IEnumerable<IBatteryProvider> providers,
        IHardwareMonitor hardware,
        IRgbController rgb,
        AppSettings settings,
        ILogger<MonitoringService> log,
        OpenRgbServerLauncher? openRgbLauncher = null)
    {
        _providers = providers.Where(p => p.IsSupported).ToList();
        _hardware = hardware;
        _rgb = rgb;
        _settings = settings;
        _log = log;
        _openRgbLauncher = openRgbLauncher;
        _hardwareStatus = hardware.Status;
        _rgbStatus = rgb.Status;

        _hardware.StatusChanged += OnHardwareStatusChanged;
        _rgb.StatusChanged += OnRgbStatusChanged;
        _log.LogInformation("Battery providers: {Providers}", string.Join(", ", _providers.Select(p => p.Name).DefaultIfEmpty("(none)")));
    }

    /// <summary>Raised on the UI thread after each hardware poll.</summary>
    public event EventHandler<HardwareSnapshot>? HardwareUpdated;

    /// <summary>Raised on the UI thread after each battery poll (all devices, including hidden ones).</summary>
    public event EventHandler<IReadOnlyList<BatteryDevice>>? BatteriesUpdated;

    /// <summary>Raised on the UI thread whenever <see cref="Summary"/> was recomputed (tray icon / tooltip input).</summary>
    public event EventHandler<TraySummary>? SummaryChanged;

    public IReadOnlyList<IBatteryProvider> BatteryProviders => _providers;
    public IHardwareMonitor Hardware => _hardware;
    public IRgbController Rgb => _rgb;
    public AppSettings Settings => _settings;

    /// <summary>Starts the polling loops and the hardware initialization (idempotent).</summary>
    public void Start()
    {
        lock (_startGate)
        {
            if (_batteryLoop is not null || _disposed) return;
            var ct = _cts.Token;
            _ = Task.Run(() => InitializeHardwareAsync(ct), ct);
            _batteryLoop = Task.Run(() => BatteryLoopAsync(ct), ct);
            _hardwareLoop = Task.Run(() => HardwareLoopAsync(ct), ct);
            if (_settings.AutoConnectOpenRgb)
            {
                _ = ConnectRgbAsync(ct);
            }
        }
    }

    /// <summary>Refreshes batteries and hardware right away (user-triggered).</summary>
    public async Task RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        await Task.WhenAll(
            RefreshBatteriesAsync(waitIfBusy: true, linked.Token),
            RefreshHardwareAsync(waitIfBusy: true, linked.Token)).ConfigureAwait(false);
    }

    /// <summary>Call when the popover becomes visible: triggers a battery poll unless one ran very recently.</summary>
    public void NotifyPopoverOpened()
    {
        if (DateTimeOffset.Now - _lastBatteryPoll < PopoverRefreshThrottle) return;
        _ = RefreshBatteriesAsync(waitIfBusy: false, _cts.Token);
    }

    /// <summary>Raised on the UI thread when the RGB device list changed without a reconnect (bundled OpenRGB finished detecting).</summary>
    public event EventHandler? RgbDevicesChanged;

    /// <summary>
    /// (Re)configures the RGB endpoint from settings and connects. Safe to call repeatedly. When nothing answers on the
    /// SDK port and <see cref="AppSettings.UseBundledOpenRgb"/> is on, the OpenRGB shipped with Pulse is started first.
    /// </summary>
    public async Task<bool> ConnectRgbAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var launched = false;
            if (_openRgbLauncher is not null && _settings.UseBundledOpenRgb)
            {
                var launch = await _openRgbLauncher.EnsureRunningAsync(_settings.OpenRgbHost, _settings.OpenRgbPort, cancellationToken).ConfigureAwait(false);
                launched = launch == OpenRgbLaunchResult.Started;
                _log.LogInformation("Bundled OpenRGB: {Result} (bundled copy present: {Bundled})", launch, OpenRgbServerLauncher.IsBundled);
            }

            _rgb.Configure(_settings.OpenRgbHost, _settings.OpenRgbPort);
            var ok = await Task.Run(() => _rgb.ConnectAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            _log.LogInformation("RGB connect to {Host}:{Port} → {Result}", _settings.OpenRgbHost, _settings.OpenRgbPort, ok ? "connected" : _rgb.Status.Message);
            if (ok && launched) _ = WatchDeviceDetectionAsync(cancellationToken);
            return ok;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RGB connect failed");
            return false;
        }
    }

    /// <summary>
    /// A freshly started OpenRGB opens its SDK port before it has finished detecting devices, so the first list can be
    /// short. Re-read it for up to 30 s and tell the UI whenever it grows or shrinks.
    /// </summary>
    private async Task WatchDeviceDetectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var last = (await _rgb.GetDevicesAsync(refresh: false, cancellationToken).ConfigureAwait(false)).Count;
            for (var i = 0; i < 15 && !cancellationToken.IsCancellationRequested; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                var count = (await _rgb.GetDevicesAsync(refresh: true, cancellationToken).ConfigureAwait(false)).Count;
                if (count == last) continue;
                _log.LogInformation("Bundled OpenRGB detection: {Count} RGB device(s)", count);
                last = count;
                Post(() => RgbDevicesChanged?.Invoke(this, EventArgs.Empty));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Watching OpenRGB device detection failed");
        }
    }

    /// <summary>Re-evaluates hidden devices and the lowest-battery summary after settings changed.</summary>
    public void RecomputeSummary() => Dispatcher.UIThread.Post(() => Publish(LatestBatteries, LatestHardware, LastBatteryError));

    public Task RefreshBatteriesAsync(CancellationToken cancellationToken = default) => RefreshBatteriesAsync(waitIfBusy: true, cancellationToken);

    public Task RefreshHardwareAsync(CancellationToken cancellationToken = default) => RefreshHardwareAsync(waitIfBusy: true, cancellationToken);

    private async Task InitializeHardwareAsync(CancellationToken ct)
    {
        try
        {
            _log.LogInformation("Initializing hardware monitor ({Type})", _hardware.GetType().Name);
            await _hardware.InitializeAsync(ct).ConfigureAwait(false);
            _log.LogInformation("Hardware monitor state: {State} ({Message})", _hardware.Status.State, _hardware.Status.Message);
            await RefreshHardwareAsync(waitIfBusy: true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Hardware monitor initialization failed");
        }
    }

    private async Task BatteryLoopAsync(CancellationToken ct)
    {
        try
        {
            await RefreshBatteriesAsync(waitIfBusy: true, ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(BatteryPeriod);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var period = BatteryPeriod;
                if (timer.Period != period) timer.Period = period;
                await RefreshBatteriesAsync(waitIfBusy: false, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Battery loop terminated unexpectedly");
        }
    }

    private async Task HardwareLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(HardwarePeriod);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var period = HardwarePeriod;
                if (timer.Period != period) timer.Period = period;
                await RefreshHardwareAsync(waitIfBusy: false, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Hardware loop terminated unexpectedly");
        }
    }

    private TimeSpan BatteryPeriod => TimeSpan.FromSeconds(Math.Clamp(_settings.BatteryRefreshSeconds, 5, 86400));
    /// <summary>The telemetry log samples every second, so it forces a 1 s poll while enabled.</summary>
    private TimeSpan HardwarePeriod => TimeSpan.FromSeconds(_settings.TelemetryEnabled ? 1 : Math.Clamp(_settings.HardwareRefreshSeconds, 1, 3600));

    private async Task RefreshBatteriesAsync(bool waitIfBusy, CancellationToken ct)
    {
        if (_disposed) return;
        if (waitIfBusy)
        {
            try { await _batteryGate.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
        else if (!_batteryGate.Wait(0))
        {
            return;
        }

        try
        {
            _lastBatteryPoll = DateTimeOffset.Now;
            Post(() => IsRefreshingBatteries = true);

            var results = await Task.WhenAll(_providers.Select(p => PollProviderAsync(p, ct))).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            var devices = results.SelectMany(r => r.Devices)
                .OrderByDescending(d => d.IsConnected)
                .ThenBy(d => d.Percent ?? int.MaxValue)
                .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var error = results.Select(r => r.Error).FirstOrDefault(e => e is not null);
            LogBatteries(devices);

            Post(() =>
            {
                LastBatteryUpdate = DateTimeOffset.Now;
                Publish(devices, LatestHardware, error);
                BatteriesUpdated?.Invoke(this, devices);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Battery refresh failed");
        }
        finally
        {
            Post(() => IsRefreshingBatteries = false);
            _batteryGate.Release();
        }
    }

    private async Task<(IReadOnlyList<BatteryDevice> Devices, string? Error)> PollProviderAsync(IBatteryProvider provider, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProviderTimeout);
        try
        {
            var devices = await Task.Run(() => provider.GetDevicesAsync(timeout.Token), timeout.Token).ConfigureAwait(false);
            _log.LogDebug("{Provider}: {Count} device(s)", provider.Name, devices.Count);
            return (devices, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("{Provider} timed out after {Timeout}s", provider.Name, ProviderTimeout.TotalSeconds);
            return (Array.Empty<BatteryDevice>(), $"{provider.Name}: timeout");
        }
        catch (OperationCanceledException)
        {
            return (Array.Empty<BatteryDevice>(), null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Provider} failed", provider.Name);
            return (Array.Empty<BatteryDevice>(), $"{provider.Name}: {ex.Message}");
        }
    }

    private async Task RefreshHardwareAsync(bool waitIfBusy, CancellationToken ct)
    {
        if (_disposed) return;
        if (waitIfBusy)
        {
            try { await _hardwareGate.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
        else if (!_hardwareGate.Wait(0))
        {
            return;
        }

        try
        {
            var state = _hardware.Status.State;
            if (state is HardwareMonitorState.NotStarted or HardwareMonitorState.Initializing or HardwareMonitorState.Unsupported or HardwareMonitorState.Failed)
            {
                return;
            }

            Post(() => IsRefreshingHardware = true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HardwareTimeout);
            var snapshot = await Task.Run(() => _hardware.GetSnapshotAsync(timeout.Token), timeout.Token).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;
            LogHardware(snapshot);

            Post(() =>
            {
                LastHardwareUpdate = DateTimeOffset.Now;
                Publish(LatestBatteries, snapshot, LastBatteryError);
                HardwareUpdated?.Invoke(this, snapshot);
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Hardware snapshot timed out after {Timeout}s", HardwareTimeout.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Hardware refresh failed");
        }
        finally
        {
            Post(() => IsRefreshingHardware = false);
            _hardwareGate.Release();
        }
    }

    /// <summary>UI thread only: stores the new data and recomputes the derived summary.</summary>
    private void Publish(IReadOnlyList<BatteryDevice> devices, HardwareSnapshot hardware, string? batteryError)
    {
        LatestBatteries = devices;
        LatestHardware = hardware;
        LastBatteryError = batteryError;

        var hidden = _settings.HiddenDeviceIds;
        var visible = hidden.Count == 0 ? devices : devices.Where(d => !hidden.Contains(d.Id)).ToList();
        VisibleBatteries = visible;

        var connected = visible.Where(d => d.IsConnected).ToList();
        var lowestDevice = connected.Where(d => d.Percent is not null).MinBy(d => d.Percent);
        LowestBattery = lowestDevice is null
            ? null
            : new BatterySummary(lowestDevice.Id, lowestDevice.Name, lowestDevice.Percent!.Value, lowestDevice.Status == BatteryStatus.Charging, lowestDevice.Kind);

        var gpu = hardware.Gpus.FirstOrDefault();
        var summary = new TraySummary
        {
            ConnectedDevices = connected,
            Lowest = LowestBattery,
            CpuLoadPercent = hardware.Cpu?.LoadPercent,
            CpuTempC = hardware.Cpu?.PackageTempC,
            GpuLoadPercent = gpu?.LoadPercent,
            GpuTempC = gpu?.CoreTempC,
        };

        if (summary != Summary)
        {
            Summary = summary;
            SummaryChanged?.Invoke(this, summary);
        }
    }

    /// <summary>Information-level device list, written only when a device appears/disappears or changes level/status.</summary>
    private void LogBatteries(IReadOnlyList<BatteryDevice> devices)
    {
        var signature = string.Join(";", devices.Select(d => $"{d.Id}|{d.Percent}|{d.Status}|{d.IsConnected}"));
        if (signature == _lastBatteryLogSignature) return;
        _lastBatteryLogSignature = signature;
        if (devices.Count == 0)
        {
            _log.LogInformation("Batteries: no devices reported");
            return;
        }
        var list = string.Join(", ", devices.Select(d =>
            $"{d.Name} {(d.Percent is { } p ? p + "%" : "n/a")} [{d.Source}/{d.Connection}, {d.Status}{(d.IsConnected ? "" : ", disconnected")}]"));
        _log.LogInformation("Batteries ({Count}): {Devices}", devices.Count, list);
    }

    /// <summary>Information-level hardware summary on the first snapshot and then every <see cref="HardwareLogEvery"/> polls.</summary>
    private void LogHardware(HardwareSnapshot snapshot)
    {
        var n = Interlocked.Increment(ref _hardwarePolls);
        if (n != 1 && n % HardwareLogEvery != 0) return;
        var cpu = snapshot.Cpu is { } c
            ? $"CPU {c.Name}: load {Fmt(c.LoadPercent, "%")}, temp {Fmt(c.PackageTempC, "°C")}, clock {Fmt(c.ClockMhz, " MHz")}, power {Fmt(c.PowerWatts, " W")}"
            : "CPU n/a";
        var gpus = snapshot.Gpus.Count == 0
            ? "GPU n/a"
            : string.Join(" | ", snapshot.Gpus.Select(g =>
                $"GPU {g.Name}: load {Fmt(g.LoadPercent, "%")}, temp {Fmt(g.CoreTempC, "°C")}, hotspot {Fmt(g.HotSpotTempC, "°C")}, vram {Fmt(g.MemoryUsedMb, " MB")}/{Fmt(g.MemoryTotalMb, " MB")}, fan {Fmt(g.FanPercent, "%")}/{Fmt(g.FanRpm, " RPM")}, power {Fmt(g.PowerWatts, " W")}"));
        var mem = snapshot.Memory is { } m ? $"memory {Fmt(m.UsedGb, " GB")}/{Fmt(m.TotalGb, " GB")}" : "memory n/a";
        var fans = $"fans {snapshot.Fans.Count} ({snapshot.Fans.Count(f => f.CanControl)} controllable)";
        _log.LogInformation("Hardware #{Poll}: {Cpu} | {Gpus} | {Memory} | {Fans} | other temps {Other}", n, cpu, gpus, mem, fans, snapshot.OtherTemperatures.Count);

        static string Fmt(double? v, string unit) => v is { } x ? $"{Math.Round(x, 1)}{unit}" : "n/a";
    }

    private void OnHardwareStatusChanged(object? sender, HardwareMonitorStatus status)
    {
        _log.LogInformation("Hardware status → {State}: {Message}", status.State, status.Message);
        Post(() => HardwareStatus = status);
    }

    private void OnRgbStatusChanged(object? sender, RgbStatus status)
    {
        _log.LogInformation("RGB status → {State}: {Message}", status.State, status.Message);
        Post(() => RgbStatus = status);
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    /// <summary>Stops the loops. Providers are disposed by the DI container.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hardware.StatusChanged -= OnHardwareStatusChanged;
        _rgb.StatusChanged -= OnRgbStatusChanged;
        _cts.Cancel();
        try
        {
            var loops = new[] { _batteryLoop, _hardwareLoop }.Where(t => t is not null).Select(t => t!).ToArray();
            if (loops.Length > 0) Task.WhenAll(loops).Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Loops swallow cancellation; nothing else to do here.
        }
        _cts.Dispose();
    }
}
