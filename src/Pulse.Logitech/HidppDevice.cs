using Pulse.Core.Models;
using Microsoft.Extensions.Logging;

namespace Pulse.Logitech;

/// <summary>One battery sample. <see cref="Feature"/> names the HID++ feature / register it came from.</summary>
internal sealed record BatteryReading(int? Percent, BatteryStatus Status, double? VoltageMillivolts, string Feature);

/// <summary>
/// Cached state of one HID++ device (a receiver slot 1..6, or 0xFF for a device connected directly) plus the
/// protocol operations on it. Discovery results (name, kind, feature indices, ids) survive across polls; each poll
/// then costs one battery request for an awake device and one ping for a sleeping one.
/// </summary>
internal sealed class HidppDevice
{
    public const int RequestTimeoutMs = 1200;

    private static int s_pingCounter;

    private readonly ILogger _logger;
    private byte _deviceNameFeature;
    private byte _fwVersionFeature;
    private byte _unifiedBatteryFeature;
    private byte _batteryStatusFeature;
    private byte _batteryVoltageFeature;
    private bool _unifiedStateOfChargeSupported;

    public HidppDevice(ILogger logger, byte index)
    {
        _logger = logger;
        Index = index;
    }

    public byte Index { get; }

    /// <summary>Name from HID++ 2.0 feature 0x0005; preferred over the receiver's pairing name.</summary>
    public string? Hidpp2Name { get; private set; }

    /// <summary>Name stored in the receiver's pairing registers (available while the device sleeps).</summary>
    public string? ReceiverName { get; set; }

    public string? Name => string.IsNullOrWhiteSpace(Hidpp2Name) ? ReceiverName : Hidpp2Name;

    public DeviceKind Kind { get; set; } = DeviceKind.Unknown;

    /// <summary>Wireless product id from the receiver pairing info, e.g. "40A9".</summary>
    public string? Wpid { get; set; }

    /// <summary>Device serial from the receiver's extended pairing info; stable even while the device is off.</summary>
    public string? PairingSerial { get; set; }

    /// <summary>Unit id from feature 0x0003 (0 = unknown).</summary>
    public uint UnitId { get; private set; }

    public bool IsOnline { get; private set; }

    /// <summary>0 = never answered, 1 = HID++ 1.0, 2+ = HID++ 2.0 / 4.x.</summary>
    public int ProtocolMajor { get; private set; }

    public int ProtocolMinor { get; private set; }

    public Hidpp1Error LastPingError { get; private set; }

    public bool FeaturesDiscovered { get; private set; }

    public bool HasBatteryFeature => _unifiedBatteryFeature != 0 || _batteryStatusFeature != 0 || _batteryVoltageFeature != 0;

    public BatteryReading? LastReading { get; private set; }

    public DateTimeOffset? LastReadingAt { get; private set; }

    public void MarkOffline() => IsOnline = false;

    /// <summary>Root ping. Sets <see cref="IsOnline"/> and the protocol version; returns true when the device answered.</summary>
    public async Task<bool> PingAsync(HidppTransport transport, CancellationToken cancellationToken)
    {
        var pingByte = (byte)(0x40 + (Interlocked.Increment(ref s_pingCounter) & 0x3F));
        var result = await transport.RequestAsync(
            HidppProtocol.Long(Index, Hidpp.RootFeatureIndex, 1, 0, 0, pingByte), RequestTimeoutMs, cancellationToken, matchParam: 2).ConfigureAwait(false);

        switch (result.Kind)
        {
            case HidppResultKind.Success:
                ProtocolMajor = Math.Max(2, (int)result.Param(0));
                ProtocolMinor = result.Param(1);
                LastPingError = Hidpp1Error.None;
                IsOnline = true;
                return true;

            case HidppResultKind.Hidpp1Error when result.Hidpp1Code == Hidpp1Error.InvalidSubId:
                ProtocolMajor = 1;
                ProtocolMinor = 0;
                LastPingError = Hidpp1Error.None;
                IsOnline = true;
                return true;

            case HidppResultKind.Hidpp1Error:
                LastPingError = result.Hidpp1Code;
                IsOnline = false;
                return false;

            case HidppResultKind.Hidpp2Error:
                // A 2.0 error from the root feature still proves a 2.0 device is awake.
                if (ProtocolMajor < 2) ProtocolMajor = 2;
                IsOnline = true;
                return true;

            default:
                IsOnline = false;
                return false;
        }
    }

