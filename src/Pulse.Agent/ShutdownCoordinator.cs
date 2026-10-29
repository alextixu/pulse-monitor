using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Pulse.Core.Abstractions;

namespace Pulse.Agent;

/// <summary>
/// Runs the agent's clean shutdown exactly once, whoever asks first (tray 結束, the engine's ShutdownRequested, the
/// Global\Pulse.Quit event, WM_QUERYENDSESSION, WM_CLOSE). It runs on the thread pool so the tray window keeps pumping
/// (WM_ENDSESSION has to be able to join it, and a window that stops answering hangs broadcasting senders). When it
/// finishes, <see cref="Completed"/> lets the message loop end. A shutdown that throws or takes longer than 15 s falls back
/// to the synchronous fan hand-back so the process can still exit (the --quit waiter gives up after 20 s).
/// </summary>
internal sealed class ShutdownCoordinator
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    private readonly Func<AgentExitReason, Task> _shutdown;
    private readonly Action<string> _restoreFans;
    private readonly ILogger _log;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;

    public ShutdownCoordinator(Func<AgentExitReason, Task> shutdown, Action<string> restoreFans, ILogger log)
    {
        _shutdown = shutdown;
        _restoreFans = restoreFans;
        _log = log;
    }

    /// <summary>Raised once, on the requesting thread, when the shutdown begins.</summary>
    public Action? Started { get; set; }

    /// <summary>Raised once, on a pool thread, after the shutdown finished (or was abandoned).</summary>
    public Action? Completed { get; set; }

    public bool IsStarted => Volatile.Read(ref _started) != 0;

    public AgentExitReason? Reason { get; private set; }

    public void Request(AgentExitReason reason, string source)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            _log.LogDebug("Shutdown already in progress (asked again by {Source})", source);
            return;
        }

        Reason = reason;
        _log.LogInformation("Shutting down ({Reason}, requested by {Source})", reason, source);
        try { Started?.Invoke(); } catch (Exception ex) { _log.LogDebug(ex, "Shutdown start notification failed"); }
        _ = Task.Run(() => RunAsync(reason));
    }

    /// <summary>Waits for the shutdown to finish; false on timeout.</summary>
    public bool Wait(TimeSpan timeout) => _done.Task.Wait(timeout);

    private async Task RunAsync(AgentExitReason reason)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var shutdown = _shutdown(reason);
            if (await Task.WhenAny(shutdown, Task.Delay(Budget)).ConfigureAwait(false) != shutdown)
            {
                _log.LogError("Agent shutdown did not finish within {Seconds} s; restoring fans directly", Budget.TotalSeconds);
                RestoreFans("shutdown timeout");
            }
            else
            {
                await shutdown.ConfigureAwait(false);
                _log.LogInformation("Agent shut down in {Ms} ms", stopwatch.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Agent shutdown failed; restoring fans directly");
            RestoreFans("shutdown failure");
        }
        finally
        {
            _done.TrySetResult();
            try { Completed?.Invoke(); } catch (Exception ex) { _log.LogDebug(ex, "Shutdown completion notification failed"); }
        }
    }

    private void RestoreFans(string reason)
    {
        try
        {
            _restoreFans(reason);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Direct fan restore failed");
        }
    }
}
