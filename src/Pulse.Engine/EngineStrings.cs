using System.Globalization;

namespace Pulse.Engine;

/// <summary>
/// User-facing texts produced by the engine that <see cref="Pulse.Core.Localization.Strings"/> does not have yet
/// (Pulse.Core is frozen for this rewrite). Traditional Chinese like every other string.
/// </summary>
internal static class EngineStrings
{
    // ---- manual fan writes (SetFanAsync) ----
    public const string FanNotIndividual = "只有在「個別」模式下才能手動設定風扇。";
    public const string FanUnknown = "找不到這個風扇。";
    public const string FanNotControllable = "這個風扇僅支援監測，無法控制轉速。";
    public const string FanOrphaned = "這個風扇目前由 Pulse 代管，重新開機前無法手動設定。";
    public const string FanInvalidPercent = "風扇轉速必須是 0–100% 之間的數值。";
    public const string FanTimeout = "風扇控制沒有回應，請稍後再試。";
    public const string FanWriteFailedFormat = "無法設定風扇：{0}";
    public const string FanUnknownId = "請指定風扇。";
    public const string FanInvalidMode = "未知的風扇模式。";

    // ---- hardware watchdog ----
    public const string HardwareStalled = "硬體監控已超過 30 秒沒有更新資料；風扇模式暫停，恢復後會自動繼續。";

    // ---- lifecycle ----
    public const string ShuttingDown = "Pulse 正在結束。";
    public const string AutoStartFailed = "無法變更開機自動啟動設定。";

    // ---- RGB ----
    public const string RgbDeviceChanged = "裝置已變更，請重新整理";
    public const string RgbNeedsDevice = "請先選擇燈光裝置。";
    public const string RgbNeedsColor = "請先選擇顏色。";
    public const string RgbNeedsZone = "請先選擇區域。";
    public const string RgbUnknownModeFormat = "這個裝置沒有「{0}」模式。";
    public const string RgbCommandFailedFormat = "燈光操作失敗：{0}";

    // ---- batteries ----
    /// <summary>「上次 14:05」 (today) / 「上次 10/26 14:05」 (another day) for a remembered, disconnected device.</summary>
    public static string LastSeen(DateTimeOffset lastConnected, DateTimeOffset now)
    {
        var local = lastConnected.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var format = local.Date == today ? "HH:mm" : "M/d HH:mm";
        return "上次 " + local.ToString(format, CultureInfo.InvariantCulture);
    }
}
