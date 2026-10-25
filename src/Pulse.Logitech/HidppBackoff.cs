namespace Pulse.Logitech;

/// <summary>
/// Retry schedule for one HID++ interface whose handle could not be opened or whose transport faulted (write or read
/// failure): the next attempt is allowed after 30 s, then 1 min, 2 min, 5 min, and every 15 min after that, until a
/// poll succeeds and <see cref="Reset"/> is called. Pure bookkeeping without a clock so the schedule can be unit tested;
/// callers pass "now".
/// </summary>
internal sealed class HidppBackoff
{
    public static readonly IReadOnlyList<TimeSpan> Steps = new[]
    {
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
    };

    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);

    /// <summary>Consecutive failed attempts since the last successful poll.</summary>
    public int Failures { get; private set; }

    /// <summary>Earliest time of the next attempt; null while the interface is not backing off.</summary>
    public DateTimeOffset? RetryAt { get; private set; }

    /// <summary>True after at least one failure and until <see cref="Reset"/>.</summary>
    public bool IsActive => Failures > 0;

    /// <summary>True when an attempt may be made now: never failed, or the current delay has elapsed.</summary>
    public bool IsDue(DateTimeOffset now) => RetryAt is null || now >= RetryAt.Value;

    /// <summary>Records a failed attempt at <paramref name="now"/> and returns the delay until the next one.</summary>
    public TimeSpan Fail(DateTimeOffset now)
    {
        var delay = DelayAfter(Failures);
        Failures++;
        RetryAt = now + delay;
        return delay;
    }

    /// <summary>Clears the schedule after a successful poll.</summary>
    public void Reset()
    {
        Failures = 0;
        RetryAt = null;
    }

    /// <summary>Delay that follows the given number of consecutive failures (0 = the delay after the first failure).</summary>
    public static TimeSpan DelayAfter(int failures)
        => failures >= 0 && failures < Steps.Count ? Steps[failures] : MaxDelay;

    /// <summary>"30 s" / "2 min" for log lines.</summary>
    public static string Describe(TimeSpan delay)
        => delay < TimeSpan.FromMinutes(1) ? $"{delay.TotalSeconds:0} s" : $"{delay.TotalMinutes:0} min";
}
