using Pulse.Core.Models;
using Pulse.Rgb;
using Xunit;

namespace Pulse.Core.Tests.Rgb;

/// <summary>
/// Golden tests for the OpenRGB device identity rules (src/Pulse.Rgb/RgbDeviceIdentity.cs): key normalisation, the
/// migration of defaults saved under older keys, and the first-seen capture guard. The keys are the ones in the test
/// machine's %APPDATA%\Pulse\rgb-defaults.json.
/// </summary>
public sealed class RgbDeviceIdentityTests
{
    // ── the five keys of the real defaults file ──
    internal const string FalchionA = @"ASUS ROG Falchion (Wireless)|HID: \\?\HID#VID_0B05&PID_193E&MI_01#8&1b8da785&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}|";
    internal const string FalchionB = @"ASUS ROG Falchion (Wireless)|HID: \\?\HID#VID_0B05&PID_193E&MI_01#8&1ba891e3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}|";
    internal const string Gpu = @"ASUS ROG GeForce RTX STRIX 3080 Ti Gaming OC|I2C: Nvidia NvAPI I2C on GPU 0, address 0x67|";
    internal const string Motherboard = @"ASUS ROG STRIX Z790-I GAMING WIFI|HID: \\?\HID#VID_0B05&PID_19AF&MI_02#7&382c88b3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}|9876543210";
    internal const string Dram = @"Kingston Fury DDR5 DRAM|I2C: PawnIO SMBus i801 0, addresses [0x60,0x62]|";

    // ── what they normalise to ──
    internal const string FalchionKey = @"ASUS ROG Falchion (Wireless)|HID: \\?\HID#VID_0B05&PID_193E&MI_01#{4d1e55b2-f16f-11cf-88cb-001111000030}|";
    internal const string MotherboardKey = @"ASUS ROG STRIX Z790-I GAMING WIFI|HID: \\?\HID#VID_0B05&PID_19AF&MI_02#{4d1e55b2-f16f-11cf-88cb-001111000030}|9876543210";

    internal const string FalchionName = "ASUS ROG Falchion (Wireless)";
    internal const string GpuName = "ASUS ROG GeForce RTX STRIX 3080 Ti Gaming OC";
    internal const string MotherboardName = "ASUS ROG STRIX Z790-I GAMING WIFI";
    internal const string DramName = "Kingston Fury DDR5 DRAM";

    internal static string Location(string key) => key.Split('|')[1];

    /// <summary>The real file's entries (keys, names, modes, LED counts / colours and capture times as stored).</summary>
    internal static RgbDeviceDefault[] RealDefaults() =>
    [
        Entry(FalchionA, FalchionName, "Direct", 0, perLed: true, leds: 68, "#FF3B30", new DateTimeOffset(2026, 10, 8, 4, 4, 8, TimeSpan.FromHours(8))),
        Entry(FalchionB, FalchionName, "Direct", 0, perLed: true, leds: 68, "#000000", new DateTimeOffset(2026, 10, 18, 11, 57, 34, TimeSpan.FromHours(8))),
        Entry(Gpu, GpuName, "Rainbow", 6, perLed: false, leds: 22, "#FFFFFF", new DateTimeOffset(2026, 10, 8, 4, 4, 8, TimeSpan.FromHours(8))),
        Entry(Motherboard, MotherboardName, "Direct", 0, perLed: true, leds: 1, "#FFFFFF", new DateTimeOffset(2026, 10, 8, 4, 4, 8, TimeSpan.FromHours(8))),
        Entry(Dram, DramName, "Rainbow", 2, perLed: false, leds: 24, "#FFD60A", new DateTimeOffset(2026, 10, 8, 4, 4, 8, TimeSpan.FromHours(8))),
    ];

    internal static RgbDeviceDefault Entry(string key, string name, string mode, int modeIndex, bool perLed, int leds, string color, DateTimeOffset capturedAt, int? type = null)
        => new()
        {
            Key = key,
            DeviceName = name,
            DeviceType = type,
            CapturedAt = capturedAt,
            ModeIndex = modeIndex,
            ModeName = mode,
            ModeIsPerLed = perLed,
            ColorMode = perLed ? 1 : 0,
            LedColors = Enumerable.Repeat(color, leds).ToList(),
        };

