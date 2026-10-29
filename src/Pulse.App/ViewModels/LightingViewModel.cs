using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Pulse.App.Controls;
using Pulse.App.Converters;
using Pulse.Core.Localization;
using Pulse.App.Services;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Pulse.App.ViewModels;

/// <summary>
/// 燈光 tab: OpenRGB status, global colour presets and one card per RGB device. The device list (the only payload with
/// per-LED colours) is loaded lazily: when the tab is first shown, then again on <see cref="AgentConnection.RgbDevicesChanged"/>
/// or a (re)connect. Every command addresses devices by <see cref="RgbDevice.Key"/> and the reply carries the device list after it.
/// </summary>
public sealed partial class LightingViewModel : TabViewModelBase
{
    /// <summary>Preset palette shared by the global row, device cards and zones.</summary>
    public static readonly IReadOnlyList<string> PresetHexes = new[]
    {
        "#FF3B30", "#FF9F0A", "#FFD60A", "#34C759", "#30D5C8", "#0A84FF", "#5E5CE6", "#AF52DE", "#FF375F", "#FFFFFF",
    };

    private readonly ILogger<LightingViewModel> _log;
    private int _loadVersion;
    /// <summary>The tab was shown at least once: from then on device changes are followed.</summary>
    private bool _devicesWanted;

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

    public LightingViewModel(AgentConnection agent, ILogger<LightingViewModel> log)
        : base(MainTab.Lighting, agent)
    {
        _log = log;
        Presets = PresetHexes.Select(h => new ColorSwatchItem(RgbColor.FromHex(h), ApplyToAllCommand)).ToList();
        Agent.PropertyChanged += OnAgentPropertyChanged;
        Agent.RgbDevicesChanged += (_, _) => { if (IsConnected && _devicesWanted) _ = LoadDevicesAsync(); };
        ApplyStatus(Agent.RgbStatus);
    }

    public IReadOnlyList<ColorSwatchItem> Presets { get; }

    public ObservableCollection<RgbDeviceViewModel> Devices { get; } = new();

    protected override void OnActivated()
    {
        _devicesWanted = true;
        if (IsConnected && Devices.Count == 0 && !IsLoadingDevices) _ = LoadDevicesAsync();
    }

    /// <summary>Loads the device list now (screenshot runs capture the tab right after selecting it).</summary>
    public Task EnsureDevicesLoadedAsync()
    {
        _devicesWanted = true;
        return IsConnected && Devices.Count == 0 ? LoadDevicesAsync() : Task.CompletedTask;
    }

    public override Task FlushPendingAsync() => Task.WhenAll(Devices.Select(d => d.FlushPendingAsync()));

    partial void OnErrorChanged(string? value) => HasError = !string.IsNullOrWhiteSpace(value);

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ReconnectAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            var result = await Agent.ConnectRgbAsync();
            if (result.Ok)
            {
                _devicesWanted = true;
                await LoadDevicesAsync();
            }
            else if (!string.IsNullOrWhiteSpace(result.Error))
            {
                Error = result.Error;
            }
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
            Process.Start(new ProcessStartInfo(Strings.OpenRgbDownloadUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open {Url}", Strings.OpenRgbDownloadUrl);
            Error = $"{Strings.OpenFailed}：{Strings.OpenRgbDownloadUrl}";
        }
    }

    [RelayCommand]
    private Task ApplyToAllAsync(RgbColor color) => RunAsync(new RgbCommand { Kind = RgbCommandKind.SetColor, Color = color }, _ =>
    {
        foreach (var device in Devices) device.ReflectColor(color);
        _log.LogInformation("RGB: all devices → {Color}", color);
    });

    [RelayCommand]
    private Task ApplyCustomToAllAsync() => ApplyToAllAsync(new RgbColor(CustomColor.R, CustomColor.G, CustomColor.B));

    [RelayCommand]
    private Task TurnOffAllAsync() => RunAsync(new RgbCommand { Kind = RgbCommandKind.TurnOff }, devices =>
    {
        SyncDevices(devices);
        _log.LogInformation("RGB: all devices off");
    });

