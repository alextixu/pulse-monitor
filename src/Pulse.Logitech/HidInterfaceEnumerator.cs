using System.Text.RegularExpressions;
using HidSharp;
using Microsoft.Extensions.Logging;

namespace Pulse.Logitech;

/// <summary>
/// One HID++ capable USB interface (or Bluetooth HID service): the long-report collection, plus the short-report
/// collection of the same interface when the OS exposes it separately (Windows does, one HidDevice per top-level collection).
/// </summary>
internal sealed record HidppInterfaceInfo
{
    /// <summary>Stable key shared by all collections of one interface (device path with the collection number removed).</summary>
    public required string Key { get; init; }
    public required HidDevice LongDevice { get; init; }
    public HidDevice? ShortDevice { get; init; }
    public required int VendorId { get; init; }
    public required int ProductId { get; init; }
    public required string SerialNumber { get; init; }
    public required string ProductName { get; init; }
    public required bool IsBluetooth { get; init; }
    public required ushort UsagePage { get; init; }

    public override string ToString() => $"{Key} (PID 0x{ProductId:X4} '{ProductName}' serial '{SerialNumber}' bt={IsBluetooth})";
}

/// <summary>Finds Logitech HID++ collections with HidSharp and groups them per interface.</summary>
internal static partial class HidInterfaceEnumerator
{
    private const string BluetoothHidServiceGuid = "00001124-0000-1000-8000-00805f9b34fb";

    [GeneratedRegex(@"&col\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CollectionToken();

    // Windows appends "&<collection index>" to the instance part: "#7&19ba346c&0&0001#{guid}" → strip "&0001".
    [GeneratedRegex(@"&\d{4}(?=#\{)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CollectionInstanceToken();

    private sealed record Collection(HidDevice Device, ushort UsagePage, bool HasShort, bool HasLong, bool HasVeryLong);

    public static IReadOnlyList<HidppInterfaceInfo> Enumerate(ILogger logger)
    {
        IEnumerable<HidDevice> devices;
        try
        {
            devices = DeviceList.Local.GetHidDevices(Hidpp.LogitechVendorId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "HID enumeration failed");
            return Array.Empty<HidppInterfaceInfo>();
        }

        var groups = new Dictionary<string, List<Collection>>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            var path = device.DevicePath ?? string.Empty;
            if (path.Contains("LGHUBDEVICE", StringComparison.OrdinalIgnoreCase)) continue;

            var collection = Inspect(device, logger);
            if (collection is null) continue;

            var key = CollectionInstanceToken().Replace(CollectionToken().Replace(path, string.Empty), string.Empty);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<Collection>();
                groups[key] = list;
            }
            list.Add(collection);
        }

        var result = new List<HidppInterfaceInfo>();
        foreach (var (key, collections) in groups)
        {
            var longCol = collections.FirstOrDefault(c => c.HasLong);
            if (longCol is null)
            {
                logger.LogDebug("Skipping {Key}: no long (0x11) HID++ report collection", key);
                continue;
            }
            var shortCol = collections.FirstOrDefault(c => c.HasShort);
            var dev = longCol.Device;

            var isBluetooth = longCol.UsagePage == Hidpp.UsagePageBluetooth
                || key.Contains(BluetoothHidServiceGuid, StringComparison.OrdinalIgnoreCase);

            result.Add(new HidppInterfaceInfo
            {
                Key = key,
                LongDevice = dev,
                ShortDevice = shortCol?.Device,
                VendorId = dev.VendorID,
                ProductId = dev.ProductID,
                SerialNumber = Safe(dev.GetSerialNumber),
                ProductName = Safe(dev.GetProductName),
                IsBluetooth = isBluetooth,
                UsagePage = longCol.UsagePage,
            });
        }

        return result;
    }

    /// <summary>Returns the HID++ collection description, or null when this top-level collection is not HID++ (keyboard, mouse, consumer…).</summary>
    private static Collection? Inspect(HidDevice device, ILogger logger)
    {
        try
        {
            var descriptor = device.GetReportDescriptor();
            ushort usagePage = 0;
            foreach (var item in descriptor.DeviceItems)
            {
                foreach (var usage in item.Usages.GetAllValues())
                {
                    var page = (ushort)(usage >> 16);
                    if (page is Hidpp.UsagePageUsb or Hidpp.UsagePageBluetooth) { usagePage = page; break; }
                }
                if (usagePage != 0) break;
            }
            if (usagePage == 0) return null;

            var inputIds = descriptor.InputReports.Select(r => r.ReportID).ToHashSet();
            var outputIds = descriptor.OutputReports.Select(r => r.ReportID).ToHashSet();
            bool Declares(byte id) => inputIds.Contains(id) && outputIds.Contains(id);

            var hasShort = Declares(Hidpp.ShortReportId);
            var hasLong = Declares(Hidpp.LongReportId);
            var hasVeryLong = Declares(Hidpp.VeryLongReportId);
            if (!hasShort && !hasLong) return null;

            return new Collection(device, usagePage, hasShort, hasLong, hasVeryLong);
        }
        catch (Exception ex)
        {
            // HidSharp cannot rebuild some descriptors (e.g. consumer-control collections); those are never HID++.
            logger.LogTrace("Cannot read report descriptor of {Path}: {Message}", device.DevicePath, ex.Message);
            return null;
        }
    }

    private static string Safe(Func<string> getter)
    {
        try { return getter() ?? string.Empty; }
        catch { return string.Empty; }
    }
}
