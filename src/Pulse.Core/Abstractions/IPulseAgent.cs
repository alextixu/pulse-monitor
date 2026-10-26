using Pulse.Core.FanControl;
using Pulse.Core.Models;
using Pulse.Core.Tray;

namespace Pulse.Core.Abstractions;

/// <summary>
/// The resident Pulse engine as seen by its clients: the Win32 tray, the named-pipe server, the popover UI (through the
/// pipe, or in-process in demo / screenshot mode). State properties return immutable snapshots and are safe to read from
/// any thread. Events are raised on the engine thread; UI clients marshal them. Commands never throw for an expected
/// failure — they return a result with a user-facing <see cref="CommandResult.Error"/>.
/// </summary>
public interface IPulseAgent
{
    AgentInfo Info { get; }
    SettingsState Settings { get; }
    HardwareMonitorStatus HardwareStatus { get; }
    RgbStatus RgbStatus { get; }
    HardwareFrame Hardware { get; }
    BatteryState Batteries { get; }

    event EventHandler<AgentInfo>? InfoChanged;
    event EventHandler<SettingsState>? SettingsChanged;
    event EventHandler<HardwareMonitorStatus>? HardwareStatusChanged;
    event EventHandler<RgbStatus>? RgbStatusChanged;
    event EventHandler<HardwareFrame>? HardwareUpdated;
    event EventHandler<BatteryState>? BatteriesUpdated;
    /// <summary>The RGB device list or a device's state changed; clients re-read with <see cref="GetRgbDevicesAsync"/>.</summary>
    event EventHandler? RgbDevicesChanged;
    event EventHandler<LowBatteryToast>? LowBattery;
    event EventHandler<AgentExitReason>? Exiting;

    /// <summary>
    /// A popover is showing (true) or was hidden / closed (false). While nothing is visible the engine polls the hardware
    /// every 2 s instead of 1 s. Showing also refreshes batteries (at most every 5 s).
    /// </summary>
    void SetUiVisible(bool visible);

    /// <summary>Low-battery alerts raised while no popover was showing; returned once, then cleared.</summary>
    IReadOnlyList<LowBatteryToast> TakePendingToasts();

    Task<CommandResult> RefreshNowAsync(CancellationToken cancellationToken = default);

    Task<ConnectRgbResult> ConnectRgbAsync(CancellationToken cancellationToken = default);
    Task<RgbDevicesResult> GetRgbDevicesAsync(bool refresh, CancellationToken cancellationToken = default);
    /// <summary>Runs one lighting command; the result carries the device list after it (when the backend is connected).</summary>
    Task<RgbDevicesResult> RgbCommandAsync(RgbCommand command, CancellationToken cancellationToken = default);

    /// <summary>Manual duty for one fan (null = hand it back). Only accepted in 個別 mode for a controllable, non-orphaned fan.</summary>
    Task<CommandResult> SetFanAsync(string fanId, double? percent, CancellationToken cancellationToken = default);
    Task<CommandResult> SetFanModeAsync(FanMode mode, CancellationToken cancellationToken = default);
    Task<CommandResult> SetSyncedPercentAsync(double percent, CancellationToken cancellationToken = default);
    /// <summary>"納入群組控制": an explicit choice either way (include overrides the pump heuristic).</summary>
    Task<CommandResult> SetFanMembershipAsync(string fanId, bool include, CancellationToken cancellationToken = default);

    /// <summary>Changes one setting (see <see cref="SettingsPatch"/>). The fan-mode fields are only changed by the fan commands.</summary>
    Task<CommandResult> UpdateSettingAsync(SettingsPatch patch, CancellationToken cancellationToken = default);
    Task<CommandResult> SetAutoStartAsync(bool enabled, CancellationToken cancellationToken = default);
    /// <summary>Restarts the agent elevated (UAC prompt). On success the agent exits with <see cref="AgentExitReason.Relaunch"/>.</summary>
    Task<CommandResult> ElevateAsync(bool showUi, CancellationToken cancellationToken = default);
    /// <summary>Clean shutdown (fans handed back first).</summary>
    void RequestQuit();
}

