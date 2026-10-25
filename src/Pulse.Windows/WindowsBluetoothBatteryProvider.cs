using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;

namespace Pulse.Windows;

/// <summary>
/// Lists every paired Bluetooth (classic + LE) device straight from the PnP device tree (CfgMgr32) and attaches the
/// battery level Windows caches on the BTHENUM\ / BTHLE\ device node (DEVPKEY_Bluetooth_Battery).
/// Paired-but-absent devices are located as phantoms so a remembered device keeps its last known level.
/// Devices without a battery value are reported only while connected; devices with one are always reported.
/// A full pass is a few dozen registry-backed reads (~1 ms); nothing talks to the radio.
/// </summary>
public sealed class WindowsBluetoothBatteryProvider : IBatteryProvider
{
    public const string ProviderName = "Bluetooth";

    private const string ClassicEnumerator = "BTHENUM";
    private const string LowEnergyEnumerator = "BTHLE";

    /// <summary>Upper bound for the child walk (a GATT device exposes a handful of service nodes).</summary>
    private const int MaxChildren = 64;

    private readonly ILogger<WindowsBluetoothBatteryProvider> _logger;
    private int _failureLogged; // first failure is logged at Warning, repeats (timer polling) at Debug

    public WindowsBluetoothBatteryProvider(ILogger<WindowsBluetoothBatteryProvider> logger)
    {
        _logger = logger;
    }