    /// <summary>Looks up feature indices, name, type and unit id of a HID++ 2.0 device. Safe to call again later.</summary>
    public async Task DiscoverFeaturesAsync(HidppTransport transport, CancellationToken cancellationToken)
    {
        if (ProtocolMajor < 2) return;

        var (nameIndex, kind) = await GetFeatureIndexAsync(transport, HidppFeature.DeviceName, cancellationToken).ConfigureAwait(false);
        if (!Continue(kind)) return;
        _deviceNameFeature = nameIndex;

        if (_deviceNameFeature != 0)
        {
            await ReadDeviceNameAsync(transport, cancellationToken).ConfigureAwait(false);
            var type = await transport.RequestAsync(HidppProtocol.Long(Index, _deviceNameFeature, 2), RequestTimeoutMs, cancellationToken).ConfigureAwait(false);
            if (type.IsSuccess) Kind = HidppProtocol.KindFromDeviceType(type.Param(0));
        }

        var (fwIndex, fwKind) = await GetFeatureIndexAsync(transport, HidppFeature.DeviceFwVersion, cancellationToken).ConfigureAwait(false);
        if (!Continue(fwKind)) return;
        _fwVersionFeature = fwIndex;
        if (_fwVersionFeature != 0)
        {
            var info = await transport.RequestAsync(HidppProtocol.Long(Index, _fwVersionFeature, 0), RequestTimeoutMs, cancellationToken).ConfigureAwait(false);
            if (info.IsSuccess)
            {
                UnitId = (uint)(info.Param(1) << 24 | info.Param(2) << 16 | info.Param(3) << 8 | info.Param(4));
            }
        }

        // First supported battery feature wins: 0x1004 → 0x1000 → 0x1001.
        _unifiedBatteryFeature = _batteryStatusFeature = _batteryVoltageFeature = 0;
        var (unified, unifiedKind) = await GetFeatureIndexAsync(transport, HidppFeature.UnifiedBattery, cancellationToken).ConfigureAwait(false);
        if (!Continue(unifiedKind)) return;
        if (unified != 0)
        {
            _unifiedBatteryFeature = unified;
            var caps = await transport.RequestAsync(HidppProtocol.Long(Index, unified, 0), RequestTimeoutMs, cancellationToken).ConfigureAwait(false);
            _unifiedStateOfChargeSupported = caps.IsSuccess && (caps.Param(1) & 0x02) != 0;
        }
        else
        {
            var (status, statusKind) = await GetFeatureIndexAsync(transport, HidppFeature.BatteryStatus, cancellationToken).ConfigureAwait(false);
            if (!Continue(statusKind)) return;
            if (status != 0)
            {
                _batteryStatusFeature = status;
            }
            else
            {
                var (voltage, voltageKind) = await GetFeatureIndexAsync(transport, HidppFeature.BatteryVoltage, cancellationToken).ConfigureAwait(false);
                if (!Continue(voltageKind)) return;
                _batteryVoltageFeature = voltage;
            }
        }

        FeaturesDiscovered = true;
        _logger.LogDebug(
            "Device {Index} '{Name}' kind={Kind} protocol={Major}.{Minor} unitId={UnitId:X8} features: name=0x{NameIdx:X2} fw=0x{FwIdx:X2} unified=0x{Unified:X2} status=0x{Status:X2} voltage=0x{Voltage:X2}",
            Index, Name, Kind, ProtocolMajor, ProtocolMinor, UnitId, _deviceNameFeature, _fwVersionFeature, _unifiedBatteryFeature, _batteryStatusFeature, _batteryVoltageFeature);

        bool Continue(HidppResultKind k)
        {
            if (k is HidppResultKind.Success or HidppResultKind.Hidpp2Error) return true;
            if (k == HidppResultKind.Hidpp1Error) IsOnline = false;
            return false;
        }
    }

