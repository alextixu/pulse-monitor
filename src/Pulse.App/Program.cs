using Avalonia;
using Pulse.App.Services;
using Microsoft.Extensions.Logging;

namespace Pulse.App;

internal static class Program
{
    /// <summary>How long an "--elevated-relaunch" instance waits for the previous instance to release the mutex.</summary>
    private static readonly TimeSpan RelaunchMutexWait = TimeSpan.FromSeconds(10);

    /// <summary>How long "--quit" waits for the running instance to finish its clean shutdown.</summary>
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(15);

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);
        var fileLogger = new FileLoggerProvider(FileLoggerProvider.DefaultPath);
        using var loggerFactory = LoggerFactory.Create(builder => AppLogging.Configure(builder, fileLogger));
        var log = loggerFactory.CreateLogger("Pulse.App.Program");

        InstallGlobalExceptionLogging(log);

        if (options.Quit)
        {
            return SingleInstance.RequestQuit(QuitTimeout, log) ? 0 : 1;
        }

        SingleInstance? instance = null;
        if (!options.Demo)
        {
            instance = SingleInstance.TryAcquire(options.ElevatedRelaunch ? RelaunchMutexWait : TimeSpan.Zero, log);
            if (instance is null)
            {
                log.LogInformation("Another instance is already running; exiting");
                return 0;
            }
        }

        App.Options = options;
        App.LoggerFactory = loggerFactory;
        App.Instance = instance;

        log.LogInformation("Pulse starting (pid {Pid}, args: {Args})", Environment.ProcessId, string.Join(' ', args));
        try
        {
            var code = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            log.LogInformation("Pulse exited with code {Code}", code);
            return code;
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Fatal error");
            return 1;
        }
        finally
        {
            instance?.Dispose();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void InstallGlobalExceptionLogging(ILogger log)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) log.LogCritical(ex, "Unhandled exception (terminating={Terminating})", e.IsTerminating);
            else log.LogCritical("Unhandled non-exception object: {Object}", e.ExceptionObject);
            // The process is about to die: hand manual fans back before it does.
            if (e.IsTerminating) App.RestoreFansOnAbnormalExit("unhandled exception");
        };
        // Covers exits that skip the Avalonia Exit event (e.g. Environment.Exit, console close). A no-op after a clean exit.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => App.RestoreFansOnAbnormalExit("process exit");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }
}
