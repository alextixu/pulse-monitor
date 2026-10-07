using Pulse.Core.Models;

namespace Pulse.Windows;

/// <summary>
/// Maps Bluetooth metadata to a <see cref="DeviceKind"/>: BLE appearance first, then classic Class-of-Device,
/// then name heuristics.
/// </summary>
internal static class BluetoothDeviceKindInference
{
    public static DeviceKind Infer(ushort? appearance, ushort? codMajor, ushort? codMinor, string? name)
    {
        var kind = DeviceKind.Unknown;
        if (appearance is { } ap) kind = FromAppearance(ap);
        if (kind == DeviceKind.Unknown && codMajor is { } major) kind = FromClassOfDevice(major, codMinor);
        if (kind == DeviceKind.Unknown && !string.IsNullOrWhiteSpace(name)) kind = FromName(name);
        return kind;
    }

    /// <summary>Bluetooth SIG "Appearance" values: category in bits 15..6, sub-category in bits 5..0.</summary>
    internal static DeviceKind FromAppearance(ushort appearance)
    {
        var category = appearance >> 6;
        var sub = appearance & 0x3F;
        return category switch
        {
            0x001 => DeviceKind.Phone,
            0x002 => sub == 0x07 ? DeviceKind.Tablet : DeviceKind.Laptop, // computer: 0x0087 tablet, 0x0083 laptop, ...
            0x003 => DeviceKind.Watch,
            0x00F => sub switch // HID (0x03C0)
            {
                0x01 => DeviceKind.Keyboard,
                0x02 => DeviceKind.Mouse,
                0x03 => DeviceKind.Controller, // joystick
                0x04 => DeviceKind.Controller, // gamepad
                0x05 => DeviceKind.Tablet, // digitizer tablet
                0x07 => DeviceKind.Stylus, // digital pen
                0x09 => DeviceKind.Trackpad,
                0x0A => DeviceKind.Presenter, // presentation remote
                _ => DeviceKind.Other,
            },
            0x025 => DeviceKind.Speaker, // audio sink (0x0940): speaker, soundbar, ...
            0x026 => DeviceKind.Headset, // microphone (0x0980)
            0x029 => DeviceKind.Headset, // wearable audio (0x0A40): earbud, headset, headphones, neck band
            0x02E => DeviceKind.Headset, // hearing aid (0x0B80)
            0x02F => DeviceKind.Controller, // gaming (0x0BC0)
            _ => DeviceKind.Unknown,
        };
    }

    /// <summary>
    /// Classic Class-of-Device. <paramref name="minor"/> is accepted either as the 6-bit minor field (what
    /// <c>System.Devices.Aep.Bluetooth.Cod.Minor</c> reports) or as the raw CoD byte with the minor bits in 7..2.
    /// </summary>
    internal static DeviceKind FromClassOfDevice(ushort major, ushort? minorValue)
    {
        var minor = minorValue ?? 0;
        if (minor > 0x3F) minor >>= 2;

        switch (major)
        {
            case 1: // computer
                return minor == 7 ? DeviceKind.Tablet : DeviceKind.Laptop;
            case 2: // phone
                return DeviceKind.Phone;
            case 4: // audio / video
                return minor switch
                {
                    1 or 2 or 6 => DeviceKind.Headset, // wearable headset, hands-free, headphones
                    0 => DeviceKind.Unknown,
                    _ => DeviceKind.Speaker, // loudspeaker, portable audio, car audio, hifi, ...
                };
            case 5: // peripheral: bits 5..4 keyboard / pointing, bits 3..0 device type
            {
                var lowNibble = minor & 0x0F;
                switch (lowNibble)
                {
                    case 1: return DeviceKind.Controller; // joystick
                    case 2: return DeviceKind.Controller; // gamepad
                    case 3: return DeviceKind.Presenter; // remote control
                    case 5: return DeviceKind.Tablet; // digitizer tablet
                    case 7: return DeviceKind.Stylus; // digital pen
                }

                return (minor & 0x30) switch
                {
                    0x10 => DeviceKind.Keyboard,
                    0x20 => DeviceKind.Mouse,
                    0x30 => DeviceKind.Keyboard, // keyboard + pointing combo
                    _ => DeviceKind.Other,
                };
            }
            case 7: // wearable
                return minor == 1 ? DeviceKind.Watch : DeviceKind.Other;
            default:
                return DeviceKind.Unknown;
        }
    }

    internal static DeviceKind FromName(string name)
    {
        var n = name.ToLowerInvariant();
        bool Has(params string[] words) => words.Any(w => n.Contains(w, StringComparison.Ordinal));

        if (Has("controller", "gamepad", "joystick", "joy-con", "dualsense", "dualshock")) return DeviceKind.Controller;
        if (Has("soundbar", "speaker")) return DeviceKind.Speaker;
        if (Has("headset", "headphone", "earbud", "buds", "airpods", "earphone")) return DeviceKind.Headset;
        if (Has("trackpad", "touchpad")) return DeviceKind.Trackpad;
        if (Has("keyboard", "keypad")) return DeviceKind.Keyboard;
        if (Has("mouse")) return DeviceKind.Mouse;
        if (Has("pencil", "stylus")) return DeviceKind.Stylus;
        if (Has("watch")) return DeviceKind.Watch;
        if (Has("presenter", "spotlight", "clicker")) return DeviceKind.Presenter;
        if (Has("ipad", "tablet")) return DeviceKind.Tablet;
        if (Has("iphone", "phone", "pixel", "galaxy")) return DeviceKind.Phone;
        if (Has("macbook", "laptop", "notebook")) return DeviceKind.Laptop;
        return DeviceKind.Unknown;
    }
}
