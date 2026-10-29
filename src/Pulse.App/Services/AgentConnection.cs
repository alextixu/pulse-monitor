using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Pulse.Core.Tray;

namespace Pulse.App.Services;

/// <summary>
/// The view models' single view of the agent (the pipe client, or the in-process demo <c>AgentCore</c>). Everything here
/// lives on the UI thread: agent events are posted to <see cref="Dispatcher.UIThread"/> in arrival order, state is exposed
/// as observable properties, and commands are expected to be called from the UI thread (their continuations resume there).
/// Until the agent answered (<see cref="IsReady"/>) every property holds defaults, so the popover can be shown at once.
/// </summary>
/// <remarks>
/// <see cref="Settings"/> is a mutable copy for bindings: the agent's last settings plus the patches still in flight.
/// A setting changed in the UI is applied to the copy immediately (no flicker while the agent answers), sent as a
/// <see cref="SettingsPatch"/>, and dropped from the overlay when the reply arrives; a failed patch is rolled back.
/// </remarks>
public sealed partial class AgentConnection : ObservableObject
{
    private readonly IPulseAgent _agent;
    private readonly ILogger _log;
    private readonly object _inflightGate = new();
    private readonly HashSet<Task> _inflight = new();
    private readonly List<(long Seq, SettingsPatch Patch)> _pendingPatches = new();
    private SettingsState _agentSettings = SettingsState.Empty;
    private bool _settingsLoaded;
    private long _patchSeq;

    [ObservableProperty] private AgentInfo _info = AgentInfo.Empty;
    [ObservableProperty] private HardwareMonitorStatus _hardwareStatus = new();
    [ObservableProperty] private RgbStatus _rgbStatus = new();
    [ObservableProperty] private HardwareFrame _hardware = HardwareFrame.Empty;
    [ObservableProperty] private BatteryState _batteries = BatteryState.Empty;
    /// <summary><see cref="BatteryState.All"/> without the (locally known) hidden ids, so 隱藏 takes effect at once.</summary>
    [ObservableProperty] private IReadOnlyList<BatteryDevice> _visibleBatteries = Array.Empty<BatteryDevice>();
    /// <summary>Connected visible device with the lowest level (same rule as the tray).</summary>
    [ObservableProperty] private BatterySummary? _lowestBattery;
    /// <summary>True once the agent's first state (Hello, or the demo engine's start) was loaded.</summary>
    [ObservableProperty] private bool _isReady;
    /// <summary>Set when no agent could be reached or it speaks another protocol; shown in the header.</summary>
    [ObservableProperty] private string? _connectionError;

    public AgentConnection(IPulseAgent agent, bool isDemo, ILogger<AgentConnection> log)
    {
        _agent = agent;
        _log = log;
        IsDemo = isDemo;

        agent.InfoChanged += (_, info) => Post(() => Info = info);
        agent.SettingsChanged += (_, state) => Post(() => ApplySettingsState(state, force: false));
        agent.HardwareStatusChanged += (_, status) => Post(() => HardwareStatus = status);
        agent.RgbStatusChanged += (_, status) => Post(() => RgbStatus = status);
        agent.HardwareUpdated += (_, frame) => Post(() => ApplyHardware(frame));
        agent.BatteriesUpdated += (_, state) => Post(() => ApplyBatteries(state));
        agent.RgbDevicesChanged += (_, _) => Post(() => RgbDevicesChanged?.Invoke(this, EventArgs.Empty));
        agent.LowBattery += (_, toast) => Post(() => LowBattery?.Invoke(this, toast));
        agent.Exiting += (_, reason) => Post(() => Exiting?.Invoke(this, reason));
        if (agent is IpcPulseClient ipc)
        {
            ipc.ShowPopoverRequested += (_, request) => Post(() => ShowPopoverRequested?.Invoke(this, request));
        }
    }

    /// <summary>The wrapped agent (for lifetime management by <see cref="App"/>).</summary>
    public IPulseAgent Agent => _agent;

    public bool IsDemo { get; }