    private static RgbDeviceDescriptor D(string name, string location, int type, int leds, string serial = "") => new(name, location, serial, type, leds);

    /// <summary>The test machine's devices as OpenRGB reports them today (the Falchion on yet another receiver instance).</summary>
    private static RgbDeviceDescriptor[] PresentDevices(string falchionInstance = "8&2f00aa11&0&0000") =>
    [
        D(FalchionName, Location(FalchionA).Replace("8&1b8da785&0&0000", falchionInstance), type: 5, leds: 68),
        D(GpuName, Location(Gpu), type: 2, leds: 22),
        D(MotherboardName, Location(Motherboard), type: 0, leds: 1, serial: "9876543210"),
        D(DramName, Location(Dram), type: 1, leds: 24),
    ];

    private static RgbDefaultsMigration Plan(RgbDeviceDescriptor[] devices, params RgbDeviceDefault[] stored)
        => RgbDeviceIdentity.PlanMigration(RgbDeviceIdentity.ComputeKeys(devices), devices, stored);

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    // ───────────────────────────── key normalisation ─────────────────────────────

    [Theory]
    [InlineData(FalchionA, FalchionKey)]
    [InlineData(FalchionB, FalchionKey)]
    [InlineData(Gpu, Gpu)]                 // I2C: unchanged
    [InlineData(Motherboard, MotherboardKey)] // HID motherboard: instance segment stripped, serial kept
    [InlineData(Dram, Dram)]               // I2C: unchanged
    public void NormalizeKey_TheRealDefaultsFileKeys(string stored, string expected)
    {
        Assert.Equal(expected, RgbDeviceIdentity.NormalizeKey(stored));
    }

    [Fact]
    public void BothFalchionKeys_DifferOnlyInTheInstanceSegment_AndBecomeOneKey()
    {
        Assert.NotEqual(FalchionA, FalchionB);
        Assert.Equal(RgbDeviceIdentity.NormalizeKey(FalchionA), RgbDeviceIdentity.NormalizeKey(FalchionB));
    }

