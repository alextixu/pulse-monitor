using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Pulse.App.Controls;
using Pulse.App.Converters;
using Pulse.App.Localization;
using Pulse.App.Services;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Pulse.Rgb;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Pulse.App.ViewModels;

/// <summary>燈光 tab: OpenRGB status, global colour presets and one card per RGB device.</summary>
public sealed partial class LightingViewModel : TabViewModelBase
{
    /// <summary>Preset palette shared by the global row, device cards and zones.</summary>
    public static readonly IReadOnlyList<string> PresetHexes = new[]
    {
        "#FF3B30", "#FF9F0A", "#FFD60A", "#34C759", "#30D5C8", "#0A84FF", "#5E5CE6", "#AF52DE", "#FF375F", "#FFFFFF",
    };

    private readonly ILogger<LightingViewModel> _log;
    private int _loadVersion;

    [ObservableProperty] private string _statusText = Strings.RgbDisconnected;
    [ObservableProperty] private BccTone _statusTone = BccTone.Neutral;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _hasStatusMessage;
    [ObservableProperty] private string _hostPortText = string.Empty;
    [ObservableProperty] private string? _serverVersionText;
    [ObservableProperty] private bool _hasServerVersion;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLoadingDevices;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _hasDevices;
    [ObservableProperty] private bool _showNoDevices;
    [ObservableProperty] private Color _customColor = Color.FromRgb(0x0A, 0x84, 0xFF);

    public LightingViewModel(MonitoringService monitoring, SettingsService settings, IRgbController rgb, ILogger<LightingViewModel> log)
        : base(MainTab.Lighting, monitoring, settings)
    {
        Rgb = rgb;
        _log = log;
        Presets = PresetHexes.Select(h => new ColorSwatchItem(RgbColor.FromHex(h), ApplyToAllCommand)).ToList();
        Monitoring.PropertyChanged += OnMonitoringPropertyChanged;
        ApplyStatus(Monitoring.RgbStatus);
    }

    public IRgbController Rgb { get; }

    public IReadOnlyList<ColorSwatchItem> Presets { get; }

    public ObservableCollection<RgbDeviceViewModel> Devices { get; } = new();

    public override string PlaceholderText => Strings.PlaceholderLighting;

    protected override void OnActivated()
    {
        if (IsConnected && Devices.Count == 0 && !IsLoadingDevices) _ = LoadDevicesAsync();
    }