/// <summary>Facts about the agent process that rarely change.</summary>
public sealed record AgentInfo
{
    public bool IsElevated { get; init; }
    public bool CanElevate { get; init; }
    public bool AutoStartSupported { get; init; }
    public bool AutoStartEnabled { get; init; }
    /// <summary>Auto-start comes from the "Pulse" scheduled task (elevated, no UAC) rather than the Run key.</summary>
    public bool AutoStartViaTask { get; init; }
    public bool OpenRgbBundled { get; init; }
    public bool IsDemo { get; init; }
    public string Version { get; init; } = "0.0.0";
    public string SettingsPath { get; init; } = string.Empty;
    public string LogPath { get; init; } = string.Empty;
    public string TelemetryDirectory { get; init; } = string.Empty;
    public string TelemetryPath { get; init; } = string.Empty;
    public int ProcessId { get; init; }

    public static AgentInfo Empty { get; } = new();
}

/// <summary>The current settings (an immutable copy — never mutate it) and which property changed last.</summary>
public sealed record SettingsState(AppSettings Settings, long Revision, string? Changed)
{
    public static SettingsState Empty { get; } = new(new AppSettings(), 0, null);
}

/// <summary>One hardware poll and the fan-mode evaluation that followed it.</summary>
public sealed record HardwareFrame(HardwareSnapshot Snapshot, FanModeResult Fans)
{
    public static HardwareFrame Empty { get; } = new(HardwareSnapshot.Empty, FanModeResult.Empty);
}

/// <summary>The battery list after a poll (remembered devices included) and what the tray derives from it.</summary>
public sealed record BatteryState
{
    /// <summary>Every device, hidden ones included: connected first, then lowest level, then name.</summary>
    public IReadOnlyList<BatteryDevice> All { get; init; } = Array.Empty<BatteryDevice>();
    /// <summary><see cref="All"/> without <see cref="AppSettings.HiddenDeviceIds"/>.</summary>
    public IReadOnlyList<BatteryDevice> Visible { get; init; } = Array.Empty<BatteryDevice>();
    public BatterySummary? Lowest { get; init; }
    public DateTimeOffset? LastUpdate { get; init; }
    /// <summary>First provider failure of the last poll ("Bluetooth: timeout"), or null.</summary>
    public string? LastError { get; init; }

    public static BatteryState Empty { get; } = new();
}

public sealed record LowBatteryToast(BatteryDevice Device, string Title, string Message);

public enum AgentExitReason
{
    /// <summary>The user quit (tray 結束, popover 結束, --quit, logoff): clients exit too.</summary>
    Quit,
    /// <summary>The agent restarts itself (elevation); a new agent follows.</summary>
    Relaunch,
}

public record CommandResult(bool Ok, string? Error = null)
{
    public static CommandResult Success { get; } = new(true);
    public static CommandResult Fail(string error) => new(false, error);
}

public sealed record ConnectRgbResult(bool Ok, RgbStatus Status, string? Error = null) : CommandResult(Ok, Error);

public sealed record RgbDevicesResult(bool Ok, IReadOnlyList<RgbDevice> Devices, string? Error = null) : CommandResult(Ok, Error)
{
    public static RgbDevicesResult Failed(string error) => new(false, Array.Empty<RgbDevice>(), error);
}

public enum RgbCommandKind
{
    /// <summary>Paint every LED of <see cref="RgbCommand.Key"/> (or of every device when Key is null) one colour.</summary>
    SetColor,
    SetZoneColor,
    /// <summary>Activate <see cref="RgbCommand.ModeName"/> (resolved by name, <see cref="RgbCommand.ModeIndex"/> as a hint).</summary>
    SetMode,
    /// <summary>Lights off (Off mode, else black) for one device or, with Key null, every device.</summary>
    TurnOff,
    /// <summary>Back to the first-seen state for one device or, with Key null, every device.</summary>
    RestoreDefault,
    SaveAsDefault,
    /// <summary>Stop remembering (and re-applying) this device's lighting.</summary>
    Forget,
}

/// <summary>
/// One lighting operation. Devices are addressed by their stable <see cref="RgbDevice.Key"/>; <see cref="Index"/> is only a
/// hint. When the key is no longer present the agent answers 「裝置已變更，請重新整理」.
/// </summary>
public sealed record RgbCommand
{
    public required RgbCommandKind Kind { get; init; }
    /// <summary>Target device; null addresses every device (SetColor / TurnOff / RestoreDefault only).</summary>
    public string? Key { get; init; }
    public int? Index { get; init; }
    public int? ZoneIndex { get; init; }
    public RgbColor? Color { get; init; }
    public string? ModeName { get; init; }
    public int? ModeIndex { get; init; }
    public int? Speed { get; init; }
    public int? Brightness { get; init; }
}