    [Theory]
    [InlineData(@"I2C: ASUS Aura SMBus Motherboard 0, address 0x4E", @"I2C: ASUS Aura SMBus Motherboard 0, address 0x4E")] // I2C motherboard
    [InlineData(@"  I2C: Nvidia NvAPI I2C on GPU 0, address 0x67  ", @"I2C: Nvidia NvAPI I2C on GPU 0, address 0x67")]
    [InlineData(@"HID: \\?\hid#vid_046d&pid_c539&mi_01&col02#8&2a8c1ce5&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}",
        @"HID: \\?\hid#vid_046d&pid_c539&mi_01&col02#{4d1e55b2-f16f-11cf-88cb-001111000030}")] // lower case, collection kept
    [InlineData(@"HID: \\?\HID#{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02046d_PID&b023_REV&0007_d2fa7b8e8c8e&Col01#a&3a0fd5d4&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
        @"HID: \\?\HID#{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02046d_PID&b023_REV&0007_d2fa7b8e8c8e&Col01#{4d1e55b2-f16f-11cf-88cb-001111000030}")] // Bluetooth LE HID
    [InlineData(@"HID: \\?\HID#VID_1234&PID_5678#SERIAL0042#{4d1e55b2-f16f-11cf-88cb-001111000030}",
        @"HID: \\?\HID#VID_1234&PID_5678#SERIAL0042#{4d1e55b2-f16f-11cf-88cb-001111000030}")] // not a generated instance id
    [InlineData(@"HID: \\?\HID#VID_0B05&PID_193E&MI_01#8&1b8da785&0&0000", @"HID: \\?\HID#VID_0B05&PID_193E&MI_01")] // no interface GUID
    [InlineData(@"USB: \\?\USB#VID_1B1C&PID_0C1A#5&1d4c1f5&0&3#{a5dcbf10-6530-11d2-901f-00c04fb951ed}",
        @"USB: \\?\USB#VID_1B1C&PID_0C1A#5&1d4c1f5&0&3#{a5dcbf10-6530-11d2-901f-00c04fb951ed}")] // only HID paths
    [InlineData(@"HID: /dev/hidraw3", @"HID: /dev/hidraw3")]
    [InlineData(@"COM3", @"COM3")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeLocation_StripsOnlyTheHidInstanceSegment(string? location, string expected)
    {
        Assert.Equal(expected, RgbDeviceIdentity.NormalizeLocation(location));
    }

    [Fact]
    public void NormalizeKey_KeepsTheDuplicateSuffixAndTheFallbackForm()
    {
        Assert.Equal(FalchionKey + "#2", RgbDeviceIdentity.NormalizeKey(FalchionB + "#2"));
        Assert.Equal("Strip|type4|30 LEDs#3", RgbDeviceIdentity.NormalizeKey("Strip|type4|30 LEDs#3"));
        Assert.Equal(string.Empty, RgbDeviceIdentity.NormalizeKey(string.Empty));
    }

    [Fact]
    public void BaseKey_NameLocationSerial_WithTheNormalisedLocation_TrimmedParts()
    {
        Assert.Equal(FalchionKey, RgbDeviceIdentity.BaseKey(D("  " + FalchionName + " ", " " + Location(FalchionB) + " ", type: 5, leds: 68)));
        Assert.Equal(MotherboardKey, RgbDeviceIdentity.BaseKey(D(MotherboardName, Location(Motherboard), type: 0, leds: 1, serial: " 9876543210 ")));
        Assert.Equal(Gpu, RgbDeviceIdentity.BaseKey(D(GpuName, Location(Gpu), type: 2, leds: 22)));
        Assert.Equal(Dram, RgbDeviceIdentity.BaseKey(D(DramName, Location(Dram), type: 1, leds: 24)));
    }

    [Fact]
    public void BaseKey_SerialOnly_AndFallbackWithoutLocationOrSerial()
    {
        Assert.Equal("Pad||SN1", RgbDeviceIdentity.BaseKey(D("Pad", "", type: 7, leds: 15, serial: "SN1")));
        Assert.Equal("Strip|type4|30 LEDs", RgbDeviceIdentity.BaseKey(D("Strip", "  ", type: 4, leds: 30)));
        Assert.Equal("|type4|0 LEDs", RgbDeviceIdentity.BaseKey(new RgbDeviceDescriptor(null, null, null, 4, 0)));
    }

    [Fact]
    public void ComputeKeys_SameDeviceOnTwoInstances_IsOneBaseKeyWithADuplicateSuffix_InEnumerationOrder()
    {
        var keys = RgbDeviceIdentity.ComputeKeys(
        [
            D(FalchionName, Location(FalchionA), 5, 68),
            D(GpuName, Location(Gpu), 2, 22),
            D(FalchionName, Location(FalchionB), 5, 68),
            D("Strip", "", 4, 30),
            D("Strip", "", 4, 30),
            D("Strip", "", 4, 30),
        ]);

        Assert.Equal(new[] { FalchionKey, Gpu, FalchionKey + "#2", "Strip|type4|30 LEDs", "Strip|type4|30 LEDs#2", "Strip|type4|30 LEDs#3" }, keys);
    }

    [Theory]
    [InlineData("Strip|type4|30 LEDs", 4)]
    [InlineData("Strip|type4|30 LEDs#2", 4)]
    [InlineData("|type13|0 LEDs", 13)]
    [InlineData(Gpu, null)]
    [InlineData(FalchionA, null)]
    public void TypeFromKey_ReadsOnlyTheFallbackForm(string key, int? expected)
    {
        Assert.Equal(expected, RgbDeviceIdentity.TypeFromKey(key));
    }

    // ───────────────────────────── capture guard ─────────────────────────────

    private static RgbDevice Device(string activeMode, bool perLed, params string[] colors)
    {
        var modes = new[]
        {
            new RgbMode { Index = 0, Name = "Direct", IsPerLed = true, SupportsColor = true },
            new RgbMode { Index = 1, Name = "Static", SupportsColor = true },
            new RgbMode { Index = 2, Name = "Off" },
            new RgbMode { Index = 3, Name = activeMode, IsPerLed = perLed },
        };
        return new RgbDevice
        {
            Index = 0, Name = "Keyboard", Type = "Keyboard", Modes = modes, ActiveModeIndex = 3,
            LedCount = colors.Length, Colors = colors.Select(RgbColor.FromHex).ToArray(),
        };
    }

    [Theory]
    [InlineData("Off", false, new[] { "#FF0000" }, true)]             // Off mode, whatever the colours
    [InlineData(" off ", false, new string[0], true)]
    [InlineData("Direct", true, new[] { "#000000", "#000000" }, true)] // per-LED, every LED black
    [InlineData("Custom", true, new[] { "#000000", "#000001" }, false)] // one LED lit
    [InlineData("Direct", true, new string[0], false)]               // no LEDs: nothing says it is off
    [InlineData("Static", false, new[] { "#000000" }, false)]         // only per-LED modes count
    [InlineData("Rainbow", false, new[] { "#000000" }, false)]
    public void LooksOff_OffMode_OrPerLedModeWithEveryLedBlack(string mode, bool perLed, string[] colors, bool expected)
    {
        Assert.Equal(expected, RgbDeviceIdentity.LooksOff(Device(mode, perLed, colors)));
    }

    [Fact]
    public void LooksOff_ActiveModeOutOfRange_IsNotOff()
    {
        Assert.False(RgbDeviceIdentity.LooksOff(Device("Off", false) with { ActiveModeIndex = 9 }));
        Assert.False(RgbDeviceIdentity.LooksOff(Device("Off", false) with { ActiveModeIndex = -1 }));
    }

    [Fact]
    public void LooksOff_StoredSnapshot_SameRule()
    {
        var real = RealDefaults();
        Assert.False(RgbDeviceIdentity.LooksOff(real[0])); // Falchion, red
        Assert.True(RgbDeviceIdentity.LooksOff(real[1]));  // Falchion, captured all black
        Assert.False(RgbDeviceIdentity.LooksOff(real[2])); // GPU rainbow
        Assert.True(RgbDeviceIdentity.LooksOff(real[2] with { ModeName = "OFF" }));
        Assert.False(RgbDeviceIdentity.LooksOff(real[1] with { LedColors = ["#000000", "garbage"] })); // unreadable = lit
        Assert.False(RgbDeviceIdentity.LooksOff(real[1] with { ModeIsPerLed = false }));
    }

    // ───────────────────────────── migration ─────────────────────────────

    [Fact]
    public void Migration_TheRealDefaultsFile_FalchionAndMotherboardMove_TheBlackFalchionDuplicateIsDropped_I2cUntouched()
    {
        var plan = Plan(PresentDevices(), RealDefaults());

        Assert.Equal(2, plan.Moves.Count);
        var falchion = Assert.Single(plan.Moves, m => m.ToKey == FalchionKey);
        Assert.Equal(FalchionA, falchion.FromKey);                    // the lit, earlier capture
        Assert.Equal(RgbDefaultMatch.NormalizedKey, falchion.Match);
        Assert.Equal(FalchionKey, falchion.Entry.Key);
        Assert.Equal(5, falchion.Entry.DeviceType);                   // filled in from the device
        Assert.All(falchion.Entry.LedColors, c => Assert.Equal("#FF3B30", c));
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 4, 4, 8, TimeSpan.FromHours(8)), falchion.Entry.CapturedAt);

