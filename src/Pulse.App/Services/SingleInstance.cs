using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace Pulse.App.Services;

/// <summary>
/// Named-mutex single-instance guard. The first instance owns the mutex and listens on two named events:
/// "show" (a second instance asks it to open its popover) and "quit" (<c>Pulse.exe --quit</c> asks it to exit cleanly,
/// which hands manual fans back to the firmware — unlike killing the process).
/// The objects grant authenticated users signal/wait access, so a normal process can reach an elevated instance.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Global\Pulse.SingleInstance";
    public const string ShowEventName = @"Global\Pulse.ShowPopover";
    public const string QuitEventName = @"Global\Pulse.Quit";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _showEvent;
    private readonly EventWaitHandle? _quitEvent;
    private readonly List<RegisteredWaitHandle> _registrations = new();
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle? showEvent, EventWaitHandle? quitEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        _quitEvent = quitEvent;
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
            mutex = CreateMutex();
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists and belongs to an instance we may not fully open (older build or other ACL): it is running.
            SignalExisting(log);
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Single-instance mutex unavailable; continuing without guard");
            return new SingleInstance(new Mutex(false), null, null);
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

        return new SingleInstance(mutex, CreateEvent(ShowEventName, log), CreateEvent(QuitEventName, log));
    }

    /// <summary>
    /// Asks the running instance to exit cleanly and waits until it released the mutex.
    /// Returns true when it exited (or none was running), false on timeout.
    /// </summary>
    public static bool RequestQuit(TimeSpan timeout, ILogger log)
    {
        if (!EventWaitHandle.TryOpenExisting(QuitEventName, out var quit))
        {
            log.LogInformation("--quit: no running instance");
            return true;
        }

        using (quit) quit.Set();
        log.LogInformation("--quit: signalled the running instance");

        if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return true;
        using (mutex)
        {
            try
            {
                if (!mutex.WaitOne(timeout, false))
                {
                    log.LogWarning("--quit: the running instance did not exit within {Timeout} s", timeout.TotalSeconds);
                    return false;
                }
                mutex.ReleaseMutex();
            }
            catch (AbandonedMutexException)
            {
                // It ended without releasing; still gone.
            }
            return true;
        }
    }

    /// <summary>Invokes the callbacks (thread-pool thread) each time another process signals us.</summary>
    public void StartListening(Action onShowRequested, Action onQuitRequested)
    {
        if (_registrations.Count > 0) return;
        Register(_showEvent, onShowRequested);
        Register(_quitEvent, onQuitRequested);
    }

    private void Register(EventWaitHandle? handle, Action callback)
    {
        if (handle is null) return;
        _registrations.Add(ThreadPool.RegisterWaitForSingleObject(handle, (_, _) =>
        {
            if (!_disposed) callback();
        }, null, Timeout.Infinite, false));
    }

    private static Mutex CreateMutex()
    {
        if (!OperatingSystem.IsWindows()) return new Mutex(false, MutexName, out _);

        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(CurrentUser(), MutexRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(AuthenticatedUsers(), MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
        return MutexAcl.Create(false, MutexName, out _, security);
    }

    private static EventWaitHandle? CreateEvent(string name, ILogger log)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return new EventWaitHandle(false, EventResetMode.AutoReset, name, out _);

            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(CurrentUser(), EventWaitHandleRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new EventWaitHandleAccessRule(AuthenticatedUsers(),
                EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
            return EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out _, security);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Event {Name} unavailable; other processes cannot signal us", name);
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
        foreach (var registration in _registrations) registration.Unregister(null);
        _showEvent?.Dispose();
        _quitEvent?.Dispose();
        try { _mutex.ReleaseMutex(); } catch { /* not owned on this thread */ }
        _mutex.Dispose();
    }
}
