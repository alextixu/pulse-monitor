using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pulse.Core.Abstractions;
using Pulse.Core.FanControl;
using Pulse.Core.Ipc;
using Pulse.Core.Localization;
using Pulse.Core.Models;
using Pulse.Core.Tray;
using Pulse.Engine.Memory;
using Pulse.Engine.Rgb;
using Pulse.Engine.Telemetry;
using FanMemoryEntry = Pulse.Core.Models.FanMemory;

namespace Pulse.Engine;

/// <summary>Everything <see cref="AgentCore"/> is built from (composed by the agent executable, or by the UI in demo mode).</summary>
public sealed record AgentCoreOptions
{
    public required ILoggerFactory LoggerFactory { get; init; }
    public required ISettingsStore SettingsStore { get; init; }
    /// <summary>state.json in the agent; <see cref="InMemoryStateStore"/> in demo mode.</summary>
    public required IStateStore StateStore { get; init; }
    public required IHardwareMonitor Hardware { get; init; }
    public required IRgbController Rgb { get; init; }
    public IRgbServerLauncher RgbLauncher { get; init; } = new NullRgbServerLauncher();
    public required IReadOnlyList<IBatteryProvider> BatteryProviders { get; init; }
    public required IElevationService Elevation { get; init; }
    public required IAutoStartService AutoStart { get; init; }
    /// <summary>
    /// Whether auto-start currently comes from the "Pulse" scheduled task (shown in <see cref="AgentInfo.AutoStartViaTask"/>).
    /// Optional: when null the engine reads a public bool <c>IsViaTask</c> property of <see cref="AutoStart"/> if it has one.
    /// Called on the thread pool.
    /// </summary>
    public Func<bool>? AutoStartViaTask { get; init; }
    /// <summary>Folder for telemetry.csv / telemetry-previous.csv; null disables telemetry entirely (demo mode).</summary>
    public string? TelemetryDirectory { get; init; }
    public string LogPath { get; init; } = string.Empty;
    /// <summary>Demo / screenshot engine: never writes settings, state or telemetry, whatever the stores point at.</summary>
    public bool IsDemo { get; init; }
    public string Version { get; init; } = "0.0.0";
    /// <summary>
    /// Starts an elevated copy of the agent (the argument says whether it should open the popover once it is up).
    /// Returns true when the new process was started; the agent then shuts down with <see cref="AgentExitReason.Relaunch"/>.
    /// Null when elevation is not possible in this host (demo); <see cref="IElevationService.RelaunchElevated"/> is used then.
    /// </summary>
    public Func<bool, bool>? RelaunchElevated { get; init; }
}

/// <summary>
/// The resident engine. Owns the settings, the polling loops (hardware on a dedicated thread, 1 s while a popover is
/// visible and 2 s otherwise; batteries every BatteryRefreshSeconds), the fan-mode controller, telemetry, the memory
/// (state.json) and the RGB backend. All state is mutated on one engine thread (<see cref="EngineScheduler"/>) and
/// every event is raised there; state properties return immutable snapshots readable from any thread. See
/// decisions-v2.md for the contracts.
/// </summary>
public sealed partial class AgentCore : IPulseAgent, IAsyncDisposable
{
    private static readonly TimeSpan PendingToastLimitAge = TimeSpan.FromHours(12);
    private const int PendingToastLimit = 20;

    private readonly AgentCoreOptions _options;
    private readonly ILogger<AgentCore> _log;
    private readonly EngineScheduler _scheduler;
    private readonly IHardwareMonitor _hardware;
    private readonly IRgbController _rgb;
    private readonly IReadOnlyList<IBatteryProvider> _providers;
    private readonly FanModeController _controller;
    private readonly RgbService _rgbService;
    private readonly TelemetryLogger? _telemetry;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _lifecycleGate = new();

