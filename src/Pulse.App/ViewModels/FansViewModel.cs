using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using Pulse.App.Controls;
using Pulse.Core.Localization;
using Pulse.App.Services;
using Pulse.Core.Abstractions;
using Pulse.Core.FanControl;
using Pulse.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Pulse.App.ViewModels;

/// <summary>
/// 風扇 tab: the fan-mode card plus fans grouped by <see cref="FanInfo.Group"/>, updated in place after every hardware
/// frame. Headers that never reported a fan are hidden (the agent's <see cref="FanModeFanStatus.Presence"/>).
/// The agent owns the fan settings: mode / synced duty / membership / manual duties are commands
/// (<see cref="AgentConnection.SetFanModeAsync"/> …) and the settings broadcast only mirrors them back.
/// </summary>
public sealed partial class FansViewModel : TabViewModelBase
{
    private static readonly TimeSpan SyncedDebounce = TimeSpan.FromMilliseconds(300);

    private readonly ILogger<FansViewModel> _log;
    private readonly DispatcherTimer _syncedTimer;
    private bool _syncingSynced;
    /// <summary>
    /// Set while <see cref="Mode"/> is changed to mirror the agent's settings, so a broadcast is never sent back as a
    /// SetFanMode (each extra mode switch hands every held fan back to the firmware).
    /// </summary>
    private bool _syncingMode;
    /// <summary>SetFanMode / SetSyncedPercent commands not answered yet: broadcasts older than them are not mirrored.</summary>
    private int _modeRequests;
    private int _syncedRequests;

    [ObservableProperty] private bool _showBanner;
    [ObservableProperty] private string _bannerText = Strings.FanControlUnavailable;
    [ObservableProperty] private bool _showElevate;
    [ObservableProperty] private bool _hasFans;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _emptyHint = Strings.FansEmptyHint;
    [ObservableProperty] private string? _recoveryNotice;
    [ObservableProperty] private bool _hasRecoveryNotice;

    // ---- Fan modes ----
    [ObservableProperty] private FanMode _mode;
    [ObservableProperty] private bool _modesEnabled;
    [ObservableProperty] private bool _showModesUnavailable;
    [ObservableProperty] private string _modeDescription = string.Empty;
    [ObservableProperty] private bool _isQuietMode;
    [ObservableProperty] private bool _isSyncedMode;
    [ObservableProperty] private string _quietLiveText = Strings.FanQuietWaiting;
    [ObservableProperty] private double _syncedPercent;
    [ObservableProperty] private string _syncedPercentText = string.Empty;
    [ObservableProperty] private int _hiddenCount;
    [ObservableProperty] private bool _hasHiddenFans;
    [ObservableProperty] private string _hiddenText = string.Empty;

    public FansViewModel(AgentConnection agent, ILogger<FansViewModel> log)
        : base(MainTab.Fans, agent)
    {
        _log = log;

        _syncedTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SyncedDebounce };
        _syncedTimer.Tick += (_, _) =>
        {
            _syncedTimer.Stop();
            _ = SendSyncedPercentAsync(SyncedPercent);
        };

        _mode = Agent.Settings.FanMode;
        _syncingSynced = true;
        SyncedPercent = Agent.Settings.SyncedFanPercent;
        _syncingSynced = false;
        ApplyModeFlags();

