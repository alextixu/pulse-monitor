using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using OpenRGB.NET;
using Pulse.Rgb;
using Xunit;

namespace Pulse.Core.Tests.Rgb;

/// <summary>
/// Golden tests for the hand-built RGBCONTROLLER_UPDATEMODE packet (src/Pulse.Rgb/OpenRgbModePacket.cs). OpenRGB.NET's
/// <see cref="Mode"/> has an internal constructor, so modes are built through reflection with the server-reported values.
/// </summary>
public sealed class OpenRgbModePacketTests
{
    private static readonly ConstructorInfo ModeCtor =
        typeof(Mode).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Single();

    private static Mode NewMode(
        string name = "Static",
        ModeFlags flags = ModeFlags.HasModeSpecificColor,
        uint speedMin = 0, uint speedMax = 0,
        uint brightnessMin = 0, uint brightnessMax = 0,
        uint colorMin = 0, uint colorMax = 0,
        uint speed = 0, uint brightness = 0,
        Direction direction = Direction.None,
        ColorMode colorMode = ColorMode.ModeSpecific,
        Color[]? colors = null,
        int index = 0, int value = 0, uint protocol = 4)
        => (Mode)ModeCtor.Invoke(new object[]
        {
            ProtocolVersion.FromNumber(protocol), index, name, value, flags,
            speedMin, speedMax, brightnessMin, brightnessMax, colorMin, colorMax,
            speed, brightness, direction, colorMode, colors ?? Array.Empty<Color>(),
        });

    private sealed record Parsed(
        uint Size, int ModeIndex, string Name, int Value, uint Flags,
        uint SpeedMin, uint SpeedMax, uint? BrightnessMin, uint? BrightnessMax, uint ColorMin, uint ColorMax,
        uint Speed, uint? Brightness, uint Direction, uint ColorMode, (byte R, byte G, byte B, byte Pad)[] Colors);

