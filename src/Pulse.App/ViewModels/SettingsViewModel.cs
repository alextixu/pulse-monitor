using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Pulse.App.Controls;
using Pulse.Core.Localization;
using Pulse.App.Services;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Pulse.Engine;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Pulse.App.ViewModels;

/// <summary>A ComboBox entry for an enum value with its localized label.</summary>
public sealed record OptionItem(object Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 設定 tab. Every setter sends one <see cref="SettingsPatch"/> to the agent (the agent owns settings.json); the settings
/// copy in <see cref="AgentConnection"/> shows the new value at once and is rolled back if the agent refuses it.
/// Paths, elevation, auto-start support and the bundled-OpenRGB flag come from the agent's Info.
/// </summary>
public sealed partial class SettingsViewModel : TabViewModelBase
{
    /// <summary>Which bound properties show a given <see cref="AppSettings"/> property.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> BoundProperties = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [nameof(AppSettings.Theme)] = new[] { nameof(SelectedTheme) },
        [nameof(AppSettings.TrayIconMode)] = new[] { nameof(SelectedTrayIconMode) },
        [nameof(AppSettings.StartWithSystem)] = new[] { nameof(StartWithSystem) },
        [nameof(AppSettings.RequestElevationOnStartup)] = new[] { nameof(RequestElevationOnStartup) },
        [nameof(AppSettings.HardwareRefreshSeconds)] = new[] { nameof(HardwareRefreshSeconds), nameof(HardwareRefreshText) },
        [nameof(AppSettings.BatteryRefreshSeconds)] = new[] { nameof(BatteryRefreshSeconds), nameof(BatteryRefreshText) },
        [nameof(AppSettings.OpenRgbHost)] = new[] { nameof(OpenRgbHost) },
        [nameof(AppSettings.OpenRgbPort)] = new[] { nameof(OpenRgbPort) },
        [nameof(AppSettings.AutoConnectOpenRgb)] = new[] { nameof(AutoConnectOpenRgb) },
        [nameof(AppSettings.UseBundledOpenRgb)] = new[] { nameof(UseBundledOpenRgb) },
        [nameof(AppSettings.NotifyOnLowBattery)] = new[] { nameof(NotifyOnLowBattery) },
        [nameof(AppSettings.LowBatteryThresholdPercent)] = new[] { nameof(LowBatteryThresholdPercent), nameof(LowBatteryThresholdText) },
        [nameof(AppSettings.TelemetryEnabled)] = new[] { nameof(TelemetryEnabled) },
    };

    /// <summary>Bound properties that come from the agent's Info.</summary>
    private static readonly string[] InfoProperties =
    {
        nameof(AutoStartSupported), nameof(CanElevate), nameof(SettingsFilePath), nameof(LogFilePath),
        nameof(BundledOpenRgbHint), nameof(TelemetryPath), nameof(VersionText),
    };

    private readonly ILogger<SettingsViewModel> _log;
    /// <summary>The auto-start switch position while SetAutoStart is waiting for the agent.</summary>
    private bool? _pendingAutoStart;

    [ObservableProperty] private string? _testResultText;
    [ObservableProperty] private BccTone _testResultTone = BccTone.Neutral;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string? _actionError;
    [ObservableProperty] private bool _hasActionError;
    [ObservableProperty] private string? _autoStartError;

    public SettingsViewModel(AgentConnection agent, AppOptions options, ILogger<SettingsViewModel> log)
        : base(MainTab.Settings, agent)
    {
        Options = options;
        _log = log;

        ThemeOptions = Enum.GetValues<AppTheme>().Select(t => new OptionItem(t, Strings.ThemeName(t))).ToList();
        TrayIconOptions = Enum.GetValues<TrayIconMode>().Select(m => new OptionItem(m, Strings.TrayIconModeName(m))).ToList();

        // The agent reconciles StartWithSystem with the real registration at its start; nothing to do here.
        Agent.SettingsChanged += OnSettingsChanged;
        Agent.PropertyChanged += OnAgentPropertyChanged;
    }

    /// <summary>Never null: defaults until the agent answered, then the agent's settings plus pending changes.</summary>
    private AppSettings S => Agent.Settings;

    public AppOptions Options { get; }

    public IReadOnlyList<OptionItem> ThemeOptions { get; }

    public IReadOnlyList<OptionItem> TrayIconOptions { get; }

    public bool AutoStartSupported => Agent.Info.AutoStartSupported;

    public bool CanElevate => Agent.Info.CanElevate;

    public bool IsDemo => Options.Demo || Agent.IsDemo;

    public string SettingsFilePath => Agent.Info.SettingsPath;

    /// <summary>The agent's log (app.log) — where hardware, fan and RGB problems are recorded.</summary>
    public string LogFilePath => string.IsNullOrEmpty(Agent.Info.LogPath) ? FileLoggerProvider.DefaultPath : Agent.Info.LogPath;

    public string AuthorLine => Strings.SettingAuthorLine;

    private void OnSettingsChanged(object? sender, string? name)
    {
        if (name is null)
        {
            foreach (var names in BoundProperties.Values)
            {
                foreach (var property in names) OnPropertyChanged(property);
            }
            return;
        }
        if (BoundProperties.TryGetValue(name, out var bound))
        {
            foreach (var property in bound) OnPropertyChanged(property);
        }
    }

    private void OnAgentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AgentConnection.Info)) return;
        foreach (var property in InfoProperties) OnPropertyChanged(property);
    }

    /// <summary>Sends one setting; the copy already shows it, and a refused patch is rolled back by the connection.</summary>
    private void Send(SettingsPatch patch) => _ = Agent.UpdateSettingAsync(patch);

    // ---- 一般 ----

    public OptionItem SelectedTheme
    {
        get => ThemeOptions.First(o => Equals(o.Value, S.Theme));
        set
        {
            if (value is null || Equals(value.Value, S.Theme)) return;
            // The theme itself follows SettingsChanged(Theme) in App, raised as soon as the copy changes.
            Send(SettingsPatch.Of(nameof(AppSettings.Theme), (AppTheme)value.Value));
        }
    }

    public OptionItem SelectedTrayIconMode
    {
        get => TrayIconOptions.First(o => Equals(o.Value, S.TrayIconMode));
        set
        {
            if (value is null || Equals(value.Value, S.TrayIconMode)) return;
            Send(SettingsPatch.Of(nameof(AppSettings.TrayIconMode), (TrayIconMode)value.Value));
        }
    }

    /// <summary>
    /// Registers / removes auto-start through the agent (Run key or the "Pulse" scheduled task); the agent then updates
    /// the setting. The switch stays where the user put it while waiting and reverts on failure.
    /// </summary>
    public bool StartWithSystem
    {
        get => _pendingAutoStart ?? S.StartWithSystem;
        set
        {
            if (StartWithSystem == value) return;
            AutoStartError = null;
            _pendingAutoStart = value;
            OnPropertyChanged();
            _ = SetAutoStartAsync(value);
        }
    }

    private async Task SetAutoStartAsync(bool enabled)
    {
        var result = await Agent.SetAutoStartAsync(enabled);
        if (_pendingAutoStart == enabled) _pendingAutoStart = null;
        if (result.Ok)
        {
            _log.LogInformation("Auto start → {Enabled}", enabled);
        }
        else
        {
            _log.LogWarning("Changing auto start failed: {Error}", result.Error);
            AutoStartError = result.Error;
        }
        OnPropertyChanged(nameof(StartWithSystem));
    }

    public bool RequestElevationOnStartup
    {
        get => S.RequestElevationOnStartup;
        set
        {
            if (S.RequestElevationOnStartup == value) return;
            Send(SettingsPatch.Of(nameof(AppSettings.RequestElevationOnStartup), value));
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
            Send(SettingsPatch.Of(nameof(AppSettings.HardwareRefreshSeconds), v));
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
            Send(SettingsPatch.Of(nameof(AppSettings.BatteryRefreshSeconds), v));
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
            Send(SettingsPatch.Of(nameof(AppSettings.OpenRgbHost), v));
        }
    }

    public decimal? OpenRgbPort
    {
        get => S.OpenRgbPort;
        set
        {
            var v = (int)Math.Clamp(value ?? 6742, 1, 65535);
            if (S.OpenRgbPort == v) return;
            Send(SettingsPatch.Of(nameof(AppSettings.OpenRgbPort), v));
        }
    }

    public bool AutoConnectOpenRgb
    {
        get => S.AutoConnectOpenRgb;
        set
        {
            if (S.AutoConnectOpenRgb == value) return;
            Send(SettingsPatch.Of(nameof(AppSettings.AutoConnectOpenRgb), value));
        }
    }

    public bool UseBundledOpenRgb
    {
        get => S.UseBundledOpenRgb;
        set
        {
            if (S.UseBundledOpenRgb == value) return;
            Send(SettingsPatch.Of(nameof(AppSettings.UseBundledOpenRgb), value));
        }
    }

    /// <summary>Whether an OpenRGB copy ships next to the agent (otherwise the toggle has nothing to start).</summary>
    public string BundledOpenRgbHint => Agent.Info.OpenRgbBundled
        ? Strings.SettingBundledOpenRgbPresent
        : Strings.SettingBundledOpenRgbMissing;

    // ---- 通知 ----

    public bool NotifyOnLowBattery
    {
        get => S.NotifyOnLowBattery;
        set
        {
            if (S.NotifyOnLowBattery == value) return;
            Send(SettingsPatch.Of(nameof(AppSettings.NotifyOnLowBattery), value));
        }
    }

    public double LowBatteryThresholdPercent
    {
        get => S.LowBatteryThresholdPercent;
        set
        {
            var v = (int)Math.Clamp(Math.Round(value), 5, 50);
            if (S.LowBatteryThresholdPercent == v) return;
            Send(SettingsPatch.Of(nameof(AppSettings.LowBatteryThresholdPercent), v));
        }
    }

    public string LowBatteryThresholdText => string.Format(Strings.PercentFormat, S.LowBatteryThresholdPercent);

    // ---- 記錄 ----

    public bool TelemetryEnabled
    {
        get => S.TelemetryEnabled;
        set
        {
            if (S.TelemetryEnabled == value) return;
            Send(SettingsPatch.Of(nameof(AppSettings.TelemetryEnabled), value));
        }
    }

    public string TelemetryPath => Agent.Info.TelemetryPath;

    /// <summary>The agent's telemetry folder (default location when the agent has none, e.g. in demo mode).</summary>
    private string TelemetryDirectory => string.IsNullOrEmpty(Agent.Info.TelemetryDirectory)
        ? Path.GetDirectoryName(FileLoggerProvider.DefaultPath) ?? Path.GetTempPath()
        : Agent.Info.TelemetryDirectory;

    [RelayCommand]
    private void OpenTelemetryFolder()
    {
        var dir = TelemetryDirectory;
        var current = TelemetryPath;
        Open(() =>
        {
            Directory.CreateDirectory(dir);
            var target = current.Length > 0 && File.Exists(current) ? $"/select,\"{current}\"" : $"\"{dir}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }, dir);
    }

    // ---- 關於 ----

    /// <summary>"版本 0.1.0": the agent's version once connected, this executable's before.</summary>
    public string VersionText
    {
        get
        {
            var version = Agent.Info.Version;
            if (string.IsNullOrWhiteSpace(version) || version == AgentInfo.Empty.Version)
            {
                var asm = Assembly.GetExecutingAssembly();
                version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? asm.GetName().Version?.ToString(3)
                          ?? "0.0.0";
            }
            var plus = version.IndexOf('+');
            if (plus > 0) version = version[..plus];
            return string.Format(Strings.VersionFormat, version);
        }
    }

    partial void OnActionErrorChanged(string? value) => HasActionError = !string.IsNullOrWhiteSpace(value);

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task TestConnectionAsync()
    {
        IsTesting = true;
        TestResultText = Strings.TestConnectionRunning;
        TestResultTone = BccTone.Accent;
        try
        {
            var result = await Agent.ConnectRgbAsync();
            // The reply carries the status after the attempt (a status broadcast may lag behind it).
            var message = result.Status?.Message ?? result.Error;
            TestResultText = result.Ok
                ? (string.IsNullOrWhiteSpace(message) ? Strings.TestConnectionOk : $"{Strings.TestConnectionOk}：{message}")
                : (string.IsNullOrWhiteSpace(message) ? Strings.TestConnectionFailed : $"{Strings.TestConnectionFailed}：{message}");
            TestResultTone = result.Ok ? BccTone.Success : BccTone.Danger;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void OpenSettingsFolder()
    {
        var path = SettingsFilePath.Length > 0 ? SettingsFilePath : JsonSettingsStore.DefaultPath;
        var dir = Path.GetDirectoryName(path) ?? path;
        Open(() =>
        {
            Directory.CreateDirectory(dir);
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }, dir);
    }

    [RelayCommand]
    private void OpenLogFile()
    {
        var path = LogFilePath;
        Open(() =>
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"") { UseShellExecute = true });
        }, path);
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