        Agent.HardwareUpdated += (_, frame) =>
        {
            Sync(frame.Snapshot.Fans, frame.Fans);
            ApplyResult(frame.Fans);
        };
        Agent.SettingsChanged += OnSettingsChanged;
        Agent.PropertyChanged += OnAgentPropertyChanged;
        ApplyStatus(Agent.HardwareStatus);
        Sync(Agent.Hardware.Snapshot.Fans, Agent.Hardware.Fans);
    }

    public ObservableCollection<FanGroupViewModel> Groups { get; } = new();

    /// <summary>The latest fan-mode evaluation (part of the last hardware frame).</summary>
    private FanModeResult Current => Agent.Hardware.Fans;

    public bool IsIndividualMode => Mode == FanMode.Individual;

    /// <summary>Quiet / Synced: fans are driven as a group.</summary>
    public bool IsGroupMode => Mode is FanMode.Quiet or FanMode.Synced;

    partial void OnModeChanged(FanMode value)
    {
        ApplyModeFlags();
        if (_syncingMode) return;
        _ = SendModeAsync(value);
    }

    private async Task SendModeAsync(FanMode mode)
    {
        _modeRequests++;
        CommandResult result;
        try
        {
            result = await Agent.SetFanModeAsync(mode);
        }
        finally
        {
            _modeRequests--;
        }
        if (result.Ok) _log.LogInformation("Fan mode → {Mode}", mode);
        else _log.LogWarning("Fan mode {Mode} was not applied: {Error}", mode, result.Error);
        // Mirror the agent once the last request is answered (reverts a refused switch).
        if (_modeRequests == 0) SyncModeFromSettings();
    }

    private async Task SendSyncedPercentAsync(double percent)
    {
        _syncedRequests++;
        CommandResult result;
        try
        {
            result = await Agent.SetSyncedPercentAsync(percent);
        }
        finally
        {
            _syncedRequests--;
        }
        if (!result.Ok) _log.LogWarning("Synced duty {Percent:0}% was not applied: {Error}", percent, result.Error);
        if (_syncedRequests == 0) SyncSyncedFromSettings();
    }

    public override Task FlushPendingAsync()
    {
        var pending = new List<Task>();
        if (_syncedTimer.IsEnabled)
        {
            _syncedTimer.Stop();
            pending.Add(SendSyncedPercentAsync(SyncedPercent));
        }
        foreach (var item in Groups.SelectMany(g => g.Fans)) pending.Add(item.FlushPendingAsync());
        return Task.WhenAll(pending);
    }

    partial void OnSyncedPercentChanged(double value)
    {
        SyncedPercentText = $"{value:0}%";
        if (_syncingSynced) return;
        _syncedTimer.Stop();
        _syncedTimer.Start();
    }

    [RelayCommand]
    private void SyncedPreset(double percent)
    {
        _syncedTimer.Stop();
        _syncingSynced = true;
        SyncedPercent = percent;
        _syncingSynced = false;
        _ = SendSyncedPercentAsync(percent);
    }

    private void OnSettingsChanged(object? sender, string? name)
    {
        // Keep the card in step when the agent's settings change (Hello, another client, the screenshot run).
        if (name is null or nameof(AppSettings.FanMode)) SyncModeFromSettings();
        if (name is null or nameof(AppSettings.SyncedFanPercent)) SyncSyncedFromSettings();
    }

    private void SyncModeFromSettings()
    {
        // A broadcast that overtook our own pending switch would flip the card back and forth: wait for the reply.
        if (_modeRequests > 0) return;
        var mode = Agent.Settings.FanMode;
        if (Mode == mode) return;
        _syncingMode = true;
        try
        {
            Mode = mode;
        }
        finally
        {
            _syncingMode = false;
        }
    }

    private void SyncSyncedFromSettings()
    {
        if (_syncedTimer.IsEnabled || _syncedRequests > 0) return;
        if (Math.Abs(SyncedPercent - Agent.Settings.SyncedFanPercent) < 0.5) return;
        _syncingSynced = true;
        SyncedPercent = Agent.Settings.SyncedFanPercent;
        _syncingSynced = false;
    }

    private void ApplyModeFlags()
    {
        IsQuietMode = Mode == FanMode.Quiet;
        IsSyncedMode = Mode == FanMode.Synced;
        ModeDescription = Mode switch
        {
            FanMode.Auto => Strings.FanModeAutoHint,
            FanMode.Quiet => Strings.FanModeQuietHint,
            FanMode.Synced => Strings.FanModeSyncedHint,
            _ => Strings.FanModeIndividualHint,
        };
        OnPropertyChanged(nameof(IsIndividualMode));
        OnPropertyChanged(nameof(IsGroupMode));
        foreach (var item in Groups.SelectMany(g => g.Fans)) item.SetMode(Mode, ModesEnabled);
        ApplyResult(Current);
    }

    private void OnAgentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentConnection.HardwareStatus) or nameof(AgentConnection.Info)) ApplyStatus(Agent.HardwareStatus);
    }

    private void ApplyStatus(HardwareMonitorStatus status)
    {
        var ready = status.State is HardwareMonitorState.Ready or HardwareMonitorState.Degraded;
        ShowBanner = !ready || !status.FanControlAvailable;
        ShowElevate = !Agent.Info.IsElevated && Agent.Info.CanElevate;
        RecoveryNotice = status.RecoveryNotice;
        HasRecoveryNotice = !string.IsNullOrWhiteSpace(status.RecoveryNotice);
        BannerText = !string.IsNullOrWhiteSpace(status.Message)
            ? status.Message!
            : status.FanControlAvailable ? Strings.ElevateHint : Strings.FanControlUnavailable;
        EmptyHint = status.State switch
        {
            HardwareMonitorState.Ready or HardwareMonitorState.Degraded => Strings.NoFans,
            HardwareMonitorState.Failed or HardwareMonitorState.Unsupported => status.Message ?? Strings.StatusUnsupported,
            _ => Strings.FansEmptyHint,
        };
        ModesEnabled = ready && status.FanControlAvailable;
        // Only explain the disabled card once the monitor has decided (not while it is still starting).
        ShowModesUnavailable = !ModesEnabled && status.State is not (HardwareMonitorState.NotStarted or HardwareMonitorState.Initializing);
        foreach (var item in Groups.SelectMany(g => g.Fans)) item.SetMode(Mode, ModesEnabled);
    }

    /// <summary>Per-fan chips + the Quiet live line from the latest evaluation.</summary>
    private void ApplyResult(FanModeResult result)
    {
        foreach (var item in Groups.SelectMany(g => g.Fans))
        {
            item.ApplyStatus(result.Mode == Mode && result.Fans.TryGetValue(item.Id, out var s) ? s : null);
        }

        if (!IsQuietMode || result.Mode != FanMode.Quiet)
        {
            QuietLiveText = Strings.FanQuietWaiting;
            return;
        }

        var temps = $"CPU {Strings.Celsius(result.CpuTempC)} · GPU {Strings.Celsius(result.GpuTempC)}";
        var parts = new List<string>();
        AddGroup(Strings.FanBoardShort, result.Fans.Values.Where(f => !f.IsGpuFan));
        AddGroup(Strings.FanGpuShort, result.Fans.Values.Where(f => f.IsGpuFan));
        QuietLiveText = parts.Count == 0 ? temps : $"{temps} → {string.Join("、", parts)}";

        void AddGroup(string label, IEnumerable<FanModeFanStatus> fans)
        {
            var members = fans.Where(f => f.State is FanModeState.Quiet or FanModeState.HandedOff or FanModeState.NoTemperature).ToList();
            if (members.Count == 0) return;
            if (members.Any(f => f.State != FanModeState.Quiet)) parts.Add($"{label} {Strings.FanAutoChip}");
            else parts.Add($"{label} {Strings.Percent(members.Max(f => f.Percent))}");
        }
    }

    private async void SetMembership(FanItemViewModel item, bool include)
    {
        var result = await Agent.SetFanMembershipAsync(item.Id, include);
        if (result.Ok)
        {
            _log.LogInformation("Fan {Fan} group membership → {Include}", item.Name, include);
            return;
        }
        _log.LogWarning("Fan {Fan} membership change failed: {Error}", item.Name, result.Error);
        // Put the switch back to what the last evaluation says, then show why (ApplyStatus resets the error).
        item.ApplyStatus(Current.Mode == Mode && Current.Fans.TryGetValue(item.Id, out var s) ? s : null);
        item.Error = result.Error;
    }

    /// <summary>Headers the agent decided are not connected (never spun) are hidden; unknown ids are shown.</summary>
    private static bool IsHidden(FanModeResult result, string fanId)
        => result.Fans.TryGetValue(fanId, out var status) && status.Presence == FanPresence.Hidden;

    private void Sync(IReadOnlyList<FanInfo> all, FanModeResult result)
    {
        var fans = all.Where(f => !IsHidden(result, f.Id)).ToList();
        HiddenCount = all.Count - fans.Count;
        HasHiddenFans = HiddenCount > 0;
        HiddenText = string.Format(Strings.FanHiddenHeadersFormat, HiddenCount);

        var grouped = fans.GroupBy(f => f.Group).ToList();

        var sameLayout = grouped.Count == Groups.Count;
        if (sameLayout)
        {
            for (var g = 0; g < grouped.Count && sameLayout; g++)
            {
                var ids = grouped[g].Select(f => f.Id).ToList();
                var group = Groups[g];
                sameLayout = group.Name == grouped[g].Key && ids.Count == group.Fans.Count && ids.SequenceEqual(group.Fans.Select(f => f.Id));
            }
        }

        if (sameLayout)
        {
            for (var g = 0; g < grouped.Count; g++)
            {
                var i = 0;
                foreach (var fan in grouped[g]) Groups[g].Fans[i++].Update(fan);
            }
        }
        else
        {
            var existing = Groups.SelectMany(g => g.Fans).ToDictionary(f => f.Id);
            Groups.Clear();
            foreach (var g in grouped)
            {
                var group = new FanGroupViewModel(g.Key);
                foreach (var fan in g)
                {
                    if (!existing.TryGetValue(fan.Id, out var item))
                    {
                        item = new FanItemViewModel(fan.Id, Agent, _log, SetMembership);
                        item.SetMode(Mode, ModesEnabled);
                        item.ApplyStatus(result.Mode == Mode && result.Fans.TryGetValue(fan.Id, out var s) ? s : null);
                    }
                    item.Update(fan);
                    group.Fans.Add(item);
                }
                Groups.Add(group);
            }
            _log.LogDebug("Fan layout: {Groups} (hidden headers: {Hidden})", string.Join(" | ", grouped.Select(g => $"{g.Key}: {g.Count()}")), HiddenCount);
        }

        HasFans = fans.Count > 0;
        IsEmpty = fans.Count == 0;
    }
}

