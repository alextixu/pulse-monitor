using Microsoft.Extensions.Logging;

namespace Pulse.App.Services;

/// <summary>
/// Named-mutex single-instance guard. The first instance owns the mutex and listens on a named event;
/// a second instance signals that event (so the first one shows its popover) and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Global\Pulse.SingleInstance";
    public const string ShowEventName = @"Global\Pulse.ShowPopover";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _registration;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle? showEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
    }

    /// <summary>
    /// Tries to become the primary instance. Returns null when another instance already runs (after signalling it).
    /// <paramref name="wait"/> lets an elevated relaunch wait for the previous instance to release the mutex.
    /// </summary>
    public static SingleInstance? TryAcquire(TimeSpan wait, ILogger log)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, MutexName, out _);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Single-instance mutex unavailable; continuing without guard");
            return new SingleInstance(new Mutex(false), null);
        }

        bool owned;
        try
        {
            owned = mutex.WaitOne(wait, false);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // previous owner died; we hold it now
        }

        if (!owned)
        {
            mutex.Dispose();
            SignalExisting(log);
            return null;
        }

        EventWaitHandle? showEvent = null;
        try
        {
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out _);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Show-popover event unavailable; second instances cannot signal us");
        }

        return new SingleInstance(mutex, showEvent);
    }

    /// <summary>Invokes <paramref name="onShowRequested"/> (thread-pool thread) each time another instance signals us.</summary>
    public void StartListening(Action onShowRequested)
    {
        if (_showEvent is null || _registration is not null) return;
        _registration = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) =>
        {
            if (!_disposed) onShowRequested();
        }, null, Timeout.Infinite, false);
    }

    private static void SignalExisting(ILogger log)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
            {
                using (existing) existing.Set();
                log.LogInformation("Signalled the running instance to show its popover");
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not signal the running instance");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registration?.Unregister(null);
        _showEvent?.Dispose();
        try { _mutex.ReleaseMutex(); } catch { /* not owned on this thread */ }
        _mutex.Dispose();
    }
}
