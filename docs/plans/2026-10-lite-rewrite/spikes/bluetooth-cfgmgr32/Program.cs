// Spike: enumerate paired Bluetooth devices + cached battery level WITHOUT WinRT.
// Only cfgmgr32.dll (CM_* devnode API + the Win32 DevQuery API that WinRT DeviceInformation wraps)
// and bthprops.cpl (classic BluetoothFindFirstDevice) are used. TargetFramework is plain net9.0-windows.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace SpikeBtBattery;

[StructLayout(LayoutKind.Sequential)]
internal struct DEVPROPKEY : IEquatable<DEVPROPKEY>
{
    public Guid Fmtid;
    public uint Pid;

    public DEVPROPKEY(string guid, uint pid)
    {
        Fmtid = new Guid(guid);
        Pid = pid;
    }

    public bool Equals(DEVPROPKEY other) => Fmtid == other.Fmtid && Pid == other.Pid;
    public override bool Equals(object? obj) => obj is DEVPROPKEY k && Equals(k);
    public override int GetHashCode() => HashCode.Combine(Fmtid, Pid);
    public override string ToString() => $"{{{Fmtid:D}}},{Pid}";
}

[StructLayout(LayoutKind.Sequential)]
internal struct DEVPROPCOMPKEY
{
    public DEVPROPKEY Key;
    public int Store;          // DEVPROP_STORE_SYSTEM = 0
    public IntPtr LocaleName;  // PCWSTR
}

