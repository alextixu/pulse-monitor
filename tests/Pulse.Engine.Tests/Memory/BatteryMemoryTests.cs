using Pulse.Core.Localization;
using Pulse.Core.Models;
using Pulse.Engine.Memory;

namespace Pulse.Engine.Tests.Memory;

public class BatteryMemoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 14, 5, 0, TimeSpan.FromHours(8));

    private static BatteryDevice Mouse(int? percent = 80, bool connected = true, BatteryStatus status = BatteryStatus.Discharging, string id = "hidpp:mouse", string name = "G PRO")
        => new() { Id = id, Name = name, Source = "Logitech", Percent = percent, IsConnected = connected, Status = status, Detail = "LIGHTSPEED", VoltageMillivolts = percent is null ? null : 3900 };

    [Fact]
    public void ConnectedReportReplacesTheMemoryAndStampsLastConnected()
    {
        var memory = new BatteryMemory(null, T0);
        var shown = memory.Merge(new[] { Mouse(80) }, T0, out var changed);

        Assert.True(changed);
        Assert.Equal(80, Assert.Single(shown).Percent);
        var remembered = Assert.Single(memory.Export());
        Assert.Equal(T0, remembered.LastConnectedAt);
        Assert.Equal(T0, remembered.LastReportedAt);
    }

    [Fact]
    public void DisconnectedReportKeepsTheRememberedLevel()
    {
        var memory = new BatteryMemory(null, T0);
        memory.Merge(new[] { Mouse(80) }, T0, out _);

        // The provider still knows the device but not its level: the remembered 80 % survives (field-wise merge).
        var shown = memory.Merge(new[] { Mouse(null, connected: false) }, T0.AddSeconds(30), out _);
        var device = Assert.Single(shown);
        Assert.Equal(80, device.Percent);
        Assert.Equal(3900, device.VoltageMillivolts);
    }

    [Fact]
    public void MissingDeviceSleepsForThreePollsAndNinetySecondsThenShowsLastSeen()
    {
        var memory = new BatteryMemory(null, T0);
        memory.Merge(new[] { Mouse(80) }, T0, out _);

        for (var i = 1; i <= 2; i++)
        {
            var sleeping = Assert.Single(memory.Merge(Array.Empty<BatteryDevice>(), T0.AddSeconds(30 * i), out _));
            Assert.True(sleeping.IsConnected);
            Assert.Equal(BatteryStatus.Unknown, sleeping.Status);
            Assert.Equal(Strings.DeviceSleeping, sleeping.Detail);
            Assert.Equal(80, sleeping.Percent);
        }

        var gone = Assert.Single(memory.Merge(Array.Empty<BatteryDevice>(), T0.AddSeconds(90), out _));
        Assert.False(gone.IsConnected);
        Assert.Equal(80, gone.Percent);
        Assert.Equal(EngineStrings.LastSeen(T0, T0.AddSeconds(90)), gone.Detail);
        Assert.Equal("上次 " + T0.ToLocalTime().ToString("HH:mm"), gone.Detail);
    }

    [Fact]
    public void FastPollsStillWaitNinetySeconds()
    {
        var memory = new BatteryMemory(null, T0);
        memory.Merge(new[] { Mouse(80) }, T0, out _);

        for (var i = 1; i <= 17; i++)
        {
            Assert.True(Assert.Single(memory.Merge(Array.Empty<BatteryDevice>(), T0.AddSeconds(5 * i), out _)).IsConnected);
        }
        Assert.False(Assert.Single(memory.Merge(Array.Empty<BatteryDevice>(), T0.AddSeconds(90), out _)).IsConnected);
    }

    [Fact]
    public void FlipToDisconnectedIsAlsoSmoothedAndAReturnResetsIt()
    {
        var memory = new BatteryMemory(null, T0);
        memory.Merge(new[] { Mouse(80) }, T0, out _);

        Assert.True(Assert.Single(memory.Merge(new[] { Mouse(null, connected: false) }, T0.AddSeconds(30), out _)).IsConnected);
        Assert.True(Assert.Single(memory.Merge(new[] { Mouse(79) }, T0.AddSeconds(60), out _)).IsConnected);
        // The miss counter restarted: two more misses are not enough.
        memory.Merge(Array.Empty<BatteryDevice>(), T0.AddSeconds(90), out _);
        Assert.True(Assert.Single(memory.Merge(Array.Empty<BatteryDevice>(), T0.AddSeconds(120), out _)).IsConnected);
        Assert.False(Assert.Single(memory.Merge(Array.Empty<BatteryDevice>(), T0.AddSeconds(150), out _)).IsConnected);
    }

    [Fact]
    public void RememberedDevicesStartDisconnectedWithTheirLastLevel()
    {
        var yesterday = T0.AddDays(-1);
        var stored = new[] { new RememberedBattery { Device = Mouse(55), LastConnectedAt = yesterday, LastReportedAt = yesterday } };
        var memory = new BatteryMemory(stored, T0);

        var device = Assert.Single(memory.Current(T0));
        Assert.False(device.IsConnected);
        Assert.Equal(55, device.Percent);
        Assert.Equal("上次 " + yesterday.ToLocalTime().ToString("M/d HH:mm", System.Globalization.CultureInfo.InvariantCulture), device.Detail);
    }

    [Fact]
    public void ProviderLastConnectedTimeWinsForTheDetail()
    {
        var memory = new BatteryMemory(null, T0);
        var bluetoothSaysLast = T0.AddHours(-3);
        var report = Mouse(null, connected: false) with { LastConnectedAt = bluetoothSaysLast };

        var device = Assert.Single(memory.Merge(new[] { report }, T0, out _));
        Assert.False(device.IsConnected);
        Assert.Equal(EngineStrings.LastSeen(bluetoothSaysLast, T0), device.Detail);
    }

    [Fact]
    public void DevicesNotReportedForThirtyDaysAreForgotten()
    {
        var old = T0.AddDays(-31);
        var stored = new[]
        {
            new RememberedBattery { Device = Mouse(55), LastConnectedAt = old, LastReportedAt = old },
            new RememberedBattery { Device = Mouse(60, id: "bt:kb", name: "Keyboard"), LastConnectedAt = T0.AddDays(-29), LastReportedAt = T0.AddDays(-29) },
        };
        var memory = new BatteryMemory(stored, T0);
        Assert.Equal("bt:kb", Assert.Single(memory.Current(T0)).Id);

        var shown = memory.Merge(Array.Empty<BatteryDevice>(), T0.AddDays(2), out var changed);
        Assert.Empty(shown);
        Assert.True(changed);
        Assert.Empty(memory.Export());
    }

    [Fact]
    public void SortsConnectedFirstThenLowestThenName()
    {
        var memory = new BatteryMemory(null, T0);
        var shown = memory.Merge(new[]
        {
            Mouse(50, id: "a", name: "b-device"),
            Mouse(null, id: "b", name: "unknown level"),
            Mouse(30, connected: false, id: "c", name: "off"),
            Mouse(50, id: "d", name: "A-device"),
            Mouse(20, id: "e", name: "low"),
        }, T0, out _);

        Assert.Equal(new[] { "e", "d", "a", "b", "c" }, shown.Select(d => d.Id));
    }

    [Fact]
    public void UnchangedPollsAreNotWorthAWriteUntilTimestampsMoveTenMinutes()
    {
        var memory = new BatteryMemory(null, T0);
        memory.Merge(new[] { Mouse(80) }, T0, out _);
        memory.Export();

        memory.Merge(new[] { Mouse(80) }, T0.AddMinutes(1), out var changed);
        Assert.False(changed);
        memory.Merge(new[] { Mouse(79) }, T0.AddMinutes(2), out changed);
        Assert.True(changed);
        memory.Export();
        memory.Merge(new[] { Mouse(79) }, T0.AddMinutes(13), out changed);
        Assert.True(changed);
    }

    [Fact]
    public void LastSeenUsesTheTimeOnlyForToday()
    {
        var now = new DateTimeOffset(2026, 10, 27, 23, 0, 0, TimeSpan.Zero).ToLocalTime();
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 9, 7, 0, now.Offset);
        Assert.Equal("上次 09:07", EngineStrings.LastSeen(today, now));
        var earlier = today.AddDays(-3);
        Assert.Equal($"上次 {earlier.Month}/{earlier.Day} 09:07", EngineStrings.LastSeen(earlier, now));
    }
}
