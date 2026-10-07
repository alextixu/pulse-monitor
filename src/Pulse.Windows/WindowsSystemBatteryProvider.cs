using System.Runtime.InteropServices;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;

namespace Pulse.Windows;

/// <summary>The computer's own battery (laptops / tablets) via <c>GetSystemPowerStatus</c>. Empty on desktops.</summary>
public sealed partial class WindowsSystemBatteryProvider : IBatteryProvider
{
    public const string ProviderName = "System";
    public const string DeviceId = "system:0";

    private const byte AcOnline = 1;
    private const byte FlagLow = 2;
    private const byte FlagCritical = 4;
    private const byte FlagCharging = 8;
    private const byte FlagNoSystemBattery = 128;
    private const byte FlagUnknown = 255;
    private const byte PercentUnknown = 255;
    private const uint LifeTimeUnknown = uint.MaxValue;

    private readonly ILogger<WindowsSystemBatteryProvider> _logger;

    public WindowsSystemBatteryProvider(ILogger<WindowsSystemBatteryProvider> logger)
    {
        _logger = logger;
    }

    public string Name => ProviderName;

    public bool IsSupported => OperatingSystem.IsWindows();

    public Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetDevices());
    }

    private IReadOnlyList<BatteryDevice> GetDevices()
    {
        if (!IsSupported) return Array.Empty<BatteryDevice>();

        SYSTEM_POWER_STATUS status;
        try
        {
            if (!GetSystemPowerStatus(out status))
            {
                _logger.LogDebug("GetSystemPowerStatus failed with Win32 error {Error}", Marshal.GetLastPInvokeError());
                return Array.Empty<BatteryDevice>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetSystemPowerStatus threw");
            return Array.Empty<BatteryDevice>();
        }

        if ((status.BatteryFlag & FlagNoSystemBattery) != 0 || status.BatteryFlag == FlagUnknown || status.BatteryLifePercent == PercentUnknown)
        {
            return Array.Empty<BatteryDevice>();
        }

        var percent = Math.Min((int)status.BatteryLifePercent, 100);
        var machine = Environment.MachineName;
        return new[]
        {
            new BatteryDevice
            {
                Id = DeviceId,
                Name = string.IsNullOrWhiteSpace(machine) ? "這台電腦" : machine,
                Kind = DeviceKind.Laptop,
                Connection = ConnectionType.Internal,
                Source = ProviderName,
                Percent = percent,
                Status = ToStatus(status, percent),
                IsConnected = true,
                Detail = BuildDetail(status),
            },
        };
    }

    private static BatteryStatus ToStatus(in SYSTEM_POWER_STATUS status, int percent)
    {
        if ((status.BatteryFlag & FlagCharging) != 0) return percent >= 100 ? BatteryStatus.Full : BatteryStatus.Charging;
        if (status.ACLineStatus == AcOnline) return percent >= 100 ? BatteryStatus.Full : BatteryStatus.NotCharging;
        if ((status.BatteryFlag & FlagCritical) != 0 || percent <= 10) return BatteryStatus.Critical;
        if ((status.BatteryFlag & FlagLow) != 0 || percent <= 20) return BatteryStatus.Low;
        return BatteryStatus.Discharging;
    }

    private static string BuildDetail(in SYSTEM_POWER_STATUS status)
    {
        if (status.ACLineStatus != AcOnline && status.BatteryLifeTime != LifeTimeUnknown)
        {
            var remaining = TimeSpan.FromSeconds(status.BatteryLifeTime);
            return remaining.TotalHours >= 1
                ? $"內建電池 · 約 {(int)remaining.TotalHours} 小時 {remaining.Minutes} 分"
                : $"內建電池 · 約 {remaining.Minutes} 分";
        }

        return "內建電池";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);
}
