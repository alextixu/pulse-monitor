namespace Pulse.Asus;

/// <summary>
/// ASUS ROG / TUF peripheral protocol (the "TUF keyboard" protocol OpenRGB uses for Falchion, Strix Scope, Azoth…):
/// 64-byte output/input reports without a report id on the vendor collection (usage page 0xFF00, usage 0x0001).
/// Request <c>[cmd, sub, 0…]</c>; the reply echoes <c>cmd, sub</c> in bytes 0..1, a rejected request answers <c>FF AA</c>.
/// Offsets below are into the 65-byte HidSharp buffer, i.e. payload index + 1.
/// </summary>
internal static class AsusProtocol
{
    public const int VendorId = 0x0B05;
    public const ushort VendorUsagePage = 0xFF00;
    public const int ReportLength = 65;

    public const byte CmdQuery = 0x12;
    /// <summary>Firmware version. Used as the "speaks this protocol" probe.</summary>
    public const byte SubVersion = 0x00;
    /// <summary>Keyboard status. Verified on ROG Falchion (2.4 GHz): byte 11 = battery %, bytes 12..13 = little-endian value that looks like mV.</summary>
    public const byte SubKeyboardStatus = 0x01;
    /// <summary>ROG mouse battery query (rogdrv). Not verified on hardware: byte 5 = battery %.</summary>
    public const byte SubMouseBattery = 0x07;

    public const int KeyboardBatteryOffset = 11;
    public const int KeyboardVoltageOffset = 12;
    public const int MouseBatteryOffset = 5;

    /// <summary>Aura motherboard / LED-strip controllers expose a 65-byte vendor collection too but speak a different protocol; never probe them.</summary>
    public static readonly IReadOnlySet<int> AuraControllerProductIds = new HashSet<int>
    {
        0x1867, 0x1872, 0x18A3, 0x18A5, 0x1939, 0x19AF, 0x1AA6,
    };

    /// <summary>Products known to be the 2.4 GHz receiver of a wireless peripheral.</summary>
    public static readonly IReadOnlySet<int> WirelessReceiverProductIds = new HashSet<int>
    {
        0x193E, // ROG Falchion (wireless)
        0x1B04, // ROG Falchion RX Low Profile
    };

    public static bool IsNack(byte[] reply) => reply.Length > 2 && reply[1] == 0xFF && reply[2] == 0xAA;

    public static bool IsReplyTo(byte[] reply, byte cmd, byte sub) => reply.Length > 2 && reply[1] == cmd && reply[2] == sub;

    public static byte[] Request(byte cmd, byte sub)
    {
        var buffer = new byte[ReportLength];
        buffer[1] = cmd;
        buffer[2] = sub;
        return buffer;
    }
}
