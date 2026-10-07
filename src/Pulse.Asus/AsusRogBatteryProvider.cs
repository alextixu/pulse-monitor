using System.Diagnostics;
using HidSharp;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pulse.Asus;

/// <summary>
/// Battery levels of ASUS ROG / TUF wireless keyboards and mice. Only collections of products whose name says ROG/TUF
/// are probed (read-only <c>12 xx</c> queries); Aura LED controllers are excluded. Streams stay open between polls and are
/// dropped on I/O errors; devices that reject the protocol are not asked again for a while.
/// </summary>
public sealed class AsusRogBatteryProvider : IBatteryProvider, IDisposable
{
    public const string ProviderName = "ASUS ROG";

    private static readonly TimeSpan PollBudget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan UnsupportedRetryInterval = TimeSpan.FromMinutes(2);

    private readonly ILogger<AsusRogBatteryProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Endpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _unsupportedUntil = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _disposed;

    public AsusRogBatteryProvider(ILogger<AsusRogBatteryProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<AsusRogBatteryProvider>.Instance;
    }

    public string Name => ProviderName;

    public bool IsSupported => true;

    public async Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return Array.Empty<BatteryDevice>();

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return Array.Empty<BatteryDevice>();
        }

        try
        {
            if (_disposed) return Array.Empty<BatteryDevice>();
            // HidSharp I/O is synchronous; keep the caller (UI timer) off the blocking path.
            return await Task.Run(() => Poll(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ASUS ROG poll failed");
            return Array.Empty<BatteryDevice>();
        }
        finally
        {
            try { _gate.Release(); } catch (ObjectDisposedException) { /* disposed while polling */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var acquired = false;
        try { acquired = _gate.Wait(TimeSpan.FromSeconds(5)); } catch (ObjectDisposedException) { }
        try
        {
            foreach (var endpoint in _endpoints.Values) endpoint.Dispose();
            _endpoints.Clear();
        }
        finally
        {
            if (acquired) _gate.Release();
            _gate.Dispose();
        }
    }

    private IReadOnlyList<BatteryDevice> Poll(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var deadline = stopwatch.Elapsed + PollBudget;
        var candidates = Enumerate();
        var present = new HashSet<string>(candidates.Select(c => c.DevicePath), StringComparer.OrdinalIgnoreCase);

        foreach (var (path, endpoint) in _endpoints.ToList())
        {
            if (present.Contains(path)) continue;
            _logger.LogInformation("ASUS endpoint gone: {Path}", path);
            endpoint.Dispose();
            _endpoints.Remove(path);
        }

        var result = new List<BatteryDevice>();
        foreach (var device in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed > deadline)
            {
                _logger.LogWarning("ASUS ROG poll exceeded {Budget} s; skipping remaining devices", PollBudget.TotalSeconds);
                break;
            }

            var path = device.DevicePath;
            if (_unsupportedUntil.TryGetValue(path, out var until) && until > DateTimeOffset.Now) continue;

            try
            {
                if (!_endpoints.TryGetValue(path, out var endpoint))
                {
                    endpoint = Endpoint.TryOpen(device, _logger);
                    if (endpoint is null)
                    {
                        _unsupportedUntil[path] = DateTimeOffset.Now + UnsupportedRetryInterval;
                        continue;
                    }
                    _endpoints[path] = endpoint;
                }

                var reading = endpoint.ReadBattery();
                if (reading is null)
                {
                    _logger.LogDebug("{Product}: no battery reading (protocol not supported)", endpoint.ProductName);
                    endpoint.Dispose();
                    _endpoints.Remove(path);
                    _unsupportedUntil[path] = DateTimeOffset.Now + UnsupportedRetryInterval;
                    continue;
                }

                result.Add(endpoint.ToBatteryDevice(reading.Value));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "ASUS endpoint {Path} failed; dropping it", path);
                if (_endpoints.Remove(path, out var dead)) dead.Dispose();
            }
        }

        _logger.LogDebug("ASUS ROG poll: {Count} device(s) in {Elapsed} ms", result.Count, stopwatch.ElapsedMilliseconds);
        return result;
    }

    /// <summary>Vendor collections (0xFF00, 64-byte in/out reports) of ROG/TUF peripherals.</summary>
    private List<HidDevice> Enumerate()
    {
        IEnumerable<HidDevice> devices;
        try
        {
            devices = DeviceList.Local.GetHidDevices(AsusProtocol.VendorId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HID enumeration failed");
            return new List<HidDevice>();
        }

        var result = new List<HidDevice>();
        foreach (var device in devices)
        {
            try
            {
                if (AsusProtocol.AuraControllerProductIds.Contains(device.ProductID)) continue;
                if (device.GetMaxInputReportLength() != AsusProtocol.ReportLength || device.GetMaxOutputReportLength() != AsusProtocol.ReportLength) continue;

                var name = SafeProductName(device);
                if (!LooksLikePeripheral(name)) continue;

                var descriptor = device.GetReportDescriptor();
                var isVendor = descriptor.DeviceItems
                    .SelectMany(item => item.Usages.GetAllValues())
                    .Any(usage => (usage >> 16) == AsusProtocol.VendorUsagePage);
                if (isVendor) result.Add(device);
            }
            catch (Exception ex)
            {
                _logger.LogTrace("Cannot inspect {Path}: {Message}", device.DevicePath, ex.Message);
            }
        }
        return result;
    }

    private static bool LooksLikePeripheral(string productName)
    {
        if (productName.Contains("AURA", StringComparison.OrdinalIgnoreCase)) return false;
        return productName.Contains("ROG", StringComparison.OrdinalIgnoreCase)
            || productName.Contains("TUF", StringComparison.OrdinalIgnoreCase);
    }

    internal static string SafeProductName(HidDevice device)
    {
        try { return device.GetProductName() ?? string.Empty; }
        catch { return string.Empty; }
    }

    internal static string SafeSerial(HidDevice device)
    {
        try { return device.GetSerialNumber() ?? string.Empty; }
        catch { return string.Empty; }
    }

    internal readonly record struct BatteryReading(int Percent, double? VoltageMillivolts);

    /// <summary>One opened vendor collection.</summary>
    private sealed class Endpoint : IDisposable
    {
        private readonly HidDevice _device;
        private readonly HidStream _stream;
        private readonly ILogger _logger;
        private bool? _isKeyboardProtocol;

        private Endpoint(HidDevice device, HidStream stream, ILogger logger)
        {
            _device = device;
            _stream = stream;
            _logger = logger;
            ProductName = SafeProductName(device);
            Serial = SafeSerial(device);
            // Mice do not use the keyboard status layout; skip straight to the mouse battery query.
            if (InferKind(ProductName, keyboardProtocol: false) == DeviceKind.Mouse) _isKeyboardProtocol = false;
        }

        public string ProductName { get; }

        public string Serial { get; }

        public static Endpoint? TryOpen(HidDevice device, ILogger logger)
        {
            if (!device.TryOpen(out var stream))
            {
                logger.LogDebug("Cannot open {Path}", device.DevicePath);
                return null;
            }

            var endpoint = new Endpoint(device, stream, logger);
            var version = endpoint.Query(AsusProtocol.SubVersion);
            if (version is null)
            {
                logger.LogDebug("{Product} (PID 0x{Pid:X4}) does not answer the ASUS query protocol", endpoint.ProductName, device.ProductID);
                endpoint.Dispose();
                return null;
            }

            // HidSharp keeps the leading report-id byte, so OpenRGB's version bytes 4..6 are 5..7 here.
            logger.LogInformation("Opened ASUS endpoint {Product} (PID 0x{Pid:X4}) firmware {Major:X}.{Minor:X2}.{Patch:X2}",
                endpoint.ProductName, device.ProductID, version[7], version[6], version[5]);
            return endpoint;
        }

        public BatteryReading? ReadBattery()
        {
            if (_isKeyboardProtocol != false)
            {
                var status = Query(AsusProtocol.SubKeyboardStatus);
                if (status is not null && status[AsusProtocol.KeyboardBatteryOffset] <= 100)
                {
                    _isKeyboardProtocol = true;
                    _logger.LogTrace("{Product} status: {Bytes}", ProductName, Convert.ToHexString(status, 1, 16));
                    var raw = status[AsusProtocol.KeyboardVoltageOffset] | (status[AsusProtocol.KeyboardVoltageOffset + 1] << 8);
                    double? millivolts = raw is >= 3000 and <= 4400 ? raw : null;
                    return new BatteryReading(status[AsusProtocol.KeyboardBatteryOffset], millivolts);
                }
                if (_isKeyboardProtocol == true) return null;
                _isKeyboardProtocol = false;
            }

            var mouse = Query(AsusProtocol.SubMouseBattery);
            if (mouse is not null && mouse[AsusProtocol.MouseBatteryOffset] <= 100)
            {
                _logger.LogTrace("{Product} battery: {Bytes}", ProductName, Convert.ToHexString(mouse, 1, 16));
                return new BatteryReading(mouse[AsusProtocol.MouseBatteryOffset], null);
            }
            return null;
        }

        public BatteryDevice ToBatteryDevice(BatteryReading reading)
        {
            var wireless = AsusProtocol.WirelessReceiverProductIds.Contains(_device.ProductID);
            var kind = InferKind(ProductName, _isKeyboardProtocol == true);
            var serialPart = string.IsNullOrEmpty(Serial) ? Convert.ToHexString(BitConverter.GetBytes(_device.DevicePath.GetHashCode()))[..8] : Serial;
            return new BatteryDevice
            {
                Id = $"asus:{_device.ProductID:x4}:{serialPart}".ToLowerInvariant(),
                Name = string.IsNullOrWhiteSpace(ProductName) ? "ASUS ROG" : ProductName,
                Kind = kind,
                Connection = wireless ? ConnectionType.UsbReceiver : ConnectionType.Usb,
                Source = ProviderName,
                Percent = reading.Percent,
                Status = reading.Percent switch
                {
                    <= 10 => BatteryStatus.Critical,
                    <= 20 => BatteryStatus.Low,
                    _ => BatteryStatus.Unknown,
                },
                IsConnected = true,
                VoltageMillivolts = reading.VoltageMillivolts,
                Detail = wireless ? "2.4 GHz" : "USB",
            };
        }

        /// <summary>Sends <c>12 sub</c> and returns the matching reply, or null on NACK / timeout. Unrelated input reports are skipped.</summary>
        private byte[]? Query(byte sub)
        {
            _stream.Write(AsusProtocol.Request(AsusProtocol.CmdQuery, sub));
            var deadline = Stopwatch.StartNew();
            var reply = new byte[AsusProtocol.ReportLength];
            while (deadline.Elapsed < RequestTimeout)
            {
                _stream.ReadTimeout = Math.Max(50, (int)(RequestTimeout - deadline.Elapsed).TotalMilliseconds);
                int read;
                try
                {
                    read = _stream.Read(reply, 0, reply.Length);
                }
                catch (TimeoutException)
                {
                    break;
                }
                if (read < 3) continue;
                if (AsusProtocol.IsReplyTo(reply, AsusProtocol.CmdQuery, sub)) return reply;
                if (AsusProtocol.IsNack(reply)) return null;
            }
            return null;
        }

        private static DeviceKind InferKind(string name, bool keyboardProtocol)
        {
            string[] keyboards = ["FALCHION", "AZOTH", "SCOPE", "CLAYMORE", "FLARE", "KEYBOARD"];
            string[] mice = ["CHAKRAM", "GLADIUS", "KERIS", "HARPE", "SPATHA", "PUGIO", "IMPACT", "MOUSE"];
            if (keyboards.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase))) return DeviceKind.Keyboard;
            if (mice.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase))) return DeviceKind.Mouse;
            return keyboardProtocol ? DeviceKind.Keyboard : DeviceKind.Unknown;
        }

        public void Dispose()
        {
            try { _stream.Dispose(); } catch { /* already gone */ }
        }
    }
}