    // ───── engine-thread state ─────
    /// <summary>The live settings; mutated only on the engine thread (clients get clones through <see cref="Settings"/>).</summary>
    private readonly AppSettings _settings;
    private readonly bool _settingsFileExisted;
    private readonly BatteryMemory _batteryMemory;
    private readonly LowBatteryWatcher _lowBattery;
    private IReadOnlyList<FanMemoryEntry> _stateFans;
    private DateTimeOffset? _stateFanBoot;
    private long _revision = 1;
    private string? _lastBatteryLogSignature;
    private int _hardwarePolls;

    // ───── published snapshots (any thread) ─────
    private volatile AgentInfo _info;
    private volatile SettingsState _settingsState;
    private volatile HardwareMonitorStatus _hardwareStatus;
    private volatile RgbStatus _rgbStatus;
    private volatile HardwareFrame _hardwareFrame = HardwareFrame.Empty;
    private volatile BatteryState _batteries;
    private volatile TraySummary _summary = TraySummary.Empty;
    private volatile bool _uiVisible;
    private long _lastUiHiddenAtTicks;

    private readonly List<LowBatteryToast> _pendingToasts = new();
    private bool _started;
    private volatile bool _stopping;
    private int _shutdownRequested;
    private Task? _shutdownTask;

    public AgentCore(AgentCoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _log = options.LoggerFactory.CreateLogger<AgentCore>();
        _hardware = options.Hardware;
        _rgb = options.Rgb;
        _providers = options.BatteryProviders.Where(p => p.IsSupported).ToList();

        // Settings and memory are read up front so every property is meaningful before StartAsync (a client may
        // connect to the pipe between the two).
        _settingsFileExisted = SafeFileExists(options.SettingsStore.FilePath);
        _settings = options.SettingsStore.Load();
        _settings.Sanitize();
        _settingsState = new SettingsState(_settings.Clone(), _revision, null);

        var state = LoadState(options.StateStore);
        var now = DateTimeOffset.Now;
        _batteryMemory = new BatteryMemory(state.Batteries, now);
        _lowBattery = new LowBatteryWatcher(state.LowBatteryNotified);
        _fanMemory = new FanPresenceMemory(FanPresenceMemory.CurrentBootTime);
        var seeded = _fanMemory.Seed(_fanTracker, state);
        _stateFans = seeded > 0 ? state.Fans : Array.Empty<FanMemoryEntry>();
        _stateFanBoot = seeded > 0 ? state.FanBootTime : null;

        _controller = new FanModeController(_hardware, options.LoggerFactory.CreateLogger<FanModeController>());
        _monitorStatus = _hardware.Status;
        _hardwareStatus = _monitorStatus;
        _fanInputs = BuildFanInputs();
        _rgbStatus = _rgb.Status;

        var remembered = _batteryMemory.Current(now);
        var visible = VisibleOf(remembered);
        TraySummary.From(visible, HardwareSnapshot.Empty, out var lowest);
        _batteries = new BatteryState { All = remembered, Visible = visible, Lowest = lowest };
        _summary = TraySummary.From(visible, HardwareSnapshot.Empty, out _);

        _info = new AgentInfo
        {
            IsElevated = SafeGet(() => options.Elevation.IsElevated),
            CanElevate = SafeGet(() => options.Elevation.CanElevate),
            AutoStartSupported = SafeGet(() => options.AutoStart.IsSupported),
            // Reconciled with the real registration (Run key / scheduled task) by StartAsync, off this thread.
            AutoStartEnabled = _settings.StartWithSystem,
            OpenRgbBundled = SafeGet(() => options.RgbLauncher.IsBundled),
            IsDemo = options.IsDemo,
            Version = options.Version,
            SettingsPath = options.SettingsStore.FilePath,
            LogPath = options.LogPath,
            TelemetryDirectory = options.TelemetryDirectory ?? string.Empty,
            TelemetryPath = options.TelemetryDirectory is { } dir ? TelemetryLogger.FileNameIn(dir) : string.Empty,
            ProcessId = Environment.ProcessId,
        };

        _scheduler = new EngineScheduler(options.LoggerFactory.CreateLogger<EngineScheduler>());
        _telemetry = options.TelemetryDirectory is { } telemetryDir && !options.IsDemo
            ? new TelemetryLogger(telemetryDir, options.LoggerFactory.CreateLogger<TelemetryLogger>())
            : null;

        _rgbService = new RgbService(_rgb, options.RgbLauncher, new LightingMemory(state.Lighting), () => _settingsState.Settings,
            options.LoggerFactory.CreateLogger<RgbService>());
        _rgbService.DevicesChanged += () => _scheduler.Post(() => RgbDevicesChanged?.Invoke(this, EventArgs.Empty));
        _rgbService.MemoryChanged += () => _scheduler.Post(MarkStateDirty);

        _hwThread = new Thread(HardwareThreadMain) { Name = "Pulse.Hardware", IsBackground = true };
        _watchdogTimer = new Timer(_ => _scheduler.Post(CheckWatchdog), null, Timeout.Infinite, Timeout.Infinite);
        _settingsTimer = new Timer(_ => _scheduler.Post(SaveSettingsNow), null, Timeout.Infinite, Timeout.Infinite);
        _stateTimer = new Timer(_ => _scheduler.Post(SaveStateNow), null, Timeout.Infinite, Timeout.Infinite);

        _hardware.StatusChanged += OnMonitorStatusChanged;
        _rgb.StatusChanged += OnRgbStatusChanged;

        _log.LogInformation("Pulse engine {Version}{Demo}: battery providers {Providers}; memory {Batteries} batteries, {Lighting} lighting intents, {Fans} fan first sights{FanScope}",
            options.Version, options.IsDemo ? " (demo)" : string.Empty,
            string.Join(", ", _providers.Select(p => p.Name).DefaultIfEmpty("(none)")),
            _batteryMemory.Count, state.Lighting.Count, seeded, seeded > 0 ? " (same boot)" : state.Fans.Count > 0 ? " (discarded: earlier boot)" : string.Empty);
    }

