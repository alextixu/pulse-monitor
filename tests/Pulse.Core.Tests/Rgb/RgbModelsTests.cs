using Pulse.Core.Models;
using Xunit;

namespace Pulse.Core.Tests.Rgb;

/// <summary>Pins the mode-name conventions shared by the RGB backends and the UI (RgbModeRules in src/Pulse.Core/Models/RgbModels.cs).</summary>
public sealed class RgbModeRulesTests
{
    private static RgbMode M(int index, string name, bool perLed = false) => new() { Index = index, Name = name, IsPerLed = perLed };

    [Theory]
    [InlineData("Off", true)]
    [InlineData("OFF", true)]
    [InlineData("  off ", true)]
    [InlineData("Offset", false)]
    [InlineData("Static", false)]
    public void IsOff_IsACaseInsensitiveTrimmedNameMatch(string name, bool expected)
    {
        Assert.Equal(expected, RgbModeRules.IsOff(M(0, name)));
    }

    [Theory]
    [InlineData("Direct", false, true)]
    [InlineData("direct", false, true)]
    [InlineData(" Static ", false, true)]
    [InlineData("Custom", true, true)]      // per-LED flag alone qualifies
    [InlineData("Rainbow", false, false)]
    [InlineData("Static Wave", false, false)]
    public void IsDirectOrStatic_PerLedFlag_OrDirectOrStaticName(string name, bool perLed, bool expected)
    {
        Assert.Equal(expected, RgbModeRules.IsDirectOrStatic(M(0, name, perLed)));
    }

    [Fact]
    public void FindOffMode_ReturnsTheOffMode_OrNull()
    {
        var modes = new[] { M(0, "Direct"), M(1, "off"), M(2, "Rainbow") };
        Assert.Equal(1, RgbModeRules.FindOffMode(modes)!.Index);
        Assert.Null(RgbModeRules.FindOffMode(new[] { M(0, "Direct") }));
    }

    [Fact]
    public void FindFirmwareEffect_IsTheFirstModeThatIsNeitherOffNorDirectStaticNorPerLed()
    {
        var modes = new[] { M(0, "Direct", perLed: true), M(1, "Static"), M(2, "Off"), M(3, "Custom", perLed: true), M(4, "Rainbow"), M(5, "Breathing") };
        Assert.Equal(4, RgbModeRules.FindFirmwareEffect(modes)!.Index);
    }

    [Fact]
    public void FindFirmwareEffect_IsNull_WhenEveryModeIsDirectStaticOrOff()
    {
        Assert.Null(RgbModeRules.FindFirmwareEffect(new[] { M(0, "Direct"), M(1, "Static"), M(2, "Off") }));
        Assert.Null(RgbModeRules.FindFirmwareEffect(Array.Empty<RgbMode>()));
    }
}

/// <summary>Pins the "#RRGGBB" colour format used in the defaults file (RgbColor in src/Pulse.Core/Models/RgbModels.cs).</summary>
public sealed class RgbColorTests
{
    [Theory]
    [InlineData("#FF8000", 255, 128, 0)]
    [InlineData("ff8000", 255, 128, 0)]
    [InlineData("#000000", 0, 0, 0)]
    [InlineData("#ffffff", 255, 255, 255)]
    public void FromHex_ParsesRRGGBB_WithOrWithoutHash_CaseInsensitively(string hex, byte r, byte g, byte b)
    {
        Assert.Equal(new RgbColor(r, g, b), RgbColor.FromHex(hex));
    }

    [Fact]
    public void ToHex_IsUppercaseWithHash_AndToStringMatches()
    {
        var c = new RgbColor(255, 128, 7);
        Assert.Equal("#FF8007", c.ToHex());
        Assert.Equal("#FF8007", c.ToString());
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 2, 3)]
    [InlineData(255, 128, 0)]
    [InlineData(255, 255, 255)]
    public void FromHex_ToHex_RoundTrips(byte r, byte g, byte b)
    {
        var c = new RgbColor(r, g, b);
        Assert.Equal(c, RgbColor.FromHex(c.ToHex()));
    }

    [Theory]
    [InlineData("#FFF")]
    [InlineData("#FF80001")]
    [InlineData("")]
    [InlineData("#GG0000")]
    public void FromHex_RejectsWrongLengthOrNonHexDigits(string hex)
    {
        Assert.Throws<FormatException>(() => RgbColor.FromHex(hex));
    }
}