[StructLayout(LayoutKind.Sequential)]
internal struct DEVPROPERTY
{
    public DEVPROPCOMPKEY CompKey;
    public uint Type;
    public uint BufferSize;
    public IntPtr Buffer;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DEVPROP_FILTER_EXPRESSION
{
    public uint Operator;
    public DEVPROPERTY Property;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DEV_OBJECT
{
    public int ObjectType;
    public IntPtr pszObjectId;
    public uint cPropertyCount;
    public IntPtr pProperties;
}

// DEV_QUERY_RESULT_ACTION_DATA: { DEV_QUERY_RESULT_ACTION Action; union { DEV_QUERY_STATE State; DEV_OBJECT DeviceObject; } Data; }
[StructLayout(LayoutKind.Explicit, Size = 40)]
internal struct DEV_QUERY_RESULT_ACTION_DATA
{
    [FieldOffset(0)] public int Action;
    [FieldOffset(8)] public int State;
    [FieldOffset(8)] public DEV_OBJECT DeviceObject;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEMTIME
{
    public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    public override string ToString() => Year == 0 ? "-" : $"{Year:D4}-{Month:D2}-{Day:D2} {Hour:D2}:{Minute:D2}:{Second:D2}";
}

[StructLayout(LayoutKind.Sequential)]
internal struct BLUETOOTH_DEVICE_SEARCH_PARAMS
{
    public uint dwSize;
    public int fReturnAuthenticated;
    public int fReturnRemembered;
    public int fReturnUnknown;
    public int fReturnConnected;
    public int fIssueInquiry;
    public byte cTimeoutMultiplier;
    public IntPtr hRadio;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct BLUETOOTH_DEVICE_INFO
{
    public uint dwSize;
    public ulong Address;
    public uint ulClassofDevice;
    public int fConnected;
    public int fRemembered;
    public int fAuthenticated;
    public SYSTEMTIME stLastSeen;
    public SYSTEMTIME stLastUsed;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
    public string szName;
}

internal static unsafe class Native
{
    private const string CfgMgr = "cfgmgr32.dll";

    public const uint CR_SUCCESS = 0x00;
    public const uint CR_NO_SUCH_DEVNODE = 0x0D;
    public const uint CR_BUFFER_SMALL = 0x1A;
    public const uint CR_NO_SUCH_VALUE = 0x25;

    public const uint CM_GETIDLIST_FILTER_NONE = 0x0;
    public const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;
    public const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;
    public const uint CM_LOCATE_DEVNODE_NORMAL = 0x0;
    public const uint CM_LOCATE_DEVNODE_PHANTOM = 0x4;

    public const uint DN_STARTED = 0x8;
    public const uint DN_HAS_PROBLEM = 0x400;
    public const uint DN_DEVICE_DISCONNECTED = 0x02000000; // == DN_NEEDS_LOCKING

    // DEVPROP_TYPE_*
    public const uint T_EMPTY = 0, T_NULL = 1, T_SBYTE = 2, T_BYTE = 3, T_INT16 = 4, T_UINT16 = 5, T_INT32 = 6, T_UINT32 = 7,
        T_INT64 = 8, T_UINT64 = 9, T_FLOAT = 0xA, T_DOUBLE = 0xB, T_DECIMAL = 0xC, T_GUID = 0xD, T_CURRENCY = 0xE, T_DATE = 0xF,
        T_FILETIME = 0x10, T_BOOLEAN = 0x11, T_STRING = 0x12, T_SECURITY_DESCRIPTOR = 0x13, T_SECURITY_DESCRIPTOR_STRING = 0x14,
        T_DEVPROPKEY = 0x15, T_DEVPROPTYPE = 0x16, T_ERROR = 0x17, T_NTSTATUS = 0x18, T_STRING_INDIRECT = 0x19,
        T_STRING_LIST = 0x2012, T_BINARY = 0x1003;

    // DevQuery
    public const int DevObjectTypeDevice = 3;
    public const int DevObjectTypeAEP = 5;
    public const uint DevQueryFlagAllProperties = 0x2;
    public const uint DEVPROP_OPERATOR_EQUALS = 0x2;

    [DllImport(CfgMgr, CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszFilter, uint ulFlags);

    [DllImport(CfgMgr, CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint CM_Get_Device_ID_ListW(string? pszFilter, char* buffer, uint bufferLen, uint ulFlags);

    [DllImport(CfgMgr, CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport(CfgMgr, ExactSpelling = true)]
    public static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    [DllImport(CfgMgr, ExactSpelling = true)]
    public static extern uint CM_Get_DevNode_PropertyW(uint dnDevInst, DEVPROPKEY* propertyKey, out uint propertyType, byte* buffer, ref uint bufferSize, uint ulFlags);

    [DllImport(CfgMgr, ExactSpelling = true)]
    public static extern uint CM_Get_DevNode_Property_Keys(uint dnDevInst, DEVPROPKEY* keys, ref uint count, uint ulFlags);

    [DllImport(CfgMgr, ExactSpelling = true)]
    public static extern uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport(CfgMgr, ExactSpelling = true)]
    public static extern uint CM_Get_Sibling(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport(CfgMgr, CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint CM_Get_Device_IDW(uint dnDevInst, char* buffer, uint bufferLen, uint ulFlags);

    [DllImport(CfgMgr, ExactSpelling = true)]
    public static extern int DevGetObjects(int objectType, uint queryFlags, uint cRequestedProperties, DEVPROPCOMPKEY* requested,
        uint cFilterExpressionCount, DEVPROP_FILTER_EXPRESSION* filters, out uint pcObjectCount, out DEV_OBJECT* ppObjects);

    [DllImport(CfgMgr, ExactSpelling = true)]
    public static extern void DevFreeObjects(uint cObjectCount, DEV_OBJECT* pObjects);

    // Async variant: results stream in through the callback; EnumCompleted may lag far behind the last result.
    [DllImport(CfgMgr, ExactSpelling = true)]
    public static extern int DevCreateObjectQuery(int objectType, uint queryFlags, uint cRequestedProperties, DEVPROPCOMPKEY* requested,
        uint cFilterExpressionCount, DEVPROP_FILTER_EXPRESSION* filters, IntPtr callback, IntPtr context, out IntPtr hDevQuery);

    [DllImport(CfgMgr, ExactSpelling = true)]
    public static extern void DevCloseObjectQuery(IntPtr hDevQuery);

    public const int DevQueryResultStateChange = 0, DevQueryResultAdd = 1, DevQueryResultUpdate = 2, DevQueryResultRemove = 3;
    public const int DevQueryStateInitialized = 0, DevQueryStateEnumCompleted = 1, DevQueryStateAborted = 2, DevQueryStateClosed = 3;

    [DllImport("bthprops.cpl", ExactSpelling = true, SetLastError = true)]
    public static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS searchParams, ref BLUETOOTH_DEVICE_INFO deviceInfo);

    [DllImport("bthprops.cpl", ExactSpelling = true, SetLastError = true)]
    public static extern int BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO deviceInfo);

    [DllImport("bthprops.cpl", ExactSpelling = true)]
    public static extern int BluetoothFindDeviceClose(IntPtr hFind);
}

internal static class Keys
{
    private const string Dev = "a45c254e-df1c-4efd-8020-67d146a850e0";
    private const string Rel = "4340a6c5-93fa-4706-972c-7b648008a5a7";
    private const string Ext = "540b947e-8b40-45bc-a8a2-6a0b894cbda2";
    private const string Bt = "2bd67d8b-8beb-48d5-87e0-6cda3428040a";
    private const string Aep = "a35996ab-11cf-4935-8b61-a6761081ecdf";
    private const string AepLe = "995ef0b0-7eb3-4a8b-b9ce-068bb3f4af69";
    private const string AepCod = "5fbd34cd-561a-412e-ba98-478a6b0fef1d";

    public static readonly DEVPROPKEY NAME = new("b725f130-47ef-101a-a5f1-02608c9eebac", 10);
    public static readonly DEVPROPKEY Device_DeviceDesc = new(Dev, 2);
    public static readonly DEVPROPKEY Device_HardwareIds = new(Dev, 3);
    public static readonly DEVPROPKEY Device_Class = new(Dev, 9);
    public static readonly DEVPROPKEY Device_FriendlyName = new(Dev, 14);
    public static readonly DEVPROPKEY Device_EnumeratorName = new(Dev, 24);
    public static readonly DEVPROPKEY Device_DevNodeStatus = new(Rel, 2);
    public static readonly DEVPROPKEY Device_ProblemCode = new(Rel, 3);
    public static readonly DEVPROPKEY Device_Parent = new(Rel, 8);
    public static readonly DEVPROPKEY Device_Children = new(Rel, 9);
    public static readonly DEVPROPKEY Device_IsPresent = new(Ext, 5);
    public static readonly DEVPROPKEY Device_HasProblem = new(Ext, 6);
    public static readonly DEVPROPKEY Device_InstanceId = new("78c34fc8-104a-4aca-9ea4-524d52996e57", 256);
    public static readonly DEVPROPKEY Device_ContainerId = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
    public static readonly DEVPROPKEY Device_LastArrivalDate = new("83da6326-97a6-4088-9453-a1923f573b29", 102);
    public static readonly DEVPROPKEY Device_LastRemovalDate = new("83da6326-97a6-4088-9453-a1923f573b29", 103);

    public static readonly DEVPROPKEY Bluetooth_DeviceAddress = new(Bt, 1);
    public static readonly DEVPROPKEY Bluetooth_ServiceGUID = new(Bt, 2);
    public static readonly DEVPROPKEY Bluetooth_DeviceFlags = new(Bt, 3);
    public static readonly DEVPROPKEY Bluetooth_DeviceManufacturer = new(Bt, 4);
    public static readonly DEVPROPKEY Bluetooth_DeviceModelNumber = new(Bt, 5);
    public static readonly DEVPROPKEY Bluetooth_DeviceVIDSource = new(Bt, 6);
    public static readonly DEVPROPKEY Bluetooth_DeviceVID = new(Bt, 7);
    public static readonly DEVPROPKEY Bluetooth_DevicePID = new(Bt, 8);
    public static readonly DEVPROPKEY Bluetooth_DeviceProductVersion = new(Bt, 9);
    public static readonly DEVPROPKEY Bluetooth_ClassOfDevice = new(Bt, 10);
    public static readonly DEVPROPKEY Bluetooth_LastConnectedTime = new(Bt, 11);
    public static readonly DEVPROPKEY Bluetooth_LastSeenTime = new(Bt, 12);
    public static readonly DEVPROPKEY Bluetooth_Battery = new("104ea319-6ee2-4701-bd47-8ddbf425bbe5", 2);
    public static readonly DEVPROPKEY DeviceContainer_Category = new("78c34fc8-104a-4aca-9ea4-524d52996e57", 90);

    // AEP (association endpoint) keys = the System.Devices.Aep.* properties the WinRT provider reads.
    public static readonly DEVPROPKEY Aep_ProtocolId = new("3b2ce006-5e61-4fde-bab8-9b8aac9b26df", 5);
    public static readonly DEVPROPKEY Aep_AepId = new("3b2ce006-5e61-4fde-bab8-9b8aac9b26df", 8);
    public static readonly DEVPROPKEY Aep_IsConnected = new(Aep, 7);
    public static readonly DEVPROPKEY Aep_IsPresent = new(Aep, 9);
    public static readonly DEVPROPKEY Aep_DeviceAddress = new(Aep, 12);
    public static readonly DEVPROPKEY Aep_IsPaired = new(Aep, 16);
    public static readonly DEVPROPKEY Aep_Le_Appearance = new(AepLe, 1);
    public static readonly DEVPROPKEY Aep_Cod_Major = new(AepCod, 2);
    public static readonly DEVPROPKEY Aep_Cod_Minor = new(AepCod, 3);

    public static readonly Guid ProtocolBluetoothClassic = new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
    public static readonly Guid ProtocolBluetoothLE = new("bb7bb05e-5972-42b5-94fc-76eaa7084d49");

    public static readonly Dictionary<DEVPROPKEY, string> Names = new()
    {
        [NAME] = "DEVPKEY_NAME",
        [Device_DeviceDesc] = "Device_DeviceDesc",
        [Device_HardwareIds] = "Device_HardwareIds",
        [new(Dev, 4)] = "Device_CompatibleIds",
        [new(Dev, 6)] = "Device_Service",
        [Device_Class] = "Device_Class",
        [new(Dev, 10)] = "Device_ClassGuid",
        [new(Dev, 11)] = "Device_Driver",
        [new(Dev, 12)] = "Device_ConfigFlags",
        [new(Dev, 13)] = "Device_Manufacturer",
        [Device_FriendlyName] = "Device_FriendlyName",
        [new(Dev, 15)] = "Device_LocationInfo",
        [new(Dev, 16)] = "Device_PDOName",
        [new(Dev, 17)] = "Device_Capabilities",
        [new(Dev, 18)] = "Device_UINumber",
        [new(Dev, 21)] = "Device_BusTypeGuid",
        [new(Dev, 22)] = "Device_LegacyBusType",
        [new(Dev, 23)] = "Device_BusNumber",
        [Device_EnumeratorName] = "Device_EnumeratorName",
        [new(Dev, 25)] = "Device_Security",
        [new(Dev, 26)] = "Device_SecuritySDS",
        [new(Dev, 27)] = "Device_DevType",
        [new(Dev, 28)] = "Device_Exclusive",
        [new(Dev, 29)] = "Device_Characteristics",
        [new(Dev, 30)] = "Device_Address",
        [new(Dev, 32)] = "Device_PowerData",
        [new(Dev, 33)] = "Device_RemovalPolicy",
        [new(Dev, 34)] = "Device_RemovalPolicyDefault",
        [new(Dev, 36)] = "Device_InstallState",
        [new(Dev, 37)] = "Device_LocationPaths",
        [new(Dev, 38)] = "Device_BaseContainerId",
        [Device_DevNodeStatus] = "Device_DevNodeStatus",
        [Device_ProblemCode] = "Device_ProblemCode",
        [new(Rel, 7)] = "Device_BusRelations",
        [Device_Parent] = "Device_Parent",
        [Device_Children] = "Device_Children",
        [new(Rel, 10)] = "Device_Siblings",
        [new(Rel, 12)] = "Device_ProblemStatus",
        [new(Ext, 4)] = "Device_BusReportedDeviceDesc",
        [Device_IsPresent] = "Device_IsPresent",
        [Device_HasProblem] = "Device_HasProblem",
        [new(Ext, 7)] = "Device_ConfigurationId",
        [new(Ext, 8)] = "Device_ReportedDeviceIdsHash",
        [new(Ext, 11)] = "Device_DriverProblemDesc",
        [new(Ext, 14)] = "Device_Stack",
        [new(Ext, 16)] = "Device_IsRebootRequired",
        [new(Ext, 22)] = "Device_SoftRestartSupported",
        [new(Ext, 23)] = "Device_ExtendedAddress",
        [Device_InstanceId] = "Device_InstanceId",
        [new("78c34fc8-104a-4aca-9ea4-524d52996e57", 39)] = "Device_Model",
        [Device_ContainerId] = "Device_ContainerId",
        [new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 4)] = "Device_InLocalMachineContainer",
        [new("80497100-8c73-48b9-aad9-ce387e19c56e", 2)] = "Device_Reported",
        [new("80497100-8c73-48b9-aad9-ce387e19c56e", 3)] = "Device_Legacy",
        [new("80d81ea6-7473-4b0c-8216-efc11a2c4c8b", 2)] = "Device_ModelId",
        [new("80d81ea6-7473-4b0c-8216-efc11a2c4c8b", 3)] = "Device_FriendlyNameAttributes",
        [new("80d81ea6-7473-4b0c-8216-efc11a2c4c8b", 4)] = "Device_ManufacturerAttributes",
        [new("80d81ea6-7473-4b0c-8216-efc11a2c4c8b", 5)] = "Device_PresenceNotForDevice",
        [new("80d81ea6-7473-4b0c-8216-efc11a2c4c8b", 6)] = "Device_SignalStrength",
        [new("80d81ea6-7473-4b0c-8216-efc11a2c4c8b", 7)] = "Device_IsAssociateableByUserAction",
        [new("80d81ea6-7473-4b0c-8216-efc11a2c4c8b", 8)] = "Device_ShowInUninstallUI",
        [new("83da6326-97a6-4088-9453-a1923f573b29", 2)] = "Device_DriverDate",
        [new("83da6326-97a6-4088-9453-a1923f573b29", 3)] = "Device_DriverVersion",
        [new("83da6326-97a6-4088-9453-a1923f573b29", 4)] = "Device_DriverDesc",
        [new("83da6326-97a6-4088-9453-a1923f573b29", 5)] = "Device_DriverInfPath",
        [new("83da6326-97a6-4088-9453-a1923f573b29", 6)] = "Device_DriverInfSection",
        [new("83da6326-97a6-4088-9453-a1923f573b29", 7)] = "Device_DriverInfSectionExt",
        [new("83da6326-97a6-4088-9453-a1923f573b29", 8)] = "Device_MatchingDeviceId",
        [new("83da6326-97a6-4088-9453-a1923f573b29", 9)] = "Device_DriverProvider",
        [new("83da6326-97a6-4088-9453-a1923f573b29", 14)] = "Device_DriverRank",
        [new("83da6326-97a6-4088-9453-a1923f573b29", 15)] = "Device_DriverLogoLevel",
        [new("83da6326-97a6-4088-9453-a1923f573b29", 17)] = "Device_NoConnectSound",
        [new("83da6326-97a6-4088-9453-a1923f573b29", 18)] = "Device_GenericDriverInstalled",
        [new("83da6326-97a6-4088-9453-a1923f573b29", 19)] = "Device_AdditionalSoftwareRequested",
        [new("83da6326-97a6-4088-9453-a1923f573b29", 100)] = "Device_InstallDate",
        [new("83da6326-97a6-4088-9453-a1923f573b29", 101)] = "Device_FirstInstallDate",
        [Device_LastArrivalDate] = "Device_LastArrivalDate",
        [Device_LastRemovalDate] = "Device_LastRemovalDate",
        [new("afd97640-86a3-4210-b67c-289c41aabe55", 2)] = "Device_SafeRemovalRequired",
        [new("afd97640-86a3-4210-b67c-289c41aabe55", 3)] = "Device_SafeRemovalRequiredOverride",
        [new("259abffc-50a7-47ce-af08-68c9a7d73366", 10)] = "DrvPkg_Model",
        [new("259abffc-50a7-47ce-af08-68c9a7d73366", 11)] = "DrvPkg_VendorWebSite",
        [new("259abffc-50a7-47ce-af08-68c9a7d73366", 12)] = "DrvPkg_DetailedDescription",
        [new("259abffc-50a7-47ce-af08-68c9a7d73366", 13)] = "DrvPkg_DocumentationLink",
        [new("259abffc-50a7-47ce-af08-68c9a7d73366", 14)] = "DrvPkg_Icon",
        [new("259abffc-50a7-47ce-af08-68c9a7d73366", 15)] = "DrvPkg_BrandingIcon",
        [new("4321918b-f69e-470d-a5de-4d88c75ad24b", 1)] = "DeviceClass_UpperFilters",
        [Bluetooth_DeviceAddress] = "Bluetooth_DeviceAddress",
        [Bluetooth_ServiceGUID] = "Bluetooth_ServiceGUID",
        [Bluetooth_DeviceFlags] = "Bluetooth_DeviceFlags",
        [Bluetooth_DeviceManufacturer] = "Bluetooth_DeviceManufacturer",
        [Bluetooth_DeviceModelNumber] = "Bluetooth_DeviceModelNumber",
        [Bluetooth_DeviceVIDSource] = "Bluetooth_DeviceVIDSource",
        [Bluetooth_DeviceVID] = "Bluetooth_DeviceVID",
        [Bluetooth_DevicePID] = "Bluetooth_DevicePID",
        [Bluetooth_DeviceProductVersion] = "Bluetooth_DeviceProductVersion",
        [Bluetooth_ClassOfDevice] = "Bluetooth_ClassOfDevice",
        [Bluetooth_LastConnectedTime] = "Bluetooth_LastConnectedTime",
        [Bluetooth_LastSeenTime] = "Bluetooth_LastSeenTime",
        [Bluetooth_Battery] = "Bluetooth_Battery",
        [Aep_ProtocolId] = "Aep.ProtocolId",
        [Aep_AepId] = "Aep.AepId",
        [new(Aep, 3)] = "Aep.ModelName",
        [new(Aep, 4)] = "Aep.ModelId",
        [new(Aep, 5)] = "Aep.Manufacturer",
        [new(Aep, 6)] = "Aep.SignalStrength",
        [Aep_IsConnected] = "Aep.IsConnected",
        [Aep_IsPresent] = "Aep.IsPresent",
        [Aep_DeviceAddress] = "Aep.DeviceAddress",
        [Aep_IsPaired] = "Aep.IsPaired",
        [new(Aep, 17)] = "Aep.Category",
        [new("e7c3fb29-caa7-4f47-8c8b-be59b330d4c5", 2)] = "Aep.ContainerId",
        [new("e7c3fb29-caa7-4f47-8c8b-be59b330d4c5", 3)] = "Aep.CanPair",
        [Aep_Le_Appearance] = "Aep.Bluetooth.Le.Appearance",
        [new(AepLe, 4)] = "Aep.Bluetooth.Le.AddressType",
        [new(AepLe, 5)] = "Aep.Bluetooth.Le.Appearance.Category (observed UINT16)",
        [new(AepLe, 6)] = "Aep.Bluetooth.Le.Appearance.Subcategory (observed UINT16)",
        [new(AepLe, 8)] = "Aep.Bluetooth.Le.IsConnectable",
        [new("78c34fc8-104a-4aca-9ea4-524d52996e57", 52)] = "DeviceContainer_DiscoveryMethod",
        [new("78c34fc8-104a-4aca-9ea4-524d52996e57", 57)] = "DeviceContainer_Icon",
        [new("78c34fc8-104a-4aca-9ea4-524d52996e57", 90)] = "DeviceContainer_Category",
        [new("78c34fc8-104a-4aca-9ea4-524d52996e57", 98)] = "DeviceContainer_UnpairUninstall",
        [new("78c34fc8-104a-4aca-9ea4-524d52996e57", 101)] = "DeviceContainer_AlwaysShowDeviceAsConnected",
        [new("104ea319-6ee2-4701-bd47-8ddbf425bbe5", 3)] = "Bluetooth_Battery fmtid pid 3 (BOOLEAN, unnamed)",
        [new("104ea319-6ee2-4701-bd47-8ddbf425bbe5", 7)] = "Bluetooth_Battery fmtid pid 7 (FILETIME, unnamed)",
        [new(AepCod, 1)] = "Aep.Bluetooth.IssueInquiry?",
        [Aep_Cod_Major] = "Aep.Bluetooth.Cod.Major",
        [Aep_Cod_Minor] = "Aep.Bluetooth.Cod.Minor",
        [new(AepCod, 4)] = "Aep.Bluetooth.Cod.Services.LimitedDiscovery",
        [new(AepCod, 6)] = "Aep.Bluetooth.Cod.Services.Positioning",
        [new(AepCod, 7)] = "Aep.Bluetooth.Cod.Services.Networking",
        [new(AepCod, 8)] = "Aep.Bluetooth.Cod.Services.Rendering",
        [new(AepCod, 9)] = "Aep.Bluetooth.Cod.Services.Capturing",
        [new(AepCod, 10)] = "Aep.Bluetooth.Cod.Services.ObjectXfer",
        [new(AepCod, 11)] = "Aep.Bluetooth.Cod.Services.Audio",
        [new(AepCod, 12)] = "Aep.Bluetooth.Cod.Services.Telephony",
        [new(AepCod, 13)] = "Aep.Bluetooth.Cod.Services.Information",
    };

    public static string NameOf(DEVPROPKEY key) => Names.TryGetValue(key, out var n) ? n : key.ToString();
}

internal sealed record PropValue(uint Type, byte[] Data)
{
    public string? AsString() => Type is Native.T_STRING or Native.T_STRING_INDIRECT ? Encoding.Unicode.GetString(Data).TrimEnd('\0') : null;
    public byte? AsByte() => Type == Native.T_BYTE && Data.Length >= 1 ? Data[0] : null;
    public ushort? AsUInt16() => Type == Native.T_UINT16 && Data.Length >= 2 ? BitConverter.ToUInt16(Data, 0) : null;
    public uint? AsUInt32() => Type == Native.T_UINT32 && Data.Length >= 4 ? BitConverter.ToUInt32(Data, 0) : null;
    public bool? AsBool() => Type == Native.T_BOOLEAN && Data.Length >= 1 ? Data[0] != 0 : null;
    public Guid? AsGuid() => Type == Native.T_GUID && Data.Length >= 16 ? new Guid(Data.AsSpan(0, 16)) : null;
    public DateTime? AsFileTime()
    {
        if (Type != Native.T_FILETIME || Data.Length < 8) return null;
        var ft = BitConverter.ToInt64(Data, 0);
        if (ft <= 0) return null;
        try { return DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch { return null; }
    }

    /// <summary>The Bluetooth stack writes DEVPKEY_Bluetooth_Last*Time as a LOCAL-time FILETIME (observed: LastConnectedTime == InstallDate + 8 h on a UTC+8 box).</summary>
    public DateTime? AsLocalFileTime()
    {
        if (Type != Native.T_FILETIME || Data.Length < 8) return null;
        var ft = BitConverter.ToInt64(Data, 0);
        if (ft <= 0) return null;
        try { return DateTime.SpecifyKind(DateTime.FromFileTimeUtc(ft), DateTimeKind.Local); } catch { return null; }
    }
}

internal sealed class NodeInfo
{
    public string Id = string.Empty;
    public string Enumerator = string.Empty;
    public string? Address;          // 12 upper-case hex
    public uint LocateCr;
    public uint StatusCr;
    public uint Status;
    public uint Problem;
    public bool Present => StatusCr == Native.CR_SUCCESS;
    public bool Started => Present && (Status & Native.DN_STARTED) != 0;
    public bool HasProblem => Present && (Status & Native.DN_HAS_PROBLEM) != 0;
    public bool DisconnectedBit => Present && (Status & Native.DN_DEVICE_DISCONNECTED) != 0;
    /// <summary>CfgMgr32-only connection heuristic: node present, started, no problem and DN_DEVICE_DISCONNECTED clear.</summary>
    public bool ConnectedHeuristic => Started && !HasProblem && !DisconnectedBit;
    public bool? IsPresentProp;
    public string? Category;
    public string? Name;
    public string? FriendlyName;
    public string? DeviceDesc;
    public string? Class;
    public string? Parent;
    public byte? Battery;
    public string? BatteryFrom;
    public uint? ClassOfDevice;
    public ushort? Vid, Pid;
    public byte? VidSource;
    public uint? DeviceFlags;
    public string? Manufacturer, ModelNumber;
    public DateTime? LastConnected, LastSeen, LastArrival, LastRemoval;
    public int ChildCount, ChildPresentCount;
    public List<string> OtherBluetoothKeys = new();
}

internal sealed record AepInfo(string Id, bool IsLowEnergy, string? Name, string? Address, bool? IsConnected, bool? IsPaired, bool? IsPresent,
    ushort? Appearance, ushort? CodMajor, ushort? CodMinor, DateTime? LastSeen, List<(DEVPROPKEY Key, uint Type, byte[] Data)> All);

internal static unsafe class Program
{
    private static readonly StringBuilder Out = new();

    private static void W(string s = "")
    {
        Console.Out.WriteLine(s);
        Out.AppendLine(s);
    }

    private static void Section(string title)
    {
        W();
        W(new string('=', 100));
        W(title);
        W(new string('=', 100));
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        W($"SpikeBtBattery  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {RuntimeInformation.FrameworkDescription}  {RuntimeInformation.OSDescription}  elevated={IsElevated()}");
        W($"sizeof: DEVPROPKEY={sizeof(DEVPROPKEY)} DEVPROPCOMPKEY={sizeof(DEVPROPCOMPKEY)} DEVPROPERTY={sizeof(DEVPROPERTY)} FILTER_EXPRESSION={sizeof(DEVPROP_FILTER_EXPRESSION)} DEV_OBJECT={sizeof(DEV_OBJECT)} (expected 20/32/48/56/32 on x64)");
        W($"Loaded assemblies: {string.Join(", ", AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).OrderBy(n => n))}");

        // ---------------------------------------------------------------- 1. ID lists
        Section("1. CM_Get_Device_ID_ListW per enumerator (FILTER_ENUMERATOR; with and without FILTER_PRESENT)");
        var lists = new Dictionary<(string Enumerator, bool PresentOnly), List<string>>();
        foreach (var enumerator in new[] { "BTHENUM", "BTHLE", "BTH", "BTHLEDevice", "BTHHFENUM" })
        {
            foreach (var presentOnly in new[] { true, false })
            {
                var sw = Stopwatch.StartNew();
                var ids = ListIds(enumerator, presentOnly, out var cr);
                sw.Stop();
                lists[(enumerator, presentOnly)] = ids;
                W($"[{enumerator}] flags=ENUMERATOR{(presentOnly ? "|PRESENT" : "")} cr=0x{cr:X2} count={ids.Count} ({sw.Elapsed.TotalMilliseconds:F2} ms)");
                foreach (var id in ids) W($"    {id}");
            }

            var missing = lists[(enumerator, false)].Except(lists[(enumerator, true)], StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
            {
                W($"  -> {missing.Count} id(s) only in the non-PRESENT list (paired but not present / phantom):");
                foreach (var id in missing) W($"       {id}");
            }
        }

        // ---------------------------------------------------------------- 2. Dump
        Section("2. Device node dump: BTHENUM + BTHLE (all, phantoms located with CM_LOCATE_DEVNODE_PHANTOM)");
        var nodes = new List<NodeInfo>();
        var dumpIds = lists[("BTHENUM", false)].Concat(lists[("BTHLE", false)]).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var id in dumpIds)
        {
            nodes.Add(DumpNode(id, verbose: true));
        }

        Section("2b. BTH\\ bus nodes and BTHLEDevice\\ GATT service nodes (brief: status + Bluetooth-family keys)");
        foreach (var id in lists[("BTH", false)].Concat(lists[("BTHLEDevice", false)]).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DumpNode(id, verbose: false);
        }

        // ---------------------------------------------------------------- 3. Table
        Section("3. Aggregated per Bluetooth address (CfgMgr32 devnodes only)");
        var byAddress = nodes.Where(n => n.Address is not null).GroupBy(n => n.Address!).OrderBy(g => g.Key).ToList();
        W($"{"Address",-14}{"Enumerators",-16}{"Present",-9}{"Started",-9}{"Problem",-9}{"DiscBit",-9}{"Conn(CM)",-10}{"Battery",-9}{"CoD",-11}{"VID:PID",-11}{"LastConnected",-21}Name");
        foreach (var g in byAddress)
        {
            var enums = string.Join("+", g.Select(n => n.Enumerator).Distinct());
            var present = string.Join("/", g.Select(n => n.Present ? "Y" : "n"));
            var started = string.Join("/", g.Select(n => n.Started ? "Y" : "n"));
            var problem = string.Join("/", g.Select(n => n.Present ? (n.HasProblem ? n.Problem.ToString() : "-") : "ph"));
            var discBit = string.Join("/", g.Select(n => n.DisconnectedBit ? "Y" : "n"));
            var conn = string.Join("/", g.Select(n => n.ConnectedHeuristic ? "Y" : "n"));
            var battery = g.Select(n => n.Battery).FirstOrDefault(b => b is not null);
            var cod = g.Select(n => n.ClassOfDevice).FirstOrDefault(c => c is not null);
            var vid = g.Select(n => n.Vid).FirstOrDefault(v => v is not null);
            var pid = g.Select(n => n.Pid).FirstOrDefault(v => v is not null);
            var last = g.Select(n => n.LastConnected).Where(d => d is not null).OrderByDescending(d => d).FirstOrDefault();
            var name = g.Select(n => n.FriendlyName ?? n.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "?";
            W($"{g.Key,-14}{enums,-16}{present,-9}{started,-9}{problem,-9}{discBit,-9}{conn,-10}{(battery is null ? "n/a" : battery + "%"),-9}{(cod is null ? "-" : "0x" + cod.Value.ToString("X6")),-11}{(vid is null ? "-" : $"{vid:X4}:{pid:X4}"),-11}{(last is null ? "-" : last.Value.ToString("yyyy-MM-dd HH:mm:ss")),-21}{name}");
            foreach (var n in g)
            {
                W($"    [{n.Enumerator}] {n.Id}");
                W($"        present={n.Present} started={n.Started} problem={n.Problem} disconnectedBit={n.DisconnectedBit} => connected(CM)={n.ConnectedHeuristic} IsPresentProp={n.IsPresentProp} category={n.Category ?? "-"}");
                W($"        battery={(n.Battery is null ? "none" : n.Battery + "% from " + n.BatteryFrom)} children={n.ChildCount} (present {n.ChildPresentCount}) lastConnected(local)={n.LastConnected:yyyy-MM-dd HH:mm:ss} lastSeen(local)={n.LastSeen:yyyy-MM-dd HH:mm:ss} arrival={n.LastArrival:yyyy-MM-dd HH:mm:ss} removal={n.LastRemoval:yyyy-MM-dd HH:mm:ss}");
                if (cod is not null) W($"        CoD 0x{cod:X6}: major={(cod >> 8) & 0x1F} minor={(cod >> 2) & 0x3F} services=0x{cod >> 13:X}");
            }
        }

        // ---------------------------------------------------------------- 4. AEP via Win32 DevQuery
        Section("4. Win32 DevQuery (cfgmgr32!DevGetObjects, DevObjectTypeAEP) — the API WinRT DeviceInformation.FindAllAsync wraps; no CsWinRT");
        var aepAll = args.Contains("--aep-all", StringComparer.OrdinalIgnoreCase);
        var aeps = new List<AepInfo>();

        W("4a. Async DevCreateObjectQuery + deadline (results arrive as DevQueryResultAdd; EnumCompleted lags ~30 s behind):");
        foreach (var (proto, isLe) in new[] { (Keys.ProtocolBluetoothClassic, false), (Keys.ProtocolBluetoothLE, true) })
        {
            var r = QueryPairedAepsAsync(proto, isLe, deadlineMs: 5000, quietMs: 300);
            W($"  {(isLe ? "BluetoothLE" : "Bluetooth"),-12} hr=0x{r.Hr:X8} results={r.Items.Count} firstResult@{r.FirstResultMs:F1} ms lastResult@{r.LastResultMs:F1} ms enumCompleted@{(r.EnumCompletedMs is null ? "not within deadline" : r.EnumCompletedMs.Value.ToString("F1") + " ms")} aborted={r.Aborted} total={r.TotalMs:F1} ms (incl. close)");
            foreach (var a in r.Items)
            {
                W($"      {a.Id}");
                W($"          name=\"{a.Name}\" address={a.Address} IsConnected={a.IsConnected} IsPaired={a.IsPaired} IsPresent={a.IsPresent} Le.Appearance={(a.Appearance is null ? "-" : "0x" + a.Appearance.Value.ToString("X4"))} Cod.Major={a.CodMajor} Cod.Minor={a.CodMinor}");
            }
            aeps.AddRange(r.Items);
        }

        W();
        W(aepAll
            ? "4b. Synchronous DevGetObjects with DevQueryFlagAllProperties (--aep-all): blocks until the Bluetooth AEP provider reports EnumCompleted (~30 s per query)"
            : "4b. Synchronous DevGetObjects with 8 requested properties (measured once; the first run with AllProperties took 30.0 s per query):");
        foreach (var (proto, isLe) in new[] { (Keys.ProtocolBluetoothClassic, false), (Keys.ProtocolBluetoothLE, true) })
        {
            var sw = Stopwatch.StartNew();
            var result = QueryPairedAeps(proto, isLe, allProperties: aepAll, out var hr);
            sw.Stop();
            W($"  Paired {(isLe ? "BluetoothLE" : "Bluetooth")} AEPs: hr=0x{hr:X8} count={result.Count} ({sw.Elapsed.TotalMilliseconds:F1} ms)");
            foreach (var a in result)
            {
                W($"      {a.Id}");
                W($"          name=\"{a.Name}\" address={a.Address} IsConnected={a.IsConnected} IsPaired={a.IsPaired} IsPresent={a.IsPresent} Le.Appearance={(a.Appearance is null ? "-" : "0x" + a.Appearance.Value.ToString("X4"))} Cod.Major={a.CodMajor} Cod.Minor={a.CodMinor} LastSeen={a.LastSeen:yyyy-MM-dd HH:mm:ss}");
                if (aepAll)
                {
                    foreach (var (key, type, data) in a.All)
                    {
                        W($"          {Keys.NameOf(key),-48} {TypeName(type),-14} {FormatValue(type, data)}");
                    }
                }
            }
            if (!aeps.Any(x => x.IsLowEnergy == isLe)) aeps.AddRange(result);
        }

        // ---------------------------------------------------------------- 5. bthprops
        Section("5. bthprops.cpl BluetoothFindFirstDevice (classic Win32, fIssueInquiry=false)");
        var classic = EnumerateClassic(out var classicMs);
        W($"count={classic.Count} ({classicMs:F1} ms)");
        foreach (var d in classic)
        {
            W($"  {d.Address:X12} \"{d.szName}\" connected={d.fConnected != 0} remembered={d.fRemembered != 0} authenticated={d.fAuthenticated != 0} CoD=0x{d.ulClassofDevice:X6} lastSeen={d.stLastSeen} lastUsed={d.stLastUsed}");
        }

        // ---------------------------------------------------------------- 6. Cross-check
        Section("6. Cross-check: CfgMgr32 devnode state vs AEP IsConnected vs bthprops fConnected");
        W($"{"Address",-14}{"CM present/started",-22}{"AEP IsConnected",-18}{"bthprops",-10}{"Battery(CM)",-13}{"CoD(CM)",-10}{"Cod(AEP)",-10}{"Appear(AEP)",-13}Name");
        var allAddresses = byAddress.Select(g => g.Key).Union(aeps.Select(a => a.Address ?? "")).Union(classic.Select(c => c.Address.ToString("X12")))
            .Where(a => a.Length == 12).Distinct().OrderBy(a => a);
        foreach (var addr in allAddresses)
        {
            var cm = byAddress.FirstOrDefault(g => g.Key == addr);
            var aepMatches = aeps.Where(a => a.Address == addr).ToList();
            var bp = classic.FirstOrDefault(c => c.Address.ToString("X12") == addr);
            var cmState = cm is null ? "-" : string.Join(" ", cm.Select(n => $"{n.Enumerator}:{(n.Present ? "P" : "ph")}{(n.Started ? "S" : "")}"));
            var aepState = aepMatches.Count == 0 ? "-" : string.Join(" ", aepMatches.Select(a => $"{(a.IsLowEnergy ? "LE" : "BR")}:{(a.IsConnected == true ? "conn" : "disc")}"));
            var bpState = bp.dwSize == 0 ? "-" : (bp.fConnected != 0 ? "conn" : "disc");
            var bat = cm?.Select(n => n.Battery).FirstOrDefault(b => b is not null);
            var cod = cm?.Select(n => n.ClassOfDevice).FirstOrDefault(c => c is not null);
            var aepCod = aepMatches.Select(a => a.CodMajor).FirstOrDefault(c => c is not null);
            var aepCodMin = aepMatches.Select(a => a.CodMinor).FirstOrDefault(c => c is not null);
            var app = aepMatches.Select(a => a.Appearance).FirstOrDefault(c => c is not null);
            var name = cm?.Select(n => n.FriendlyName ?? n.Name).FirstOrDefault(n => n is not null) ?? aepMatches.Select(a => a.Name).FirstOrDefault() ?? bp.szName;
            W($"{addr,-14}{cmState,-22}{aepState,-18}{bpState,-10}{(bat is null ? "n/a" : bat + "%"),-13}{(cod is null ? "-" : "0x" + cod.Value.ToString("X6")),-10}{(aepCod is null ? "-" : $"{aepCod}/{aepCodMin}"),-10}{(app is null ? "-" : "0x" + app.Value.ToString("X4")),-13}{name}");
        }

        // ---------------------------------------------------------------- 7. Timing
        Section("7. Timing (Stopwatch; up to 25 iterations each within a time budget; first = cold)");
        Time("CfgMgr32 production pass (BTHENUM+BTHLE ids, locate, status, ~10 props, child/sibling battery walk)", () => CfgMgrProductionPass().Count);
        Time("bthprops BluetoothFindFirstDevice pass (classic only)", () => EnumerateClassic(out _).Count);
        Time("DevQuery AEP async pass (2 paired-filter queries, 8 props, 300 ms quiet period / 5 s deadline)", () =>
            QueryPairedAepsAsync(Keys.ProtocolBluetoothClassic, false, 5000, 300).Items.Count + QueryPairedAepsAsync(Keys.ProtocolBluetoothLE, true, 5000, 300).Items.Count, budgetMs: 15000);
        Time("Combined: CfgMgr32 production pass + bthprops", () => CfgMgrProductionPass().Count + EnumerateClassic(out _).Count);
        Time("DevQuery AEP synchronous pass (2 paired-filter queries, 8 requested props) -- stops after the budget", () =>
            QueryPairedAeps(Keys.ProtocolBluetoothClassic, false, false, out _).Count + QueryPairedAeps(Keys.ProtocolBluetoothLE, true, false, out _).Count, budgetMs: 2000);

        Section("8. Production-pass result (what a CfgMgr32-only provider would emit)");
        foreach (var r in CfgMgrProductionPass())
        {
            W($"  {r}");
        }

        var outPath = Path.Combine(AppContext.BaseDirectory, "spike-output.txt");
        File.WriteAllText(outPath, Out.ToString());
        W();
        W($"(output also written to {outPath})");
        return 0;
    }

    // ------------------------------------------------------------------------------------------------ CfgMgr32 helpers

    private static List<string> ListIds(string enumerator, bool presentOnly, out uint cr)
    {
        var flags = Native.CM_GETIDLIST_FILTER_ENUMERATOR | (presentOnly ? Native.CM_GETIDLIST_FILTER_PRESENT : 0);
        var result = new List<string>();
        cr = Native.CM_Get_Device_ID_List_SizeW(out var len, enumerator, flags);
        if (cr != Native.CR_SUCCESS || len == 0) return result;
        var buffer = new char[len];
        fixed (char* p = buffer)
        {
            cr = Native.CM_Get_Device_ID_ListW(enumerator, p, len, flags);
        }

        if (cr != Native.CR_SUCCESS) return result;
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0') continue;
            if (i == start) break; // double NUL terminator
            result.Add(new string(buffer, start, i - start));
            start = i + 1;
        }

        return result;
    }

    private static PropValue? GetProperty(uint devInst, DEVPROPKEY key, out uint cr)
    {
        uint size = 0;
        var k = key;
        cr = Native.CM_Get_DevNode_PropertyW(devInst, &k, out var type, null, ref size, 0);
        if (cr != Native.CR_BUFFER_SMALL && cr != Native.CR_SUCCESS) return null;
        var data = new byte[Math.Max(size, 1u)];
        fixed (byte* p = data)
        {
            cr = Native.CM_Get_DevNode_PropertyW(devInst, &k, out type, p, ref size, 0);
        }

        if (cr != Native.CR_SUCCESS) return null;
        if (size < data.Length) Array.Resize(ref data, (int)size);
        return new PropValue(type, data);
    }

    private static PropValue? GetProperty(uint devInst, DEVPROPKEY key) => GetProperty(devInst, key, out _);

    private static DEVPROPKEY[] GetPropertyKeys(uint devInst, out uint cr)
    {
        uint count = 0;
        cr = Native.CM_Get_DevNode_Property_Keys(devInst, null, ref count, 0);
        if (cr != Native.CR_BUFFER_SMALL && cr != Native.CR_SUCCESS) return [];
        var keys = new DEVPROPKEY[count];
        fixed (DEVPROPKEY* p = keys)
        {
            cr = Native.CM_Get_DevNode_Property_Keys(devInst, p, ref count, 0);
        }

        return cr == Native.CR_SUCCESS ? keys[..(int)count] : [];
    }

    private static string? GetDeviceId(uint devInst)
    {
        var buffer = new char[512];
        fixed (char* p = buffer)
        {
            if (Native.CM_Get_Device_IDW(devInst, p, (uint)buffer.Length, 0) != Native.CR_SUCCESS) return null;
        }

        return new string(buffer, 0, Array.IndexOf(buffer, '\0') is var i and >= 0 ? i : buffer.Length);
    }

    private static NodeInfo DumpNode(string id, bool verbose)
    {
        var info = new NodeInfo { Id = id, Enumerator = id.Split('\\')[0] };
        info.Address = AddressFromInstanceId(id);
        W();
        W($"--- {id}");
        info.LocateCr = Native.CM_Locate_DevNodeW(out var dev, id, Native.CM_LOCATE_DEVNODE_PHANTOM);
        if (info.LocateCr != Native.CR_SUCCESS)
        {
            W($"    CM_Locate_DevNodeW(PHANTOM) failed cr=0x{info.LocateCr:X2}");
            return info;
        }

        var normalCr = Native.CM_Locate_DevNodeW(out _, id, Native.CM_LOCATE_DEVNODE_NORMAL);
        info.StatusCr = Native.CM_Get_DevNode_Status(out info.Status, out info.Problem, dev, 0);
        W($"    devInst=0x{dev:X8} locate(NORMAL) cr=0x{normalCr:X2}  CM_Get_DevNode_Status cr=0x{info.StatusCr:X2}{(info.StatusCr == Native.CR_NO_SUCH_DEVNODE ? " (CR_NO_SUCH_DEVNODE => phantom / not present)" : "")}");
        if (info.StatusCr == Native.CR_SUCCESS)
        {
            W($"    status=0x{info.Status:X8} [{DecodeStatus(info.Status)}] problem={info.Problem} {ProblemName(info.Problem)}");
        }

        var keys = GetPropertyKeys(dev, out var keysCr);
        W($"    CM_Get_DevNode_Property_Keys cr=0x{keysCr:X2} count={keys.Length}");
        foreach (var key in keys)
        {
            var v = GetProperty(dev, key, out var cr);
            var isBtFamily = key.Fmtid == Keys.Bluetooth_Battery.Fmtid || key.Fmtid == Keys.Bluetooth_DeviceAddress.Fmtid;
            if (verbose || isBtFamily || key.Equals(Keys.NAME) || key.Equals(Keys.Device_IsPresent) || key.Equals(Keys.Device_Parent) || key.Equals(Keys.Device_DevNodeStatus) || key.Equals(Keys.Device_ProblemCode))
            {
                W(v is null
                    ? $"    {Keys.NameOf(key),-40} <read failed cr=0x{cr:X2}>"
                    : $"    {Keys.NameOf(key),-40} {TypeName(v.Type),-14} {FormatValue(v.Type, v.Data)}");
            }

            if (v is null) continue;
            Capture(info, key, v, id);
        }

        // Children (CM_Get_Child / CM_Get_Sibling)
        var childCr = Native.CM_Get_Child(out var child, dev, 0);
        if (childCr != Native.CR_SUCCESS)
        {
            W($"    CM_Get_Child cr=0x{childCr:X2} (no children in the live tree)");
        }

        while (childCr == Native.CR_SUCCESS)
        {
            WalkChild(child, info, depth: 1, maxDepth: 3);
            childCr = Native.CM_Get_Sibling(out child, child, 0);
        }

        // Children as recorded in DEVPKEY_Device_Children (covers phantoms whose tree is not live)
        var childrenProp = GetProperty(dev, Keys.Device_Children);
        if (childrenProp is { Type: Native.T_STRING_LIST })
        {
            var list = ReadStringList(childrenProp.Data);
            W($"    Device_Children property: {list.Count} id(s)");
            foreach (var cid in list)
            {
                if (Native.CM_Locate_DevNodeW(out var cdev, cid, Native.CM_LOCATE_DEVNODE_PHANTOM) != Native.CR_SUCCESS) continue;
                var scr = Native.CM_Get_DevNode_Status(out var cst, out var cpr, cdev, 0);
                var bat = GetProperty(cdev, Keys.Bluetooth_Battery);
                var cname = GetProperty(cdev, Keys.NAME)?.AsString();
                W($"        {cid}  status={(scr == Native.CR_SUCCESS ? $"0x{cst:X8} prob={cpr}" : $"cr=0x{scr:X2} (phantom)")} name=\"{cname}\" battery={(bat?.AsByte() is { } b ? b + "%" : "none")}");
                if (bat?.AsByte() is { } bb && info.Battery is null)
                {
                    info.Battery = bb;
                    info.BatteryFrom = "child " + cid;
                }
            }
        }

        return info;
    }

    private static void WalkChild(uint dev, NodeInfo owner, int depth, int maxDepth)
    {
        var id = GetDeviceId(dev) ?? "?";
        var scr = Native.CM_Get_DevNode_Status(out var st, out var pr, dev, 0);
        var name = GetProperty(dev, Keys.NAME)?.AsString();
        var bat = GetProperty(dev, Keys.Bluetooth_Battery);
        var indent = new string(' ', 4 + depth * 4);
        if (depth == 1)
        {
            owner.ChildCount++;
            if (scr == Native.CR_SUCCESS && (st & Native.DN_HAS_PROBLEM) == 0) owner.ChildPresentCount++;
        }

        W($"{indent}child[{depth}] {id}");
        W($"{indent}    status={(scr == Native.CR_SUCCESS ? $"0x{st:X8} [{DecodeStatus(st)}] prob={pr}" : $"cr=0x{scr:X2}")} name=\"{name}\" battery={(bat?.AsByte() is { } b ? b + "%" : "none")}");
        foreach (var key in GetPropertyKeys(dev, out _))
        {
            if (key.Fmtid != Keys.Bluetooth_Battery.Fmtid && key.Fmtid != Keys.Bluetooth_DeviceAddress.Fmtid) continue;
            var v = GetProperty(dev, key);
            if (v is not null) W($"{indent}    {Keys.NameOf(key),-36} {TypeName(v.Type),-14} {FormatValue(v.Type, v.Data)}");
        }

        if (bat?.AsByte() is { } bval && owner.Battery is null)
        {
            owner.Battery = bval;
            owner.BatteryFrom = "child " + id;
        }

        if (depth >= maxDepth) return;
        var cr = Native.CM_Get_Child(out var grandChild, dev, 0);
        while (cr == Native.CR_SUCCESS)
        {
            WalkChild(grandChild, owner, depth + 1, maxDepth);
            cr = Native.CM_Get_Sibling(out grandChild, grandChild, 0);
        }
    }

    private static void Capture(NodeInfo info, DEVPROPKEY key, PropValue v, string id)
    {
        if (key.Equals(Keys.NAME)) info.Name = v.AsString();
        else if (key.Equals(Keys.Device_FriendlyName)) info.FriendlyName = v.AsString();
        else if (key.Equals(Keys.Device_DeviceDesc)) info.DeviceDesc = v.AsString();
        else if (key.Equals(Keys.Device_Class)) info.Class = v.AsString();
        else if (key.Equals(Keys.Device_Parent)) info.Parent = v.AsString();
        else if (key.Equals(Keys.Device_IsPresent)) info.IsPresentProp = v.AsBool();
        else if (key.Equals(Keys.DeviceContainer_Category)) info.Category = v.Type == Native.T_STRING_LIST ? string.Join("|", ReadStringList(v.Data)) : null;
        else if (key.Equals(Keys.Bluetooth_DeviceAddress)) { if (TryNormalizeAddress(v.AsString(), out var a)) info.Address = a; }
        else if (key.Equals(Keys.Bluetooth_Battery)) { info.Battery = v.AsByte(); info.BatteryFrom = "node " + id; }
        else if (key.Equals(Keys.Bluetooth_ClassOfDevice)) info.ClassOfDevice = v.AsUInt32();
        else if (key.Equals(Keys.Bluetooth_DeviceVID)) info.Vid = v.AsUInt16();
        else if (key.Equals(Keys.Bluetooth_DevicePID)) info.Pid = v.AsUInt16();
        else if (key.Equals(Keys.Bluetooth_DeviceVIDSource)) info.VidSource = v.AsByte();
        else if (key.Equals(Keys.Bluetooth_DeviceFlags)) info.DeviceFlags = v.AsUInt32();
        else if (key.Equals(Keys.Bluetooth_DeviceManufacturer)) info.Manufacturer = v.AsString();
        else if (key.Equals(Keys.Bluetooth_DeviceModelNumber)) info.ModelNumber = v.AsString();
        else if (key.Equals(Keys.Bluetooth_LastConnectedTime)) info.LastConnected = v.AsLocalFileTime();
        else if (key.Equals(Keys.Bluetooth_LastSeenTime)) info.LastSeen = v.AsLocalFileTime();
        else if (key.Equals(Keys.Device_LastArrivalDate)) info.LastArrival = v.AsFileTime();
        else if (key.Equals(Keys.Device_LastRemovalDate)) info.LastRemoval = v.AsFileTime();
        else if (key.Fmtid == Keys.Bluetooth_DeviceAddress.Fmtid || key.Fmtid == Keys.Bluetooth_Battery.Fmtid) info.OtherBluetoothKeys.Add($"{key}={FormatValue(v.Type, v.Data)}");
    }

    // ------------------------------------------------------------------------------------------------ production-shaped pass

    private sealed record ProductionDevice(string Address, string Name, bool IsLowEnergy, bool Present, bool Started, uint Problem, byte? Battery, string? BatteryFrom,
        uint? ClassOfDevice, DateTime? LastConnected, ushort? Vid, ushort? Pid)
    {
        public override string ToString()
            => $"{Name} {(Battery is null ? "n/a" : Battery + "%")} [{(IsLowEnergy ? "BluetoothLE" : "Bluetooth")} {Address}, present={Present} started={Started} problem={Problem}, CoD={(ClassOfDevice is null ? "-" : "0x" + ClassOfDevice.Value.ToString("X6"))}, VID:PID={(Vid is null ? "-" : $"{Vid:X4}:{Pid:X4}")}, lastConnected={LastConnected:yyyy-MM-dd HH:mm:ss}, battery from {BatteryFrom ?? "-"}]";
    }

    private static List<ProductionDevice> CfgMgrProductionPass()
    {
        var result = new List<ProductionDevice>();
        foreach (var (enumerator, isLe) in new[] { ("BTHENUM", false), ("BTHLE", true) })
        {
            foreach (var id in ListIds(enumerator, presentOnly: false, out _))
            {
                if (!id.Contains("\\DEV_", StringComparison.OrdinalIgnoreCase)) continue; // device nodes only; service nodes are handled as children/siblings below
                if (Native.CM_Locate_DevNodeW(out var dev, id, Native.CM_LOCATE_DEVNODE_PHANTOM) != Native.CR_SUCCESS) continue;
                var statusCr = Native.CM_Get_DevNode_Status(out var status, out var problem, dev, 0);
                var present = statusCr == Native.CR_SUCCESS;
                var name = GetProperty(dev, Keys.Device_FriendlyName)?.AsString() ?? GetProperty(dev, Keys.NAME)?.AsString();
                var addressProp = GetProperty(dev, Keys.Bluetooth_DeviceAddress)?.AsString();
                if (!TryNormalizeAddress(addressProp, out var address)) address = AddressFromInstanceId(id) ?? string.Empty;
                var battery = GetProperty(dev, Keys.Bluetooth_Battery)?.AsByte();
                string? batteryFrom = battery is null ? null : "node";
                var cod = GetProperty(dev, Keys.Bluetooth_ClassOfDevice)?.AsUInt32();
                var last = GetProperty(dev, Keys.Bluetooth_LastConnectedTime)?.AsFileTime();
                var vid = GetProperty(dev, Keys.Bluetooth_DeviceVID)?.AsUInt16();
                var pid = GetProperty(dev, Keys.Bluetooth_DevicePID)?.AsUInt16();
                if (battery is null)
                {
                    var cr = Native.CM_Get_Child(out var child, dev, 0);
                    while (cr == Native.CR_SUCCESS && battery is null)
                    {
                        battery = GetProperty(child, Keys.Bluetooth_Battery)?.AsByte();
                        if (battery is not null) batteryFrom = "child " + GetDeviceId(child);
                        cr = Native.CM_Get_Sibling(out child, child, 0);
                    }
                }

                result.Add(new ProductionDevice(address, name ?? address, isLe, present, present && (status & Native.DN_STARTED) != 0, present ? problem : 0,
                    battery, batteryFrom, cod, last, vid, pid));
            }
        }

        return result;
    }

    // ------------------------------------------------------------------------------------------------ DevQuery AEP

    private static List<AepInfo> QueryPairedAeps(Guid protocolId, bool isLe, bool allProperties, out int hr)
    {
        var result = new List<AepInfo>();
        var guidPtr = Marshal.AllocHGlobal(16);
        var boolPtr = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.Copy(protocolId.ToByteArray(), 0, guidPtr, 16);
            Marshal.WriteByte(boolPtr, 0xFF); // DEVPROP_TRUE
            var filters = new DEVPROP_FILTER_EXPRESSION[2];
            filters[0].Operator = Native.DEVPROP_OPERATOR_EQUALS;
            filters[0].Property.CompKey.Key = Keys.Aep_ProtocolId;
            filters[0].Property.Type = Native.T_GUID;
            filters[0].Property.BufferSize = 16;
            filters[0].Property.Buffer = guidPtr;
            filters[1].Operator = Native.DEVPROP_OPERATOR_EQUALS;
            filters[1].Property.CompKey.Key = Keys.Aep_IsPaired;
            filters[1].Property.Type = Native.T_BOOLEAN;
            filters[1].Property.BufferSize = 1;
            filters[1].Property.Buffer = boolPtr;

            DEVPROPCOMPKEY[] requested =
            [
                new() { Key = Keys.NAME }, new() { Key = Keys.Aep_DeviceAddress }, new() { Key = Keys.Aep_IsConnected }, new() { Key = Keys.Aep_IsPaired },
                new() { Key = Keys.Aep_IsPresent }, new() { Key = Keys.Aep_Le_Appearance }, new() { Key = Keys.Aep_Cod_Major }, new() { Key = Keys.Aep_Cod_Minor },
            ];

            fixed (DEVPROP_FILTER_EXPRESSION* pf = filters)
            fixed (DEVPROPCOMPKEY* pr = requested)
            {
                hr = Native.DevGetObjects(Native.DevObjectTypeAEP, allProperties ? Native.DevQueryFlagAllProperties : 0,
                    allProperties ? 0 : (uint)requested.Length, allProperties ? null : pr, 2, pf, out var count, out var objects);
                if (hr < 0) return result;
                try
                {
                    for (var i = 0; i < count; i++)
                    {
                        var o = objects[i];
                        var id = Marshal.PtrToStringUni(o.pszObjectId) ?? string.Empty;
                        var all = new List<(DEVPROPKEY, uint, byte[])>();
                        string? name = null, address = null;
                        bool? conn = null, paired = null, present = null;
                        ushort? appearance = null, major = null, minor = null;
                        DateTime? lastSeen = null;
                        var props = (DEVPROPERTY*)o.pProperties;
                        for (var j = 0; j < o.cPropertyCount; j++)
                        {
                            var p = props[j];
                            var data = new byte[p.BufferSize];
                            if (p.BufferSize > 0) Marshal.Copy(p.Buffer, data, 0, (int)p.BufferSize);
                            var v = new PropValue(p.Type, data);
                            var key = p.CompKey.Key;
                            all.Add((key, p.Type, data));
                            if (key.Equals(Keys.NAME)) name = v.AsString();
                            else if (key.Equals(Keys.Aep_DeviceAddress)) { if (TryNormalizeAddress(v.AsString(), out var a)) address = a; }
                            else if (key.Equals(Keys.Aep_IsConnected)) conn = v.AsBool();
                            else if (key.Equals(Keys.Aep_IsPaired)) paired = v.AsBool();
                            else if (key.Equals(Keys.Aep_IsPresent)) present = v.AsBool();
                            else if (key.Equals(Keys.Aep_Le_Appearance)) appearance = v.AsUInt16();
                            else if (key.Equals(Keys.Aep_Cod_Major)) major = v.AsUInt16() ?? (ushort?)v.AsUInt32();
                            else if (key.Equals(Keys.Aep_Cod_Minor)) minor = v.AsUInt16() ?? (ushort?)v.AsUInt32();
                            else if (key.Equals(Keys.Bluetooth_LastSeenTime)) lastSeen = v.AsFileTime();
                        }

                        if (address is null)
                        {
                            var dash = id.LastIndexOf('-');
                            if (dash >= 0 && TryNormalizeAddress(id[(dash + 1)..], out var a)) address = a;
                        }

                        result.Add(new AepInfo(id, isLe, name, address, conn, paired, present, appearance, major, minor, lastSeen, all));
                    }
                }
                finally
                {
                    Native.DevFreeObjects(count, objects);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(guidPtr);
            Marshal.FreeHGlobal(boolPtr);
        }

        return result;
    }

    // ------------------------------------------------------------------------------------------------ bthprops

    private static List<BLUETOOTH_DEVICE_INFO> EnumerateClassic(out double ms)
    {
        var sw = Stopwatch.StartNew();
        var result = new List<BLUETOOTH_DEVICE_INFO>();
        var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
            fReturnAuthenticated = 1,
            fReturnRemembered = 1,
            fReturnUnknown = 0,
            fReturnConnected = 1,
            fIssueInquiry = 0,
            cTimeoutMultiplier = 0,
            hRadio = IntPtr.Zero,
        };
        var info = new BLUETOOTH_DEVICE_INFO { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };
        try
        {
            var h = Native.BluetoothFindFirstDevice(ref search, ref info);
            if (h == IntPtr.Zero)
            {
                ms = sw.Elapsed.TotalMilliseconds;
                W($"  BluetoothFindFirstDevice returned NULL, GetLastError={Marshal.GetLastWin32Error()}");
                return result;
            }

            do
            {
                result.Add(info);
                info = new BLUETOOTH_DEVICE_INFO { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };
            } while (Native.BluetoothFindNextDevice(h, ref info) != 0);

            Native.BluetoothFindDeviceClose(h);
        }
        catch (Exception ex)
        {
            W($"  bthprops failed: {ex.GetType().Name}: {ex.Message}");
        }

        ms = sw.Elapsed.TotalMilliseconds;
        return result;
    }

    // ------------------------------------------------------------------------------------------------ formatting / misc

    private static void Time(string label, Func<int> pass, int iterations = 25, double budgetMs = 5000)
    {
        var times = new List<double>();
        var count = 0;
        var total = 0.0;
        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            count = pass();
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
            total += times[^1];
            if (total > budgetMs) break;
        }

        var sorted = times.OrderBy(t => t).ToArray();
        W($"{label}");
        W($"    iterations={times.Count} items={count}  first={times[0]:F2} ms  min={sorted[0]:F2} ms  median={sorted[times.Count / 2]:F2} ms  avg={times.Average():F2} ms  max={sorted[^1]:F2} ms");
    }

    private static string DecodeStatus(uint s)
    {
        var names = new List<string>();
        void Add(uint bit, string name) { if ((s & bit) != 0) names.Add(name); }
        Add(0x1, "ROOT_ENUMERATED"); Add(0x2, "DRIVER_LOADED"); Add(0x4, "ENUM_LOADED"); Add(0x8, "STARTED"); Add(0x10, "MANUAL"); Add(0x20, "NEED_TO_ENUM");
        Add(0x40, "NOT_FIRST_TIME/DRIVER_BLOCKED"); Add(0x80, "HARDWARE_ENUM"); Add(0x100, "LIAR/NEED_RESTART"); Add(0x200, "HAS_MARK/CHILD_WITH_INVALID_ID"); Add(0x400, "HAS_PROBLEM");
        Add(0x800, "FILTERED"); Add(0x1000, "MOVED/LEGACY_DRIVER"); Add(0x2000, "DISABLEABLE"); Add(0x4000, "REMOVABLE"); Add(0x8000, "PRIVATE_PROBLEM"); Add(0x10000, "MF_PARENT");
        Add(0x20000, "MF_CHILD"); Add(0x40000, "WILL_BE_REMOVED"); Add(0x80000, "NOT_FIRST_TIMEE"); Add(0x100000, "STOP_FREE_RES"); Add(0x200000, "REBAL_CANDIDATE");
        Add(0x400000, "BAD_PARTIAL"); Add(0x800000, "NT_ENUMERATOR"); Add(0x1000000, "NT_DRIVER"); Add(0x2000000, "NEEDS_LOCKING/DEVICE_DISCONNECTED"); Add(0x4000000, "ARM_WAKEUP");
        Add(0x8000000, "APM_ENUMERATOR"); Add(0x10000000, "APM_DRIVER"); Add(0x20000000, "SILENT_INSTALL"); Add(0x40000000, "NO_SHOW_IN_DM"); Add(0x80000000, "BOOT_LOG_PROB");
        return string.Join("|", names);
    }

    private static string ProblemName(uint p) => p switch
    {
        0 => "",
        10 => "CM_PROB_FAILED_START",
        14 => "CM_PROB_NEED_RESTART",
        22 => "CM_PROB_DISABLED",
        24 => "CM_PROB_DEVICE_NOT_THERE",
        28 => "CM_PROB_FAILED_INSTALL",
        43 => "CM_PROB_FAILED_POST_START",
        45 => "CM_PROB_PHANTOM",
        47 => "CM_PROB_HELD_FOR_EJECT",
        51 => "CM_PROB_WAITING_ON_DEPENDENCY",
        _ => $"CM_PROB_{p}",
    };

    private static string TypeName(uint t) => t switch
    {
        Native.T_EMPTY => "EMPTY", Native.T_NULL => "NULL", Native.T_SBYTE => "SBYTE", Native.T_BYTE => "BYTE", Native.T_INT16 => "INT16", Native.T_UINT16 => "UINT16",
        Native.T_INT32 => "INT32", Native.T_UINT32 => "UINT32", Native.T_INT64 => "INT64", Native.T_UINT64 => "UINT64", Native.T_FLOAT => "FLOAT", Native.T_DOUBLE => "DOUBLE",
        Native.T_GUID => "GUID", Native.T_FILETIME => "FILETIME", Native.T_BOOLEAN => "BOOLEAN", Native.T_STRING => "STRING", Native.T_STRING_LIST => "STRING_LIST",
        Native.T_SECURITY_DESCRIPTOR => "SECDESC", Native.T_SECURITY_DESCRIPTOR_STRING => "SECDESC_STR", Native.T_DEVPROPKEY => "DEVPROPKEY", Native.T_DEVPROPTYPE => "DEVPROPTYPE",
        Native.T_ERROR => "ERROR", Native.T_NTSTATUS => "NTSTATUS", Native.T_STRING_INDIRECT => "STRING_IND", Native.T_BINARY => "BINARY",
        _ => $"0x{t:X}",
    };

    private static string FormatValue(uint type, byte[] data)
    {
        try
        {
            switch (type)
            {
                case Native.T_STRING:
                case Native.T_STRING_INDIRECT:
                case Native.T_SECURITY_DESCRIPTOR_STRING:
                    return "\"" + Encoding.Unicode.GetString(data).TrimEnd('\0') + "\"";
                case Native.T_STRING_LIST:
                    return "[" + string.Join(", ", ReadStringList(data).Select(s => "\"" + s + "\"")) + "]";
                case Native.T_BOOLEAN:
                    return data.Length > 0 ? (data[0] != 0 ? "true" : "false") : "?";
                case Native.T_BYTE:
                    return $"{data[0]} (0x{data[0]:X2})";
                case Native.T_SBYTE:
                    return ((sbyte)data[0]).ToString(CultureInfo.InvariantCulture);
                case Native.T_UINT16:
                    { var v = BitConverter.ToUInt16(data, 0); return $"{v} (0x{v:X4})"; }
                case Native.T_INT16:
                    return BitConverter.ToInt16(data, 0).ToString(CultureInfo.InvariantCulture);
                case Native.T_UINT32:
                case Native.T_ERROR:
                case Native.T_NTSTATUS:
                    { var v = BitConverter.ToUInt32(data, 0); return $"{v} (0x{v:X8})"; }
                case Native.T_INT32:
                    return BitConverter.ToInt32(data, 0).ToString(CultureInfo.InvariantCulture);
                case Native.T_UINT64:
                    { var v = BitConverter.ToUInt64(data, 0); return $"{v} (0x{v:X16})"; }
                case Native.T_INT64:
                    return BitConverter.ToInt64(data, 0).ToString(CultureInfo.InvariantCulture);
                case Native.T_GUID:
                    return new Guid(data.AsSpan(0, 16)).ToString("B");
                case Native.T_FILETIME:
                    {
                        var ft = BitConverter.ToInt64(data, 0);
                        if (ft <= 0) return $"FILETIME 0x{ft:X}";
                        try { return DateTime.FromFileTimeUtc(ft).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " (local)"; }
                        catch { return $"FILETIME 0x{ft:X}"; }
                    }
                case Native.T_DEVPROPKEY:
                    return new Guid(data.AsSpan(0, 16)).ToString("B") + "," + BitConverter.ToUInt32(data, 16);
                case Native.T_EMPTY:
                case Native.T_NULL:
                    return "(empty)";
                default:
                    return Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 64))) + (data.Length > 64 ? $"... ({data.Length} bytes)" : $" ({data.Length} bytes)");
            }
        }
        catch (Exception ex)
        {
            return $"<format error {ex.GetType().Name}: {Convert.ToHexString(data)}>";
        }
    }

    private static List<string> ReadStringList(byte[] data)
    {
        var s = Encoding.Unicode.GetString(data);
        return s.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static string? AddressFromInstanceId(string id)
    {
        // "BTHENUM\Dev_FC039F42E11F\..." or "BTHLE\Dev_C8BC9E1A2B3C\..." or service node "...&FC039F42E11F_C00000000"
        var dev = id.IndexOf("\\DEV_", StringComparison.OrdinalIgnoreCase);
        if (dev >= 0 && dev + 5 + 12 <= id.Length && TryNormalizeAddress(id.Substring(dev + 5, 12), out var a)) return a;
        var last = id[(id.LastIndexOf('\\') + 1)..];
        foreach (var token in last.Split('&', '_'))
        {
            if (token.Length == 12 && TryNormalizeAddress(token, out var b)) return b;
        }

        return null;
    }

    private static bool TryNormalizeAddress(string? text, out string hex12)
    {
        hex12 = string.Empty;
        if (string.IsNullOrEmpty(text)) return false;
        var sb = new StringBuilder(12);
        foreach (var c in text)
        {
            if (c is ':' or '-' or ' ') continue;
            if (sb.Length == 12 || !char.IsAsciiHexDigit(c)) return false;
            sb.Append(char.ToUpperInvariant(c));
        }

        if (sb.Length != 12) return false;
        hex12 = sb.ToString();
        return true;
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