    // ───────────── IPulseAgent ─────────────

    public AgentInfo Info => _info;
    public SettingsState Settings => _settingsState;
    public HardwareMonitorStatus HardwareStatus => _hardwareStatus;
    public RgbStatus RgbStatus => _rgbStatus;
    public HardwareFrame Hardware => _hardwareFrame;
    public BatteryState Batteries => _batteries;

    public event EventHandler<AgentInfo>? InfoChanged;
    public event EventHandler<SettingsState>? SettingsChanged;
    public event EventHandler<HardwareMonitorStatus>? HardwareStatusChanged;
    public event EventHandler<RgbStatus>? RgbStatusChanged;
    public event EventHandler<HardwareFrame>? HardwareUpdated;
    public event EventHandler<BatteryState>? BatteriesUpdated;
    public event EventHandler? RgbDevicesChanged;
    public event EventHandler<LowBatteryToast>? LowBattery;
    public event EventHandler<AgentExitReason>? Exiting;

    public void SetUiVisible(bool visible)
    {
        if (_uiVisible == visible) return;
        _uiVisible = visible;
        if (!visible)
        {
            Interlocked.Exchange(ref _lastUiHiddenAtTicks, DateTimeOffset.UtcNow.UtcTicks);
            _log.LogDebug("Popover hidden: hardware every 2 s");
            return;
        }

        _log.LogDebug("Popover visible: hardware every 1 s");
        if (!_started || _stopping) return;
        // Switch to the 1 s cadence right away, with a fresh frame for the popover.
        WakeHardware();
        if (TimeSinceBatteryPoll() >= PopoverBatteryThrottle)
        {
            _ = Task.Run(() => RefreshBatteriesAsync(waitIfBusy: false, _lifetimeCts.Token));
        }
    }

