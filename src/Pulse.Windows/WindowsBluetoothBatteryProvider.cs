using System.Globalization;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Enumeration.Pnp;

namespace Pulse.Windows;

/// <summary>
/// Lists every paired Bluetooth (classic + LE) device via WinRT association endpoints and attaches the battery
/// level Windows caches on the BTHENUM\ / BTHLE\ device node (DEVPKEY_Bluetooth_Battery).
/// Devices without a battery value are reported only while connected; devices with one are always reported
/// so the last known level stays visible.
/// </summary>
public sealed class WindowsBluetoothBatteryProvider : IBatteryProvider
{
    public const string ProviderName = "Bluetooth";

    /// <summary>DEVPKEY_Bluetooth_Battery (DEVPROP_TYPE_BYTE, 0..100) as a WinRT property string.</summary>
    public const string BatteryPropertyKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    private const string PropIsConnected = "System.Devices.Aep.IsConnected";
    private const string PropDeviceAddress = "System.Devices.Aep.DeviceAddress";
    private const string PropLeAppearance = "System.Devices.Aep.Bluetooth.Le.Appearance";
    private const string PropCodMajor = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string PropCodMinor = "System.Devices.Aep.Bluetooth.Cod.Minor";
    private const string PropIsPaired = "System.Devices.Aep.IsPaired";
    private const string PropInstanceId = "System.Devices.DeviceInstanceId";
    private const string PropDisplayName = "System.ItemNameDisplay";

    /// <summary>Limits the PnP walk to the Bluetooth enumerators (BTHENUM\, BTHLE\, BTH\): a dozen nodes instead of ~500.</summary>
    private const string PnpAqsFilter = "System.Devices.DeviceInstanceId:~<\"BTH\"";

    private static readonly string[] EndpointProperties =
    [
        PropIsConnected, PropDeviceAddress, PropLeAppearance, PropCodMajor, PropCodMinor, PropIsPaired,
    ];

    private static readonly string[] PnpProperties = [PropInstanceId, PropDisplayName, BatteryPropertyKey];

    private readonly ILogger<WindowsBluetoothBatteryProvider> _logger;
    private int _failureLogged; // first failure is logged at Warning, repeats (timer polling) at Debug

    public WindowsBluetoothBatteryProvider(ILogger<WindowsBluetoothBatteryProvider> logger)
    {
        _logger = logger;
    }

    public string Name => ProviderName;

    public bool IsSupported => OperatingSystem.IsWindows();

    public async Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return Array.Empty<BatteryDevice>();
        cancellationToken.ThrowIfCancellationRequested();

