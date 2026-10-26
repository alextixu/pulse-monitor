using Pulse.Core.FanControl;
using Xunit;

namespace Pulse.Core.Tests.FanControl;

/// <summary>Pins the EMA-with-time-constant smoothing (src/Pulse.Core/FanControl/TemperatureSmoother.cs).</summary>
public sealed class TemperatureSmootherTests
{
    private static readonly TimeSpan Tau = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static double Ema(double previous, double raw, double dtSeconds)
        => previous + (1 - Math.Exp(-dtSeconds / Tau.TotalSeconds)) * (raw - previous);

    [Fact]
    public void FirstSample_ReturnsTheRawValue()
    {
        var s = new TemperatureSmoother(Tau);
        Assert.Equal(63.5, s.Update("cpu", 63.5, T0));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(10.0)]  // one time constant: ~63 % of the step
    [InlineData(25.0)]
    public void Update_AppliesAlphaEqualToOneMinusExpOfMinusDtOverTau(double dt)
    {
        var s = new TemperatureSmoother(Tau);
        s.Update("cpu", 50, T0);

        var v = s.Update("cpu", 60, T0.AddSeconds(dt));

        Assert.Equal(Ema(50, 60, dt), v, 1e-12);
    }

    [Fact]
    public void Update_AfterOneTimeConstant_IsAbout63PercentOfTheStep()
    {
        var s = new TemperatureSmoother(Tau);
        s.Update("cpu", 0, T0);
        Assert.Equal(63.21, s.Update("cpu", 100, T0.AddSeconds(10)), 0.01);
    }

    [Fact]
    public void Update_ChainsFromThePreviousSmoothedValue()
    {
        var s = new TemperatureSmoother(Tau);
        s.Update("cpu", 50, T0);
        var v1 = s.Update("cpu", 70, T0.AddSeconds(5));
        var v2 = s.Update("cpu", 70, T0.AddSeconds(12));

        Assert.Equal(Ema(Ema(50, 70, 5), 70, 7), v2, 1e-12);
        Assert.True(v2 > v1);
    }

    [Fact]
    public void Gap_LongerThanThreeTimeConstants_RestartsFromTheRawValue()
    {
        var s = new TemperatureSmoother(Tau);
        s.Update("cpu", 50, T0);

        Assert.Equal(80, s.Update("cpu", 80, T0.AddSeconds(30.001)));
    }

    [Fact]
    public void Gap_OfExactlyThreeTimeConstants_StillSmooths()
    {
        var s = new TemperatureSmoother(Tau);
        s.Update("cpu", 50, T0);

        Assert.Equal(Ema(50, 80, 30), s.Update("cpu", 80, T0.AddSeconds(30)), 1e-12);
    }

    [Fact]
    public void TimeGoingBackwards_RestartsFromTheRawValue_AndContinuesFromThere()
    {
        var s = new TemperatureSmoother(Tau);
        s.Update("cpu", 50, T0.AddSeconds(100));

        Assert.Equal(80, s.Update("cpu", 80, T0.AddSeconds(90)));
        Assert.Equal(Ema(80, 60, 5), s.Update("cpu", 60, T0.AddSeconds(95)), 1e-12);
    }

    [Fact]
    public void SameTimestamp_ReturnsThePreviousValue_WithoutChangingState()
    {
        var s = new TemperatureSmoother(Tau);
        s.Update("cpu", 50, T0);
        var v1 = s.Update("cpu", 70, T0.AddSeconds(5));

        Assert.Equal(v1, s.Update("cpu", 99, T0.AddSeconds(5)));                      // second fan in the same sample
        Assert.Equal(Ema(v1, 70, 5), s.Update("cpu", 70, T0.AddSeconds(10)), 1e-12); // the 99 was never folded in
    }

    [Fact]
    public void Sources_AreIndependent()
    {
        var s = new TemperatureSmoother(Tau);
        s.Update("cpu", 50, T0);
        Assert.Equal(40, s.Update("gpu", 40, T0)); // first GPU sample is raw even though the CPU has state

        s.Update("gpu", 60, T0.AddSeconds(10));
        Assert.Equal(Ema(50, 50, 10), s.Update("cpu", 50, T0.AddSeconds(10)), 1e-12);
    }

    [Fact]
    public void Reset_ForgetsEverySource()
    {
        var s = new TemperatureSmoother(Tau);
        s.Update("cpu", 50, T0);
        s.Reset();

        Assert.Equal(80, s.Update("cpu", 80, T0.AddSeconds(1)));
    }

    [Fact]
    public void Constructor_RejectsANonPositiveTimeConstant()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemperatureSmoother(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemperatureSmoother(TimeSpan.FromSeconds(-1)));
    }
}
