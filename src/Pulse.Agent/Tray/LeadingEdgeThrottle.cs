namespace Pulse.Agent.Tray;

/// <summary>
/// "Now, then at most once per interval": the first signal fires at once (leading edge); signals arriving within the
/// interval after a fire are folded into one trailing fire when the interval has passed. Used for WM_DEVICECHANGE /
/// WM_DISPLAYCHANGE, where the engine has to react to the first broadcast immediately (it pauses GPU sensor reads before
/// a display driver unloads) but a driver install sends dozens of them. Pure bookkeeping on a caller-supplied clock in
/// milliseconds; single-threaded (the tray window thread).
/// </summary>
internal sealed class LeadingEdgeThrottle
{
    private readonly long _intervalMs;
    private long? _lastFireAt;
    private bool _trailingPending;
    private bool _timerArmed;

    public LeadingEdgeThrottle(TimeSpan interval) => _intervalMs = (long)interval.TotalMilliseconds;

    /// <summary>
    /// A signal arrived at <paramref name="nowMs"/>. Returns whether to fire now, and how long until the caller must call
    /// <see cref="TimerElapsed"/> when a trailing fire was scheduled (null: no timer to arm).
    /// </summary>
    public (bool FireNow, long? ArmTimerMs) Signal(long nowMs)
    {
        if (!_timerArmed && (_lastFireAt is not { } last || nowMs - last >= _intervalMs))
        {
            _lastFireAt = nowMs;
            return (true, null);
        }

        _trailingPending = true;
        if (_timerArmed) return (false, null);

        _timerArmed = true;
        var due = _lastFireAt is { } fired ? Math.Max(1, fired + _intervalMs - nowMs) : 1;
        return (false, due);
    }

    /// <summary>The timer armed by <see cref="Signal"/> elapsed; returns whether the trailing fire happens now.</summary>
    public bool TimerElapsed(long nowMs)
    {
        _timerArmed = false;
        if (!_trailingPending) return false;
        _trailingPending = false;
        _lastFireAt = nowMs;
        return true;
    }
}
