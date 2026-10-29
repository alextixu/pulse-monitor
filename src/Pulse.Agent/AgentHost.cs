using Microsoft.Extensions.Logging;
using Pulse.Agent.Ipc;
using Pulse.Agent.Tray;
using Pulse.Asus;
using Pulse.Core.Abstractions;
using Pulse.Core.Ipc;
using Pulse.Core.Tray;
using Pulse.Engine;
using Pulse.Engine.Ipc;
using Pulse.Hardware;
using Pulse.Logitech;
using Pulse.Rgb;
using Pulse.Windows;

namespace Pulse.Agent;

/// <summary>
/// Composition and lifetime of the running agent, after the single-instance guard was taken (decisions-v2 §24):
/// settings → RequestElevationOnStartup relaunch → crash hooks → tray icon (static glyph) → providers + engine →
/// pipe server → engine start (state, hardware, batteries, RGB) → message loop. Leaving: the clean shutdown
/// (<see cref="AgentCore.ShutdownAsync"/>, fans first) runs while the window keeps pumping, then the pipe server, the battery
/// providers and the tray go; <see cref="Program"/> releases the single-instance mutex last.
/// </summary>
internal sealed class AgentHost
{
    private static readonly TimeSpan IpcDisposeBudget = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FinalShutdownWait = TimeSpan.FromSeconds(20);

    private readonly AgentOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly string _logPath;
    private readonly ILogger _log;

    private TrayWindow? _window;
    private NotifyIcon? _icon;
    private TrayController? _controller;
    private UiLauncher? _uiLauncher;
    private AgentCore? _agent;
    private IpcServer? _ipc;
    private ShutdownCoordinator? _coordinator;
    private IReadOnlyList<IBatteryProvider> _batteryProviders = Array.Empty<IBatteryProvider>();
    /// <summary>Owned by the engine once it exists; disposed here only when composing the engine failed.</summary>
    private IDisposable[] _engineParts = Array.Empty<IDisposable>();
    private int _firstFrameLogged;

    public AgentHost(AgentOptions options, ILoggerFactory loggers, string logPath)
    {
        _options = options;
        _loggers = loggers;
        _logPath = logPath;
        _log = loggers.CreateLogger("Pulse.Agent.AgentHost");
    }

    public int Run(SingleInstance instance)
    {
        // ── settings: only RequestElevationOnStartup is needed here; the engine gets the same instance on its first Load.
        var settingsStore = new PreloadedSettingsStore(new JsonSettingsStore(_loggers.CreateLogger<JsonSettingsStore>()));
        var settings = settingsStore.Preload();

        // ── elevation before anything is initialised, so nothing (driver, OpenRGB, journal) is set up twice.
        var elevation = new WindowsElevationService(_loggers.CreateLogger<WindowsElevationService>());
        if (!_options.ElevatedRelaunch && settings.RequestElevationOnStartup && elevation.CanElevate && !elevation.IsElevated)
        {
            _log.LogInformation("RequestElevationOnStartup: relaunching elevated");
            if (elevation.RelaunchElevated())
            {
                // The elevated copy waits up to 10 s for the mutex, which Program releases right after this returns.
                return 0;
            }
            _log.LogWarning("Elevated relaunch declined or failed; continuing unelevated");
        }

        Program.InstallCrashHooks(_loggers.CreateLogger("Pulse.Agent.Crash"));

        try
        {
            StartUp(settingsStore, elevation, instance);
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Startup failed");
            Teardown();
            return 2;
        }

        _window!.Run();

        var requested = _coordinator!.IsStarted;
        if (!requested) _log.LogError("Message loop ended without a shutdown request");
        Teardown();
        return requested ? 0 : 1;
    }

