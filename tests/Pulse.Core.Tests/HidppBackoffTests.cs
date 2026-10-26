using Pulse.Logitech;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>Pins the retry schedule a faulted HID++ interface follows: 30 s, 1 min, then every 2 min.</summary>
public sealed class HidppBackoffTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fresh_schedule_is_due_and_inactive()
    {
        var backoff = new HidppBackoff();

        Assert.False(backoff.IsActive);
        Assert.Null(backoff.RetryAt);
        Assert.Equal(0, backoff.Failures);
        Assert.True(backoff.IsDue(T0));
    }

    [Fact]
    public void Consecutive_failures_follow_the_schedule_and_then_stay_at_two_minutes()
    {
        var backoff = new HidppBackoff();
        var expected = new[]
        {
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(2),
        };

        var now = T0;
        for (var i = 0; i < expected.Length; i++)
        {
            var delay = backoff.Fail(now);

            Assert.Equal(expected[i], delay);
            Assert.Equal(i + 1, backoff.Failures);
            Assert.Equal(now + expected[i], backoff.RetryAt);
            now = backoff.RetryAt!.Value;
        }
    }

    [Fact]
    public void Not_due_until_the_delay_has_elapsed()
    {
        var backoff = new HidppBackoff();
        backoff.Fail(T0);

        Assert.True(backoff.IsActive);
        Assert.False(backoff.IsDue(T0));
        Assert.False(backoff.IsDue(T0 + TimeSpan.FromSeconds(29)));
        Assert.True(backoff.IsDue(T0 + TimeSpan.FromSeconds(30)));
        Assert.True(backoff.IsDue(T0 + TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Reset_starts_the_schedule_over()
    {
        var backoff = new HidppBackoff();
        backoff.Fail(T0);
        backoff.Fail(T0);
        Assert.Equal(TimeSpan.FromMinutes(2), backoff.Fail(T0));

        backoff.Reset();

        Assert.False(backoff.IsActive);
        Assert.Equal(0, backoff.Failures);
        Assert.Null(backoff.RetryAt);
        Assert.True(backoff.IsDue(T0));
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.Fail(T0));
    }

    [Fact]
    public void RetrySoon_makes_the_next_attempt_due_but_keeps_the_failure_count()
    {
        var backoff = new HidppBackoff();
        backoff.Fail(T0);
        backoff.Fail(T0);

        backoff.RetrySoon();

        Assert.True(backoff.IsDue(T0));
        Assert.True(backoff.IsActive);
        Assert.Equal(TimeSpan.FromMinutes(2), backoff.Fail(T0));
    }

    [Fact]
    public void RetrySoon_on_a_healthy_interface_changes_nothing()
    {
        var backoff = new HidppBackoff();

        backoff.RetrySoon();

        Assert.False(backoff.IsActive);
        Assert.Null(backoff.RetryAt);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 120)]
    [InlineData(100, 120)]
    public void DelayAfter_maps_failure_count_to_seconds(int failures, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), HidppBackoff.DelayAfter(failures));
    }

    [Theory]
    [InlineData(30, "30 s")]
    [InlineData(60, "1 min")]
    [InlineData(120, "2 min")]
    public void Describe_uses_seconds_below_a_minute_and_minutes_above(int seconds, string text)
    {
        Assert.Equal(text, HidppBackoff.Describe(TimeSpan.FromSeconds(seconds)));
    }
}