        var board = Assert.Single(plan.Moves, m => m.ToKey == MotherboardKey);
        Assert.Equal((Motherboard, RgbDefaultMatch.NormalizedKey), (board.FromKey, board.Match));

        Assert.Equal(new[] { FalchionB }, plan.Superseded);
        Assert.DoesNotContain(plan.Moves, m => m.FromKey is Gpu or Dram);
    }

    [Fact]
    public void Migration_AfterItRan_IsANoOp()
    {
        var first = Plan(PresentDevices(), RealDefaults());
        var migrated = RealDefaults()
            .Where(e => !first.Moves.Any(m => m.FromKey == e.Key) && !first.Superseded.Contains(e.Key))
            .Concat(first.Moves.Select(m => m.Entry))
            .ToArray();

        Assert.Equal(4, migrated.Length);
        Assert.True(Plan(PresentDevices(), migrated).IsEmpty);
        Assert.True(Plan(PresentDevices("8&1b8da785&0&0000"), migrated).IsEmpty); // back on the first receiver: same key
    }

    [Fact]
    public void Migration_SingleNormalisedCandidate_IsUsedEvenWhenItLooksOff()
    {
        var plan = Plan(PresentDevices(), RealDefaults()[1]);

        var move = Assert.Single(plan.Moves);
        Assert.Equal((FalchionB, FalchionKey), (move.FromKey, move.ToKey));
        Assert.Empty(plan.Superseded);
    }

    [Fact]
    public void Migration_SeveralLitCandidates_TheEarliestCaptureWins()
    {
        var a = Entry(FalchionA, FalchionName, "Direct", 0, true, 68, "#FF0000", T0.AddDays(2));
        var b = Entry(FalchionB, FalchionName, "Direct", 0, true, 68, "#00FF00", T0);

        var plan = Plan(PresentDevices(), a, b);

        Assert.Equal(FalchionB, Assert.Single(plan.Moves).FromKey);
        Assert.Equal(new[] { FalchionA }, plan.Superseded);
    }

    [Fact]
    public void Migration_AllCandidatesLookOff_TheEarliestCaptureWins()
    {
        var a = Entry(FalchionA, FalchionName, "Off", 2, false, 68, "#FF0000", T0);
        var b = Entry(FalchionB, FalchionName, "Direct", 0, true, 68, "#000000", T0.AddDays(1));

        Assert.Equal(FalchionA, Assert.Single(Plan(PresentDevices(), a, b).Moves).FromKey);
    }

    [Fact]
    public void Migration_DeviceAlreadyKnownUnderItsCurrentKey_OldFormEntriesAreSupersededOnly()
    {
        var current = Entry(FalchionKey, FalchionName, "Direct", 0, true, 68, "#0000FF", T0, type: 5);

        var plan = Plan(PresentDevices(), current, RealDefaults()[0], RealDefaults()[1]);

        Assert.Empty(plan.Moves);
        Assert.Equal(new[] { FalchionA, FalchionB }, plan.Superseded.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Migration_NormalisedMatch_IgnoresTheCaseOfTheDevicePath()
    {
        var lower = Entry(@"ASUS ROG Falchion (Wireless)|HID: \\?\hid#vid_0b05&pid_193e&mi_01#8&1b8da785&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}|",
            FalchionName, "Direct", 0, true, 68, "#FF0000", T0);

        var move = Assert.Single(Plan(PresentDevices(), lower).Moves);
        Assert.Equal((FalchionKey, RgbDefaultMatch.NormalizedKey), (move.ToKey, move.Match));
    }

    [Fact]
    public void Migration_NameTypeLedCount_MovesTheOnlyMatchingEntry_WhenTheLocationChanged()
    {
        // OpenRGB renamed the SMBus adapter; old entry saved before DeviceType was recorded (type = any).
        var old = Entry(@"Kingston Fury DDR5 DRAM|I2C: SMBus i801 0, addresses [0x60,0x62]|", DramName, "Rainbow", 2, false, 24, "#FFD60A", T0);

        var plan = Plan(PresentDevices(), old);

        var move = Assert.Single(plan.Moves);
        Assert.Equal((Dram, RgbDefaultMatch.NameTypeLedCount, 1), (move.ToKey, move.Match, move.Entry.DeviceType));
        Assert.Empty(plan.Superseded);
    }

    [Fact]
    public void Migration_NameTypeLedCount_FromAFallbackKey_ChecksTheTypeInTheKey()
    {
        var sameType = Entry("Kingston Fury DDR5 DRAM|type1|24 LEDs", DramName, "Rainbow", 2, false, 24, "#FFD60A", T0);
        var otherType = Entry("Kingston Fury DDR5 DRAM|type4|24 LEDs", DramName, "Rainbow", 2, false, 24, "#FFD60A", T0);

        Assert.Equal(Dram, Assert.Single(Plan(PresentDevices(), sameType).Moves).ToKey);
        Assert.True(Plan(PresentDevices(), otherType).IsEmpty);
    }

    [Fact]
    public void Migration_NameTypeLedCount_RequiresNameTypeAndLedCountToMatch()
    {
        const string oldKey = @"Kingston Fury DDR5 DRAM|I2C: old|";
        Assert.True(Plan(PresentDevices(), Entry(oldKey, DramName, "Rainbow", 2, false, 24, "#FFD60A", T0, type: 2)).IsEmpty); // type
        Assert.True(Plan(PresentDevices(), Entry(oldKey, DramName, "Rainbow", 2, false, 23, "#FFD60A", T0)).IsEmpty);          // LEDs
        Assert.True(Plan(PresentDevices(), Entry(oldKey, "Kingston Fury DDR4 DRAM", "Rainbow", 2, false, 24, "#FFD60A", T0)).IsEmpty); // name
        Assert.Single(Plan(PresentDevices(), Entry(oldKey, " " + DramName + " ", "Rainbow", 2, false, 24, "#FFD60A", T0, type: 1)).Moves);
    }

    [Fact]
    public void Migration_NameTypeLedCount_TwoStoredCandidates_IsAmbiguous()
    {
        var a = Entry(@"Kingston Fury DDR5 DRAM|I2C: old, address 0x60|", DramName, "Rainbow", 2, false, 24, "#FFD60A", T0);
        var b = Entry(@"Kingston Fury DDR5 DRAM|I2C: old, address 0x62|", DramName, "Static", 1, false, 24, "#FFFFFF", T0);

        Assert.True(Plan(PresentDevices(), a, b).IsEmpty);
    }

    [Fact]
    public void Migration_NameTypeLedCount_TwoDevicesWantingTheSameEntry_IsAmbiguous()
    {
        RgbDeviceDescriptor[] sticks =
        [
            D(DramName, "I2C: new, address 0x60", 1, 24),
            D(DramName, "I2C: new, address 0x62", 1, 24),
        ];
        var old = Entry(@"Kingston Fury DDR5 DRAM|I2C: old, address 0x60|", DramName, "Rainbow", 2, false, 24, "#FFD60A", T0);

        Assert.True(Plan(sticks, old).IsEmpty);
    }

    [Fact]
    public void Migration_NameTypeLedCount_NeverTakesAnEntryThatBelongsToAPresentDevice()
    {
        // A second keyboard with the Falchion's name and LED count on another PID: the old Falchion entries belong to
        // the Falchion (they normalise to its key), the GPU entry is the GPU's own.
        RgbDeviceDescriptor[] devices =
        [
            .. PresentDevices(),
            D(FalchionName, @"HID: \\?\HID#VID_0B05&PID_193C&MI_01#8&11111111&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", 5, 68),
            D(GpuName, "I2C: Nvidia NvAPI I2C on GPU 1, address 0x67", 2, 22),
        ];

        var plan = Plan(devices, RealDefaults());

        Assert.DoesNotContain(plan.Moves, m => m.ToKey.Contains("PID_193C") || m.ToKey.Contains("GPU 1"));
        Assert.Equal(FalchionA, Assert.Single(plan.Moves, m => m.ToKey == FalchionKey).FromKey);
    }

    [Fact]
    public void Migration_NameTypeLedCount_RequiresANonEmptyName()
    {
        var nameless = Entry("|I2C: old|", "", "Static", 1, false, 4, "#FFFFFF", T0);
        Assert.True(Plan([D("", "I2C: new", 3, 4)], nameless).IsEmpty);
    }

    [Fact]
    public void Migration_EntriesOfAbsentDevices_AreNeverTouched_AndEmptyInputsAreANoOp()
    {
        var plan = Plan([D(GpuName, Location(Gpu), 2, 22)], RealDefaults());
        Assert.True(plan.IsEmpty); // Falchion / motherboard / DRAM are not connected: their entries wait

        Assert.Same(RgbDefaultsMigration.None, Plan(PresentDevices()));
        Assert.Same(RgbDefaultsMigration.None, RgbDeviceIdentity.PlanMigration([], [], RealDefaults()));
    }

    [Fact]
    public void Migration_DuplicateSuffixedDevice_DoesNotGetTheFirstDevicesLeftovers()
    {
        // Both Falchion instances connected at once: keys K and K#2. K gets the lit snapshot; K#2 (an order-dependent
        // key) gets nothing from the old entries, which all describe K.
        RgbDeviceDescriptor[] devices = [D(FalchionName, Location(FalchionA), 5, 68), D(FalchionName, Location(FalchionB), 5, 68)];

        var plan = Plan(devices, RealDefaults()[0], RealDefaults()[1]);

        Assert.Equal((FalchionA, FalchionKey), (Assert.Single(plan.Moves).FromKey, plan.Moves[0].ToKey));
        Assert.Equal(new[] { FalchionB }, plan.Superseded);
    }
}
