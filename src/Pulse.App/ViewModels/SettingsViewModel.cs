using System.Diagnostics;
using System.Reflection;
using Pulse.App.Controls;
using Pulse.App.Localization;
using Pulse.App.Services;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Pulse.App.ViewModels;

/// <summary>A ComboBox entry for an enum value with its localized label.</summary>
public sealed record OptionItem(object Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>設定 tab. Every property writes through to <c>Settings.Settings</c> and calls <c>Settings.NotifyChanged(name)</c> (debounced save, live apply).</summary>
public sealed partial class SettingsViewModel : TabViewModelBase
{
    private readonly ILogger<SettingsViewModel> _log;

    [ObservableProperty] private string? _testResultText;
    [ObservableProperty] private BccTone _testResultTone = BccTone.Neutral;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string? _actionError;
    [ObservableProperty] private bool _hasActionError;
    [ObservableProperty] private string? _autoStartError;

    public SettingsViewModel(
        MonitoringService monitoring,
        SettingsService settings,
        IAutoStartService autoStart,
        IElevationService elevation,
        AppOptions options,
        ILogger<SettingsViewModel> log)
        : base(MainTab.Settings, monitoring, settings)
    {
        AutoStart = autoStart;
        Elevation = elevation;
        Options = options;
        _log = log;

        ThemeOptions = Enum.GetValues<AppTheme>().Select(t => new OptionItem(t, Strings.ThemeName(t))).ToList();
        TrayIconOptions = Enum.GetValues<TrayIconMode>().Select(m => new OptionItem(m, Strings.TrayIconModeName(m))).ToList();

        // Reflect the real registration state (the registry may have been changed outside the app).
        if (autoStart.IsSupported)
        {
            try
            {
                var enabled = autoStart.IsEnabled;
                if (S.StartWithSystem != enabled)
                {
                    S.StartWithSystem = enabled;
                    Settings.Save();
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Reading auto-start state failed");
            }
        }
    }

    private AppSettings S => Settings.Settings;

    public IAutoStartService AutoStart { get; }

    public IElevationService Elevation { get; }

    public AppOptions Options { get; }

    public IReadOnlyList<OptionItem> ThemeOptions { get; }

    public IReadOnlyList<OptionItem> TrayIconOptions { get; }

    public bool AutoStartSupported => AutoStart.IsSupported;

    public bool CanElevate => Elevation.CanElevate;

    public bool IsDemo => Options.Demo;

    public string SettingsFilePath => Settings.FilePath;

    public string LogFilePath => FileLoggerProvider.DefaultPath;

    public string AuthorLine => Strings.SettingAuthorLine;

    // ---- 一般 ----

    public OptionItem SelectedTheme
    {
        get => ThemeOptions.First(o => Equals(o.Value, S.Theme));
        set
        {
            if (value is null || Equals(value.Value, S.Theme)) return;
            S.Theme = (AppTheme)value.Value;
            OnPropertyChanged();
            Settings.NotifyChanged(nameof(AppSettings.Theme));
        }
    }

    public OptionItem SelectedTrayIconMode
    {
        get => TrayIconOptions.First(o => Equals(o.Value, S.TrayIconMode));
        set
        {
            if (value is null || Equals(value.Value, S.TrayIconMode)) return;
            S.TrayIconMode = (TrayIconMode)value.Value;
            OnPropertyChanged();
            Settings.NotifyChanged(nameof(AppSettings.TrayIconMode));
        }
    }

    public bool StartWithSystem
    {
        get => S.StartWithSystem;
        set
        {
            if (S.StartWithSystem == value) return;
            AutoStartError = null;
            try
            {
                if (AutoStart.IsSupported) AutoStart.SetEnabled(value);
                S.StartWithSystem = value;
                _log.LogInformation("Auto start → {Enabled}", value);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Changing auto start failed");
                AutoStartError = ex.Message;
            }
            OnPropertyChanged();
            Settings.NotifyChanged(nameof(AppSettings.StartWithSystem));
        }
    }

    public bool RequestElevationOnStartup
    {
        get => S.RequestElevationOnStartup;
        set
        {
            if (S.RequestElevationOnStartup == value) return;
            S.RequestElevationOnStartup = value;
            OnPropertyChanged();
            Settings.NotifyChanged(nameof(AppSettings.RequestElevationOnStartup));
        }
    }

    // ---- 更新頻率 ----

    public double HardwareRefreshSeconds
    {
        get => S.HardwareRefreshSeconds;
        set
        {
            var v = (int)Math.Clamp(Math.Round(value), 1, 10);
            if (S.HardwareRefreshSeconds == v) return;
            S.HardwareRefreshSeconds = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HardwareRefreshText));
            Settings.NotifyChanged(nameof(AppSettings.HardwareRefreshSeconds));
        }
    }

    public string HardwareRefreshText => string.Format(Strings.SecondsFormat, S.HardwareRefreshSeconds);

    public double BatteryRefreshSeconds
    {
        get => S.BatteryRefreshSeconds;
        set
        {
            var v = (int)Math.Clamp(Math.Round(value / 10) * 10, 10, 300);
            if (S.BatteryRefreshSeconds == v) return;
            S.BatteryRefreshSeconds = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BatteryRefreshText));
            Settings.NotifyChanged(nameof(AppSettings.BatteryRefreshSeconds));
        }
    }

    public string BatteryRefreshText => string.Format(Strings.SecondsFormat, S.BatteryRefreshSeconds);

    // ---- OpenRGB ----

    public string OpenRgbHost
    {
        get => S.OpenRgbHost;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim();
            if (S.OpenRgbHost == v) return;
            S.OpenRgbHost = v;
            OnPropertyChanged();
            Settings.NotifyChanged(nameof(AppSettings.OpenRgbHost));
        }
    }

    public decimal? OpenRgbPort
    {
        get => S.OpenRgbPort;
        set
        {
            var v = (int)Math.Clamp(value ?? 6742, 1, 65535);
            if (S.OpenRgbPort == v) return;
            S.OpenRgbPort = v;
            OnPropertyChanged();
            Settings.NotifyChanged(nameof(AppSettings.OpenRgbPort));
        }
    }

    public bool AutoConnectOpenRgb
    {
        get => S.AutoConnectOpenRgb;
        set
        {
            if (S.AutoConnectOpenRgb == value) return;
            S.AutoConnectOpenRgb = value;
            OnPropertyChanged();
            Settings.NotifyChanged(nameof(AppSettings.AutoConnectOpenRgb));
        }
    }

    // ---- 通知 ----

    public bool NotifyOnLowBattery
    {
        get => S.NotifyOnLowBattery;
        set
        {
            if (S.NotifyOnLowBattery == value) return;
            S.NotifyOnLowBattery = value;
            OnPropertyChanged();
            Settings.NotifyChanged(nameof(AppSettings.NotifyOnLowBattery));
        }
    }

    public double LowBatteryThresholdPercent
    {
        get => S.LowBatteryThresholdPercent;
        set
        {
            var v = (int)Math.Clamp(Math.Round(value), 5, 50);
            if (S.LowBatteryThresholdPercent == v) return;
            S.LowBatteryThresholdPercent = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LowBatteryThresholdText));
            Settings.NotifyChanged(nameof(AppSettings.LowBatteryThresholdPercent));
        }
    }

    public string LowBatteryThresholdText => string.Format(Strings.PercentFormat, S.LowBatteryThresholdPercent);

    // ---- 關於 ----

    /// <summary>"版本 0.1.0" from the assembly informational version.</summary>
    public string VersionText
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? asm.GetName().Version?.ToString(3)
                          ?? "0.0.0";
            var plus = version.IndexOf('+');
            if (plus > 0) version = version[..plus];
            return string.Format(Strings.VersionFormat, version);
        }
    }

    public override string PlaceholderText => Strings.PlaceholderSettings;

    partial void OnActionErrorChanged(string? value) => HasActionError = !string.IsNullOrWhiteSpace(value);

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task TestConnectionAsync()
    {
        IsTesting = true;
        TestResultText = Strings.TestConnectionRunning;
        TestResultTone = BccTone.Accent;
        try
        {
            var ok = await Monitoring.ConnectRgbAsync();
            var status = Monitoring.Rgb.Status;
            TestResultText = ok
                ? (string.IsNullOrWhiteSpace(status.Message) ? Strings.TestConnectionOk : $"{Strings.TestConnectionOk}：{status.Message}")
                : (string.IsNullOrWhiteSpace(status.Message) ? Strings.TestConnectionFailed : $"{Strings.TestConnectionFailed}：{status.Message}");
            TestResultTone = ok ? BccTone.Success : BccTone.Danger;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void OpenSettingsFolder()
    {
        var dir = Path.GetDirectoryName(SettingsFilePath) ?? SettingsFilePath;
        Open(() =>
        {
            Directory.CreateDirectory(dir);
            if (File.Exists(SettingsFilePath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SettingsFilePath}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }, dir);
    }

    [RelayCommand]
    private void OpenLogFile()
    {
        Open(() =>
        {
            if (File.Exists(LogFilePath))
                Process.Start(new ProcessStartInfo(LogFilePath) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(LogFilePath)}\"") { UseShellExecute = true });
        }, LogFilePath);
    }

    private void Open(Action action, string target)
    {
        ActionError = null;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open {Target}", target);
            ActionError = $"{Strings.OpenFailed}：{target}";
        }
    }
}