/// <summary>Preset duty cycles for the chip row (bound via x:Static as command parameters).</summary>
public static class FanPresets
{
    public static double Quiet => 30;
    public static double Balanced => 50;
    public static double Performance => 75;
    public static double Full => 100;
}

public sealed class FanGroupViewModel
{
    public FanGroupViewModel(string name) => Name = name;

    public string Name { get; }

    public ObservableCollection<FanItemViewModel> Fans { get; } = new();
}

/// <summary>
/// One fan card. 個別 mode: manual toggle + debounced (300 ms) slider → <see cref="AgentConnection.SetFanAsync"/> (the
/// agent validates it). 靜音 / 全部同步: a status chip and the "納入群組控制" switch; the manual controls are hidden.
/// </summary>
public sealed partial class FanItemViewModel : ObservableObject
{
    private static readonly TimeSpan SliderDebounce = TimeSpan.FromMilliseconds(300);

    private readonly AgentConnection _agent;
    private readonly ILogger _log;
    private readonly Action<FanItemViewModel, bool> _setMembership;
    private readonly DispatcherTimer _timer;
    private bool _syncing;
    private bool _syncingMember;
    private FanMode _mode;
    private bool _controlAvailable;
    /// <summary>Duty cycle last reported by the hardware, used as the starting point when manual control is switched on.</summary>
    private double? _measuredPercent;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _rpmText = Strings.NotAvailable;
    [ObservableProperty] private string _percentText = Strings.NotAvailable;
    [ObservableProperty] private bool _hasPercent;
    [ObservableProperty] private BccTone _dutyTone = BccTone.Neutral;
    [ObservableProperty] private bool _canControl;
    [ObservableProperty] private bool _isMonitorOnly = true;
    [ObservableProperty] private bool _isManual;
    [ObservableProperty] private double _sliderValue;
    [ObservableProperty] private string _sliderText = string.Empty;
    [ObservableProperty] private double _minPercent;
    [ObservableProperty] private double _maxPercent = 100;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasError;

