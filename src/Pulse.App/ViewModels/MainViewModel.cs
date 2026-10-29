using System.ComponentModel;
using Pulse.App.Controls;
using Pulse.Core.Localization;
using Pulse.App.Services;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Pulse.App.ViewModels;

/// <summary>
/// Root view model of the popover. Owns the tab list, the five tab view models and the header state.
/// Showing the popover is surfaced as an event that <see cref="App"/> wires to the window; quitting and elevation are
/// agent commands (the agent's Exiting message then ends this process).
/// </summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly AgentConnection _agent;
    private readonly ILogger<MainViewModel> _log;

    [ObservableProperty] private MainTab _selectedTab = MainTab.Devices;
    [ObservableProperty] private TabViewModelBase _activeViewModel;
    [ObservableProperty] private string _statusText = Strings.StatusStarting;
    [ObservableProperty] private BccTone _statusTone = BccTone.Neutral;
    [ObservableProperty] private string? _statusDetail;
    [ObservableProperty] private string _lowestBatteryText = Strings.NoBatteryDevices;
    [ObservableProperty] private string _lastUpdatedText = string.Empty;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isPopoverVisible;

    public MainViewModel(
        AgentConnection agent,
        AppOptions options,
        DevicesViewModel devices,
        SystemViewModel system,
        LightingViewModel lighting,
        FansViewModel fans,
        SettingsViewModel settingsTab,
        ILogger<MainViewModel> log)
    {
        _agent = agent;
        _log = log;
        Options = options;
        DevicesTab = devices;
        SystemTab = system;
        LightingTab = lighting;
        FansTab = fans;
        SettingsTab = settingsTab;

        Tabs = new[]
        {
            new MainTabItem(MainTab.Devices, MainTabItem.TitleOf(MainTab.Devices), MainTabItem.IconKeyOf(MainTab.Devices)),
            new MainTabItem(MainTab.System, MainTabItem.TitleOf(MainTab.System), MainTabItem.IconKeyOf(MainTab.System)),
            new MainTabItem(MainTab.Lighting, MainTabItem.TitleOf(MainTab.Lighting), MainTabItem.IconKeyOf(MainTab.Lighting)),
            new MainTabItem(MainTab.Fans, MainTabItem.TitleOf(MainTab.Fans), MainTabItem.IconKeyOf(MainTab.Fans)),
            new MainTabItem(MainTab.Settings, MainTabItem.TitleOf(MainTab.Settings), MainTabItem.IconKeyOf(MainTab.Settings)),
        };
        foreach (var tab in Tabs)
        {
            tab.PropertyChanged += OnTabItemPropertyChanged;
        }

        foreach (var tab in AllTabs)
        {
            tab.ElevateCommand = ElevateCommand;
        }

        _activeViewModel = devices;
        devices.Activate();
        Tabs[0].IsSelected = true;

        _agent.PropertyChanged += OnAgentPropertyChanged;
        _agent.BatteriesUpdated += (_, _) =>
        {
            UpdateStatus();
            UpdateBatterySummary();
        };
        _agent.LowBattery += OnLowBattery;
        UpdateStatus();
        UpdateBatterySummary();
    }

    // ---- Tabs ----
    public IReadOnlyList<MainTabItem> Tabs { get; }
    public DevicesViewModel DevicesTab { get; }
    public SystemViewModel SystemTab { get; }
    public LightingViewModel LightingTab { get; }
    public FansViewModel FansTab { get; }
    public SettingsViewModel SettingsTab { get; }

    private IEnumerable<TabViewModelBase> AllTabs => new TabViewModelBase[] { DevicesTab, SystemTab, LightingTab, FansTab, SettingsTab };

    // ---- Shared state ----
    public AgentConnection Agent => _agent;
    public AppSettings Settings => _agent.Settings;
    public AppOptions Options { get; }

    // ---- Elevation (from the agent's Info; notified when it changes) ----
    public bool IsElevated => _agent.Info.IsElevated;
    public bool CanElevate => _agent.Info.CanElevate;
    /// <summary>True when the "run as administrator" action should be offered (not elevated but possible).</summary>
    public bool ShowElevateAction => !IsElevated && CanElevate;
    public string ElevationBadgeText => IsElevated ? Strings.ElevatedBadge : Strings.NotElevatedBadge;
    public bool IsDemo => Options.Demo || _agent.IsDemo;

    /// <summary>Raised when something (OpenSettingsCommand, ShowPopoverCommand) wants the popover shown.</summary>
    public event EventHandler? ShowPopoverRequested;

    /// <summary>Raised on the UI thread when the agent reports a low battery (live, or queued while no popover was open).</summary>
    public event EventHandler<LowBatteryEventArgs>? LowBatteryDetected;

    public TabViewModelBase ViewModelFor(MainTab tab) => tab switch
    {
        MainTab.System => SystemTab,
        MainTab.Lighting => LightingTab,
        MainTab.Fans => FansTab,
        MainTab.Settings => SettingsTab,
        _ => DevicesTab,
    };

    /// <summary>Fires every armed debounce timer (fan / RGB sliders) so the last drag reaches the agent before exit.</summary>
    public Task FlushPendingAsync() => Task.WhenAll(AllTabs.Select(t => t.FlushPendingAsync()));

    partial void OnSelectedTabChanged(MainTab oldValue, MainTab newValue)
    {
        var next = ViewModelFor(newValue);
        if (!ReferenceEquals(next, ActiveViewModel))
        {
            ActiveViewModel.Deactivate();
            ActiveViewModel = next;
            next.Activate();
        }

        foreach (var item in Tabs)
        {
            item.IsSelected = item.Tab == newValue;
        }
        _log.LogDebug("Tab → {Tab}", newValue);
    }

    /// <summary>The window reports its visibility here; the agent streams hardware frames (and polls at 1 s) only while visible.</summary>
    partial void OnIsPopoverVisibleChanged(bool value) => _agent.SetUiVisible(value);

    private void OnTabItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainTabItem.IsSelected) && sender is MainTabItem { IsSelected: true } item)
        {
            SelectedTab = item.Tab;
        }
    }

    [RelayCommand]
    private void SelectTab(MainTab tab) => SelectedTab = tab;

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            var result = await _agent.RefreshNowAsync();
            if (!result.Ok) _log.LogWarning("Manual refresh failed: {Error}", result.Error);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        SelectedTab = MainTab.Settings;
        ShowPopoverRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowPopover() => ShowPopoverRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Popover 結束: the agent quits (fans handed back first) and its Exiting message closes this process.</summary>
    [RelayCommand]
    private void Quit() => _agent.RequestQuit();

    /// <summary>
    /// Asks the agent to restart itself elevated (UAC). On success the agent exits with Relaunch, this popover exits on
    /// that, and the new agent opens a fresh popover.
    /// </summary>
    [RelayCommand(CanExecute = nameof(ShowElevateAction))]
    private async Task ElevateAsync()
    {
        var result = await _agent.ElevateAsync(showUi: true);
        if (result.Ok)
        {
            _log.LogInformation("Elevated agent started; waiting for this one to exit");
            return;
        }

        _log.LogWarning("Elevation failed: {Error}", result.Error);
        StatusText = string.IsNullOrWhiteSpace(result.Error) ? Strings.StatusElevationFailed : result.Error!;
        StatusTone = BccTone.Warning;
    }

    private void OnLowBattery(object? sender, LowBatteryToast toast)
    {
        _log.LogInformation("Low battery toast: {Name} {Percent}%", toast.Device.Name, toast.Device.Percent);
        LowBatteryDetected?.Invoke(this, new LowBatteryEventArgs(toast.Device, toast.Title, toast.Message));
    }

    private void OnAgentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AgentConnection.HardwareStatus):
            case nameof(AgentConnection.ConnectionError):
                UpdateStatus();
                break;
            case nameof(AgentConnection.Info):
                OnPropertyChanged(nameof(IsElevated));
                OnPropertyChanged(nameof(CanElevate));
                OnPropertyChanged(nameof(ShowElevateAction));
                OnPropertyChanged(nameof(ElevationBadgeText));
                OnPropertyChanged(nameof(IsDemo));
                ElevateCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    private void UpdateStatus()
    {
        if (_agent.ConnectionError is { } connectionError)
        {
            StatusText = connectionError;
            StatusTone = BccTone.Danger;
            StatusDetail = null;
            return;
        }

        var status = _agent.HardwareStatus;
        var (text, tone) = status.State switch
        {
            HardwareMonitorState.NotStarted or HardwareMonitorState.Initializing => (Strings.StatusStarting, BccTone.Accent),
            HardwareMonitorState.Ready => (Strings.StatusReady, BccTone.Success),
            HardwareMonitorState.Degraded => (Strings.StatusDegraded, BccTone.Warning),
            HardwareMonitorState.Failed => (Strings.StatusFailed, BccTone.Danger),
            _ => (Strings.StatusUnsupported, BccTone.Neutral),
        };

        if (_agent.Batteries.LastError is { } error)
        {
            tone = tone == BccTone.Danger ? tone : BccTone.Warning;
            StatusDetail = error;
        }
        else
        {
            StatusDetail = status.Message;
        }

        StatusText = text;
        StatusTone = tone;
    }

    private void UpdateBatterySummary()
    {
        LowestBatteryText = _agent.LowestBattery is { } lowest
            ? string.Format(Strings.LowestBatteryFormat, lowest.Name, lowest.Percent)
            : Strings.NoBatteryDevices;
        LastUpdatedText = _agent.Batteries.LastUpdate is { } t
            ? string.Format(Strings.StatusUpdatedFormat, t.ToString("HH:mm:ss"))
            : string.Empty;
    }
}

/// <summary>Payload of <see cref="MainViewModel.LowBatteryDetected"/>.</summary>
public sealed class LowBatteryEventArgs : EventArgs
{
    public LowBatteryEventArgs(BatteryDevice device, string title, string message)
    {
        Device = device;
        Title = title;
        Message = message;
    }

    public BatteryDevice Device { get; }
    public string Title { get; }
    public string Message { get; }
}
