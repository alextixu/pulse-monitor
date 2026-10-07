namespace Pulse.Logitech;

/// <summary>HID++ 1.0 / 2.0 wire-level constants (Solaar / logiops compatible).</summary>
internal static class Hidpp
{
    public const int LogitechVendorId = 0x046D;

    public const byte ShortReportId = 0x10;
    public const byte LongReportId = 0x11;
    public const byte VeryLongReportId = 0x12;
    public const int ShortLength = 7;
    public const int LongLength = 20;
    public const int VeryLongLength = 64;

    /// <summary>Device index used for the receiver itself and for devices connected directly over USB / Bluetooth.</summary>
    public const byte ReceiverIndex = 0xFF;
    public const byte MaxReceiverSlots = 6;

    /// <summary>Software id placed in the low nibble of the function byte so we can tell our replies from G HUB's.</summary>
    public const byte SoftwareId = 0x0A;

    public const byte RootFeatureIndex = 0x00;
    public const byte Hidpp2ErrorMarker = 0xFF;
    public const byte Hidpp1ErrorMarker = 0x8F;

    public const ushort UsagePageUsb = 0xFF00;
    public const ushort UsagePageBluetooth = 0xFF43;

    // HID++ 1.0 sub ids
    public const byte SetRegisterShort = 0x80;
    public const byte GetRegisterShort = 0x81;
    public const byte SetRegisterLong = 0x82;
    public const byte GetRegisterLong = 0x83;
    public const byte NotificationDeviceConnection = 0x41;

    // HID++ 1.0 registers
    public const byte RegisterConnectionState = 0x02;
    public const byte RegisterBatteryStatus = 0x07;
    public const byte RegisterBatteryMileage = 0x0D;
    public const byte RegisterReceiverInfo = 0xB5;

    // Sub-addresses of register 0xB5
    public const byte ReceiverInfoSerial = 0x03;
    public const byte PairingInfo = 0x20;          // + slot - 1
    public const byte ExtendedPairingInfo = 0x30;  // + slot - 1
    public const byte DeviceName = 0x40;           // + slot - 1
    public const byte BoltPairingInfo = 0x50;      // + slot
    public const byte BoltDeviceName = 0x60;       // + slot
}

/// <summary>HID++ 2.0 feature ids used by this provider.</summary>
internal static class HidppFeature
{
    public const ushort Root = 0x0000;
    public const ushort DeviceFwVersion = 0x0003;
    public const ushort DeviceName = 0x0005;
    public const ushort BatteryStatus = 0x1000;
    public const ushort BatteryVoltage = 0x1001;
    public const ushort UnifiedBattery = 0x1004;
}

internal enum Hidpp2Error : byte
{
    None = 0,
    Unknown = 1,
    InvalidArgument = 2,
    OutOfRange = 3,
    HwError = 4,
    LogitechInternal = 5,
    InvalidFeatureIndex = 6,
    InvalidFunctionId = 7,
    Busy = 8,
    Unsupported = 9,
}

internal enum Hidpp1Error : byte
{
    None = 0,
    InvalidSubId = 0x01,
    InvalidAddress = 0x02,
    InvalidValue = 0x03,
    ConnectFail = 0x04,
    TooManyDevices = 0x05,
    AlreadyExists = 0x06,
    Busy = 0x07,
    UnknownDevice = 0x08,
    ResourceError = 0x09,
    RequestUnavailable = 0x0A,
    InvalidParamValue = 0x0B,
    WrongPinCode = 0x0C,
}

internal enum ReceiverFamily
{
    None,
    Unifying,
    Nano,
    Lightspeed,
    Bolt,
}

internal static class ReceiverFamilies
{
    public static ReceiverFamily FromProductId(int pid) => pid switch
    {
        0xC52B or 0xC532 => ReceiverFamily.Unifying,
        0xC548 => ReceiverFamily.Bolt,
        0xC539 or 0xC53A or 0xC53D or 0xC53F or 0xC541 or 0xC545 or 0xC547 or 0xC54D or 0xC54E => ReceiverFamily.Lightspeed,
        0xC51B or 0xC517 or 0xC526 or 0xC52F or 0xC534 => ReceiverFamily.Nano,
        _ => ReceiverFamily.None,
    };

    /// <summary>Short transport label shown in the UI.</summary>
    public static string Label(ReceiverFamily family) => family switch
    {
        ReceiverFamily.Unifying => "Unifying",
        ReceiverFamily.Bolt => "Bolt",
        ReceiverFamily.Lightspeed => "LIGHTSPEED",
        ReceiverFamily.Nano => "Nano",
        _ => "USB",
    };
}
