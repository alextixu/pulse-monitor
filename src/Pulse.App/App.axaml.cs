using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Pulse.App.Localization;
using Pulse.App.Services;
using Pulse.App.ViewModels;
using Pulse.App.Views;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Pulse.Engine;
using Pulse.Engine.Demo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pulse.App;

/// <summary>
/// The popover process. Lifecycle: show the window at once (theme from ui-state.json), connect to the agent (or start
/// the in-process demo agent) and fill the view models when it answers; when the popover hides, send the debounced
/// writes still pending (≤ 1 s), save ui-state.json and exit — every open is a fresh process. The agent's Exiting
/// message (quit, elevation relaunch, pipe gone) also ends the process.
/// </summary>
public partial class App : Application
{
    /// <summary>How long the pipe client keeps retrying while no agent listens.</summary>
    private static readonly TimeSpan ConnectWindow = TimeSpan.FromSeconds(10);

    /// <summary>Budget for the debounced writes (and their replies) after the popover hid.</summary>
    private static readonly TimeSpan ExitFlushBudget = TimeSpan.FromSeconds(1);

    /// <summary>How long 「Pulse 已更新，請重新開啟」 stays up before the process exits by itself.</summary>
    private static readonly TimeSpan ProtocolMismatchExit = TimeSpan.FromSeconds(6);

    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private ILogger _log = NullLogger.Instance;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private PopoverWindow? _popover;
    private MainViewModel? _viewModel;
    private AgentConnection? _connection;
    private IpcPulseClient? _ipc;
    private AgentCore? _demoAgent;
    private UiState _uiState = new();
    /// <summary>The popover hid and the exit flush is running (a show request in that window cancels the exit).</summary>
    private bool _exitPending;
    /// <summary>Past the point of no return: no more shows, the pipe is closing.</summary>
    private bool _shuttingDown;

    /// <summary>Parsed command line (set by <see cref="Program"/> before Avalonia starts).</summary>
    public static AppOptions Options { get; internal set; } = new();

    /// <summary>Logger factory created in <see cref="Program"/> so logging works before and after the Avalonia lifetime.</summary>
    internal static ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>Single-instance guard owned by <see cref="Program"/>; null in demo / screenshot mode.</summary>
    internal static UiSingleInstance? Instance { get; set; }

    /// <summary>The running application (null at design time).</summary>
    public static new App? Current => Application.Current as App;