    private static Parsed Parse(byte[] payload, int protocol)
    {
        using var r = new BinaryReader(new MemoryStream(payload));
        var size = r.ReadUInt32();
        var modeIndex = r.ReadInt32();
        var nameLen = r.ReadUInt16();
        var nameBytes = r.ReadBytes(nameLen);
        Assert.Equal(0, nameBytes[^1]); // NUL terminated, the length counts the terminator
        var name = Encoding.UTF8.GetString(nameBytes, 0, nameLen - 1);
        var value = r.ReadInt32();
        var flags = r.ReadUInt32();
        var speedMin = r.ReadUInt32();
        var speedMax = r.ReadUInt32();
        uint? brightnessMin = null, brightnessMax = null;
        if (protocol >= 3) { brightnessMin = r.ReadUInt32(); brightnessMax = r.ReadUInt32(); }
        var colorMin = r.ReadUInt32();
        var colorMax = r.ReadUInt32();
        var speed = r.ReadUInt32();
        uint? brightness = protocol >= 3 ? r.ReadUInt32() : null;
        var direction = r.ReadUInt32();
        var colorMode = r.ReadUInt32();
        var count = r.ReadUInt16();
        var colors = new (byte, byte, byte, byte)[count];
        for (var i = 0; i < count; i++) colors[i] = (r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
        Assert.Equal(payload.Length, r.BaseStream.Position); // nothing trailing
        return new Parsed(size, modeIndex, name, value, flags, speedMin, speedMax, brightnessMin, brightnessMax, colorMin, colorMax,
            speed, brightness, direction, colorMode, colors);
    }

    private static Parsed Build(int protocol, int modeIndex, Mode mode, uint? speed = null, Direction? direction = null, Color[]? colors = null, uint? brightness = null)
        => Parse(OpenRgbModePacket.BuildPayload(protocol, modeIndex, mode, speed, direction, colors, brightness), protocol);

    // ───────────────────────────── layout ─────────────────────────────

    [Fact]
    public void Payload_StartsWithItsOwnTotalLength_ThenTheModeIndex_ThenTheNulTerminatedName()
    {
        var mode = NewMode(name: "Rainbow", flags: ModeFlags.HasSpeed, speedMin: 0, speedMax: 100, speed: 40, value: 7, colorMode: ColorMode.None);
        var bytes = OpenRgbModePacket.BuildPayload(3, 5, mode, null, null, null, null);

        var p = Parse(bytes, 3);
        Assert.Equal((uint)bytes.Length, p.Size);
        Assert.Equal(5, p.ModeIndex);
        Assert.Equal("Rainbow", p.Name);
        Assert.Equal(7, p.Value);
        Assert.Equal((uint)ModeFlags.HasSpeed, p.Flags);
        Assert.Equal((0u, 100u), (p.SpeedMin, p.SpeedMax));
        Assert.Equal(40u, p.Speed);
        // 4 size + 4 index + 2 len + 8 name ("Rainbow\0") + 4 value + 4 flags + 2×4 speed range + 2×4 brightness range
        // + 2×4 colour range + 4 speed + 4 brightness + 4 direction + 4 colour mode + 2 count = 68
        Assert.Equal(68, bytes.Length);
    }

    [Fact]
    public void Payload_EncodesTheNameAsUtf8_WithTheLengthCountingBytesPlusTheTerminator()
    {
        var bytes = OpenRgbModePacket.BuildPayload(3, 0, NewMode(name: "彩虹"), null, null, null, null);

        Assert.Equal("彩虹", Parse(bytes, 3).Name);
        Assert.Equal(7, BitConverter.ToUInt16(bytes, 8)); // 6 UTF-8 bytes + NUL
    }

    [Fact]
    public void Protocol2_OmitsTheThreeBrightnessFields_12BytesShorterThanProtocol3()
    {
        var mode = NewMode(flags: ModeFlags.HasModeSpecificColor | ModeFlags.HasBrightness, brightnessMin: 0, brightnessMax: 100, brightness: 50);

        var v2 = OpenRgbModePacket.BuildPayload(2, 0, mode, null, null, null, null);
        var v3 = OpenRgbModePacket.BuildPayload(3, 0, mode, null, null, null, null);

        Assert.Equal(12, v3.Length - v2.Length);
        var p2 = Parse(v2, 2);
        Assert.Null(p2.BrightnessMin);
        Assert.Null(p2.Brightness);
        var p3 = Parse(v3, 3);
        Assert.Equal((0u, 100u, 50u), (p3.BrightnessMin!.Value, p3.BrightnessMax!.Value, p3.Brightness!.Value));
    }

    // ───────────────────────────── direction ─────────────────────────────

    [Fact]
    public void Direction_IsForcedToZero_ForModesWithoutDirectionFlags_EvenWhenOpenRgbNetReportsNone()
    {
        var mode = NewMode(name: "Direct", flags: ModeFlags.HasPerLedColor, direction: Direction.None, colorMode: ColorMode.PerLed);

        Assert.Equal(0u, OpenRgbModePacket.DirectionFor(mode, null));
        Assert.Equal(0u, OpenRgbModePacket.DirectionFor(mode, Direction.Right));
        Assert.Equal(0u, Build(3, 0, mode, direction: Direction.Right).Direction); // 0xFFFFFFFF would be rejected by the server
    }

    [Fact]
    public void Direction_WithoutDirectionFlags_IsZero_EvenIfTheModeCarriesAValidLookingOne()
    {
        var mode = NewMode(flags: ModeFlags.HasSpeed, direction: Direction.Left, colorMode: ColorMode.None);
        Assert.Equal(0u, OpenRgbModePacket.DirectionFor(mode, null));
    }

    [Theory]
    [InlineData(ModeFlags.HasDirectionLR, Direction.Right, Direction.None, 1u)]        // requested and valid
    [InlineData(ModeFlags.HasDirectionLR, Direction.Up, Direction.Right, 1u)]          // requested invalid → mode's current
    [InlineData(ModeFlags.HasDirectionLR, Direction.Up, Direction.None, 0u)]           // nothing valid → first allowed (Left)
    [InlineData(ModeFlags.HasDirectionUD, null, Direction.None, 2u)]                   // Up
    [InlineData(ModeFlags.HasDirectionHV, null, Direction.None, 4u)]                   // Horizontal
    [InlineData(ModeFlags.HasDirectionHV, Direction.Vertical, Direction.None, 5u)]
    [InlineData(ModeFlags.HasDirectionLR | ModeFlags.HasDirectionUD, Direction.Down, Direction.None, 3u)]
    [InlineData(ModeFlags.HasDirectionLR, null, Direction.Horizontal, 0u)]             // current invalid for LR → Left
    public void Direction_ForDirectionalModes_RequestedIfValid_ElseCurrentIfValid_ElseTheFirstAllowed(
        ModeFlags flags, Direction? requested, Direction current, uint expected)
    {
        var mode = NewMode(flags: flags, direction: current, colorMode: ColorMode.None);

        Assert.Equal(expected, OpenRgbModePacket.DirectionFor(mode, requested));
        Assert.Equal(expected, Build(3, 0, mode, direction: requested).Direction);
    }

    // ───────────────────────────── speed / brightness clamping ─────────────────────────────

    [Theory]
    [InlineData(0u, 100u, 250u, 100u)]
    [InlineData(10u, 100u, 3u, 10u)]
    [InlineData(0u, 100u, 55u, 55u)]
    [InlineData(100u, 0u, 150u, 100u)]  // inverted range (0 = fastest on some devices)
    [InlineData(100u, 0u, 55u, 55u)]
    [InlineData(5u, 1u, 0u, 1u)]
    public void Speed_IsClampedIntoTheModesRange_EvenWhenTheRangeIsInverted(uint min, uint max, uint requested, uint expected)
    {
        var mode = NewMode(flags: ModeFlags.HasSpeed, speedMin: min, speedMax: max, speed: min, colorMode: ColorMode.None);
        Assert.Equal(expected, Build(3, 0, mode, speed: requested).Speed);
    }

    [Fact]
    public void Speed_NotRequested_UsesTheModesCurrentSpeed_StillClamped()
    {
        var inRange = NewMode(flags: ModeFlags.HasSpeed, speedMin: 0, speedMax: 100, speed: 42, colorMode: ColorMode.None);
        Assert.Equal(42u, Build(3, 0, inRange).Speed);

        var outOfRange = NewMode(flags: ModeFlags.HasSpeed, speedMin: 0, speedMax: 100, speed: 999, colorMode: ColorMode.None);
        Assert.Equal(100u, Build(3, 0, outOfRange).Speed);
    }

    [Theory]
    [InlineData(0u, 100u, 300u, 100u)]
    [InlineData(20u, 100u, 5u, 20u)]
    [InlineData(100u, 0u, 300u, 100u)]
    [InlineData(0u, 100u, 64u, 64u)]
    public void Brightness_IsClampedIntoTheModesRange_OnProtocol3AndUp(uint min, uint max, uint requested, uint expected)
    {
        var mode = NewMode(flags: ModeFlags.HasBrightness, brightnessMin: min, brightnessMax: max, brightness: min, colorMode: ColorMode.None);
        Assert.Equal(expected, Build(3, 0, mode, brightness: requested).Brightness);
        Assert.Equal(expected, Build(4, 0, mode, brightness: requested).Brightness);
    }

    [Fact]
    public void Brightness_NotRequested_UsesTheModesCurrentBrightness()
    {
        var mode = NewMode(flags: ModeFlags.HasBrightness, brightnessMin: 0, brightnessMax: 100, brightness: 77, colorMode: ColorMode.None);
        Assert.Equal(77u, Build(3, 0, mode).Brightness);
    }

    // ───────────────────────────── colours ─────────────────────────────

    [Fact]
    public void Colors_Given_AreWrittenAsRGB0_WithTheirCount()
    {
        var mode = NewMode(flags: ModeFlags.HasModeSpecificColor, colorMin: 1, colorMax: 3, colors: new[] { new Color(1, 2, 3) });
        var colors = new[] { new Color(255, 128, 0), new Color(0, 0, 255), new Color(9, 8, 7) };

        var p = Build(3, 0, mode, colors: colors);

        Assert.Equal((1u, 3u), (p.ColorMin, p.ColorMax));
        Assert.Equal(new (byte, byte, byte, byte)[] { (255, 128, 0, 0), (0, 0, 255, 0), (9, 8, 7, 0) }, p.Colors);
    }

    [Fact]
    public void Colors_NotGiven_RepeatTheModesCurrentColors_OrNone()
    {
        var withColors = NewMode(flags: ModeFlags.HasModeSpecificColor, colors: new[] { new Color(10, 20, 30), new Color(40, 50, 60) });
        Assert.Equal(new (byte, byte, byte, byte)[] { (10, 20, 30, 0), (40, 50, 60, 0) }, Build(3, 0, withColors).Colors);

        var none = NewMode(flags: ModeFlags.HasPerLedColor, colorMode: ColorMode.PerLed);
        Assert.Empty(Build(3, 0, none).Colors);
    }

    [Fact]
    public void ColorMode_ColoursGivenForAModeSpecificMode_IsModeSpecific()
    {
        var mode = NewMode(flags: ModeFlags.HasModeSpecificColor | ModeFlags.HasRandomColor, colorMode: ColorMode.Random);
        Assert.Equal((uint)ColorMode.ModeSpecific, Build(3, 0, mode, colors: new[] { new Color(1, 1, 1) }).ColorMode);
    }

    [Theory]
    [InlineData(ModeFlags.HasPerLedColor, ColorMode.PerLed, ColorMode.PerLed)]                 // current valid → kept
    [InlineData(ModeFlags.HasModeSpecificColor, ColorMode.ModeSpecific, ColorMode.ModeSpecific)]
    [InlineData(ModeFlags.HasRandomColor, ColorMode.Random, ColorMode.Random)]
    [InlineData(ModeFlags.None, ColorMode.None, ColorMode.None)]
    [InlineData(ModeFlags.HasRandomColor, ColorMode.PerLed, ColorMode.Random)]                 // current invalid → the flag's mode
    [InlineData(ModeFlags.HasSpeed, ColorMode.ModeSpecific, ColorMode.None)]                   // no colour flags → None
    [InlineData(ModeFlags.HasPerLedColor | ModeFlags.HasModeSpecificColor, ColorMode.Random, ColorMode.PerLed)] // PerLed wins the fallback
    [InlineData(ModeFlags.HasModeSpecificColor | ModeFlags.HasRandomColor, ColorMode.PerLed, ColorMode.ModeSpecific)]
    public void ColorMode_KeepsTheCurrentOneWhenTheFlagsAllowIt_OtherwiseFallsBackPerLedThenModeSpecificThenRandom(
        ModeFlags flags, ColorMode current, ColorMode expected)
    {
        var mode = NewMode(flags: flags, colorMode: current);
        Assert.Equal((uint)expected, Build(3, 0, mode).ColorMode);
    }

    // ───────────────────────────── TrySend / header ─────────────────────────────

    [Fact]
    public void IsSupported_TheBundledOpenRgbNetStillExposesTheConnectionSocket()
    {
        Assert.True(OpenRgbModePacket.IsSupported);
    }

    [Fact]
    public void TrySend_ReturnsFalse_WhenTheClientIsNotConnected()
    {
        var client = new OpenRgbClient(autoConnect: false);
        Assert.False(OpenRgbModePacket.TrySend(client, 3, 0, 0, NewMode()));
    }

    [Fact]
    public void TrySend_WritesTheOrgbHeader_DeviceIndex_Command1101_PayloadLength_ThenThePayload()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new OpenRgbClient(autoConnect: false);
        var connection = typeof(OpenRgbClient).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        var socket = (Socket)connection.GetType().GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
        try
        {
            socket.Connect((IPEndPoint)listener.LocalEndpoint);
            using var server = listener.AcceptSocket();

            var mode = NewMode(name: "Direct", flags: ModeFlags.HasPerLedColor, colorMode: ColorMode.PerLed, direction: Direction.None);
            var expectedPayload = OpenRgbModePacket.BuildPayload(3, 2, mode, null, null, null, null);

            Assert.True(OpenRgbModePacket.TrySend(client, protocolVersion: 3, deviceIndex: 7, modeIndex: 2, mode));

            var packet = new byte[16 + expectedPayload.Length];
            var read = 0;
            server.ReceiveTimeout = 5000;
            while (read < packet.Length)
            {
                var n = server.Receive(packet, read, packet.Length - read, SocketFlags.None);
                Assert.True(n > 0, "socket closed before the whole packet arrived");
                read += n;
            }

            Assert.Equal("ORGB", Encoding.ASCII.GetString(packet, 0, 4));
            Assert.Equal(7u, BitConverter.ToUInt32(packet, 4));
            Assert.Equal(1101u, BitConverter.ToUInt32(packet, 8));
            Assert.Equal((uint)expectedPayload.Length, BitConverter.ToUInt32(packet, 12));
            Assert.Equal(expectedPayload, packet[16..]);
        }
        finally
        {
            socket.Dispose();
            listener.Stop();
        }
    }
}