    partial void OnErrorChanged(string? value) => HasError = !string.IsNullOrWhiteSpace(value);

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ReconnectAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            var ok = await Monitoring.ConnectRgbAsync();
            if (ok) await LoadDevicesAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenDownload()
    {
        try
        {
            Process.Start(new ProcessStartInfo(OpenRgbInfo.DownloadUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open {Url}", OpenRgbInfo.DownloadUrl);
            Error = $"{Strings.OpenFailed}：{OpenRgbInfo.DownloadUrl}";
        }
    }

    [RelayCommand]
    private Task ApplyToAllAsync(RgbColor color) => RunAsync(async () =>
    {
        await Task.Run(() => Rgb.SetAllAsync(color));
        foreach (var device in Devices) device.ReflectColor(color);
        _log.LogInformation("RGB: all devices → {Color}", color);
    });

    [RelayCommand]
    private Task ApplyCustomToAllAsync() => ApplyToAllAsync(new RgbColor(CustomColor.R, CustomColor.G, CustomColor.B));

    [RelayCommand]
    private Task TurnOffAllAsync() => RunAsync(async () =>
    {
        await Task.Run(() => Rgb.TurnOffAllAsync());
        await SyncDevicesAsync();
        _log.LogInformation("RGB: all devices off");
    });

    [RelayCommand]
    private Task RestoreAllDefaultsAsync() => RunAsync(async () =>
    {
        await Task.Run(() => Rgb.RestoreAllDefaultsAsync());
        await SyncDevicesAsync();
        _log.LogInformation("RGB: all devices restored to their defaults");
    });

    /// <summary>Re-reads the backend's cached device list (no server round trip) and updates every card.</summary>
    private async Task SyncDevicesAsync()
    {
        var devices = await Task.Run(() => Rgb.GetDevicesAsync(refresh: false));
        foreach (var vm in Devices)
        {
            if (devices.FirstOrDefault(d => d.Index == vm.Index) is { } device) vm.Sync(device);
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        Error = null;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RGB command failed");
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnMonitoringPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitoringService.RgbStatus)) ApplyStatus(Monitoring.RgbStatus);
    }

    private void ApplyStatus(RgbStatus status)
    {
        var wasConnected = IsConnected;
        IsConnected = status.State == RgbConnectionState.Connected;
        IsConnecting = status.State == RgbConnectionState.Connecting;
        StatusTone = BccTones.ForRgbState(status.State);
        StatusMessage = status.Message;
        HasStatusMessage = !string.IsNullOrWhiteSpace(status.Message);
        HostPortText = string.Format(Strings.HostPortFormat, status.Host, status.Port);
        HasServerVersion = !string.IsNullOrWhiteSpace(status.ServerVersion);
        ServerVersionText = HasServerVersion ? string.Format(Strings.RgbServerVersionFormat, status.ServerVersion) : null;
        UpdateStatusText();

        if (IsConnected && (!wasConnected || Devices.Count == 0))
        {
            _ = LoadDevicesAsync();
        }
        else if (!IsConnected && status.State != RgbConnectionState.Connecting)
        {
            _loadVersion++;
            Devices.Clear();
            HasDevices = false;
            ShowNoDevices = false;
        }
    }

    private void UpdateStatusText()
    {
        StatusText = Monitoring.RgbStatus.State switch
        {
            RgbConnectionState.Connected => string.Format(Strings.RgbConnectedFormat, Devices.Count),
            RgbConnectionState.Connecting => Strings.RgbConnecting,
            RgbConnectionState.Error => Strings.RgbError,
            _ => Strings.RgbDisconnected,
        };
    }

    private async Task LoadDevicesAsync()
    {
        var version = ++_loadVersion;
        IsLoadingDevices = true;
        try
        {
            var devices = await Task.Run(() => Rgb.GetDevicesAsync(refresh: true));
            if (version != _loadVersion) return;

            Devices.Clear();
            foreach (var device in devices)
            {
                Devices.Add(new RgbDeviceViewModel(device, Rgb, _log));
            }
            HasDevices = Devices.Count > 0;
            ShowNoDevices = IsConnected && Devices.Count == 0;
            _log.LogInformation("RGB devices: {Count} ({Names})", devices.Count, string.Join(", ", devices.Select(d => d.Name)));
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return;
            _log.LogWarning(ex, "Loading RGB devices failed");
            Error = ex.Message;
        }
        finally
        {
            if (version == _loadVersion) IsLoadingDevices = false;
            UpdateStatusText();
        }
    }
}

/// <summary>A colour swatch button: the colour, its brush and the command to run with the colour as parameter.</summary>
public sealed class ColorSwatchItem
{
    public ColorSwatchItem(RgbColor color, ICommand command)
    {
        Color = color;
        Command = command;
        Brush = new SolidColorBrush(Avalonia.Media.Color.FromRgb(color.R, color.G, color.B));
        Hex = color.ToHex();
    }

    public RgbColor Color { get; }
    public IBrush Brush { get; }
    public string Hex { get; }
    public ICommand Command { get; }
}

/// <summary>One RGB device card. Mode / brightness / speed changes are applied through <see cref="IRgbController.SetModeAsync"/> (sliders debounced 250 ms).</summary>
public sealed partial class RgbDeviceViewModel : ObservableObject
{
    private static readonly TimeSpan SliderDebounce = TimeSpan.FromMilliseconds(250);