    public string Name => ProviderName;

    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Synchronous under the hood: the caller (MonitoringService) already runs providers on the thread pool.</summary>
    public Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return Task.FromResult<IReadOnlyList<BatteryDevice>>(Array.Empty<BatteryDevice>());
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetDevices(cancellationToken));
    }

    private IReadOnlyList<BatteryDevice> GetDevices(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        try
        {
            CollectNodes(ClassicEnumerator, isLowEnergy: false, nodes, cancellationToken);
            CollectNodes(LowEnergyEnumerator, isLowEnergy: true, nodes, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailure(ex, "Bluetooth device-node enumeration failed");
            return Array.Empty<BatteryDevice>();
        }

        var now = DateTimeOffset.Now;
        var devices = new List<BatteryDevice>(nodes.Count);
        foreach (var node in nodes.Values)
        {
            if (node.Percent is null && !node.IsConnected) continue;

            var name = node.Name ?? BluetoothAddress.ToDisplay(node.Address);
            devices.Add(new BatteryDevice
            {
                Id = "bt:" + BluetoothAddress.ToDisplay(node.Address),
                Name = name,
                // LE appearance is only exposed on the association endpoint, not on the device node, so it is never available here.
                Kind = BluetoothDeviceKindInference.Infer(appearance: null, node.ClassOfDevice, node.ContainerCategory, name),
                Connection = node.IsLowEnergy ? ConnectionType.BluetoothLE : ConnectionType.Bluetooth,
                Source = ProviderName,
                Percent = node.Percent,
                Status = ToStatus(node.Percent),
                IsConnected = node.IsConnected,
                Detail = node.IsLowEnergy ? "藍牙 LE" : "藍牙",
                UpdatedAt = now,
            });
        }

        devices.Sort(static (a, b) => a.IsConnected != b.IsConnected
            ? (a.IsConnected ? -1 : 1)
            : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

        _logger.LogTrace("Bluetooth: {Nodes} device node(s) -> {Devices} device(s) in {Elapsed:F1} ms",
            nodes.Count, devices.Count, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return devices;
    }

    /// <summary>Reads every <c>{enumerator}\Dev_&lt;address&gt;</c> node and merges it into <paramref name="nodes"/> by address.</summary>
    private void CollectNodes(string enumerator, bool isLowEnergy, Dictionary<string, Node> nodes, CancellationToken cancellationToken)
    {
        foreach (var instanceId in ListDeviceInstanceIds(enumerator))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Service nodes (BTHENUM\{uuid}_LOCALMFG&...) duplicate the device node's address; the device node carries the battery.
            if (!BluetoothAddress.TryExtractFromInstanceId(instanceId, out var address, out var priority)
                || priority != BluetoothAddress.DeviceNodePriority)
            {
                continue;
            }

            Node? node;
            try
            {
                node = ReadNode(instanceId, address, isLowEnergy);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read device node {InstanceId}", instanceId);
                continue;
            }

            if (node is null) continue;
            nodes[node.Address] = nodes.TryGetValue(node.Address, out var existing) ? Merge(existing, node) : node;
        }
    }

    private Node? ReadNode(string instanceId, string addressFromId, bool isLowEnergy)
    {
        var cr = CfgMgr32.CM_Locate_DevNodeW(out var devInst, instanceId, CfgMgr32.CM_LOCATE_DEVNODE_PHANTOM);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            _logger.LogDebug("CM_Locate_DevNodeW failed (CR 0x{Code:X2}) for {InstanceId}", cr, instanceId);
            return null;
        }

        // A phantom (paired but not present) node has no live status: CR_NO_SUCH_DEVNODE. Present LE devices that are out
        // of reach stay started with DN_DEVICE_DISCONNECTED set (and Device_IsPresent true), hence the extra bit test.
        var statusCr = CfgMgr32.CM_Get_DevNode_Status(out var status, out var problem, devInst, 0);
        var present = statusCr == CfgMgr32.CR_SUCCESS;
        if (!present && statusCr != CfgMgr32.CR_NO_SUCH_DEVNODE)
        {
            _logger.LogDebug("CM_Get_DevNode_Status failed (CR 0x{Code:X2}) for {InstanceId}", statusCr, instanceId);
        }

        var connected = present
            && (status & CfgMgr32.DN_STARTED) != 0
            && (status & CfgMgr32.DN_HAS_PROBLEM) == 0
            && problem == 0
            && (status & CfgMgr32.DN_DEVICE_DISCONNECTED) == 0;

        var address = addressFromId;
        if (BluetoothAddress.TryNormalize(ReadProperty(devInst, DevPropKeys.BluetoothDeviceAddress, instanceId)?.AsString(), out var reported))
        {
            address = reported;
        }

        var name = NullIfBlank(ReadProperty(devInst, DevPropKeys.DeviceFriendlyName, instanceId)?.AsString())
            ?? NullIfBlank(ReadProperty(devInst, DevPropKeys.Name, instanceId)?.AsString());

        var percent = ToPercent(ReadProperty(devInst, DevPropKeys.BluetoothBattery, instanceId)?.AsByte(), instanceId)
            ?? ReadChildBattery(devInst, instanceId);

        var classOfDevice = ReadProperty(devInst, DevPropKeys.BluetoothClassOfDevice, instanceId)?.AsUInt32();
        var category = NullIfBlank(ReadProperty(devInst, DevPropKeys.DeviceContainerCategory, instanceId)?.AsFirstString());

        return new Node(address, name, isLowEnergy, connected, percent, classOfDevice, category);
    }

    /// <summary>Battery level from the first direct child that has one (e.g. the BTHLEDevice\{0000180f-...} GATT battery service).</summary>
    private int? ReadChildBattery(uint devInst, string instanceId)
    {
        var cr = CfgMgr32.CM_Get_Child(out var child, devInst, 0);
        for (var i = 0; cr == CfgMgr32.CR_SUCCESS && i < MaxChildren; i++)
        {
            var percent = ToPercent(ReadProperty(child, DevPropKeys.BluetoothBattery, instanceId)?.AsByte(), instanceId);
            if (percent is not null) return percent;
            cr = CfgMgr32.CM_Get_Sibling(out child, child, 0);
        }

        return null; // CR_NO_SUCH_DEVNODE simply means "no (more) children" and is the normal exit
    }

    private unsafe List<string> ListDeviceInstanceIds(string enumerator)
    {
        var ids = new List<string>();
        const uint flags = CfgMgr32.CM_GETIDLIST_FILTER_ENUMERATOR;
        var cr = CfgMgr32.CM_Get_Device_ID_List_SizeW(out var length, enumerator, flags);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            _logger.LogDebug("CM_Get_Device_ID_List_SizeW({Enumerator}) failed (CR 0x{Code:X2})", enumerator, cr);
            return ids;
        }

        if (length == 0) return ids;

        var buffer = new char[length];
        fixed (char* p = buffer)
        {
            cr = CfgMgr32.CM_Get_Device_ID_ListW(enumerator, p, length, flags);
        }

        if (cr != CfgMgr32.CR_SUCCESS)
        {
            _logger.LogDebug("CM_Get_Device_ID_ListW({Enumerator}) failed (CR 0x{Code:X2})", enumerator, cr);
            return ids;
        }

        // REG_MULTI_SZ layout: NUL-separated ids, terminated by an empty string.
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0') continue;
            if (i == start) break;
            ids.Add(new string(buffer, start, i - start));
            start = i + 1;
        }

        return ids;
    }

    /// <summary>Reads one property; null when it is not set on the node (CR_NO_SUCH_VALUE) or the read fails (logged at Debug).</summary>
    private unsafe PropertyValue? ReadProperty(uint devInst, in DEVPROPKEY key, string instanceId)
    {
        uint size = 0;
        var cr = CfgMgr32.CM_Get_DevNode_PropertyW(devInst, in key, out var type, null, ref size, 0);
        if (cr == CfgMgr32.CR_NO_SUCH_VALUE) return null;
        if (cr == CfgMgr32.CR_SUCCESS && size == 0) return new PropertyValue(type, Array.Empty<byte>());
        if (cr != CfgMgr32.CR_BUFFER_SMALL)
        {
            _logger.LogDebug("CM_Get_DevNode_PropertyW({Key}) size query failed (CR 0x{Code:X2}) on {InstanceId}", key, cr, instanceId);
            return null;
        }

        var data = new byte[size];
        fixed (byte* p = data)
        {
            cr = CfgMgr32.CM_Get_DevNode_PropertyW(devInst, in key, out type, p, ref size, 0);
        }

        if (cr != CfgMgr32.CR_SUCCESS)
        {
            _logger.LogDebug("CM_Get_DevNode_PropertyW({Key}) failed (CR 0x{Code:X2}) on {InstanceId}", key, cr, instanceId);
            return null;
        }

        if (size < data.Length) Array.Resize(ref data, (int)size);
        return new PropertyValue(type, data);
    }

    /// <summary>
    /// Dual-mode device paired over both transports (or duplicate nodes): keep the node that reports a battery, then the
    /// connected one, then classic; pool the rest of the metadata.
    /// </summary>
    private static Node Merge(Node a, Node b)
    {
        var primary = (a.Percent is not null) != (b.Percent is not null) ? (a.Percent is not null ? a : b)
            : a.IsConnected != b.IsConnected ? (a.IsConnected ? a : b)
            : a.IsLowEnergy && !b.IsLowEnergy ? b
            : a;
        var secondary = ReferenceEquals(primary, a) ? b : a;
        return primary with
        {
            Name = primary.Name ?? secondary.Name,
            IsConnected = a.IsConnected || b.IsConnected,
            Percent = primary.Percent ?? secondary.Percent,
            ClassOfDevice = primary.ClassOfDevice ?? secondary.ClassOfDevice,
            ContainerCategory = primary.ContainerCategory ?? secondary.ContainerCategory,
        };
    }

    private int? ToPercent(byte? raw, string instanceId)
    {
        if (raw is null) return null;
        if (raw <= 100) return raw;
        _logger.LogDebug("Ignoring out-of-range battery value {Value} on {InstanceId}", raw, instanceId);
        return null;
    }

    private static BatteryStatus ToStatus(int? percent) => percent switch
    {
        <= 10 => BatteryStatus.Critical,
        <= 20 => BatteryStatus.Low,
        _ => BatteryStatus.Unknown,
    };

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private void LogFailure(Exception ex, string message)
    {
        if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
        {
            _logger.LogWarning(ex, "{Message}; returning no devices from this query", message);
        }
        else
        {
            _logger.LogDebug(ex, "{Message}", message);
        }
    }

    /// <summary>One BTHENUM\ or BTHLE\ device node, reduced to what the provider needs. Address is 12 upper-case hex digits.</summary>
    private sealed record Node(
        string Address,
        string? Name,
        bool IsLowEnergy,
        bool IsConnected,
        int? Percent,
        uint? ClassOfDevice,
        string? ContainerCategory);

    /// <summary>Raw DEVPROP value: type tag + bytes, decoded on demand.</summary>
    private readonly struct PropertyValue
    {
        private readonly uint _type;
        private readonly byte[] _data;

        public PropertyValue(uint type, byte[] data)
        {
            _type = type;
            _data = data;
        }

        public string? AsString()
            => _type is CfgMgr32.DEVPROP_TYPE_STRING or CfgMgr32.DEVPROP_TYPE_STRING_INDIRECT
                ? Encoding.Unicode.GetString(_data).TrimEnd('\0')
                : null;

        /// <summary>First entry of a STRING_LIST (NUL-separated), or the string itself.</summary>
        public string? AsFirstString()
        {
            if (_type != CfgMgr32.DEVPROP_TYPE_STRING_LIST) return AsString();
            var all = Encoding.Unicode.GetString(_data);
            var end = all.IndexOf('\0');
            return end < 0 ? all : all[..end];
        }

        public byte? AsByte() => _type == CfgMgr32.DEVPROP_TYPE_BYTE && _data.Length >= 1 ? _data[0] : null;

        public uint? AsUInt32() => _type == CfgMgr32.DEVPROP_TYPE_UINT32 && _data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(_data) : null;
    }
}
