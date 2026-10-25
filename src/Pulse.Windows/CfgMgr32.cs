using System.Runtime.InteropServices;

namespace Pulse.Windows;

/// <summary>A device property key (<c>DEVPROPKEY</c>): format GUID + property id.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct DEVPROPKEY
{
    public readonly Guid Fmtid;
    public readonly uint Pid;

    public DEVPROPKEY(Guid fmtid, uint pid)
    {
        Fmtid = fmtid;
        Pid = pid;
    }

    public override string ToString() => $"{{{Fmtid:D}}},{Pid}";
}

/// <summary>The handful of <c>DEVPKEY_*</c> keys the Bluetooth provider reads from BTHENUM\ / BTHLE\ device nodes.</summary>
internal static class DevPropKeys
{
    private static readonly Guid DeviceFmtid = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid BluetoothFmtid = new("2bd67d8b-8beb-48d5-87e0-6cda3428040a");

    /// <summary>DEVPKEY_NAME: the display name Windows shows for the node.</summary>
    public static readonly DEVPROPKEY Name = new(new Guid("b725f130-47ef-101a-a5f1-02608c9eebac"), 10);

    /// <summary>DEVPKEY_Device_FriendlyName: the user-visible Bluetooth device name.</summary>
    public static readonly DEVPROPKEY DeviceFriendlyName = new(DeviceFmtid, 14);

    /// <summary>DEVPKEY_DeviceContainer_Category (STRING_LIST, e.g. "Audio.Speakers", "Input.Gaming.Gamepad").</summary>
    public static readonly DEVPROPKEY DeviceContainerCategory = new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 90);

    /// <summary>DEVPKEY_Bluetooth_DeviceAddress (STRING, 12 hex digits).</summary>
    public static readonly DEVPROPKEY BluetoothDeviceAddress = new(BluetoothFmtid, 1);

    /// <summary>DEVPKEY_Bluetooth_ClassOfDevice (UINT32, classic devices only).</summary>
    public static readonly DEVPROPKEY BluetoothClassOfDevice = new(BluetoothFmtid, 10);

    /// <summary>DEVPKEY_Bluetooth_Battery (BYTE, 0..100): the level the Bluetooth stack last cached for the device.</summary>
    public static readonly DEVPROPKEY BluetoothBattery = new(new Guid("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), 2);
}

/// <summary>
/// Minimal CfgMgr32 (PnP configuration manager) surface: list device instance ids per enumerator, locate device nodes
/// (phantom-capable), read status and properties, walk children. Plain Win32; no WinRT, COM or SetupAPI.
/// </summary>
internal static unsafe partial class CfgMgr32
{
    private const string Library = "cfgmgr32.dll";

    // CONFIGRET codes
    public const uint CR_SUCCESS = 0x00;
    public const uint CR_NO_SUCH_DEVNODE = 0x0D;
    public const uint CR_BUFFER_SMALL = 0x1A;
    public const uint CR_NO_SUCH_VALUE = 0x25;

    public const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;

    /// <summary>Also locate devices that are installed but not currently present (paired-but-absent Bluetooth devices).</summary>
    public const uint CM_LOCATE_DEVNODE_PHANTOM = 0x4;

    // DN_* status bits (CM_Get_DevNode_Status)
    public const uint DN_STARTED = 0x8;
    public const uint DN_HAS_PROBLEM = 0x400;

    /// <summary>Set by the Bluetooth LE enumerator on a paired device whose node stays present (and started) while the device is out of reach.</summary>
    public const uint DN_DEVICE_DISCONNECTED = 0x02000000;

    // DEVPROP_TYPE_*
    public const uint DEVPROP_TYPE_BYTE = 0x03;
    public const uint DEVPROP_TYPE_UINT32 = 0x07;
    public const uint DEVPROP_TYPE_STRING = 0x12;
    public const uint DEVPROP_TYPE_STRING_INDIRECT = 0x19;
    public const uint DEVPROP_TYPE_STRING_LIST = 0x2012;

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint CM_Get_Device_ID_ListW(string? filter, char* buffer, uint bufferLength, uint flags);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint CM_Locate_DevNodeW(out uint devInst, string deviceInstanceId, uint flags);

    [LibraryImport(Library)]
    public static partial uint CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    [LibraryImport(Library)]
    public static partial uint CM_Get_DevNode_PropertyW(uint devInst, in DEVPROPKEY propertyKey, out uint propertyType, byte* buffer, ref uint bufferSize, uint flags);

    [LibraryImport(Library)]
    public static partial uint CM_Get_Child(out uint childDevInst, uint devInst, uint flags);

    [LibraryImport(Library)]
    public static partial uint CM_Get_Sibling(out uint siblingDevInst, uint devInst, uint flags);
}
