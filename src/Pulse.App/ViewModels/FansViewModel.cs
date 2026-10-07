using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using Pulse.App.Controls;
using Pulse.App.Localization;
using Pulse.App.Services;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Pulse.App.ViewModels;

/// <summary>風扇 tab: fans grouped by <see cref="FanInfo.Group"/>, updated in place from <c>Monitoring.HardwareUpdated</c>; control via <see cref="Hardware"/>.SetFanAsync.</summary>
public sealed partial class FansViewModel : TabViewModelBase
{
    private readonly ILogger<FansViewModel> _log;

    [ObservableProperty] private bool _showBanner;
    [ObservableProperty] private string _bannerText = Strings.FanControlUnavailable;
    [ObservableProperty] private bool _showElevate;
    [ObservableProperty] private bool _hasFans;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _emptyHint = Strings.FansEmptyHint;

    public FansViewModel(MonitoringService monitoring, SettingsService settings, IHardwareMonitor hardware, IElevationService elevation, ILogger<FansViewModel> log)
        : base(MainTab.Fans, monitoring, settings)
    {
        Hardware = hardware;
        Elevation = elevation;
        _log = log;

        Monitoring.HardwareUpdated += (_, snapshot) => Sync(snapshot.Fans);
        Monitoring.PropertyChanged += OnMonitoringPropertyChanged;
        ApplyStatus(Monitoring.HardwareStatus);
        Sync(Monitoring.LatestHardware.Fans);
    }

    public IHardwareMonitor Hardware { get; }

    public IElevationService Elevation { get; }

    public ObservableCollection<FanGroupViewModel> Groups { get; } = new();

    public override string PlaceholderText => Strings.PlaceholderFans;

    private void OnMonitoringPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitoringService.HardwareStatus)) ApplyStatus(Monitoring.HardwareStatus);
    }

    private void ApplyStatus(HardwareMonitorStatus status)
    {
        var ready = status.State is HardwareMonitorState.Ready or HardwareMonitorState.Degraded;
        ShowBanner = !ready || !status.FanControlAvailable;
        ShowElevate = !Elevation.IsElevated && Elevation.CanElevate;
        BannerText = !string.IsNullOrWhiteSpace(status.Message)
            ? status.Message!
            : status.FanControlAvailable ? Strings.ElevateHint : Strings.FanControlUnavailable;
        EmptyHint = status.State switch
        {
            HardwareMonitorState.Ready or HardwareMonitorState.Degraded => Strings.NoFans,
            HardwareMonitorState.Failed or HardwareMonitorState.Unsupported => status.Message ?? Strings.StatusUnsupported,
            _ => Strings.FansEmptyHint,
        };
    }

    private void Sync(IReadOnlyList<FanInfo> fans)
    {
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
                    if (!existing.TryGetValue(fan.Id, out var item)) item = new FanItemViewModel(fan.Id, Hardware, _log);
                    item.Update(fan);
                    group.Fans.Add(item);
                }
                Groups.Add(group);
            }
            _log.LogDebug("Fan layout: {Groups}", string.Join(" | ", grouped.Select(g => $"{g.Key}: {g.Count()}")));
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

/// <summary>One fan card. Manual toggle + debounced (300 ms) slider → <see cref="IHardwareMonitor.SetFanAsync"/>.</summary>
public sealed partial class FanItemViewModel : ObservableObject
{
    private static readonly TimeSpan SliderDebounce = TimeSpan.FromMilliseconds(300);

    private readonly IHardwareMonitor _hardware;
    private readonly ILogger _log;
    private readonly DispatcherTimer _timer;
    private bool _syncing;

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

    public FanItemViewModel(string id, IHardwareMonitor hardware, ILogger log)
    {
        Id = id;
        _hardware = hardware;
        _log = log;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SliderDebounce };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (IsManual) _ = ApplyAsync(SliderValue);
        };
    }

    public string Id { get; }

    public void Update(FanInfo fan)
    {
        _syncing = true;
        try
        {
            Name = fan.Name;
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
    }

    partial void OnErrorChanged(string? value) => HasError = !string.IsNullOrWhiteSpace(value);

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
        _ = ApplyAsync(value ? SliderValue : null);
    }

    [RelayCommand]
    private void Preset(double percent)
    {
        if (!CanControl) return;
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
            await Task.Run(() => _hardware.SetFanAsync(Id, percent));
            _log.LogInformation("Fan {Fan} → {Percent}", Name, percent is { } p ? $"{p:0}%" : "auto");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SetFan {Fan} failed", Name);
            Error = ex.Message;
            if (percent is not null)
            {
                _syncing = true;
                IsManual = false;
                _syncing = false;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