    // ---- Fan-mode presentation ----
    [ObservableProperty] private bool _showManualControls;
    [ObservableProperty] private bool _showManualChip;
    [ObservableProperty] private bool _showModeChip;
    /// <summary>The measured duty pill; hidden while the mode chip already shows the applied duty (靜音 45% / 同步 50%).</summary>
    [ObservableProperty] private bool _showDutyPill;
    private bool _chipCarriesDuty;
    /// <summary>Orphaned fan driven by Pulse's stand-in curve: no manual controls, even in 個別 mode.</summary>
    private bool _standIn;
    [ObservableProperty] private string _modeChipText = string.Empty;
    [ObservableProperty] private BccTone _modeChipTone = BccTone.Neutral;
    [ObservableProperty] private bool _showMemberToggle;
    [ObservableProperty] private bool _isMember = true;
    [ObservableProperty] private string? _memberHint;
    [ObservableProperty] private bool _hasMemberHint;

    public FanItemViewModel(string id, AgentConnection agent, ILogger log, Action<FanItemViewModel, bool> setMembership)
    {
        Id = id;
        _agent = agent;
        _log = log;
        _setMembership = setMembership;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SliderDebounce };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (IsManual && _mode == FanMode.Individual) _ = ApplyAsync(SliderValue);
        };
    }

    public string Id { get; }

    /// <summary>Sends a slider position still waiting in the debounce timer (before the popover exits).</summary>
    public Task FlushPendingAsync()
    {
        if (!_timer.IsEnabled) return Task.CompletedTask;
        _timer.Stop();
        return IsManual && _mode == FanMode.Individual ? ApplyAsync(SliderValue) : Task.CompletedTask;
    }

    /// <summary>Called by the tab when the fan mode or control availability changes.</summary>
    public void SetMode(FanMode mode, bool controlAvailable)
    {
        if (_mode != mode) _timer.Stop();
        _mode = mode;
        _controlAvailable = controlAvailable;
        RefreshPresentation();
    }

    /// <summary>Manual controls only in 個別 mode (or when modes are unavailable); the group switch only in 靜音 / 全部同步.</summary>
    private void RefreshPresentation()
    {
        var individual = (_mode == FanMode.Individual || !_controlAvailable) && !_standIn;
        ShowManualControls = CanControl && individual;
        ShowManualChip = IsManual && individual;
        ShowMemberToggle = CanControl && _controlAvailable && _mode is FanMode.Quiet or FanMode.Synced;
        if (individual) ShowModeChip = false;
        ShowDutyPill = HasPercent && !(ShowModeChip && _chipCarriesDuty);
    }

    /// <summary>Status from the latest fan-mode evaluation (null: none yet / not applicable).</summary>
    public void ApplyStatus(FanModeFanStatus? status)
    {
        // Orphaned fans (BIOS curve lost until reboot) are driven by Pulse in every mode, including 個別.
        _standIn = status?.State == FanModeState.StandIn && _controlAvailable;
        if (status is null || (_mode == FanMode.Individual && !_standIn) || !_controlAvailable
            || status.State is FanModeState.Individual or FanModeState.NotControllable or FanModeState.Unavailable)
        {
            ShowModeChip = false;
            MemberHint = null;
            RefreshPresentation();
            return;
        }

        (ModeChipText, ModeChipTone) = status.State switch
        {
            FanModeState.Auto => (Strings.FanAutoChip, BccTone.Neutral),
            FanModeState.Quiet => (Strings.FanChipQuiet(status.Percent), BccTone.Teal),
            FanModeState.Synced => (Strings.FanChipSynced(status.Percent), BccTone.Accent),
            FanModeState.HandedOff => (Strings.FanChipHandedOff, BccTone.Warning),
            FanModeState.StandIn => (Strings.FanChipStandIn(status.Percent), BccTone.Warning),
            FanModeState.NoTemperature => (Strings.FanChipNoTemp, BccTone.Warning),
            FanModeState.Excluded => (status.Exclusion is FanExclusion.PumpName or FanExclusion.PumpLike ? Strings.FanChipExcludedPump : Strings.FanChipExcluded, BccTone.Neutral),
            FanModeState.NotDetected => (Strings.FanChipDetecting, BccTone.Neutral),
            _ => (string.Empty, BccTone.Neutral),
        };
        if (status.Error is not null) ModeChipTone = BccTone.Danger;
        ShowModeChip = ModeChipText.Length > 0;

        _syncingMember = true;
        IsMember = status.State != FanModeState.Excluded;
        _syncingMember = false;
        MemberHint = status.State == FanModeState.Excluded
            ? status.Exclusion switch
            {
                FanExclusion.PumpName => Strings.FanExcludedPumpName,
                FanExclusion.PumpLike => Strings.FanExcludedPumpLike,
                _ => Strings.FanExcludedUser,
            }
            : null;
        if (_mode != FanMode.Individual) Error = status.Error;
        _chipCarriesDuty = status.State is FanModeState.Quiet or FanModeState.Synced or FanModeState.StandIn;
        RefreshPresentation();
    }

    public void Update(FanInfo fan)
    {
        _syncing = true;
        try
        {
            Name = fan.Name;
            _measuredPercent = fan.Percent;
            RpmText = Strings.Rpm(fan.Rpm);
            HasPercent = fan.Percent is not null;
            PercentText = Strings.Percent(fan.Percent);
            DutyTone = fan.Percent is { } p ? (p >= 85 ? BccTone.Danger : p >= 60 ? BccTone.Warning : BccTone.Accent) : BccTone.Neutral;
            CanControl = fan.CanControl;
            IsMonitorOnly = !fan.CanControl;
            MinPercent = fan.MinPercent;
            MaxPercent = fan.MaxPercent > fan.MinPercent ? fan.MaxPercent : fan.MinPercent + 1;

            if (!IsBusy && !_timer.IsEnabled)
            {
                IsManual = fan.CanControl && fan.IsManual;
                if (!IsManual && fan.Percent is { } current)
                {
                    SliderValue = Math.Clamp(current, MinPercent, MaxPercent);
                }
                else if (SliderValue < MinPercent || SliderValue > MaxPercent)
                {
                    SliderValue = Math.Clamp(SliderValue, MinPercent, MaxPercent);
                }
            }
        }
        finally
        {
            _syncing = false;
        }

        RefreshPresentation();
    }

    partial void OnErrorChanged(string? value) => HasError = !string.IsNullOrWhiteSpace(value);

    partial void OnMemberHintChanged(string? value) => HasMemberHint = !string.IsNullOrWhiteSpace(value);

    partial void OnIsMemberChanged(bool value)
    {
        if (_syncingMember) return;
        _setMembership(this, value);
    }

    partial void OnSliderValueChanged(double value)
    {
        SliderText = $"{value:0}%";
        if (_syncing || !IsManual) return;
        _timer.Stop();
        _timer.Start();
    }

    partial void OnIsManualChanged(bool value)
    {
        if (_syncing) return;
        _timer.Stop();
        if (_mode != FanMode.Individual) return;
        if (!value)
        {
            _ = ApplyAsync(null);
            return;
        }

        // Take over at the speed the fan is running right now, not a stale slider position; unknown → middle of the range.
        var start = Math.Clamp(_measuredPercent ?? (MinPercent + MaxPercent) / 2, MinPercent, MaxPercent);
        _syncing = true;
        SliderValue = start;
        _syncing = false;
        _ = ApplyAsync(start);
    }

    [RelayCommand]
    private void Preset(double percent)
    {
        if (!CanControl || _mode != FanMode.Individual) return;
        var target = Math.Clamp(percent, MinPercent, MaxPercent);
        _syncing = true;
        SliderValue = target;
        if (!IsManual) IsManual = true;
        _syncing = false;
        _timer.Stop();
        _ = ApplyAsync(target);
    }

    private async Task ApplyAsync(double? percent)
    {
        Error = null;
        IsBusy = true;
        try
        {
            var result = await _agent.SetFanAsync(Id, percent);
            if (result.Ok)
            {
                _log.LogInformation("Fan {Fan} → {Percent}", Name, percent is { } p ? $"{p:0}%" : "auto");
                return;
            }

            _log.LogWarning("SetFan {Fan} failed: {Error}", Name, result.Error);
            Error = result.Error;
            if (percent is not null)
            {
                // The agent refused the manual duty: the card goes back to "firmware in charge".
                _syncing = true;
                IsManual = false;
                _syncing = false;
                RefreshPresentation();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