    private readonly IRgbController _rgb;
    private readonly ILogger _log;
    private readonly DispatcherTimer _sliderTimer;
    private bool _syncing;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _typeText = string.Empty;
    [ObservableProperty] private Geometry? _icon;
    [ObservableProperty] private string _ledCountText = string.Empty;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private RgbMode? _selectedMode;
    [ObservableProperty] private RgbColor _currentColor;
    [ObservableProperty] private IBrush _currentBrush = Brushes.Transparent;
    [ObservableProperty] private string _currentHex = string.Empty;
    [ObservableProperty] private bool _showColor = true;
    [ObservableProperty] private bool _showBrightness;
    [ObservableProperty] private bool _showSpeed;
    [ObservableProperty] private double _brightness = 100;
    [ObservableProperty] private double _brightnessMin;
    [ObservableProperty] private double _brightnessMax = 100;
    [ObservableProperty] private double _speed = 50;
    [ObservableProperty] private double _speedMin;
    [ObservableProperty] private double _speedMax = 100;
    [ObservableProperty] private string _brightnessText = string.Empty;
    [ObservableProperty] private string _speedText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _hasZones;
    [ObservableProperty] private string _zonesHeader = Strings.Zones;
    [ObservableProperty] private Color _customColor;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RestoreDefaultCommand))] private bool _canRestore = true;
    [ObservableProperty] private bool _showRestoreHint;
    [ObservableProperty] private string _restoreTip = Strings.RestoreDefaultTip;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private bool _hasNotice;

    private readonly RgbMode? _fallbackEffect;

    public RgbDeviceViewModel(RgbDevice device, IRgbController rgb, ILogger log)
    {
        _rgb = rgb;
        _log = log;
        Index = device.Index;
        Modes = device.Modes;
        _fallbackEffect = RgbModeRules.FindFirmwareEffect(device.Modes);
        Presets = LightingViewModel.PresetHexes.Select(h => new ColorSwatchItem(RgbColor.FromHex(h), SetColorCommand)).ToList();
        Zones = device.Zones.Select(z => new RgbZoneViewModel(z, SetZoneColorCommand)).ToList();
        _sliderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SliderDebounce };
        _sliderTimer.Tick += (_, _) => { _sliderTimer.Stop(); _ = ApplyModeAsync(); };

        _syncing = true;
        Name = device.Name;
        TypeText = Strings.RgbTypeName(device.Type);
        Icon = ResourceLookup.Icon(RgbDeviceTypeToIconConverter.KeyOf(device.Type));
        LedCountText = string.Format(Strings.LedCountFormat, device.LedCount);
        Description = device.Description;
        HasZones = Zones.Count > 0;
        ZonesHeader = string.Format(Strings.ZonesFormat, Zones.Count);
        ReflectColor(device.Colors.Count > 0 ? device.Colors[0] : RgbColor.FromHex("#0A84FF"));
        SelectedMode = Modes.FirstOrDefault(m => m.Index == device.ActiveModeIndex) ?? Modes.FirstOrDefault();
        ApplyModeCapabilities(SelectedMode);
        _syncing = false;
        UpdateRestoreAvailability();
    }

    public int Index { get; }

    public IReadOnlyList<RgbMode> Modes { get; }

    public IReadOnlyList<ColorSwatchItem> Presets { get; }

    public IReadOnlyList<RgbZoneViewModel> Zones { get; }

    /// <summary>Updates the swatch without talking to the backend (after "apply to all").</summary>
    public void ReflectColor(RgbColor color)
    {
        CurrentColor = color;
        CurrentBrush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        CurrentHex = color.ToHex();
        CustomColor = Color.FromRgb(color.R, color.G, color.B);
    }

    /// <summary>Mirrors the backend's view of the device (active mode, colour) without re-applying anything.</summary>
    public void Sync(RgbDevice device)
    {
        _sliderTimer.Stop();
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            if (device.Colors.Count > 0) ReflectColor(device.Colors[0]);
            SelectedMode = Modes.FirstOrDefault(m => m.Index == device.ActiveModeIndex) ?? SelectedMode;
            ApplyModeCapabilities(SelectedMode);
        }
        finally
        {
            _syncing = wasSyncing;
        }

        UpdateRestoreAvailability();
    }

    partial void OnErrorChanged(string? value) => HasError = !string.IsNullOrWhiteSpace(value);

    partial void OnNoticeChanged(string? value) => HasNotice = !string.IsNullOrWhiteSpace(value);

    partial void OnSelectedModeChanged(RgbMode? value)
    {
        ApplyModeCapabilities(value);
        if (_syncing || value is null) return;
        _sliderTimer.Stop();
        _ = ApplyModeAsync();
    }

    partial void OnBrightnessChanged(double value)
    {
        BrightnessText = $"{value:0}";
        if (_syncing) return;
        _sliderTimer.Stop();
        _sliderTimer.Start();
    }

    partial void OnSpeedChanged(double value)
    {
        SpeedText = $"{value:0}";
        if (_syncing) return;
        _sliderTimer.Stop();
        _sliderTimer.Start();
    }

    [RelayCommand]
    private Task SetColorAsync(RgbColor color) => RunAsync(async () =>
    {
        await Task.Run(() => _rgb.SetDeviceColorAsync(Index, color));
        ReflectColor(color);
        _log.LogInformation("RGB {Device} → {Color}", Name, color);
    });

    [RelayCommand]
    private Task ApplyCustomAsync() => SetColorAsync(new RgbColor(CustomColor.R, CustomColor.G, CustomColor.B));

    [RelayCommand]
    private Task SetZoneColorAsync(ZoneColorRequest request) => RunAsync(async () =>
    {
        await Task.Run(() => _rgb.SetZoneColorAsync(Index, request.ZoneIndex, request.Color));
        _log.LogInformation("RGB {Device} zone {Zone} → {Color}", Name, request.ZoneIndex, request.Color);
    });

    [RelayCommand]
    private Task TurnOffAsync() => RunAsync(async () =>
    {
        await Task.Run(() => _rgb.TurnOffAsync(Index));
        await RefreshFromBackendAsync();
        Notice = Strings.TurnedOff;
        _log.LogInformation("RGB {Device} off", Name);
    });

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreDefaultAsync() => RunAsync(async () =>
    {
        await Task.Run(() => _rgb.RestoreDefaultAsync(Index));
        await RefreshFromBackendAsync();
        Notice = Strings.RestoredDefault;
        _log.LogInformation("RGB {Device} restored to its default", Name);
    });

    [RelayCommand]
    private Task SaveAsDefaultAsync() => RunAsync(async () =>
    {
        await Task.Run(() => _rgb.SaveCurrentAsDefaultAsync(Index));
        await RefreshFromBackendAsync();
        Notice = _rgb.HasDefault(Index) ? Strings.SavedAsDefault : null;
        _log.LogInformation("RGB {Device}: current state saved as default", Name);
    });

    private async Task RefreshFromBackendAsync()
    {
        var devices = await Task.Run(() => _rgb.GetDevicesAsync(refresh: false));
        if (devices.FirstOrDefault(d => d.Index == Index) is { } device) Sync(device);
        else UpdateRestoreAvailability();
    }

    private void UpdateRestoreAvailability()
    {
        bool hasDefault;
        try
        {
            hasDefault = _rgb.HasDefault(Index);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "HasDefault failed for {Device}", Name);
            hasDefault = false;
        }

        CanRestore = hasDefault || _fallbackEffect is not null;
        ShowRestoreHint = !CanRestore;
        RestoreTip = hasDefault
            ? Strings.RestoreDefaultTip
            : _fallbackEffect is { } effect ? string.Format(Strings.RestoreFallbackFormat, effect.Name) : Strings.RestoreUnavailable;
    }

    private Task ApplyModeAsync()
    {
        var mode = SelectedMode;
        if (mode is null) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            await Task.Run(() => _rgb.SetModeAsync(
                Index,
                mode.Index,
                mode.SupportsColor ? CurrentColor : null,
                mode.SupportsSpeed ? (int)Math.Round(Speed) : null,
                mode.SupportsBrightness ? (int)Math.Round(Brightness) : null));
            _log.LogInformation("RGB {Device} mode → {Mode}", Name, mode.Name);
        });
    }

    private void ApplyModeCapabilities(RgbMode? mode)
    {
        var wasSyncing = _syncing;
        _syncing = true;
        ShowColor = mode?.SupportsColor ?? false;
        ShowBrightness = mode?.SupportsBrightness ?? false;
        ShowSpeed = mode?.SupportsSpeed ?? false;
        BrightnessMin = mode?.MinBrightness ?? 0;
        BrightnessMax = mode?.MaxBrightness is { } bmax && bmax > BrightnessMin ? bmax : Math.Max(BrightnessMin + 1, 100);
        SpeedMin = mode?.MinSpeed ?? 0;
        SpeedMax = mode?.MaxSpeed is { } smax && smax > SpeedMin ? smax : Math.Max(SpeedMin + 1, 100);
        Brightness = Math.Clamp(Brightness, BrightnessMin, BrightnessMax);
        Speed = Math.Clamp(Speed, SpeedMin, SpeedMax);
        BrightnessText = $"{Brightness:0}";
        SpeedText = $"{Speed:0}";
        _syncing = wasSyncing;
    }

    private async Task RunAsync(Func<Task> action)
    {
        Error = null;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RGB command for {Device} failed", Name);
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        if (value) Notice = null;
    }
}