    public IReadOnlyList<LowBatteryToast> TakePendingToasts()
    {
        lock (_pendingToasts)
        {
            if (_pendingToasts.Count == 0) return Array.Empty<LowBatteryToast>();
            var cutoff = DateTimeOffset.Now - PendingToastLimitAge;
            var toasts = _pendingToasts.Where(t => t.Device.UpdatedAt >= cutoff).ToList();
            _pendingToasts.Clear();
            return toasts;
        }
    }

    public async Task<CommandResult> RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        if (_stopping) return CommandResult.Fail(EngineStrings.ShuttingDown);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, cancellationToken);
        try
        {
            await Task.WhenAll(
                RefreshBatteriesAsync(waitIfBusy: true, linked.Token),
                RefreshHardwareAsync(linked.Token)).ConfigureAwait(false);
            return CommandResult.Success;
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Fail(EngineStrings.ShuttingDown);
        }
    }

    public Task<ConnectRgbResult> ConnectRgbAsync(CancellationToken cancellationToken = default)
    {
        if (_stopping) return Task.FromResult(new ConnectRgbResult(false, _rgbStatus, EngineStrings.ShuttingDown));
        return Task.Run(async () =>
        {
            var ok = await _rgbService.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var status = _rgb.Status;
            return new ConnectRgbResult(ok, status, ok ? null : status.Message ?? Strings.RgbError);
        }, CancellationToken.None);
    }

    public Task<RgbDevicesResult> GetRgbDevicesAsync(bool refresh, CancellationToken cancellationToken = default)
        => Task.Run(() => _rgbService.GetDevicesAsync(refresh, cancellationToken), CancellationToken.None);

    public Task<RgbDevicesResult> RgbCommandAsync(RgbCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_stopping) return Task.FromResult(RgbDevicesResult.Failed(EngineStrings.ShuttingDown));
        return Task.Run(() => _rgbService.ExecuteAsync(command, cancellationToken), CancellationToken.None);
    }

    public async Task<CommandResult> ElevateAsync(bool showUi, CancellationToken cancellationToken = default)
    {
        if (_stopping) return CommandResult.Fail(EngineStrings.ShuttingDown);
        bool started;
        try
        {
            // The UAC prompt blocks the calling thread until the user answers: never the engine thread.
            started = await Task.Run(() => _options.RelaunchElevated is { } relaunch ? relaunch(showUi) : _options.Elevation.RelaunchElevated(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Elevation relaunch failed");
            started = false;
        }

        if (!started)
        {
            _log.LogInformation("Elevation relaunch did not start a new instance");
            return CommandResult.Fail(Strings.StatusElevationFailed);
        }

        _log.LogInformation("Elevated instance started; shutting this one down");
        RaiseShutdownRequested(AgentExitReason.Relaunch);
        return CommandResult.Success;
    }

    public void RequestQuit()
    {
        _log.LogInformation("Quit requested");
        RaiseShutdownRequested(AgentExitReason.Quit);
    }

    // ───────────── host-facing (agent executable / tray / pipe server) ─────────────

    /// <summary>What the tray icon and tooltip show (recomputed after every poll and after TrayIconMode / HiddenDeviceIds changes).</summary>
    public TraySummary Summary => _summary;
    /// <summary>Raised on the engine thread when <see cref="Summary"/> changed at tooltip precision or the tray mode changed.</summary>
    public event EventHandler<TraySummary>? SummaryChanged;

    /// <summary>Low battery detected while no popover was visible: the tray should show a Windows notification.</summary>
    public event EventHandler<LowBatteryToast>? NotificationRequested;

    /// <summary>
    /// The agent must stop (RequestQuit, a client's Quit, a successful ElevateAsync). Raised once, on the engine thread.
    /// The host ends its message loop and calls <see cref="ShutdownAsync"/>.
    /// </summary>
    public event EventHandler<AgentExitReason>? ShutdownRequested;

    /// <summary>
    /// Set by the pipe server: asks a connected popover to show (or toggle); returns false when no client is connected,
    /// so the tray launches the UI instead.
    /// </summary>
    public Func<bool, string?, bool>? ShowPopoverInClient { get; set; }

    /// <summary>True while at least one client reported a visible popover.</summary>
    public bool IsUiVisible => _uiVisible;

    /// <summary>When the last visible popover was hidden (for the tray's 350 ms toggle guard).</summary>
    public DateTimeOffset? LastUiHiddenAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastUiHiddenAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Windows reported a device arrival / removal (WM_DEVICECHANGE). Forwarded at once (on the calling thread) to the
    /// hardware monitor and the battery providers that implement <see cref="IDeviceChangeAware"/>; a battery poll follows
    /// a few seconds later so a newly attached device shows up without waiting for the next scheduled poll.
    /// </summary>
    public void NotifyDevicesChanged()
    {
        if (_stopping) return;
        if (_hardware is IDeviceChangeAware hardwareAware) SafeNotify(hardwareAware, "hardware monitor");
        foreach (var provider in _options.BatteryProviders)
        {
            if (provider is IDeviceChangeAware aware) SafeNotify(aware, provider.Name);
        }
        ScheduleDeviceChangeRefresh();

        void SafeNotify(IDeviceChangeAware target, string name)
        {
            try
            {
                target.OnDevicesChanged();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Device-change notification to {Target} failed", name);
            }
        }
    }

    public ValueTask DisposeAsync() => new(ShutdownAsync(AgentExitReason.Quit));

    // ───────────── helpers ─────────────

    private void RaiseShutdownRequested(AgentExitReason reason)
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0) return;
        if (!_scheduler.Post(() => ShutdownRequested?.Invoke(this, reason)))
        {
            ShutdownRequested?.Invoke(this, reason);
        }
    }

    private void UpdateInfo(AgentInfo info)
    {
        if (info == _info) return;
        _info = info;
        InfoChanged?.Invoke(this, info);
    }

    /// <summary>Engine thread: routes a low-battery alert to a visible popover, else to the tray notification + pending queue.</summary>
    private void DeliverToast(LowBatteryToast toast)
    {
        _log.LogInformation("Low battery: {Name} {Percent}% (threshold {Threshold}%)",
            toast.Device.Name, toast.Device.Percent, _settings.LowBatteryThresholdPercent);
        if (_uiVisible)
        {
            LowBattery?.Invoke(this, toast);
            return;
        }

        lock (_pendingToasts)
        {
            _pendingToasts.RemoveAll(t => t.Device.Id == toast.Device.Id);
            _pendingToasts.Add(toast);
            if (_pendingToasts.Count > PendingToastLimit) _pendingToasts.RemoveAt(0);
        }
        NotificationRequested?.Invoke(this, toast);
    }

    private IReadOnlyList<BatteryDevice> VisibleOf(IReadOnlyList<BatteryDevice> all)
    {
        var hidden = _settings.HiddenDeviceIds;
        return hidden.Count == 0 ? all : all.Where(d => !hidden.Contains(d.Id)).ToList();
    }

    /// <summary>Engine thread: re-derives the tray summary; raises <see cref="SummaryChanged"/> on a visible change (or when forced).</summary>
    private void UpdateSummary(bool force = false)
    {
        var summary = TraySummary.From(_batteries.Visible, _hardwareFrame.Snapshot, out _);
        if (!force && summary.Equals(_summary)) return;
        _summary = summary;
        SummaryChanged?.Invoke(this, summary);
    }

    private PulseState LoadState(IStateStore store)
    {
        try
        {
            return store.Load();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reading the memory from {Path} failed; starting empty", store.FilePath);
            return new PulseState();
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch
        {
            return true; // unknown: never overwrite on a guess
        }
    }

    private T SafeGet<T>(Func<T> get, T fallback = default!)
    {
        try
        {
            return get();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Platform query failed");
            return fallback;
        }
    }

    private static string SerializeSettings(AppSettings settings) => JsonSerializer.Serialize(settings, IpcJsonContext.Default.AppSettings);
}
