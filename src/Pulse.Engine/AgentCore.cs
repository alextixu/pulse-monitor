using Microsoft.Extensions.Logging;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Pulse.Core.Tray;

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
    /// <summary>Folder for telemetry.csv / telemetry-previous.csv; null disables telemetry entirely (demo mode).</summary>
    public string? TelemetryDirectory { get; init; }
    public string LogPath { get; init; } = string.Empty;
    public bool IsDemo { get; init; }
    public string Version { get; init; } = "0.0.0";
    /// <summary>
    /// Starts an elevated copy of the agent (the argument says whether it should open the popover once it is up).
    /// Returns true when the new process was started; the agent then shuts down with <see cref="AgentExitReason.Relaunch"/>.
    /// Null when elevation is not possible in this host (demo).
    /// </summary>
    public Func<bool, bool>? RelaunchElevated { get; init; }
}

/// <summary>
/// The resident engine. Owns the settings, the polling loops (hardware on a dedicated thread, 1 s while a popover is
/// visible and 2 s otherwise; batteries every BatteryRefreshSeconds), the fan-mode controller, telemetry, the memory
/// (state.json) and the RGB backend. All state is mutated on one engine thread; see decisions-v2.md for the contracts.
/// </summary>
public sealed partial class AgentCore : IPulseAgent, IAsyncDisposable
{
    public AgentCore(AgentCoreOptions options) => throw new NotImplementedException();

    // ───────────── IPulseAgent ─────────────

    public AgentInfo Info => throw new NotImplementedException();
    public SettingsState Settings => throw new NotImplementedException();
    public HardwareMonitorStatus HardwareStatus => throw new NotImplementedException();
    public RgbStatus RgbStatus => throw new NotImplementedException();
    public HardwareFrame Hardware => throw new NotImplementedException();
    public BatteryState Batteries => throw new NotImplementedException();

    public event EventHandler<AgentInfo>? InfoChanged;
    public event EventHandler<SettingsState>? SettingsChanged;
    public event EventHandler<HardwareMonitorStatus>? HardwareStatusChanged;
    public event EventHandler<RgbStatus>? RgbStatusChanged;
    public event EventHandler<HardwareFrame>? HardwareUpdated;
    public event EventHandler<BatteryState>? BatteriesUpdated;
    public event EventHandler? RgbDevicesChanged;
    public event EventHandler<LowBatteryToast>? LowBattery;
    public event EventHandler<AgentExitReason>? Exiting;

    public void SetUiVisible(bool visible) => throw new NotImplementedException();
    public IReadOnlyList<LowBatteryToast> TakePendingToasts() => throw new NotImplementedException();
    public Task<CommandResult> RefreshNowAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<ConnectRgbResult> ConnectRgbAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<RgbDevicesResult> GetRgbDevicesAsync(bool refresh, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<RgbDevicesResult> RgbCommandAsync(RgbCommand command, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<CommandResult> SetFanAsync(string fanId, double? percent, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<CommandResult> SetFanModeAsync(FanMode mode, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<CommandResult> SetSyncedPercentAsync(double percent, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<CommandResult> SetFanMembershipAsync(string fanId, bool include, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<CommandResult> UpdateSettingAsync(SettingsPatch patch, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<CommandResult> SetAutoStartAsync(bool enabled, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<CommandResult> ElevateAsync(bool showUi, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public void RequestQuit() => throw new NotImplementedException();

    // ───────────── host-facing (agent executable / tray / pipe server) ─────────────

    /// <summary>Loads settings and memory, starts hardware initialisation, the polling loops and the RGB connection. Does not wait for the hardware.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

    /// <summary>What the tray icon and tooltip show (recomputed after every poll and after TrayIconMode / HiddenDeviceIds changes).</summary>
    public TraySummary Summary => throw new NotImplementedException();
    /// <summary>Raised on the engine thread when <see cref="Summary"/> changed at tooltip precision or the tray mode changed.</summary>
    public event EventHandler<TraySummary>? SummaryChanged;

    /// <summary>Low battery detected while no popover was visible: the tray should show a Windows notification.</summary>
    public event EventHandler<LowBatteryToast>? NotificationRequested;

    /// <summary>
    /// The agent must stop (RequestQuit, a client's Quit, a successful ElevateAsync). The host ends its message loop and
    /// calls <see cref="ShutdownAsync"/>.
    /// </summary>
    public event EventHandler<AgentExitReason>? ShutdownRequested;

    /// <summary>
    /// Set by the pipe server: asks a connected popover to show (or toggle); returns false when no client is connected,
    /// so the tray launches the UI instead.
    /// </summary>
    public Func<bool, string?, bool>? ShowPopoverInClient { get; set; }

    /// <summary>True while at least one client reported a visible popover.</summary>
    public bool IsUiVisible => throw new NotImplementedException();
    /// <summary>When the last visible popover was hidden (for the tray's 350 ms toggle guard).</summary>
    public DateTimeOffset? LastUiHiddenAt => throw new NotImplementedException();

    /// <summary>Windows reported a device arrival / removal (WM_DEVICECHANGE); forwarded to <see cref="IDeviceChangeAware"/> providers.</summary>
    public void NotifyDevicesChanged() => throw new NotImplementedException();

    /// <summary>
    /// Clean shutdown, idempotent: fan controller stopped and fans handed back first (never budget-limited), then clients
    /// told (<see cref="Exiting"/>), telemetry flushed, RGB and the bundled OpenRGB stopped, settings and memory flushed.
    /// </summary>
    public Task ShutdownAsync(AgentExitReason reason) => throw new NotImplementedException();

    /// <summary>
    /// Last-chance fan restore for UnhandledException / ProcessExit: stops the fan controller and disposes the hardware
    /// monitor synchronously on the calling thread; nothing else. A no-op after <see cref="ShutdownAsync"/>.
    /// </summary>
    public void RestoreFansOnAbnormalExit(string reason) => throw new NotImplementedException();

    public ValueTask DisposeAsync() => new(ShutdownAsync(AgentExitReason.Quit));
}