    public MainViewModel? MainViewModel => _viewModel;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                Startup(desktop);
            }
            catch (Exception ex)
            {
                _log.LogCritical(ex, "Startup failed");
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

    private void Startup(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _loggerFactory = LoggerFactory ?? NullLoggerFactory.Instance;
        _log = _loggerFactory.CreateLogger<App>();
        _log.LogInformation("Starting (demo={Demo}, screenshot={Screenshot}, popover={Popover}, tab={Tab})",
            Options.Demo, Options.IsScreenshotMode, Options.Popover, Options.Tab);

        // Demo runs read nothing of the user's popover state and write nothing back.
        _uiState = Options.Demo ? new UiState() : UiStateStore.Load(_log);
        ApplyTheme(_uiState.Theme);

        IPulseAgent agent;
        if (Options.Demo)
        {
            _demoAgent = CreateDemoAgent(_loggerFactory);
            agent = _demoAgent;
        }
        else
        {
            _ipc = new IpcPulseClient(_loggerFactory.CreateLogger<IpcPulseClient>());
            agent = _ipc;
        }

        var connection = new AgentConnection(agent, Options.Demo, _loggerFactory.CreateLogger<AgentConnection>());
        _connection = connection;
        _viewModel = new MainViewModel(
            connection,
            Options,
            new DevicesViewModel(connection, _loggerFactory.CreateLogger<DevicesViewModel>()),
            new SystemViewModel(connection, _loggerFactory.CreateLogger<SystemViewModel>()),
            new LightingViewModel(connection, _loggerFactory.CreateLogger<LightingViewModel>()),
            new FansViewModel(connection, _loggerFactory.CreateLogger<FansViewModel>()),
            new SettingsViewModel(connection, Options, _loggerFactory.CreateLogger<SettingsViewModel>()),
            _loggerFactory.CreateLogger<MainViewModel>());
        _viewModel.SelectedTab = Options.Tab ?? _uiState.LastTab ?? MainTab.Devices;
        _viewModel.ShowPopoverRequested += (_, _) => ShowPopover();

        connection.SettingsChanged += OnSettingsChanged;
        connection.Exiting += OnAgentExiting;
        connection.ShowPopoverRequested += (_, request) => OnShowPopoverRequested(request);

        _popover = new PopoverWindow(_viewModel);
        _popover.PopoverHidden += (_, _) => _ = ExitAfterHideAsync();
        desktop.Exit += OnExit;

        if (Options.IsScreenshotMode)
        {
            _ = RunScreenshotsAsync(_viewModel, Options.ScreenshotDirectory!);
            return;
        }

        ShowPopover();
        _log.LogInformation("Popover shown at +{Ms:0} ms", Program.MillisecondsSinceStart());
        Instance?.StartListening(() => Dispatcher.UIThread.Post(() =>
        {
            _log.LogInformation("Show requested by a second launch");
            ShowPopover();
        }));

        _ = ConnectAsync();
    }

    /// <summary>The engine in this process with the design-data providers: no pipe, no files, no hardware.</summary>
    private static AgentCore CreateDemoAgent(ILoggerFactory loggerFactory)
    {
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return new AgentCore(new AgentCoreOptions
        {
            LoggerFactory = loggerFactory,
            // Reads the user's settings.json once (read-only) and keeps every change in memory.
            SettingsStore = new DemoSettingsStore(loggerFactory.CreateLogger<JsonSettingsStore>(), loggerFactory.CreateLogger<DemoSettingsStore>()),
            StateStore = new InMemoryStateStore(),
            Hardware = new DemoHardwareMonitor(loggerFactory.CreateLogger<DemoHardwareMonitor>()),
            Rgb = new DemoRgbController(loggerFactory.CreateLogger<DemoRgbController>()),
            BatteryProviders = new IBatteryProvider[] { new DemoBatteryProvider(loggerFactory.CreateLogger<DemoBatteryProvider>()) },
            Elevation = new DemoElevationService(loggerFactory.CreateLogger<DemoElevationService>()),
            AutoStart = new DemoAutoStartService(),
            TelemetryDirectory = null,
            LogPath = Program.UiLogPath,
            IsDemo = true,
            Version = version,
            RelaunchElevated = null,
        });
    }

    private async Task ConnectAsync()
    {
        var connection = _connection!;
        try
        {
            if (_demoAgent is { } demo)
            {
                await Task.Run(() => demo.StartAsync());
                connection.OnAgentReady();
                return;
            }

            var outcome = await _ipc!.ConnectAsync(ConnectWindow);
            switch (outcome)
            {
                case AgentConnectOutcome.Connected:
                    connection.OnAgentReady();
                    _log.LogInformation("Hello received at +{Ms:0} ms", Program.MillisecondsSinceStart());
                    break;
                case AgentConnectOutcome.ProtocolMismatch:
                    connection.ReportConnectionError(UiStrings.ProtocolMismatch);
                    await Task.Delay(ProtocolMismatchExit);
                    await ShutdownAsync(0);
                    break;
                default:
                    // Stays up with the hint until the user dismisses it (which ends the process like any hide).
                    connection.ReportConnectionError(UiStrings.AgentUnavailable);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Connecting to the agent failed");
            connection.ReportConnectionError(UiStrings.AgentUnavailable);
        }
    }

    private void ShowPopover()
    {
        if (_shuttingDown) return;
        _popover?.ShowPopover();
    }

    /// <summary>The agent's tray icon was clicked (toggle) or it asked for a tab (show).</summary>
    private void OnShowPopoverRequested(PopoverRequest request)
    {
        if (_shuttingDown || _popover is null || _viewModel is null) return;
        var tab = AppOptions.ParseTab(request.Tab);
        if (request.Toggle && tab is null)
        {
            // Visible → hide (and exit); hidden by the same click's deactivation a moment ago → stays hidden.
            _popover.TogglePopover();
            return;
        }

        if (tab is { } t) _viewModel.SelectedTab = t;
        ShowPopover();
    }

    private void OnSettingsChanged(object? sender, string? name)
    {
        // Screenshot runs drive the theme themselves; a settings broadcast must not flip it mid-capture.
        if (Options.IsScreenshotMode || _connection is null) return;
        if (name is null || name == nameof(AppSettings.Theme)) ApplyTheme(_connection.Settings.Theme);
    }

    private void OnAgentExiting(object? sender, AgentExitReason reason)
    {
        _log.LogInformation("Agent exiting ({Reason}); closing the popover", reason);
        _ = ShutdownAsync(0);
    }

    /// <summary>
    /// The popover hid: fire the debounce timers and wait (≤ 1 s) for the replies, then exit unless it was shown again
    /// meanwhile (a tray click or second launch during the flush).
    /// </summary>
    private async Task ExitAfterHideAsync()
    {
        if (Options.IsScreenshotMode || _shuttingDown || _exitPending) return;
        _exitPending = true;
        try
        {
            _log.LogInformation("Popover hidden; exiting");
            await Task.WhenAny(FlushPendingWritesAsync(), Task.Delay(ExitFlushBudget));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Flushing pending writes failed");
        }
        finally
        {
            _exitPending = false;
        }

        if (_popover is { IsVisible: true })
        {
            _log.LogInformation("Popover shown again before exiting; staying");
            return;
        }
        await ShutdownAsync(0);
    }

    private async Task FlushPendingWritesAsync()
    {
        if (_viewModel is not null) await _viewModel.FlushPendingAsync();
        if (_connection is not null) await _connection.WhenIdleAsync();
    }

    /// <summary>
    /// Ends the process: no more show requests, ui-state.json written, the pipe closed (queued messages drained first)
    /// or the demo engine stopped, then the Avalonia lifetime. The single-instance mutex is released last by Program.
    /// </summary>
    private async Task ShutdownAsync(int exitCode)
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        Instance?.StopListening();
        SaveUiState();

        try
        {
            if (_ipc is { } ipc) await ipc.CloseAsync(TimeSpan.FromMilliseconds(500));
            if (_demoAgent is { } demo) await Task.WhenAny(demo.ShutdownAsync(AgentExitReason.Quit), Task.Delay(TimeSpan.FromSeconds(3)));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Agent teardown failed");
        }

        _desktop?.Shutdown(exitCode);
    }

    private void SaveUiState()
    {
        if (Options.Demo || _viewModel is null || _connection is null) return;
        // Without an agent the settings copy holds defaults: keep the cached theme then.
        var theme = _connection.IsReady ? _connection.Settings.Theme : _uiState.Theme;
        UiStateStore.Save(new UiState { Theme = theme, LastTab = _viewModel.SelectedTab }, _log);
    }

    private async Task RunScreenshotsAsync(MainViewModel viewModel, string directory)
    {
        var exitCode = 0;
        try
        {
            await Task.Run(() => _demoAgent!.StartAsync());
            _connection!.OnAgentReady();
            // Visible from the start: 1 s hardware cadence and a battery refresh for the first shots.
            _connection.SetUiVisible(true);
            // Give the demo providers a moment to deliver their first data so the shots are not empty.
            await Task.Delay(900);
            var files = await ScreenshotService.CaptureAsync(_popover!, viewModel, directory, _log);
            _log.LogInformation("Screenshot run complete: {Count} file(s) in {Dir}", files.Count, directory);
            if (files.Count == 0) exitCode = 3;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Screenshot run failed");
            exitCode = 3;
        }
        finally
        {
            await ShutdownAsync(exitCode);
        }
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _log.LogInformation("Exiting with code {Code}", e.ApplicationExitCode);
        try
        {
            _popover?.Close();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Popover teardown failed");
        }
    }
}
