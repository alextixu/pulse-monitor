using System.ComponentModel;
using Pulse.App.Controls;
using Pulse.App.Localization;
using Pulse.App.Services;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Pulse.App.ViewModels;

/// <summary>
/// Root view model of the popover. Owns the tab list, the five tab view models and the header state.
/// Window-level actions (show popover, quit) are surfaced as events that <see cref="App"/> wires to the lifetime.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly MonitoringService _monitoring;
    private readonly SettingsService _settings;
    private readonly IElevationService _elevation;
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

    /// <summary>Devices that already produced a low-battery toast this session (one per device).</summary>
    private readonly HashSet<string> _notifiedLowBattery = new();

    public MainViewModel(
        MonitoringService monitoring,
        SettingsService settings,
        IElevationService elevation,
        AppOptions options,
        DevicesViewModel devices,
        SystemViewModel system,
        LightingViewModel lighting,
        FansViewModel fans,
        SettingsViewModel settingsTab,
        ILogger<MainViewModel> log)
    {
        _monitoring = monitoring;
        _settings = settings;
        _elevation = elevation;
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

        foreach (var tab in new TabViewModelBase[] { devices, system, lighting, fans, settingsTab })
        {
            tab.ElevateCommand = ElevateCommand;
        }

        _activeViewModel = devices;
        devices.Activate();
        Tabs[0].IsSelected = true;

        _monitoring.PropertyChanged += OnMonitoringPropertyChanged;
        _monitoring.BatteriesUpdated += OnBatteriesUpdated;
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

    // ---- Shared services ----
    public MonitoringService Monitoring => _monitoring;
    public SettingsService SettingsService => _settings;
    public AppSettings Settings => _settings.Settings;
    public AppOptions Options { get; }

    // ---- Elevation ----
    public bool IsElevated => _elevation.IsElevated;
    public bool CanElevate => _elevation.CanElevate;
    /// <summary>True when the "run as administrator" action should be offered (not elevated but possible).</summary>
    public bool ShowElevateAction => !IsElevated && CanElevate;
    public string ElevationBadgeText => IsElevated ? Strings.ElevatedBadge : Strings.NotElevatedBadge;
    public bool IsDemo => Options.Demo;

    /// <summary>Raised when something (tray menu, OpenSettingsCommand, second instance) wants the popover shown.</summary>
    public event EventHandler? ShowPopoverRequested;

    /// <summary>Raised when the application should shut down (QuitCommand, successful elevation relaunch).</summary>
    public event EventHandler? QuitRequested;

    /// <summary>Raised on the UI thread (once per device per session) when a device drops to the low-battery threshold.</summary>
    public event EventHandler<LowBatteryEventArgs>? LowBatteryDetected;

    public TabViewModelBase ViewModelFor(MainTab tab) => tab switch
    {
        MainTab.System => SystemTab,
        MainTab.Lighting => LightingTab,
        MainTab.Fans => FansTab,
        MainTab.Settings => SettingsTab,
        _ => DevicesTab,
    };

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
            await _monitoring.RefreshNowAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Manual refresh failed");
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

    [RelayCommand]
    private void Quit() => QuitRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(ShowElevateAction))]
    private void Elevate()
    {
        try
        {
            if (_elevation.RelaunchElevated())
            {
                _log.LogInformation("Elevated instance started; shutting this one down");
                QuitRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Elevation relaunch failed");
        }

        StatusText = Strings.StatusElevationFailed;
        StatusTone = BccTone.Warning;
    }

    private void OnBatteriesUpdated(object? sender, IReadOnlyList<BatteryDevice> devices)
    {
        var settings = _settings.Settings;
        if (!settings.NotifyOnLowBattery) return;
        var threshold = settings.LowBatteryThresholdPercent;
        var hidden = settings.HiddenDeviceIds;

        foreach (var device in devices)
        {
            if (!device.IsConnected || device.Percent is not { } pct) continue;
            if (hidden.Contains(device.Id)) continue;
            if (device.Status is BatteryStatus.Charging or BatteryStatus.Full) continue;
            if (pct > threshold) continue;
            if (!_notifiedLowBattery.Add(device.Id)) continue;

            _log.LogInformation("Low battery: {Name} {Percent}% (threshold {Threshold}%)", device.Name, pct, threshold);
            LowBatteryDetected?.Invoke(this, new LowBatteryEventArgs(device, Strings.LowBatteryTitle, string.Format(Strings.LowBatteryToastFormat, device.Name, pct)));
        }
    }

    private void OnMonitoringPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MonitoringService.HardwareStatus):
            case nameof(MonitoringService.LastBatteryError):
            case nameof(MonitoringService.IsRefreshingBatteries):
            case nameof(MonitoringService.IsRefreshingHardware):
                UpdateStatus();
                break;
            case nameof(MonitoringService.LowestBattery):
            case nameof(MonitoringService.VisibleBatteries):
            case nameof(MonitoringService.LastBatteryUpdate):
                UpdateBatterySummary();
                break;
        }
    }

    private void UpdateStatus()
    {
        var status = _monitoring.HardwareStatus;
        var (text, tone) = status.State switch
        {
            HardwareMonitorState.NotStarted or HardwareMonitorState.Initializing => (Strings.StatusStarting, BccTone.Accent),
            HardwareMonitorState.Ready => (Strings.StatusReady, BccTone.Success),
            HardwareMonitorState.Degraded => (Strings.StatusDegraded, BccTone.Warning),
            HardwareMonitorState.Failed => (Strings.StatusFailed, BccTone.Danger),
            _ => (Strings.StatusUnsupported, BccTone.Neutral),
        };

        if (_monitoring.LastBatteryError is { } error)
        {
            tone = tone == BccTone.Danger ? tone : BccTone.Warning;
            StatusDetail = error;
        }
        else
        {
            StatusDetail = status.Message;
        }

        if (_monitoring.IsRefreshingBatteries && _monitoring.IsRefreshingHardware)
        {
            text = Strings.StatusRefreshing;
        }

        StatusText = text;
        StatusTone = tone;
    }

    private void UpdateBatterySummary()
    {
        LowestBatteryText = _monitoring.LowestBattery is { } lowest
            ? string.Format(Strings.LowestBatteryFormat, lowest.Name, lowest.Percent)
            : Strings.NoBatteryDevices;
        LastUpdatedText = _monitoring.LastBatteryUpdate is { } t
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
