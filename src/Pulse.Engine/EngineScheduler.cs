using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Pulse.Engine;

/// <summary>
/// The engine thread: one dedicated thread draining a queue of actions. Every piece of mutable engine state is touched
/// only from here and every engine event is raised from here, so none of it needs locks. A failing action is logged
/// and the loop carries on (one bad handler must never stop the engine). While an action runs, a synchronization
/// context is installed that posts back to this thread, so <c>await</c> inside an action resumes on the engine thread.
/// </summary>
public sealed class EngineScheduler : IDisposable
{
    private readonly Channel<Action> _queue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });

    private readonly ILogger _log;
    private readonly Thread _thread;
    private readonly EngineSynchronizationContext _context;
    private volatile int _threadId;
    private volatile bool _stopping;

    public EngineScheduler(ILogger log, string threadName = "Pulse.Engine")
    {
        _log = log;
        _context = new EngineSynchronizationContext(this);
        _thread = new Thread(Run) { Name = threadName, IsBackground = true };
        _thread.Start();
    }

    /// <summary>True when called on the engine thread.</summary>
    public bool CheckAccess() => Environment.CurrentManagedThreadId == _threadId;

    /// <summary>False once <see cref="Stop"/> was called (later posts are dropped).</summary>
    public bool IsRunning => !_stopping;

    /// <summary>Queues <paramref name="action"/>; returns false when the scheduler has stopped.</summary>
    public bool Post(Action action)
    {
        if (_queue.Writer.TryWrite(action)) return true;
        _log.LogDebug("Engine thread stopped; an action was dropped");
        return false;
    }

    /// <summary>Runs <paramref name="action"/> on the engine thread (inline when already there).</summary>
    public Task InvokeAsync(Action action) => InvokeAsync(() =>
    {
        action();
        return true;
    });

    /// <summary>Runs <paramref name="func"/> on the engine thread (inline when already there) and returns its result.</summary>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        if (CheckAccess())
        {
            try
            {
                return Task.FromResult(func());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(() =>
            {
                try
                {
                    tcs.TrySetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(EngineScheduler)));
        }
        return tcs.Task;
    }

    /// <summary>
    /// Starts an async operation on the engine thread; its continuations stay on the engine thread (unless they use
    /// ConfigureAwait(false)). Used for command handlers that await I/O between state changes.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<Task<T>> func) => InvokeAsync<Task<T>>(func).Unwrap();

    /// <summary>
    /// Stops accepting work, runs what is already queued and waits (bounded) for the thread to end. Safe to call from
    /// the engine thread itself (it then does not wait).
    /// </summary>
    public bool Stop(TimeSpan timeout)
    {
        _stopping = true;
        _queue.Writer.TryComplete();
        if (CheckAccess()) return true;
        return _thread.Join(timeout);
    }

    public void Dispose() => Stop(TimeSpan.FromSeconds(2));

    private void Run()
    {
        _threadId = Environment.CurrentManagedThreadId;
        SynchronizationContext.SetSynchronizationContext(_context);
        var reader = _queue.Reader;
        try
        {
            while (true)
            {
                while (reader.TryRead(out var action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Engine action failed");
                    }
                }

                // Blocking wait is fine: this thread exists only to run the queue.
                if (!reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult()) break;
            }
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Engine thread terminated unexpectedly");
        }
    }

    /// <summary>Posts continuations back to the engine thread.</summary>
    private sealed class EngineSynchronizationContext : SynchronizationContext
    {
        private readonly EngineScheduler _owner;

        public EngineSynchronizationContext(EngineScheduler owner) => _owner = owner;

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!_owner.Post(() => d(state)))
            {
                // Stopped: run on the pool rather than losing the continuation (shutdown paths still complete).
                ThreadPool.QueueUserWorkItem(_ => d(state));
            }
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (_owner.CheckAccess()) d(state);
            else _owner.InvokeAsync(() => d(state)).GetAwaiter().GetResult();
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
