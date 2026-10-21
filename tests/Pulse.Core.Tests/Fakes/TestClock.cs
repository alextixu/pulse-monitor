namespace Pulse.Core.Tests.Fakes;

/// <summary>Injectable clock for <c>FanModeController</c> / <c>TemperatureSmoother</c>: time only moves when a test says so.</summary>
public sealed class TestClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public Func<DateTimeOffset> Func => () => Now;

    public DateTimeOffset Advance(TimeSpan by) => Now += by;

    public DateTimeOffset AdvanceSeconds(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
}
