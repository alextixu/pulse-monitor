using Pulse.Core.Localization;
using Pulse.Core.Models;
using Pulse.Engine.Memory;

namespace Pulse.Engine.Tests.Memory;

public class LowBatteryWatcherTests
{
    private static BatteryDevice Device(int? percent, BatteryStatus status = BatteryStatus.Discharging, bool connected = true, string id = "mouse")
        => new() { Id = id, Name = "G PRO", Source = "Logitech", Percent = percent, Status = status, IsConnected = connected };

    private static AppSettings Settings(int threshold = 20, bool notify = true, params string[] hidden)
        => new() { LowBatteryThresholdPercent = threshold, NotifyOnLowBattery = notify, HiddenDeviceIds = hidden.ToList() };

    [Fact]
    public void AlertsOnceAtOrBelowTheThreshold()
    {
        var watcher = new LowBatteryWatcher(null);

        Assert.Empty(watcher.Evaluate(new[] { Device(21) }, Settings(), out _));
        var toast = Assert.Single(watcher.Evaluate(new[] { Device(20) }, Settings(), out var changed));
        Assert.True(changed);
        Assert.Equal(Strings.LowBatteryTitle, toast.Title);
        Assert.Equal(string.Format(Strings.LowBatteryToastFormat, "G PRO", 20), toast.Message);

        Assert.Empty(watcher.Evaluate(new[] { Device(15) }, Settings(), out changed));
        Assert.False(changed);
    }

    [Theory]
    [InlineData(BatteryStatus.Charging, true, false, false)]
    [InlineData(BatteryStatus.Full, true, false, false)]
    [InlineData(BatteryStatus.Discharging, false, false, false)]
    [InlineData(BatteryStatus.Discharging, true, true, false)]
    [InlineData(BatteryStatus.Discharging, true, false, true)]
    public void SkipsChargingDisconnectedHiddenOrDisabled(BatteryStatus status, bool connected, bool hidden, bool disabled)
    {
        var watcher = new LowBatteryWatcher(null);
        var settings = Settings(notify: !disabled, hidden: hidden ? new[] { "mouse" } : Array.Empty<string>());
        Assert.Empty(watcher.Evaluate(new[] { Device(5, status, connected) }, settings, out _));
    }

    [Fact]
    public void ChargingMakesTheDeviceEligibleAgain()
    {
        var watcher = new LowBatteryWatcher(null);
        Assert.Single(watcher.Evaluate(new[] { Device(10) }, Settings(), out _));
        watcher.Evaluate(new[] { Device(11, BatteryStatus.Charging) }, Settings(), out var changed);
        Assert.True(changed);
        Assert.Empty(watcher.Notified);
        Assert.Single(watcher.Evaluate(new[] { Device(12) }, Settings(), out _));
    }

    [Fact]
    public void RisingFivePercentAboveTheThresholdResets()
    {
        var watcher = new LowBatteryWatcher(null);
        Assert.Single(watcher.Evaluate(new[] { Device(18) }, Settings(20), out _));

        watcher.Evaluate(new[] { Device(24) }, Settings(20), out _);
        Assert.Contains("mouse", watcher.Notified);
        watcher.Evaluate(new[] { Device(25) }, Settings(20), out _);
        Assert.DoesNotContain("mouse", watcher.Notified);
    }

    [Fact]
    public void ADisconnectedReadingDoesNotReset()
    {
        var watcher = new LowBatteryWatcher(new[] { "mouse" });
        watcher.Evaluate(new[] { Device(90, BatteryStatus.Unknown, connected: false) }, Settings(), out var changed);
        Assert.False(changed);
        Assert.Contains("mouse", watcher.Notified);
    }

    [Fact]
    public void ThePersistedSetSuppressesTheAlertAfterARestart()
    {
        var watcher = new LowBatteryWatcher(new[] { "mouse" });
        Assert.Empty(watcher.Evaluate(new[] { Device(10) }, Settings(), out _));
    }

    [Fact]
    public void ForgetsDevicesTheMemoryNoLongerKnows()
    {
        var watcher = new LowBatteryWatcher(new[] { "mouse", "expired" });
        watcher.Evaluate(new[] { Device(10) }, Settings(), out var changed);
        Assert.True(changed);
        Assert.Equal(new[] { "mouse" }, watcher.Notified);
    }
}
