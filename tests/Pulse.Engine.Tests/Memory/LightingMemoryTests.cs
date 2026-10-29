using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Pulse.Engine.Memory;
using Pulse.Engine.Tests.Fakes;

namespace Pulse.Engine.Tests.Memory;

public class LightingMemoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 20, 0, 0, TimeSpan.Zero);
    private static readonly RgbColor Red = RgbColor.FromHex("#FF0000");
    private static readonly RgbColor Blue = RgbColor.FromHex("#0000FF");

    private static readonly RgbDevice Strip = FakeRgbController.Device(0, "Strip|HID: \\\\?\\HID#VID_0B05&PID_1939&MI_02#7&abc|", zones: 3);

    private static RgbCommand Cmd(RgbCommandKind kind, RgbColor? color = null, int? zone = null, string? mode = null, int? speed = null, int? brightness = null)
        => new() { Kind = kind, Key = Strip.Key, Color = color, ZoneIndex = zone, ModeName = mode, Speed = speed, Brightness = brightness };

    [Fact]
    public void RecordsOneIntentPerCommandKind()
    {
        var memory = new LightingMemory(null);

        Assert.True(memory.Record(Cmd(RgbCommandKind.SetColor, Red), new[] { Strip }, Now));
        Assert.Equal(new LightingIntent { Kind = LightingIntentKind.Color, DeviceName = "Strip", Color = "#FF0000", UpdatedAt = Now }, memory.Get(Strip.Key));

        memory.Record(Cmd(RgbCommandKind.TurnOff), new[] { Strip }, Now);
        Assert.Equal(LightingIntentKind.Off, memory.Get(Strip.Key)!.Kind);

        memory.Record(Cmd(RgbCommandKind.RestoreDefault), new[] { Strip }, Now);
        Assert.Equal(LightingIntentKind.Default, memory.Get(Strip.Key)!.Kind);

        // Saving the current state as the default does not change what the device shows.
        Assert.False(memory.Record(Cmd(RgbCommandKind.SaveAsDefault), new[] { Strip }, Now));

        Assert.True(memory.Record(Cmd(RgbCommandKind.Forget), new[] { Strip }, Now));
        Assert.Null(memory.Get(Strip.Key));
    }

    [Fact]
    public void ModeKeepsOnlyTheParametersTheModeTakes()
    {
        var memory = new LightingMemory(null);
        memory.Record(Cmd(RgbCommandKind.SetMode, Red, mode: "breathing", speed: 70, brightness: 40), new[] { Strip }, Now);
        var intent = memory.Get(Strip.Key)!;
        Assert.Equal(LightingIntentKind.Mode, intent.Kind);
        Assert.Equal("Breathing", intent.ModeName);
        Assert.Equal("#FF0000", intent.Color);
        Assert.Equal(70, intent.Speed);
        Assert.Null(intent.Brightness); // the fake's Breathing has no brightness

        memory.Record(Cmd(RgbCommandKind.SetMode, Red, mode: "Off", speed: 70), new[] { Strip }, Now);
        intent = memory.Get(Strip.Key)!;
        Assert.Equal("Off", intent.ModeName);
        Assert.Null(intent.Color);
        Assert.Null(intent.Speed);
    }

    [Fact]
    public void AZoneAfterOffKeepsTheOtherZonesBlack()
    {
        var memory = new LightingMemory(null);
        memory.Record(Cmd(RgbCommandKind.TurnOff), new[] { Strip }, Now);
        memory.Record(Cmd(RgbCommandKind.SetZoneColor, Blue, zone: 1), new[] { Strip }, Now);

        var intent = memory.Get(Strip.Key)!;
        Assert.Equal(LightingIntentKind.Zones, intent.Kind);
        Assert.Null(intent.Color);
        AssertZones(intent.Zones, (0, "#000000"), (1, "#0000FF"), (2, "#000000"));
    }

    [Fact]
    public void ZonesExpandADeviceColourAndMergeLaterZones()
    {
        var memory = new LightingMemory(null);
        memory.Record(Cmd(RgbCommandKind.SetColor, Red), new[] { Strip }, Now);
        memory.Record(Cmd(RgbCommandKind.SetZoneColor, Blue, zone: 0), new[] { Strip }, Now);
        memory.Record(Cmd(RgbCommandKind.SetZoneColor, Blue, zone: 2), new[] { Strip }, Now);

        AssertZones(memory.Get(Strip.Key)!.Zones, (0, "#0000FF"), (1, "#FF0000"), (2, "#0000FF"));
    }

    [Fact]
    public void AZoneAfterAModeRecordsOnlyThatZone()
    {
        var memory = new LightingMemory(null);
        memory.Record(Cmd(RgbCommandKind.SetMode, mode: "Breathing"), new[] { Strip }, Now);
        memory.Record(Cmd(RgbCommandKind.SetZoneColor, Blue, zone: 2), new[] { Strip }, Now);
        AssertZones(memory.Get(Strip.Key)!.Zones, (2, "#0000FF"));
    }

    [Theory]
    [InlineData("Mouse|HID: \\\\?\\HID#VID_046D&PID_C547&MI_02#7&1b8da785&0&0000#{4d1e55b2}|", true)]
    [InlineData("Corsair DRAM|I2C: PIIX4, address 0x58|", true)]
    [InlineData("ASUS ROG Falchion (Wireless)|HID: x|#2", false)]
    [InlineData("Strip|type5|24 LEDs#3", false)]
    [InlineData("", false)]
    public void DuplicateSuffixKeysAreNotRemembered(string key, bool rememberable)
    {
        Assert.Equal(rememberable, LightingMemory.IsRememberable(key));
        var device = FakeRgbController.Device(0, key.Length == 0 ? "x" : key) with { Key = key };
        var memory = new LightingMemory(null);
        Assert.Equal(rememberable, memory.Record(new RgbCommand { Kind = RgbCommandKind.TurnOff, Key = key }, new[] { device }, Now));
    }

    [Fact]
    public void ACommandWithoutAKeyRecordsEveryDevice()
    {
        var a = FakeRgbController.Device(0, "A|x|");
        var b = FakeRgbController.Device(1, "B|y|");
        var twin = FakeRgbController.Device(2, "B|y|#2");
        var memory = new LightingMemory(null);

        memory.Record(new RgbCommand { Kind = RgbCommandKind.SetColor, Color = Red }, new[] { a, b, twin }, Now);
        Assert.Equal(2, memory.Count);
        Assert.True(memory.Forget(null));
        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public void StoredDuplicateKeysAreDroppedOnLoad()
    {
        var memory = new LightingMemory(new Dictionary<string, LightingIntent>
        {
            ["A|x|"] = new() { Kind = LightingIntentKind.Off },
            ["A|x|#2"] = new() { Kind = LightingIntentKind.Off },
        });
        Assert.Equal(new[] { "A|x|" }, memory.Export().Keys);
    }

    private static void AssertZones(IReadOnlyDictionary<int, string>? actual, params (int Zone, string Color)[] expected)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.OrderBy(e => e.Zone).ToArray(), actual!.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToArray());
    }
}
