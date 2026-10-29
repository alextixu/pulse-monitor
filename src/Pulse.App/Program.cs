using System.Diagnostics;
using Avalonia;
using Pulse.App.Services;
using Pulse.Engine;
using Microsoft.Extensions.Logging;

namespace Pulse.App;

internal static class Program
{
    /// <summary>
    /// How long a second launch waits for a popover that is just exiting (it hid a moment ago and no longer reacts to the
    /// show signal) before giving up.
    /// </summary>
    private static readonly TimeSpan SingleInstanceHandOver = TimeSpan.FromSeconds(2);

    /// <summary>%LOCALAPPDATA%\Pulse\logs\ui.log (the agent writes app.log next to it).</summary>
    public static string UiLogPath => Path.Combine(Path.GetDirectoryName(FileLoggerProvider.DefaultPath)!, "ui.log");

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);
        var fileLogger = new FileLoggerProvider(UiLogPath);
        using var loggerFactory = AppLogging.CreateFactory(fileLogger);
        var log = loggerFactory.CreateLogger("Pulse.App.Program");

        InstallGlobalExceptionLogging(log);
        log.LogInformation("Pulse.UI starting (pid {Pid}, args: {Args}); Main at +{Ms:0} ms",
            Environment.ProcessId, string.Join(' ', args), MillisecondsSinceStart());

        UiSingleInstance? instance = null;
        if (!options.Demo)
        {
            instance = UiSingleInstance.TryAcquire(SingleInstanceHandOver, log);
            if (instance is null)
            {
                log.LogInformation("Another popover is running; exiting");
                return 0;
            }
        }

        App.Options = options;
        App.LoggerFactory = loggerFactory;
        App.Instance = instance;

        try
        {
            var code = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            log.LogInformation("Pulse.UI exited with code {Code}", code);
            return code;
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Fatal error");
            return 1;
        }
        finally
        {
            // Released on the thread that acquired it, after the pipe was closed (see App.ShutdownAsync).
            instance?.Dispose();
            fileLogger.Dispose();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>Time since the OS started this process (startup deltas in the log).</summary>
    public static double MillisecondsSinceStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return (DateTime.Now - process.StartTime).TotalMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    private static void InstallGlobalExceptionLogging(ILogger log)
    {
        // The popover holds no fans or devices: unhandled exceptions are only logged (the agent restores fans itself).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) log.LogCritical(ex, "Unhandled exception (terminating={Terminating})", e.IsTerminating);
            else log.LogCritical("Unhandled non-exception object: {Object}", e.ExceptionObject);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }
}
