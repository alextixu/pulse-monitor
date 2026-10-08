namespace Pulse.Core.FanControl;

/// <summary>
/// Exponential moving average per temperature source with a time constant, so samples taken at any interval weigh
/// the same: after one time constant a step change is ~63 % through. A gap longer than three time constants (or a
/// missing reading) restarts from the raw value. Not thread-safe; the fan-mode controller calls it under its gate.
/// </summary>
public sealed class TemperatureSmoother
{
    private readonly TimeSpan _timeConstant;
    private readonly Dictionary<string, (double Value, DateTimeOffset At)> _state = new(StringComparer.Ordinal);

    public TemperatureSmoother(TimeSpan timeConstant)
    {
        if (timeConstant <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeConstant));
        _timeConstant = timeConstant;
    }

    /// <summary>Feeds a raw reading for <paramref name="source"/> taken at <paramref name="now"/> and returns the smoothed value.</summary>
    public double Update(string source, double raw, DateTimeOffset now)
    {
        if (!_state.TryGetValue(source, out var previous) || now - previous.At > 3 * _timeConstant || now < previous.At)
        {
            _state[source] = (raw, now);
            return raw;
        }

        var dt = (now - previous.At).TotalSeconds;
        if (dt <= 0) return previous.Value; // several fans share one source within the same sample
        var alpha = 1 - Math.Exp(-dt / _timeConstant.TotalSeconds);
        var value = previous.Value + alpha * (raw - previous.Value);
        _state[source] = (value, now);
        return value;
    }

    public void Reset() => _state.Clear();
}