        // Three independent WinRT queries; run them concurrently (~100 ms in total, no inquiry is issued).
        var classicTask = FindEndpointsAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true), isLowEnergy: false, cancellationToken);
        var leTask = FindEndpointsAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), isLowEnergy: true, cancellationToken);
        var batteryTask = ReadBatteryLevelsAsync(cancellationToken);
        await Task.WhenAll(classicTask, leTask, batteryTask).ConfigureAwait(false);

        var endpoints = new Dictionary<string, Endpoint>(StringComparer.Ordinal);
        foreach (var endpoint in classicTask.Result.Concat(leTask.Result))
        {
            endpoints[endpoint.Address] = endpoints.TryGetValue(endpoint.Address, out var existing)
                ? Merge(existing, endpoint)
                : endpoint;
        }

        var batteries = batteryTask.Result;
        var now = DateTimeOffset.Now;
        var devices = new List<BatteryDevice>(endpoints.Count);
        foreach (var endpoint in endpoints.Values)
        {
            batteries.TryGetValue(endpoint.Address, out var battery);
            if (battery.Percent is null && !endpoint.IsConnected) continue;

            var name = !string.IsNullOrWhiteSpace(endpoint.Name) ? endpoint.Name
                : !string.IsNullOrWhiteSpace(battery.Name) ? battery.Name
                : BluetoothAddress.ToDisplay(endpoint.Address);

            devices.Add(new BatteryDevice
            {
                Id = "bt:" + BluetoothAddress.ToDisplay(endpoint.Address),
                Name = name,
                Kind = BluetoothDeviceKindInference.Infer(endpoint.Appearance, endpoint.CodMajor, endpoint.CodMinor, name),
                Connection = endpoint.IsLowEnergy ? ConnectionType.BluetoothLE : ConnectionType.Bluetooth,
                Source = ProviderName,
                Percent = battery.Percent,
                Status = ToStatus(battery.Percent),
                IsConnected = endpoint.IsConnected,
                Detail = endpoint.IsLowEnergy ? "藍牙 LE" : "藍牙",
                UpdatedAt = now,
            });
        }

        devices.Sort(static (a, b) => a.IsConnected != b.IsConnected
            ? (a.IsConnected ? -1 : 1)
            : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return devices;
    }

    private async Task<List<Endpoint>> FindEndpointsAsync(string selector, bool isLowEnergy, CancellationToken cancellationToken)
    {
        var result = new List<Endpoint>();
        DeviceInformationCollection infos;
        try
        {
            infos = await DeviceInformation
                .FindAllAsync(selector, EndpointProperties, DeviceInformationKind.AssociationEndpoint)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailure(ex, isLowEnergy ? "Bluetooth LE endpoint enumeration failed" : "Bluetooth endpoint enumeration failed");
            return result;
        }

        foreach (var info in infos)
        {
            try
            {
                var props = info.Properties;
                if (!BluetoothAddress.TryNormalize(GetString(props, PropDeviceAddress), out var address)
                    && !BluetoothAddress.TryNormalize(AddressFromEndpointId(info.Id), out address))
                {
                    _logger.LogDebug("Skipping endpoint without a usable address: {Id}", info.Id);
                    continue;
                }

                if (GetBool(props, PropIsPaired) is false) continue; // selector already filters, belt and braces

                result.Add(new Endpoint(
                    address,
                    info.Name ?? string.Empty,
                    isLowEnergy,
                    GetBool(props, PropIsConnected) ?? false,
                    GetUInt16(props, PropLeAppearance),
                    GetUInt16(props, PropCodMajor),
                    GetUInt16(props, PropCodMinor)));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read endpoint {Id}", info.Id);
            }
        }

        return result;
    }

    /// <summary>Battery level per address from the BTHENUM\ / BTHLE\ device nodes (plus service children as a fallback).</summary>
    private async Task<Dictionary<string, BatteryNode>> ReadBatteryLevelsAsync(CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, BatteryNode>(StringComparer.Ordinal);
        PnpObjectCollection objects;
        try
        {
            objects = await PnpObject
                .FindAllAsync(PnpObjectType.Device, PnpProperties, PnpAqsFilter)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailure(ex, "Bluetooth PnP battery enumeration failed");
            return map;
        }

        foreach (var obj in objects)
        {
            try
            {
                var props = obj.Properties;
                var instanceId = GetString(props, PropInstanceId) ?? obj.Id;
                if (!BluetoothAddress.TryExtractFromInstanceId(instanceId, out var address, out var priority)) continue;
                if (!props.TryGetValue(BatteryPropertyKey, out var raw) || raw is null) continue;

                var percent = ToPercent(raw);
                if (percent is null)
                {
                    _logger.LogDebug("Ignoring out-of-range battery value {Value} on {InstanceId}", raw, instanceId);
                    continue;
                }

                if (!map.TryGetValue(address, out var existing) || priority > existing.Priority)
                {
                    map[address] = new BatteryNode(percent, GetString(props, PropDisplayName), priority);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read PnP node {Id}", obj.Id);
            }
        }

        return map;
    }

    /// <summary>Dual-mode device paired over both transports: keep the connected transport (classic when tied) and pool the metadata.</summary>
    private static Endpoint Merge(Endpoint a, Endpoint b)
    {
        var primary = a.IsConnected == b.IsConnected ? (a.IsLowEnergy ? b : a) : (a.IsConnected ? a : b);
        var secondary = ReferenceEquals(primary, a) ? b : a;
        return primary with
        {
            Name = string.IsNullOrWhiteSpace(primary.Name) ? secondary.Name : primary.Name,
            IsConnected = a.IsConnected || b.IsConnected,
            Appearance = primary.Appearance ?? secondary.Appearance,
            CodMajor = primary.CodMajor ?? secondary.CodMajor,
            CodMinor = primary.CodMinor ?? secondary.CodMinor,
        };
    }

    private static BatteryStatus ToStatus(int? percent) => percent switch
    {
        <= 10 => BatteryStatus.Critical,
        <= 20 => BatteryStatus.Low,
        _ => BatteryStatus.Unknown,
    };

    /// <summary>"Bluetooth#Bluetooth2c:0d:a7:bc:75:68-fc:03:9f:42:e1:1f" → "fc:03:9f:42:e1:1f".</summary>
    private static string? AddressFromEndpointId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var dash = id.LastIndexOf('-');
        return dash >= 0 ? id[(dash + 1)..] : null;
    }

    private static int? ToPercent(object raw)
    {
        try
        {
            var value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            return value is >= 0 and <= 100 ? value : null;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static string? GetString(IReadOnlyDictionary<string, object> props, string key)
        => props.TryGetValue(key, out var v) ? v as string : null;

    private static bool? GetBool(IReadOnlyDictionary<string, object> props, string key)
        => props.TryGetValue(key, out var v) && v is bool b ? b : null;

    private static ushort? GetUInt16(IReadOnlyDictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out var v) || v is null) return null;
        try
        {
            return v is ushort us ? us : Convert.ToUInt16(v, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return null;
        }
    }

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

    private sealed record Endpoint(
        string Address,
        string Name,
        bool IsLowEnergy,
        bool IsConnected,
        ushort? Appearance,
        ushort? CodMajor,
        ushort? CodMinor);

    private readonly record struct BatteryNode(int? Percent, string? Name, int Priority);
}
