using System.Collections.ObjectModel;
using Avalonia.Media;
using Pulse.App.Controls;
using Pulse.App.Converters;
using Pulse.Core.Localization;
using Pulse.App.Services;
using Pulse.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Pulse.App.ViewModels;

/// <summary>
/// 裝置 tab: one card per battery device (<see cref="AgentConnection.VisibleBatteries"/>); items are updated in place keyed by
/// device id. Hiding / showing devices patches <see cref="AppSettings.HiddenDeviceIds"/> (the card disappears at once:
/// the visible list is derived from the optimistic settings copy).
/// </summary>
public sealed partial class DevicesViewModel : TabViewModelBase
{
    private readonly ILogger<DevicesViewModel> _log;

    [ObservableProperty] private string _headerText = Strings.NoBatteryDevices;
    [ObservableProperty] private string _updatedText = string.Empty;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private int _hiddenCount;
    [ObservableProperty] private string _showHiddenText = string.Empty;

    public DevicesViewModel(AgentConnection agent, ILogger<DevicesViewModel> log)
        : base(MainTab.Devices, agent)
    {
        _log = log;
        Agent.BatteriesUpdated += (_, _) => Sync(Agent.VisibleBatteries);
        Sync(Agent.VisibleBatteries);
    }

    public ObservableCollection<DeviceItemViewModel> Devices { get; } = new();

    public bool HasHidden => HiddenCount > 0;

    protected override void OnActivated()
    {
        // Showing the popover already makes the agent refresh batteries (at most every 5 s); nothing to do per tab.
        _log.LogDebug("Devices tab activated");
    }

    [RelayCommand]
    private void ShowHidden()
    {
        if (Agent.Settings.HiddenDeviceIds.Count == 0) return;
        _ = Agent.UpdateSettingAsync(SettingsPatch.Of(nameof(AppSettings.HiddenDeviceIds), Array.Empty<string>()));
        _log.LogInformation("Hidden device list cleared");
    }

    private void Hide(string deviceId)
    {
        var hidden = Agent.Settings.HiddenDeviceIds;
        if (hidden.Contains(deviceId)) return;
        _ = Agent.UpdateSettingAsync(SettingsPatch.Of(nameof(AppSettings.HiddenDeviceIds), hidden.Append(deviceId).ToList()));
        _log.LogInformation("Device {Id} hidden", deviceId);
    }

    partial void OnHiddenCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasHidden));
        ShowHiddenText = string.Format(Strings.ShowHiddenDevicesFormat, value);
    }

    /// <summary>Updates existing items in place; rebuilds the collection only when the id sequence changed.</summary>
    private void Sync(IReadOnlyList<BatteryDevice> devices)
    {
        var sameOrder = devices.Count == Devices.Count;
        if (sameOrder)
        {
            for (var i = 0; i < devices.Count; i++)
            {
                if (Devices[i].Id != devices[i].Id) { sameOrder = false; break; }
            }
        }

        if (sameOrder)
        {
            for (var i = 0; i < devices.Count; i++) Devices[i].Update(devices[i]);
        }
        else
        {
            var existing = Devices.ToDictionary(d => d.Id);
            Devices.Clear();
            foreach (var device in devices)
            {
                if (existing.TryGetValue(device.Id, out var item)) item.Update(device);
                else item = new DeviceItemViewModel(device, Hide);
                Devices.Add(item);
            }
        }

        IsEmpty = devices.Count == 0;
        var hiddenIds = Agent.Settings.HiddenDeviceIds;
        HiddenCount = hiddenIds.Count == 0 ? 0 : Agent.Batteries.All.Count(d => hiddenIds.Contains(d.Id));

        var lowest = Agent.LowestBattery;
        HeaderText = devices.Count == 0
            ? Strings.NoBatteryDevices
            : string.Format(Strings.DeviceCountFormat, devices.Count, lowest is { } l ? $"{l.Percent}%" : Strings.NotAvailable);
        UpdatedText = Agent.Batteries.LastUpdate is { } at
            ? string.Format(Strings.StatusUpdatedFormat, at.ToString("HH:mm:ss"))
            : string.Empty;
    }
}

/// <summary>One battery card. All properties are refreshed by <see cref="Update"/> on the UI thread.</summary>
public sealed partial class DeviceItemViewModel : ObservableObject
{
    private readonly Action<string> _hide;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private int? _percent;
    [ObservableProperty] private double _ringValue;
    [ObservableProperty] private string _percentText = Strings.NotAvailable;
    [ObservableProperty] private bool _hasPercent;
    [ObservableProperty] private bool _isCharging;
    [ObservableProperty] private bool _isConnected = true;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private BccTone _statusTone = BccTone.Neutral;
    [ObservableProperty] private BccTone _levelTone = BccTone.Neutral;
    [ObservableProperty] private string _detail = string.Empty;
    [ObservableProperty] private Geometry? _kindIcon;
    [ObservableProperty] private Geometry? _connectionIcon;
    [ObservableProperty] private string _sourceText = string.Empty;
    [ObservableProperty] private string? _voltageText;

    public DeviceItemViewModel(BatteryDevice device, Action<string> hide)
    {
        Id = device.Id;
        _hide = hide;
        Update(device);
    }

    public string Id { get; }

    [RelayCommand]
    private void Hide() => _hide(Id);

    public void Update(BatteryDevice device)
    {
        Name = device.Name;
        Percent = device.Percent;
        HasPercent = device.Percent is not null;
        RingValue = device.Percent ?? 0;
        PercentText = device.Percent is { } p ? $"{p}%" : Strings.NotAvailable;
        IsCharging = device.Status == BatteryStatus.Charging && device.IsConnected;
        IsConnected = device.IsConnected;
        Detail = string.IsNullOrWhiteSpace(device.Detail) ? Strings.ConnectionTypeName(device.Connection) : device.Detail!;
        KindIcon = ResourceLookup.Icon(DeviceKindToIconConverter.KeyOf(device.Kind));
        ConnectionIcon = ResourceLookup.Icon(ConnectionTypeToIconConverter.KeyOf(device.Connection));
        SourceText = device.Source;
        VoltageText = device.VoltageMillivolts is { } mv ? $"{mv / 1000:0.00} V" : null;

        if (!device.IsConnected)
        {
            StatusText = Strings.DeviceDisconnected;
            StatusTone = BccTone.Neutral;
            LevelTone = BccTone.Neutral;
            return;
        }

        LevelTone = BccTones.ForBattery(device);
        (StatusText, StatusTone) = device.Status switch
        {
            BatteryStatus.Charging => (Strings.DeviceCharging, BccTone.Accent),
            BatteryStatus.Full => (Strings.DeviceFull, BccTone.Success),
            BatteryStatus.Critical => (Strings.DeviceCritical, BccTone.Danger),
            BatteryStatus.Low => (Strings.DeviceLow, BccTone.Warning),
            BatteryStatus.NotCharging => (Strings.DeviceNotCharging, BccTone.Neutral),
            _ when device.Percent is null => (Strings.DeviceSleeping, BccTone.Neutral),
            _ when device.Percent < 10 => (Strings.DeviceCritical, BccTone.Danger),
            _ when device.Percent < 20 => (Strings.DeviceLow, BccTone.Warning),
            _ => (Strings.DeviceGood, BccTone.Success),
        };
    }
}
