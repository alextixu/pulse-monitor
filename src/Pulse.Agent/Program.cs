using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Pulse.Engine;

namespace Pulse.Agent;

/// <summary>
/// Pulse.exe — the resident agent. Exit codes: 0 clean exit / already running / --quit done / elevated copy started,
/// 1 fatal error or --quit timeout, 2 startup failure (the scheduled task's restart-on-failure relies on 0 meaning "on purpose").
/// </summary>
internal static class Program
{
    /// <summary>How long an "--elevated-relaunch" instance waits for the previous instance to release the mutex.</summary>
    private static readonly TimeSpan RelaunchMutexWait = TimeSpan.FromSeconds(10);

    /// <summary>How long "--quit" waits for the running instance to finish its clean shutdown (decisions-v2 §1).</summary>
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The engine, once composed: the crash hooks hand the fans back through it.</summary>
    private static AgentCore? s_agent;

    private static int s_hooksInstalled;

    public static string Version { get; } = ReadVersion();

    [STAThread]
    public static int Main(string[] args)
    {
        var options = AgentOptions.Parse(args);
        // Developer smoke test of the tray pieces: no log in %LOCALAPPDATA%, no single-instance objects, no engine.
        if (options.TraySelfTest) return TraySelfTest.Run();

        using var fileLogger = new FileLoggerProvider(FileLoggerProvider.DefaultPath);
        using var loggerFactory = AppLogging.CreateFactory(fileLogger);
        var log = loggerFactory.CreateLogger("Pulse.Agent.Program");
        log.LogInformation("Pulse agent {Version} starting (pid {Pid}, args: {Args}); Main at +{Ms:0} ms",
            Version, Environment.ProcessId, string.Join(' ', args), StartupClock.ElapsedMs);

        if (options.Quit)
        {
            return SingleInstance.RequestQuit(QuitTimeout, log) ? 0 : 1;
        }

        var instance = SingleInstance.TryAcquire(options.ElevatedRelaunch ? RelaunchMutexWait : TimeSpan.Zero, log);
        if (instance is null)
        {
            log.LogInformation("Another instance is already running; exiting");
            return 0;
        }

        try
        {
            var code = new AgentHost(options, loggerFactory, fileLogger.FilePath).Run(instance);
            log.LogInformation("Pulse agent exited with code {Code}", code);
            return code;
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Fatal error");
            return 1;
        }
        finally
        {
            // Same (main) thread that acquired the mutex: only the owner can release it, and --quit waits for that.
            instance.Dispose();
        }
    }

    /// <summary>Called by <see cref="AgentHost"/> as soon as the engine exists.</summary>
    internal static void SetAgent(AgentCore? agent) => Volatile.Write(ref s_agent, agent);

    /// <summary>
    /// Process-wide hooks: an unhandled exception that terminates the process, or an exit that bypasses the clean path
    /// (Environment.Exit, end of Main after a failure), hands manual fans back synchronously first. A no-op after the clean
    /// shutdown (the engine makes RestoreFansOnAbnormalExit idempotent).
    /// </summary>
    internal static void InstallCrashHooks(ILogger log)
    {
        if (Interlocked.Exchange(ref s_hooksInstalled, 1) != 0) return;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                if (e.ExceptionObject is Exception ex) log.LogCritical(ex, "Unhandled exception (terminating={Terminating})", e.IsTerminating);
                else log.LogCritical("Unhandled non-exception object: {Object}", e.ExceptionObject);
            }
            catch
            {
                // Logging must not stand between the crash and the fan hand-back.
            }
            if (e.IsTerminating) RestoreFans("unhandled exception");
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreFans("process exit");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    private static void RestoreFans(string reason)
    {
        var agent = Volatile.Read(ref s_agent);
        if (agent is null) return;
        try
        {
            agent.RestoreFansOnAbnormalExit(reason);
        }
        catch
        {
            // Nothing more can be done while the process is going down.
        }
    }

    private static string ReadVersion()
    {
        var assembly = typeof(Program).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString(3)
                      ?? "0.0.0";
        var plus = version.IndexOf('+'); // drop the source-revision suffix ("0.1.0+abc123")
        return plus > 0 ? version[..plus] : version;
    }
}

/// <summary>Milliseconds since the OS started this process (startup deltas in the log, decisions-v2 §11).</summary>
internal static class StartupClock
{
    private static readonly DateTime ProcessStart = ReadStart();

    public static double ElapsedMs => (DateTime.Now - ProcessStart).TotalMilliseconds;

    private static DateTime ReadStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime;
        }
        catch
        {
            return DateTime.Now;
        }
    }
}