/// <summary>Parameter of <see cref="RgbDeviceViewModel.SetZoneColorCommand"/>.</summary>
public sealed record ZoneColorRequest(int ZoneIndex, RgbColor Color);

/// <summary>One zone row inside a device card: name, LED count and a mini swatch row.</summary>
public sealed class RgbZoneViewModel
{
    public RgbZoneViewModel(RgbZone zone, ICommand setZoneColor)
    {
        Index = zone.Index;
        Name = zone.Name;
        LedCountText = string.Format(Strings.LedCountFormat, zone.LedCount);
        Swatches = LightingViewModel.PresetHexes
            .Take(8)
            .Select(h => new ZoneSwatchItem(zone.Index, RgbColor.FromHex(h), setZoneColor))
            .ToList();
    }

    public int Index { get; }
    public string Name { get; }
    public string LedCountText { get; }
    public IReadOnlyList<ZoneSwatchItem> Swatches { get; }
}

/// <summary>Swatch of a zone row; <see cref="Request"/> is the command parameter.</summary>
public sealed class ZoneSwatchItem
{
    public ZoneSwatchItem(int zoneIndex, RgbColor color, ICommand command)
    {
        Request = new ZoneColorRequest(zoneIndex, color);
        Brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        Hex = color.ToHex();
        Command = command;
    }

    public ZoneColorRequest Request { get; }
    public IBrush Brush { get; }
    public string Hex { get; }
    public ICommand Command { get; }
}
