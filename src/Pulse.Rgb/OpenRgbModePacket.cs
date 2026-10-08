using System.Net.Sockets;
using System.Reflection;
using System.Text;
using OpenRGB.NET;

namespace Pulse.Rgb;

/// <summary>
/// Builds and sends RGBCONTROLLER_UPDATEMODE (1101) packets that OpenRGB 1.0 accepts.
/// <para>
/// OpenRGB 1.0 validates the incoming mode against its own (<c>RGBController::SetModeValuesFromMode</c>): among other
/// things a mode without direction flags must carry direction 0, and brightness / speed / colour count must be in range.
/// OpenRGB.NET 3.1.1 maps "no direction" to <see cref="Direction.None"/> (0xFFFFFFFF) and refuses to let callers change
/// it, so every switch to Direct / Static / Off is silently dropped by the server (verified on the test machine). This
/// writes the same wire format as OpenRGB.NET but with valid values, on the client's own socket.
/// </para>
/// Protocol ≤ 5 clients get no ACK for this packet, so nothing has to be read back here.
/// </summary>
internal static class OpenRgbModePacket
{
    private const int PacketUpdateMode = 1101;

    private static readonly FieldInfo? ConnectionField =
        typeof(OpenRgbClient).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? SocketField =
        ConnectionField?.FieldType.GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic);

    public static bool IsSupported => SocketField is not null;

    /// <summary>Direction to send: 0 for modes without direction flags, otherwise the requested or current one (made valid).</summary>
    public static uint DirectionFor(Mode mode, Direction? requested)
    {
        var flags = mode.Flags;
        var lr = flags.HasFlag(ModeFlags.HasDirectionLR);
        var ud = flags.HasFlag(ModeFlags.HasDirectionUD);
        var hv = flags.HasFlag(ModeFlags.HasDirectionHV);
        if (!lr && !ud && !hv) return 0;

        static bool Valid(Direction d, bool lr, bool ud, bool hv)
            => (lr && d is Direction.Left or Direction.Right)
               || (ud && d is Direction.Up or Direction.Down)
               || (hv && d is Direction.Horizontal or Direction.Vertical);

        if (requested is { } r && Valid(r, lr, ud, hv)) return (uint)r;
        if (Valid(mode.Direction, lr, ud, hv)) return (uint)mode.Direction;
        return (uint)(lr ? Direction.Left : ud ? Direction.Up : Direction.Horizontal);
    }

    /// <summary>
    /// Sends UPDATEMODE for <paramref name="mode"/> (as reported by the server) with optional overrides.
    /// Returns false when the socket cannot be reached (the caller then falls back to OpenRGB.NET's UpdateMode).
    /// </summary>
    public static bool TrySend(OpenRgbClient client, int protocolVersion, int deviceIndex, int modeIndex, Mode mode,
        uint? speed = null, Direction? direction = null, Color[]? colors = null, uint? brightness = null)
    {
        if (ConnectionField?.GetValue(client) is not { } connection || SocketField?.GetValue(connection) is not Socket socket || !socket.Connected)
            return false;

        var payload = BuildPayload(protocolVersion, modeIndex, mode, speed, direction, colors, brightness);
        var packet = new byte[16 + payload.Length];
        Encoding.ASCII.GetBytes("ORGB").CopyTo(packet, 0);
        BitConverter.TryWriteBytes(packet.AsSpan(4), (uint)deviceIndex);
        BitConverter.TryWriteBytes(packet.AsSpan(8), (uint)PacketUpdateMode);
        BitConverter.TryWriteBytes(packet.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(packet, 16);

        var sent = 0;
        while (sent < packet.Length) sent += socket.Send(packet, sent, packet.Length - sent, SocketFlags.None);
        return true;
    }

    /// <summary>[uint32 size (incl. itself)][int32 mode index][mode description for the negotiated protocol].</summary>
    internal static byte[] BuildPayload(int protocolVersion, int modeIndex, Mode mode, uint? speed, Direction? direction, Color[]? colors, uint? brightness)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        w.Write(0u); // size, patched below
        w.Write(modeIndex);

        var name = Encoding.UTF8.GetBytes(mode.Name ?? string.Empty);
        w.Write((ushort)(name.Length + 1));
        w.Write(name);
        w.Write((byte)0);
        w.Write(mode.Value);
        w.Write((uint)mode.Flags);
        w.Write(mode.SpeedMin);
        w.Write(mode.SpeedMax);
        if (protocolVersion >= 3)
        {
            w.Write(mode.BrightnessMin);
            w.Write(mode.BrightnessMax);
        }
        w.Write(mode.ColorMin);
        w.Write(mode.ColorMax);
        w.Write(InRange(speed ?? mode.Speed, mode.SpeedMin, mode.SpeedMax));
        if (protocolVersion >= 3) w.Write(InRange(brightness ?? mode.Brightness, mode.BrightnessMin, mode.BrightnessMax));
        w.Write(DirectionFor(mode, direction));

        var sendColors = colors ?? mode.Colors ?? Array.Empty<Color>();
        w.Write((uint)ColorModeFor(mode, colors is not null));
        w.Write((ushort)sendColors.Length);
        foreach (var c in sendColors)
        {
            w.Write(c.R);
            w.Write(c.G);
            w.Write(c.B);
            w.Write((byte)0);
        }

        w.Flush();
        var bytes = stream.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(0), (uint)bytes.Length);
        return bytes;
    }

    /// <summary>The server requires the colour mode to match the mode's flags.</summary>
    private static ColorMode ColorModeFor(Mode mode, bool colorsGiven)
    {
        var flags = mode.Flags;
        if (colorsGiven && flags.HasFlag(ModeFlags.HasModeSpecificColor)) return ColorMode.ModeSpecific;
        var current = mode.ColorMode;
        var valid = current switch
        {
            ColorMode.PerLed => flags.HasFlag(ModeFlags.HasPerLedColor),
            ColorMode.ModeSpecific => flags.HasFlag(ModeFlags.HasModeSpecificColor),
            ColorMode.Random => flags.HasFlag(ModeFlags.HasRandomColor),
            _ => !flags.HasFlag(ModeFlags.HasPerLedColor) && !flags.HasFlag(ModeFlags.HasModeSpecificColor) && !flags.HasFlag(ModeFlags.HasRandomColor),
        };
        if (valid) return current;
        if (flags.HasFlag(ModeFlags.HasPerLedColor)) return ColorMode.PerLed;
        if (flags.HasFlag(ModeFlags.HasModeSpecificColor)) return ColorMode.ModeSpecific;
        if (flags.HasFlag(ModeFlags.HasRandomColor)) return ColorMode.Random;
        return ColorMode.None;
    }

    /// <summary>Clamp to [min, max]; OpenRGB allows inverted ranges (e.g. speed 0 = fastest), so order them first.</summary>
    private static uint InRange(uint value, uint a, uint b) => Math.Clamp(value, Math.Min(a, b), Math.Max(a, b));
}