    private void StartUp(PreloadedSettingsStore settingsStore, WindowsElevationService elevation, SingleInstance instance)
    {
        // ── tray first (static glyph): the user sees Pulse within a second, long before the hardware is up.
        var trayLog = _loggers.CreateLogger("Pulse.Agent.Tray");
        _window = new TrayWindow(trayLog);
        var renderer = new GdiTrayIconRenderer(trayLog);
        _icon = new NotifyIcon(_window, renderer.Render(TrayIconContent.Static), trayLog);
        var added = _icon.Add();
        _log.LogInformation("Startup: tray icon {State} at +{Ms:0} ms", added ? "added" : "pending (shell not ready)", StartupClock.ElapsedMs);

        // ── providers and engine, composed by hand (no DI container in the resident process).
        var logitech = new LogitechHidppBatteryProvider(_loggers.CreateLogger<LogitechHidppBatteryProvider>());
        _batteryProviders = new IBatteryProvider[]
        {
            new WindowsBluetoothBatteryProvider(_loggers.CreateLogger<WindowsBluetoothBatteryProvider>()),
            new WindowsSystemBatteryProvider(_loggers.CreateLogger<WindowsSystemBatteryProvider>()),
            logitech,
            new AsusRogBatteryProvider(_loggers.CreateLogger<AsusRogBatteryProvider>()),
        };
        var hardware = new LibreHardwareMonitorService(_loggers.CreateLogger<LibreHardwareMonitorService>());
        var rgb = new OpenRgbController(_loggers.CreateLogger<OpenRgbController>());
        var rgbLauncher = new OpenRgbServerLauncher(_loggers.CreateLogger<OpenRgbServerLauncher>());
        _engineParts = new IDisposable[] { hardware, rgb, rgbLauncher };

        _agent = new AgentCore(new AgentCoreOptions
        {
            LoggerFactory = _loggers,
            SettingsStore = settingsStore,
            StateStore = new JsonStateStore(JsonStateStore.DefaultPath, _loggers.CreateLogger<JsonStateStore>()),
            Hardware = hardware,
            Rgb = rgb,
            RgbLauncher = rgbLauncher,
            BatteryProviders = _batteryProviders,
            Elevation = elevation,
            AutoStart = new WindowsAutoStartService(_loggers.CreateLogger<WindowsAutoStartService>()),
            TelemetryDirectory = Path.GetDirectoryName(_logPath),
            LogPath = _logPath,
            IsDemo = false,
            Version = Program.Version,
            // From an open popover the new agent reopens it; from the tray menu it stays in the tray.
            RelaunchElevated = showUi => showUi
                ? elevation.RelaunchElevated(new[] { AgentOptions.ShowUiArgument })
                : elevation.RelaunchElevated(new[] { AgentOptions.MinimizedArgument }, new[] { AgentOptions.ShowUiArgument }),
        });
        _engineParts = Array.Empty<IDisposable>(); // the engine disposes hardware / RGB / launcher in ShutdownAsync
        Program.SetAgent(_agent);
        var agent = _agent;

        _coordinator = new ShutdownCoordinator(agent.ShutdownAsync, agent.RestoreFansOnAbnormalExit, _loggers.CreateLogger("Pulse.Agent.Shutdown"));
        var coordinator = _coordinator;
        var window = _window;
        coordinator.Started = () => _controller?.BeginShutdown();
        coordinator.Completed = window.RequestExit;

        _uiLauncher = new UiLauncher(agent, _loggers.CreateLogger("Pulse.Agent.UiLauncher"));
        _controller = new TrayController(window, _icon, renderer, agent, _uiLauncher,
            () => coordinator.Request(AgentExitReason.Quit, "tray menu"), trayLog);
        _controller.Attach();

        agent.ShutdownRequested += (_, reason) => coordinator.Request(reason, "engine");
        agent.HardwareUpdated += OnHardwareUpdated;
        window.ShutdownRequested = source => coordinator.Request(AgentExitReason.Quit, source);
        window.WaitForShutdown = coordinator.Wait;
        window.DevicesChanged = () =>
        {
            try
            {
                agent.NotifyDevicesChanged();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Device-change notification failed");
            }
        };

        // ── pipe server, then the engine (it loads settings + state, starts hardware, batteries and RGB without waiting).
        _ipc = new IpcServer(agent, AgentPipeFactory.Create(IpcProtocol.PipeName, _loggers.CreateLogger("Pulse.Agent.Pipe")),
            _loggers.CreateLogger<IpcServer>());
        _ipc.Start();
        agent.StartAsync().GetAwaiter().GetResult();

        instance.StartListening(
            onShowRequested: () => window.Post(() =>
            {
                _log.LogInformation("Another Pulse.exe asked to show the popover");
                if (!coordinator.IsStarted) _uiLauncher.Show(toggle: false, tab: null);
            }),
            onQuitRequested: () => coordinator.Request(AgentExitReason.Quit, "--quit"));

        _controller.Start();
        if (_options.OpenUiAtStart) _uiLauncher.Show(toggle: false, tab: _options.Tab);
        _log.LogInformation("Startup: agent running at +{Ms:0} ms (elevated={Elevated})", StartupClock.ElapsedMs, elevation.IsElevated);
    }

    private void OnHardwareUpdated(object? sender, HardwareFrame frame)
    {
        if (Interlocked.Exchange(ref _firstFrameLogged, 1) != 0) return;
        if (_agent is { } agent) agent.HardwareUpdated -= OnHardwareUpdated;
        _log.LogInformation("Startup: first hardware snapshot at +{Ms:0} ms", StartupClock.ElapsedMs);
    }

    /// <summary>Runs on the main thread after the loop (or after a failed start); every step is bounded and never throws.</summary>
    private void Teardown()
    {
        if (_coordinator is { } coordinator)
        {
            coordinator.Request(AgentExitReason.Quit, "exit");
            if (!coordinator.Wait(FinalShutdownWait)) _log.LogError("Shutdown still running after {Seconds} s; exiting anyway", FinalShutdownWait.TotalSeconds);
        }

        _controller?.Dispose();

        if (_ipc is { } ipc)
        {
            try
            {
                if (!ipc.DisposeAsync().AsTask().Wait(IpcDisposeBudget)) _log.LogWarning("Pipe server did not close within {Seconds} s", IpcDisposeBudget.TotalSeconds);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Pipe server dispose failed");
            }
        }

        // Hardware / RGB / launcher were never handed to an engine (its construction failed): release them here.
        foreach (var part in _engineParts) DisposeQuietly(part);
        foreach (var provider in _batteryProviders)
        {
            if (provider is IDisposable disposable) DisposeQuietly(disposable);
        }

        if (_icon is not null) DisposeQuietly(_icon);
        if (_window is not null) DisposeQuietly(_window);
    }

    private void DisposeQuietly(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Disposing {Type} failed", disposable.GetType().Name);
        }
    }
}