    /// <summary>Reads the battery with the cached feature (or HID++ 1.0 registers). Updates <see cref="LastReading"/> on success.</summary>
    public async Task<HidppResultKind> ReadBatteryAsync(HidppTransport transport, CancellationToken cancellationToken)
    {
        HidppResult result;
        BatteryReading? reading = null;

        if (ProtocolMajor >= 2)
        {
            if (_unifiedBatteryFeature != 0)
            {
                result = await transport.RequestAsync(HidppProtocol.Long(Index, _unifiedBatteryFeature, 1), RequestTimeoutMs, cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    int soc = result.Param(0);
                    var levelFlags = result.Param(1);
                    var charging = result.Param(2);
                    var externalPower = result.Param(3) != 0;
                    int? percent = _unifiedStateOfChargeSupported || soc > 0 ? Math.Clamp(soc, 0, 100) : ApproximateFromLevelFlags(levelFlags);
                    var status = charging switch
                    {
                        0 => externalPower ? BatteryStatus.NotCharging : BatteryStatus.Discharging,
                        1 or 2 => BatteryStatus.Charging,
                        3 => BatteryStatus.Full,
                        _ => BatteryStatus.Unknown,
                    };
                    reading = new BatteryReading(percent, Refine(status, percent), null, "UNIFIED_BATTERY");
                }
            }
            else if (_batteryStatusFeature != 0)
            {
                result = await transport.RequestAsync(HidppProtocol.Long(Index, _batteryStatusFeature, 0), RequestTimeoutMs, cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    int? percent = Math.Clamp((int)result.Param(0), 0, 100);
                    var status = result.Param(2) switch
                    {
                        0 => BatteryStatus.Discharging,
                        1 or 2 or 4 => BatteryStatus.Charging,
                        3 => BatteryStatus.Full,
                        _ => BatteryStatus.Unknown,
                    };
                    reading = new BatteryReading(percent, Refine(status, percent), null, "BATTERY_STATUS");
                }
            }
            else if (_batteryVoltageFeature != 0)
            {
                result = await transport.RequestAsync(HidppProtocol.Long(Index, _batteryVoltageFeature, 0), RequestTimeoutMs, cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    var milliVolts = (result.Param(0) << 8) | result.Param(1);
                    var flags = result.Param(2);
                    int? percent = BatteryVoltageTable.ToPercent(milliVolts);
                    BatteryStatus status;
                    if ((flags & 0x80) != 0)
                    {
                        status = (flags & 0x07) switch
                        {
                            0 => BatteryStatus.Charging,
                            1 => BatteryStatus.Full,
                            2 => BatteryStatus.NotCharging,
                            _ => BatteryStatus.Unknown,
                        };
                    }
                    else
                    {
                        status = (flags & 0x40) != 0 ? BatteryStatus.Charging : BatteryStatus.Discharging;
                    }
                    reading = new BatteryReading(percent, Refine(status, percent), milliVolts, "BATTERY_VOLTAGE");
                }
            }
            else
            {
                return HidppResultKind.Success; // no battery feature; nothing to read
            }
        }
        else
        {
            result = await transport.RequestAsync(
                HidppProtocol.Short(Index, Hidpp.GetRegisterShort, Hidpp.RegisterBatteryMileage), RequestTimeoutMs, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                int? percent = Math.Clamp((int)result.Param(0), 0, 100);
                var status = (result.Param(2) & 0xF0) switch
                {
                    0x30 => BatteryStatus.Discharging,
                    0x50 => BatteryStatus.Charging,
                    0x90 => BatteryStatus.Full,
                    _ => BatteryStatus.Unknown,
                };
                reading = new BatteryReading(percent, Refine(status, percent), null, "REG_0D");
            }
            else if (result.Kind == HidppResultKind.Hidpp1Error && result.Hidpp1Code is Hidpp1Error.InvalidAddress or Hidpp1Error.InvalidSubId or Hidpp1Error.RequestUnavailable)
            {
                result = await transport.RequestAsync(
                    HidppProtocol.Short(Index, Hidpp.GetRegisterShort, Hidpp.RegisterBatteryStatus), RequestTimeoutMs, cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    int? percent = result.Param(0) switch { 7 => 100, 5 => 50, 3 => 20, 1 => 5, _ => null };
                    var chargingByte = result.Param(1);
                    var status = chargingByte == 0 ? BatteryStatus.Discharging
                        : (chargingByte & 0x21) == 0x21 ? BatteryStatus.Charging
                        : (chargingByte & 0x22) == 0x22 ? BatteryStatus.Full
                        : BatteryStatus.Unknown;
                    reading = new BatteryReading(percent, Refine(status, percent), null, "REG_07");
                }
            }
        }

        if (reading is not null)
        {
            LastReading = reading;
            LastReadingAt = DateTimeOffset.Now;
        }
        return result.Kind;
    }