    /// <summary>Mutable settings copy for bindings (never null; defaults until the agent answered). UI thread only.</summary>
    public AppSettings Settings { get; private set; } = new();

    /// <summary>A setting changed (agent broadcast, or a local change applied optimistically); null = possibly all.</summary>
    public event EventHandler<string?>? SettingsChanged;

    /// <summary>A new hardware frame (only while a popover is visible, at the agent's 1 s cadence).</summary>
    public event EventHandler<HardwareFrame>? HardwareUpdated;

    /// <summary><see cref="Batteries"/> / <see cref="VisibleBatteries"/> / <see cref="LowestBattery"/> changed.</summary>
    public event EventHandler<BatteryState>? BatteriesUpdated;

    public event EventHandler? RgbDevicesChanged;

    public event EventHandler<LowBatteryToast>? LowBattery;

    public event EventHandler<AgentExitReason>? Exiting;

    /// <summary>The agent asked this popover to show (or toggle) itself.</summary>
    public event EventHandler<PopoverRequest>? ShowPopoverRequested;

    // ───────────── state ─────────────

    /// <summary>Loads the agent's current state after Hello / start and delivers the low-battery alerts that waited for a popover.</summary>
    public void OnAgentReady()
    {
        Info = _agent.Info;
        HardwareStatus = _agent.HardwareStatus;
        RgbStatus = _agent.RgbStatus;
        ApplySettingsState(_agent.Settings, force: true);
        ApplyHardware(_agent.Hardware);
        ApplyBatteries(_agent.Batteries);
        ConnectionError = null;
        IsReady = true;

        IReadOnlyList<LowBatteryToast> toasts;
        try
        {
            toasts = _agent.TakePendingToasts();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "TakePendingToasts failed");
            toasts = Array.Empty<LowBatteryToast>();
        }
        foreach (var toast in toasts) LowBattery?.Invoke(this, toast);
    }

    public void ReportConnectionError(string message) => ConnectionError = message;

    private void ApplySettingsState(SettingsState state, bool force)
    {
        // Older revisions (a broadcast overtaken by Hello, a duplicate) never roll the copy back.
        if (!force && _settingsLoaded && state.Revision <= _agentSettings.Revision) return;
        _agentSettings = state;
        _settingsLoaded = true;
        RebuildSettings();
        OnSettingsChanged(force ? null : state.Changed);
    }

    /// <summary>Copy = agent settings + every patch still waiting for its reply.</summary>
    private void RebuildSettings()
    {
        var copy = _agentSettings.Settings.Clone();
        foreach (var (_, patch) in _pendingPatches) patch.ApplyTo(copy);
        Settings = copy;
    }

    private void OnSettingsChanged(string? name)
    {
        if (name is null || name == nameof(AppSettings.HiddenDeviceIds)) ApplyBatteries(Batteries);
        SettingsChanged?.Invoke(this, name);
    }

    private void ApplyHardware(HardwareFrame frame)
    {
        Hardware = frame;
        HardwareUpdated?.Invoke(this, frame);
    }

    private void ApplyBatteries(BatteryState state)
    {
        Batteries = state;
        var all = state.All.Count > 0 || state.Visible.Count == 0 ? state.All : state.Visible;
        var hidden = Settings.HiddenDeviceIds;
        var visible = hidden.Count == 0 ? all : all.Where(d => !hidden.Contains(d.Id)).ToList();
        VisibleBatteries = visible;
        var lowest = visible.Where(d => d.IsConnected && d.Percent is not null).MinBy(d => d.Percent);
        LowestBattery = lowest is null
            ? null
            : new BatterySummary(lowest.Id, lowest.Name, lowest.Percent!.Value, lowest.Status == BatteryStatus.Charging, lowest.Kind);
        BatteriesUpdated?.Invoke(this, state);
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    // ───────────── commands (UI thread) ─────────────

    public void SetUiVisible(bool visible)
    {
        try
        {
            _agent.SetUiVisible(visible);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "SetUiVisible({Visible}) failed", visible);
        }
    }

    public Task<CommandResult> RefreshNowAsync() => Run(() => _agent.RefreshNowAsync(), CommandResult.Fail, "RefreshNow");

    public async Task<ConnectRgbResult> ConnectRgbAsync()
    {
        var result = await Run(() => _agent.ConnectRgbAsync(), e => new ConnectRgbResult(false, RgbStatus, e), "ConnectRgb");
        // The reply carries the status after the attempt; the broadcast may still be on its way.
        if (result.Status is { } status) RgbStatus = status;
        return result;
    }

    public Task<RgbDevicesResult> GetRgbDevicesAsync(bool refresh)
        => Run(() => _agent.GetRgbDevicesAsync(refresh), RgbDevicesResult.Failed, "GetRgbDevices");

    public Task<RgbDevicesResult> RgbCommandAsync(RgbCommand command)
        => Run(() => _agent.RgbCommandAsync(command), RgbDevicesResult.Failed, $"Rgb {command.Kind}");

    public Task<CommandResult> SetFanAsync(string fanId, double? percent)
        => Run(() => _agent.SetFanAsync(fanId, percent), CommandResult.Fail, "SetFan");

    public Task<CommandResult> SetFanModeAsync(FanMode mode)
        => Run(() => _agent.SetFanModeAsync(mode), CommandResult.Fail, "SetFanMode");

    public Task<CommandResult> SetSyncedPercentAsync(double percent)
        => Run(() => _agent.SetSyncedPercentAsync(percent), CommandResult.Fail, "SetSyncedPercent");

    public Task<CommandResult> SetFanMembershipAsync(string fanId, bool include)
        => Run(() => _agent.SetFanMembershipAsync(fanId, include), CommandResult.Fail, "SetFanMembership");

    public Task<CommandResult> SetAutoStartAsync(bool enabled)
        => Run(() => _agent.SetAutoStartAsync(enabled), CommandResult.Fail, "SetAutoStart");

    public Task<CommandResult> ElevateAsync(bool showUi)
        => Run(() => _agent.ElevateAsync(showUi), CommandResult.Fail, "Elevate");

    public void RequestQuit()
    {
        try
        {
            _agent.RequestQuit();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RequestQuit failed");
        }
    }

    /// <summary>
    /// Changes one setting: applied to <see cref="Settings"/> right away (and <see cref="SettingsChanged"/> raised), then
    /// sent to the agent. On failure the copy is rebuilt from the agent's state, which reverts the bound control.
    /// </summary>
    public Task<CommandResult> UpdateSettingAsync(SettingsPatch patch)
    {
        var error = patch.ApplyTo(Settings);
        if (error is not null) return Task.FromResult(CommandResult.Fail(error));
        var seq = ++_patchSeq;
        _pendingPatches.Add((seq, patch));
        OnSettingsChanged(patch.Name);
        return CompleteAsync();

        async Task<CommandResult> CompleteAsync()
        {
            var result = await Run(() => _agent.UpdateSettingAsync(patch), CommandResult.Fail, $"UpdateSetting {patch.Name}");
            _pendingPatches.RemoveAll(p => p.Seq == seq);
            if (!result.Ok)
            {
                _log.LogWarning("Setting {Name} was not applied: {Error}", patch.Name, result.Error);
                RebuildSettings();
                OnSettingsChanged(patch.Name);
            }
            return result;
        }
    }

    /// <summary>Waits for every command still waiting for its reply (bounded by the caller).</summary>
    public async Task WhenIdleAsync()
    {
        Task[] tasks;
        lock (_inflightGate) tasks = _inflight.ToArray();
        if (tasks.Length == 0) return;
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // Failures are logged by Run.
        }
    }

    private async Task<T> Run<T>(Func<Task<T>> call, Func<string, T> fail, string what)
    {
        Task<T> task;
        try
        {
            task = call();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Command} failed", what);
            return fail(ex.Message);
        }

        lock (_inflightGate) _inflight.Add(task);
        try
        {
            return await task;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Command} failed", what);
            return fail(ex.Message);
        }
        finally
        {
            lock (_inflightGate) _inflight.Remove(task);
        }
    }
}
