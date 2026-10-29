using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace Pulse.App.Services;

/// <summary>
/// One popover per session: a <c>Local\Pulse.UI</c> mutex plus a <c>Local\Pulse.UI.Show</c> event a second launch sets so
/// the running popover comes to the front. Same ACL pattern as the agent's guard (current user full control,
/// authenticated users synchronize/modify) so an elevated popover (child of an elevated agent) and a normal launch of
/// the same user can still reach each other. Skipped in demo / screenshot runs.
/// </summary>
public sealed class UiSingleInstance : IDisposable
{
    public const string MutexName = @"Local\Pulse.UI";
    public const string ShowEventName = @"Local\Pulse.UI.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _registration;
    private volatile bool _listening;
    private bool _disposed;

    private UiSingleInstance(Mutex mutex, EventWaitHandle? showEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
    }

    /// <summary>
    /// Becomes the running popover, or signals the running one and returns null. A popover that is just exiting (it
    /// hid a moment ago) no longer reacts to the signal, so after signalling this waits up to <paramref name="handOver"/>
    /// for the mutex: getting it means the other one is gone and this launch takes over.
    /// </summary>
    public static UiSingleInstance? TryAcquire(TimeSpan handOver, ILogger log)
    {
        Mutex mutex;
        try
        {
            mutex = CreateMutex();
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists under an ACL we cannot open: another popover runs.
            SignalExisting(log);
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "UI single-instance mutex unavailable; continuing without guard");
            return new UiSingleInstance(new Mutex(false), null);
        }

        if (!Wait(mutex, TimeSpan.Zero))
        {
            SignalExisting(log);
            if (!Wait(mutex, handOver))
            {
                mutex.Dispose();
                return null;
            }
            log.LogInformation("The previous popover exited; taking over");
        }

        return new UiSingleInstance(mutex, CreateEvent(log));
    }

    /// <summary>Invokes <paramref name="onShowRequested"/> (thread-pool thread) whenever another launch signals us.</summary>
    public void StartListening(Action onShowRequested)
    {
        if (_registration is not null || _showEvent is null) return;
        _listening = true;
        _registration = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) =>
        {
            if (_listening) onShowRequested();
        }, null, Timeout.Infinite, false);
    }

    /// <summary>From here on show signals are ignored (the process is past the point where it could show again).</summary>
    public void StopListening() => _listening = false;

    private static bool Wait(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout, false);
        }
        catch (AbandonedMutexException)
        {
            return true; // the previous owner died; we hold it now
        }
    }

    private static Mutex CreateMutex()
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(CurrentUser(), MutexRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(AuthenticatedUsers(), MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
        return MutexAcl.Create(false, MutexName, out _, security);
    }

    private static EventWaitHandle? CreateEvent(ILogger log)
    {
        try
        {
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(CurrentUser(), EventWaitHandleRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new EventWaitHandleAccessRule(AuthenticatedUsers(),
                EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
            return EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ShowEventName, out _, security);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Event {Name} unavailable; a second launch cannot bring this popover forward", ShowEventName);
            return null;
        }
    }

    private static SecurityIdentifier AuthenticatedUsers() => new(WellKnownSidType.AuthenticatedUserSid, null);

    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }

    private static void SignalExisting(ILogger log)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
            {
                using (existing) existing.Set();
                log.LogInformation("Another popover is running; asked it to show");
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not signal the running popover");
        }
    }

    /// <summary>Releases the mutex; call on the thread that acquired it (Program.Main).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listening = false;
        _registration?.Unregister(null);
        _showEvent?.Dispose();
        try { _mutex.ReleaseMutex(); } catch { /* not owned on this thread */ }
        _mutex.Dispose();
    }
}