    /// <summary>Full per-poll cycle: wake-up check, lazy feature discovery, battery read, sleep detection.</summary>
    public async Task RefreshAsync(HidppTransport transport, CancellationToken cancellationToken)
    {
        if (!IsOnline && !await PingAsync(transport, cancellationToken).ConfigureAwait(false)) return;

        if (ProtocolMajor >= 2 && !FeaturesDiscovered)
        {
            await DiscoverFeaturesAsync(transport, cancellationToken).ConfigureAwait(false);
            if (!IsOnline) return;
        }

        var kind = await ReadBatteryAsync(transport, cancellationToken).ConfigureAwait(false);
        switch (kind)
        {
            case HidppResultKind.Success:
                break;
            case HidppResultKind.Hidpp1Error:
            case HidppResultKind.Timeout:
                _logger.LogDebug("Device {Index} '{Name}' stopped answering ({Kind}); treating as asleep", Index, Name, kind);
                IsOnline = false;
                break;
            case HidppResultKind.Hidpp2Error:
                _logger.LogDebug("Device {Index} '{Name}' battery request rejected; feature table will be re-read", Index, Name);
                FeaturesDiscovered = false;
                break;
            case HidppResultKind.IoError:
                IsOnline = false;
                break;
        }
    }

    private async Task<(byte Index, HidppResultKind Kind)> GetFeatureIndexAsync(HidppTransport transport, ushort featureId, CancellationToken cancellationToken)
    {
        var result = await transport.RequestAsync(
            HidppProtocol.Long(Index, Hidpp.RootFeatureIndex, 0, (byte)(featureId >> 8), (byte)featureId), RequestTimeoutMs, cancellationToken).ConfigureAwait(false);
        return (result.IsSuccess ? result.Param(0) : (byte)0, result.Kind);
    }

    private async Task ReadDeviceNameAsync(HidppTransport transport, CancellationToken cancellationToken)
    {
        var count = await transport.RequestAsync(HidppProtocol.Long(Index, _deviceNameFeature, 0), RequestTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (!count.IsSuccess) return;
        int length = count.Param(0);
        if (length <= 0) return;

        var name = string.Empty;
        for (var offset = 0; offset < length && offset < 64; offset += 16)
        {
            var chunk = await transport.RequestAsync(HidppProtocol.Long(Index, _deviceNameFeature, 1, (byte)offset), RequestTimeoutMs, cancellationToken).ConfigureAwait(false);
            if (!chunk.IsSuccess) break;
            name += HidppProtocol.Ascii(chunk.Frame, 4, Math.Min(16, length - offset));
        }
        if (!string.IsNullOrWhiteSpace(name)) Hidpp2Name = name.Trim();
    }

    private static int? ApproximateFromLevelFlags(byte flags)
    {
        if ((flags & 0x08) != 0) return 90;
        if ((flags & 0x04) != 0) return 50;
        if ((flags & 0x02) != 0) return 20;
        if ((flags & 0x01) != 0) return 5;
        return null;
    }

    private static BatteryStatus Refine(BatteryStatus status, int? percent)
    {
        if (status != BatteryStatus.Discharging || percent is not int p) return status;
        if (p <= 10) return BatteryStatus.Critical;
        if (p <= 20) return BatteryStatus.Low;
        return status;
    }
}