    [RelayCommand]
    private Task RestoreAllDefaultsAsync() => RunAsync(new RgbCommand { Kind = RgbCommandKind.RestoreDefault }, devices =>
    {
        SyncDevices(devices);
        _log.LogInformation("RGB: all devices restored to their defaults");
    });

    /// <summary>Mirrors the device list a command replied with into the cards (matched by key).</summary>
    private void SyncDevices(IReadOnlyList<RgbDevice> devices)
    {
        foreach (var vm in Devices)
        {
            if (RgbDeviceViewModel.Find(devices, vm.Key, vm.Index) is { } device) vm.Sync(device);
        }
    }

    /// <summary>Runs a command for every device (Key null); <paramref name="onSuccess"/> gets the device list after it.</summary>
    private async Task RunAsync(RgbCommand command, Action<IReadOnlyList<RgbDevice>> onSuccess)
    {
        Error = null;
        IsBusy = true;
        try
        {
            var result = await Agent.RgbCommandAsync(command);
            if (result.Ok)
            {
                onSuccess(result.Devices);
            }
            else
            {
                _log.LogWarning("RGB {Kind} (all devices) failed: {Error}", command.Kind, result.Error);
                Error = result.Error;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnAgentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentConnection.RgbStatus)) ApplyStatus(Agent.RgbStatus);
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

        if (IsConnected && _devicesWanted && (!wasConnected || Devices.Count == 0))
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
        StatusText = Agent.RgbStatus.State switch
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
            var result = await Agent.GetRgbDevicesAsync(refresh: true);
            if (version != _loadVersion) return;
            if (!result.Ok)
            {
                _log.LogWarning("Loading RGB devices failed: {Error}", result.Error);
                Error = result.Error;
                return;
            }

            var devices = result.Devices;
            Devices.Clear();
            foreach (var device in devices)
            {
                Devices.Add(new RgbDeviceViewModel(device, Agent, _log));
            }
            HasDevices = Devices.Count > 0;
            ShowNoDevices = IsConnected && Devices.Count == 0;
            _log.LogInformation("RGB devices: {Count} ({Names})", devices.Count, string.Join(", ", devices.Select(d => d.Name)));
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

/// <summary>
/// One RGB device card. Commands go to the agent as <see cref="RgbCommand"/> addressed by <see cref="Key"/> (the index is
/// only a hint); mode / brightness / speed changes are one SetMode command (sliders debounced 250 ms).
/// </summary>
public sealed partial class RgbDeviceViewModel : ObservableObject
{
    private static readonly TimeSpan SliderDebounce = TimeSpan.FromMilliseconds(250);

    private readonly AgentConnection _agent;
    private readonly ILogger _log;
    private readonly DispatcherTimer _sliderTimer;
    private bool _syncing;
    /// <summary>A first-seen default snapshot exists for this device (from the agent's device list).</summary>
    private bool _hasDefault;

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

    public RgbDeviceViewModel(RgbDevice device, AgentConnection agent, ILogger log)
    {
        _agent = agent;
        _log = log;
        Key = device.Key ?? string.Empty;
        Index = device.Index;
        _hasDefault = device.HasDefault;
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

    /// <summary>Stable device identity the agent resolves to the current backend index.</summary>
    public string Key { get; }

    /// <summary>Backend index when the list was loaded (a hint for the agent; the key wins).</summary>
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

    /// <summary>The device with <paramref name="key"/> in <paramref name="devices"/> (by index only for a backend without keys).</summary>
    internal static RgbDevice? Find(IReadOnlyList<RgbDevice> devices, string key, int index)
        => key.Length > 0
            ? devices.FirstOrDefault(d => d.Key == key)
            : devices.FirstOrDefault(d => string.IsNullOrEmpty(d.Key) && d.Index == index);

    /// <summary>Mirrors the backend's view of the device (active mode, colour, saved default) without re-applying anything.</summary>
    public void Sync(RgbDevice device)
    {
        _hasDefault = device.HasDefault;
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
    private Task SetColorAsync(RgbColor color) => RunAsync(Command(RgbCommandKind.SetColor) with { Color = color }, _ =>
    {
        ReflectColor(color);
        _log.LogInformation("RGB {Device} → {Color}", Name, color);
    });

    [RelayCommand]
    private Task ApplyCustomAsync() => SetColorAsync(new RgbColor(CustomColor.R, CustomColor.G, CustomColor.B));

    [RelayCommand]
    private Task SetZoneColorAsync(ZoneColorRequest request)
        => RunAsync(Command(RgbCommandKind.SetZoneColor) with { ZoneIndex = request.ZoneIndex, Color = request.Color }, _ =>
            _log.LogInformation("RGB {Device} zone {Zone} → {Color}", Name, request.ZoneIndex, request.Color));

    [RelayCommand]
    private Task TurnOffAsync() => RunAsync(Command(RgbCommandKind.TurnOff), devices =>
    {
        SyncFrom(devices);
        Notice = Strings.TurnedOff;
        _log.LogInformation("RGB {Device} off", Name);
    });

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreDefaultAsync() => RunAsync(Command(RgbCommandKind.RestoreDefault), devices =>
    {
        SyncFrom(devices);
        Notice = Strings.RestoredDefault;
        _log.LogInformation("RGB {Device} restored to its default", Name);
    });

    [RelayCommand]
    private Task SaveAsDefaultAsync() => RunAsync(Command(RgbCommandKind.SaveAsDefault), devices =>
    {
        SyncFrom(devices);
        Notice = _hasDefault ? Strings.SavedAsDefault : null;
        _log.LogInformation("RGB {Device}: current state saved as default", Name);
    });

    /// <summary>
    /// A command for this device. The key is always sent (empty for a backend without keys, never null: a null key
    /// would address every device).
    /// </summary>
    private RgbCommand Command(RgbCommandKind kind) => new() { Kind = kind, Key = Key, Index = Index };

    /// <summary>Applies this device's entry of a command reply (the device list after the command).</summary>
    private void SyncFrom(IReadOnlyList<RgbDevice> devices)
    {
        if (Find(devices, Key, Index) is { } device) Sync(device);
        else UpdateRestoreAvailability();
    }

    private void UpdateRestoreAvailability()
    {
        var hasDefault = _hasDefault;
        CanRestore = hasDefault || _fallbackEffect is not null;
        ShowRestoreHint = !CanRestore;
        RestoreTip = hasDefault
            ? Strings.RestoreDefaultTip
            : _fallbackEffect is { } effect ? string.Format(Strings.RestoreFallbackFormat, effect.Name) : Strings.RestoreUnavailable;
    }

    /// <summary>Sends a brightness / speed change still waiting in the debounce timer (before the popover exits).</summary>
    public Task FlushPendingAsync()
    {
        if (!_sliderTimer.IsEnabled) return Task.CompletedTask;
        _sliderTimer.Stop();
        return ApplyModeAsync();
    }

    private Task ApplyModeAsync()
    {
        var mode = SelectedMode;
        if (mode is null) return Task.CompletedTask;
        var command = Command(RgbCommandKind.SetMode) with
        {
            ModeName = mode.Name,
            ModeIndex = mode.Index,
            Color = mode.SupportsColor ? CurrentColor : null,
            Speed = mode.SupportsSpeed ? (int)Math.Round(Speed) : null,
            Brightness = mode.SupportsBrightness ? (int)Math.Round(Brightness) : null,
        };
        // No sync from the reply: a slider dragged meanwhile has re-armed the timer and must not be reset.
        return RunAsync(command, _ => _log.LogInformation("RGB {Device} mode → {Mode}", Name, mode.Name));
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

    private async Task RunAsync(RgbCommand command, Action<IReadOnlyList<RgbDevice>> onSuccess)
    {
        Error = null;
        IsBusy = true;
        try
        {
            var result = await _agent.RgbCommandAsync(command);
            if (result.Ok)
            {
                onSuccess(result.Devices);
            }
            else
            {
                _log.LogWarning("RGB {Kind} for {Device} failed: {Error}", command.Kind, Name, result.Error);
                Error = result.Error;
            }
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
