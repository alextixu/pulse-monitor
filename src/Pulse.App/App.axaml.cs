using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Pulse.App.Demo;
using Pulse.App.Services;
using Pulse.App.ViewModels;
using Pulse.App.Views;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Pulse.Hardware;
using Pulse.Asus;
using Pulse.Logitech;
using Pulse.Rgb;
using Pulse.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Pulse.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _log;
    private PopoverWindow? _popover;
    private TrayService? _tray;
    private MainViewModel? _viewModel;

    /// <summary>Parsed command line (set by <see cref="Program"/> before Avalonia starts).</summary>
    public static AppOptions Options { get; internal set; } = new();

    /// <summary>Logger factory created in <see cref="Program"/> so logging works before and after the Avalonia lifetime.</summary>
    internal static ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>Single-instance guard owned by <see cref="Program"/>; null in demo / screenshot mode.</summary>
    internal static SingleInstance? Instance { get; set; }

    /// <summary>The running application (null at design time).</summary>
    public static new App? Current => Application.Current as App;

    /// <summary>Root DI container (available after OnFrameworkInitializationCompleted).</summary>
    public IServiceProvider Services => _services ?? throw new InvalidOperationException("Services are not built yet.");

    public MainViewModel? MainViewModel => _viewModel;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                Startup(desktop);
            }
            catch (Exception ex)
            {
                _log?.LogCritical(ex, "Startup failed");
                desktop.Shutdown(2);
                return;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Applies <see cref="AppTheme"/> to the application (System → follow OS).</summary>
    public static void ApplyTheme(AppTheme theme)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    public void ShowPopover() => _popover?.ShowPopover();

    public void HidePopover() => _popover?.HidePopover();

    public void TogglePopover() => _popover?.TogglePopover();

    private void Startup(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _services = BuildServices();
        _log = _services.GetRequiredService<ILogger<App>>();
        _log.LogInformation("Composition done (demo={Demo}, screenshot={Screenshot}, minimized={Minimized})",
            Options.Demo, Options.IsScreenshotMode, Options.StartMinimized);

        var settingsService = _services.GetRequiredService<SettingsService>();
        ApplyTheme(settingsService.Settings.Theme);
        if (!Options.Demo && !File.Exists(settingsService.FilePath))
        {
            // Persist the defaults on first run so the user can find and edit the file.
            settingsService.Save();
        }
        settingsService.Changed += (_, name) =>
        {
            if (name is null || name == nameof(AppSettings.Theme)) ApplyTheme(settingsService.Settings.Theme);
        };

        var elevation = _services.GetRequiredService<IElevationService>();
        if (!Options.Demo && !Options.ElevatedRelaunch && settingsService.Settings.RequestElevationOnStartup
            && elevation.CanElevate && !elevation.IsElevated)
        {
            _log.LogInformation("RequestElevationOnStartup: relaunching elevated");
            if (elevation.RelaunchElevated())
            {
                desktop.Shutdown(0);
                return;
            }
            _log.LogWarning("Elevated relaunch declined or failed; continuing unelevated");
        }

        _viewModel = _services.GetRequiredService<MainViewModel>();
        _viewModel.ShowPopoverRequested += (_, _) => ShowPopover();
        _viewModel.QuitRequested += (_, _) => desktop.Shutdown(0);

        _popover = new PopoverWindow(_viewModel);
        desktop.Exit += OnExit;

        var monitoring = _services.GetRequiredService<MonitoringService>();
        monitoring.Start();

        if (Options.IsScreenshotMode)
        {
            _ = RunScreenshotsAsync(desktop, _viewModel, Options.ScreenshotDirectory!);
            return;
        }

        _tray = _services.GetRequiredService<TrayService>();
        _tray.Initialize();
        _tray.Clicked += (_, _) => TogglePopover();

        Instance?.StartListening(() => Dispatcher.UIThread.Post(ShowPopover));

        if (!Options.StartMinimized)
        {
            ShowPopover();
        }
    }

    private async Task RunScreenshotsAsync(IClassicDesktopStyleApplicationLifetime desktop, MainViewModel viewModel, string directory)
    {
        var exitCode = 0;
        try
        {
            // Give the demo providers a moment to deliver their first data so the shots are not empty.
            await Task.Delay(900);
            var files = await ScreenshotService.CaptureAsync(_popover!, viewModel, directory, _log!);
            _log!.LogInformation("Screenshot run complete: {Count} file(s) in {Dir}", files.Count, directory);
            if (files.Count == 0) exitCode = 3;
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Screenshot run failed");
            exitCode = 3;
        }
        finally
        {
            desktop.Shutdown(exitCode);
        }
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _log?.LogInformation("Exiting with code {Code}", e.ApplicationExitCode);
        try
        {
            _tray?.Dispose();
            _popover?.Close();
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "Tray/popover teardown failed");
        }

        try
        {
            // Disposes MonitoringService first (registered last), then IHardwareMonitor (restores fans) / IRgbController / providers.
            _services?.Dispose();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Service disposal failed");
        }
        _services = null;
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        // Logging: reuse the factory created in Program (file + Debug sinks).
        var loggerFactory = LoggerFactory ?? Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
            AppLogging.Configure(b, new FileLoggerProvider(FileLoggerProvider.DefaultPath)));
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        services.AddSingleton(Options);
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton(sp => sp.GetRequiredService<ISettingsStore>().Load());
        services.AddSingleton<SettingsService>();

        if (Options.Demo)
        {
            services.AddSingleton<IBatteryProvider, DemoBatteryProvider>();
            services.AddSingleton<IHardwareMonitor, DemoHardwareMonitor>();
            services.AddSingleton<IRgbController, DemoRgbController>();
            services.AddSingleton<IElevationService, DemoElevationService>();
            services.AddSingleton<IAutoStartService, DemoAutoStartService>();
        }
        else if (OperatingSystem.IsWindows())
        {
            services.AddWindowsPlatformServices();
            services.AddLogitechHidpp();
            services.AddAsusRog();
            services.AddLibreHardwareMonitor();
            services.AddOpenRgb();
        }

        // Fallbacks for anything a platform package did not register.
        services.TryAddSingleton<IHardwareMonitor, NullHardwareMonitor>();
        services.TryAddSingleton<IRgbController, NullRgbController>();
        services.TryAddSingleton<IElevationService, NullElevationService>();
        services.TryAddSingleton<IAutoStartService, NullAutoStartService>();

        services.AddSingleton<TrayIconRenderer>();
        services.AddSingleton<MonitoringService>();

        services.AddSingleton<DevicesViewModel>();
        services.AddSingleton<SystemViewModel>();
        services.AddSingleton<LightingViewModel>();
        services.AddSingleton<FansViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<TrayService>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
