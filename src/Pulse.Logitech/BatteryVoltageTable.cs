namespace Pulse.Logitech;

/// <summary>Li-ion open-circuit voltage → state of charge, for devices that only expose feature 0x1001 BATTERY_VOLTAGE.</summary>
internal static class BatteryVoltageTable
{
    // Descending by voltage; same curve Solaar uses for HID++ 2.0 BATTERY_VOLTAGE devices.
    private static readonly (int MilliVolts, int Percent)[] Curve =
    {
        (4186, 100), (4156, 95), (4143, 90), (4133, 85), (4122, 80), (4113, 75), (4103, 70),
        (4094, 65), (4086, 60), (4076, 55), (4067, 50), (4060, 45), (4051, 40), (4044, 35),
        (4038, 30), (4028, 25), (4020, 20), (4010, 15), (3996, 10), (3983, 5), (3952, 0),
    };

    /// <summary>Linear interpolation between table rows; clamped to 0..100.</summary>
    public static int ToPercent(int milliVolts)
    {
        if (milliVolts >= Curve[0].MilliVolts) return 100;
        if (milliVolts <= Curve[^1].MilliVolts) return 0;

        for (var i = 1; i < Curve.Length; i++)
        {
            var (hiMv, hiPct) = Curve[i - 1];
            var (loMv, loPct) = Curve[i];
            if (milliVolts < loMv) continue;

            var span = hiMv - loMv;
            if (span <= 0) return loPct;
            var t = (milliVolts - loMv) / (double)span;
            return (int)Math.Round(loPct + t * (hiPct - loPct));
        }

        return 0;
    }
}
