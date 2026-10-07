using System.Text;
using Pulse.Core.Models;

namespace Pulse.Logitech;

/// <summary>Frame builders, reply matching and small parsers shared by the transport and device logic.</summary>
internal static class HidppProtocol
{
    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;
        var sb = new StringBuilder(bytes.Length * 3);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>Long (0x11) HID++ 2.0 request: [id, deviceIndex, featureIndex, fn&lt;&lt;4 | swId, params…] padded to 20 bytes.</summary>
    public static byte[] Long(byte deviceIndex, byte featureIndex, byte function, params byte[] parameters)
    {
        var frame = new byte[Hidpp.LongLength];
        frame[0] = Hidpp.LongReportId;
        frame[1] = deviceIndex;
        frame[2] = featureIndex;
        frame[3] = (byte)((function << 4) | Hidpp.SoftwareId);
        var n = Math.Min(parameters.Length, Hidpp.LongLength - 4);
        Array.Copy(parameters, 0, frame, 4, n);
        return frame;
    }

    /// <summary>Short (0x10) HID++ 1.0 request: [id, deviceIndex, subId, address, p0, p1, p2].</summary>
    public static byte[] Short(byte deviceIndex, byte subId, byte address, params byte[] parameters)
    {
        var frame = new byte[Hidpp.ShortLength];
        frame[0] = Hidpp.ShortReportId;
        frame[1] = deviceIndex;
        frame[2] = subId;
        frame[3] = address;
        var n = Math.Min(parameters.Length, Hidpp.ShortLength - 4);
        Array.Copy(parameters, 0, frame, 4, n);
        return frame;
    }

    /// <summary>Re-encodes a short request as a long one (needed on Bluetooth, which only has long reports).</summary>
    public static byte[] ShortToLong(byte[] shortFrame)
    {
        var frame = new byte[Hidpp.LongLength];
        frame[0] = Hidpp.LongReportId;
        Array.Copy(shortFrame, 1, frame, 1, Math.Min(shortFrame.Length, Hidpp.ShortLength) - 1);
        return frame;
    }

    /// <summary>
    /// True when <paramref name="reply"/> answers <paramref name="request"/>: same device index and either the same
    /// (featureIndex/subId, fn+swId/address) header (plus parameter byte <paramref name="matchParam"/> echoed when it
    /// is not negative), or an error frame quoting that header.
    /// </summary>
    public static bool IsReplyTo(byte[] request, byte[] reply, int matchParam = -1)
    {
        if (reply.Length < 4 || request.Length < 4) return false;
        if (reply[1] != request[1]) return false;

        if (reply[2] == request[2] && reply[3] == request[3])
        {
            if (matchParam < 0) return true;
            var i = 4 + matchParam;
            return reply.Length > i && request.Length > i && reply[i] == request[i];
        }

        if ((reply[2] == Hidpp.Hidpp2ErrorMarker || reply[2] == Hidpp.Hidpp1ErrorMarker)
            && reply.Length >= 6 && reply[3] == request[2] && reply[4] == request[3])
        {
            return true;
        }

        return false;
    }

    public static HidppResult Classify(byte[] reply)
    {
        if (reply.Length >= 6 && reply[2] == Hidpp.Hidpp2ErrorMarker) return HidppResult.Error2(reply);
        if (reply.Length >= 6 && reply[2] == Hidpp.Hidpp1ErrorMarker) return HidppResult.Error1(reply);
        return HidppResult.Success(reply);
    }

    /// <summary>Printable ASCII run starting at <paramref name="offset"/>, at most <paramref name="maxLength"/> chars, stops at NUL.</summary>
    public static string Ascii(ReadOnlySpan<byte> frame, int offset, int maxLength)
    {
        if (offset >= frame.Length || maxLength <= 0) return string.Empty;
        var end = Math.Min(frame.Length, offset + maxLength);
        var sb = new StringBuilder();
        for (var i = offset; i < end; i++)
        {
            var b = frame[i];
            if (b == 0) break;
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '?');
        }
        return sb.ToString().Trim();
    }

    /// <summary>HID++ 2.0 DEVICE_NAME (0x0005) getDeviceType → <see cref="DeviceKind"/>.</summary>
    public static DeviceKind KindFromDeviceType(byte type) => type switch
    {
        0 => DeviceKind.Keyboard,
        2 => DeviceKind.Keyboard,   // numpad
        3 => DeviceKind.Mouse,
        4 => DeviceKind.Trackpad,
        5 => DeviceKind.Mouse,      // trackball
        6 => DeviceKind.Presenter,
        7 => DeviceKind.Receiver,
        8 => DeviceKind.Headset,
        10 or 11 or 12 or 17 or 18 => DeviceKind.Controller,
        14 => DeviceKind.Speaker,
        _ => DeviceKind.Other,
    };

    /// <summary>HID++ 1.0 receiver pairing-info device kind nibble → <see cref="DeviceKind"/>.</summary>
    public static DeviceKind KindFromPairingNibble(int nibble) => nibble switch
    {
        1 => DeviceKind.Keyboard,
        2 => DeviceKind.Mouse,
        3 => DeviceKind.Keyboard,   // numpad
        4 => DeviceKind.Presenter,
        8 => DeviceKind.Mouse,      // trackball
        9 => DeviceKind.Trackpad,
        0 => DeviceKind.Unknown,
        _ => DeviceKind.Other,
    };
}
