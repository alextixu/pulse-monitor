using Pulse.Core.Models;

namespace Pulse.App.Localization;

/// <summary>
/// Every user-facing string (Traditional Chinese). Views bind to these via <c>{x:Static loc:Strings.Xxx}</c>;
/// enum display names go through the <c>ForEnum</c> helpers so converters and view models share one source.
/// </summary>
public static class Strings
{
    // ---- App / window ----
    public const string AppTitle = "Pulse";
    public const string AppSubtitle = "裝置電量與硬體監控";

    // ---- Tabs ----
    public const string TabDevices = "裝置";
    public const string TabSystem = "系統";
    public const string TabLighting = "燈光";
    public const string TabFans = "風扇";
    public const string TabSettings = "設定";

    // ---- Tray menu ----
    public const string MenuOpen = "開啟面板";
    public const string MenuRefresh = "立即重新整理";
    public const string MenuElevate = "以系統管理員身分重新啟動";
    public const string MenuSettings = "設定";
    public const string MenuQuit = "結束";

    // ---- Header / common actions ----
    public const string Refresh = "重新整理";
    public const string Refreshing = "更新中…";
    public const string Settings = "設定";
    public const string Close = "關閉";
    public const string Connect = "連線";
    public const string Disconnect = "中斷連線";
    public const string Retry = "重試";
    public const string Save = "儲存";
    public const string Apply = "套用";
    public const string Reset = "重設";
    public const string Auto = "自動";
    public const string Manual = "手動";
    public const string On = "開啟";
    public const string Off = "關閉";
    public const string Yes = "是";
    public const string No = "否";
    public const string Unknown = "未知";
    public const string NotAvailable = "—";
    public const string Elevate = "以系統管理員身分執行";
    public const string ElevateHint = "需要系統管理員權限才能讀取 CPU 溫度與控制風扇。";
    public const string ElevatedBadge = "系統管理員";
    public const string ElevateShort = "提升權限";
    public const string NotElevatedBadge = "一般權限";

    // ---- Status (header dot / status line) ----
    public const string StatusStarting = "正在啟動…";
    public const string StatusReady = "一切正常";
    public const string StatusDegraded = "部分資料無法取得";
    public const string StatusFailed = "硬體監控無法啟動";
    public const string StatusUnsupported = "此平台不支援硬體監控";
    public const string StatusRefreshing = "正在更新…";
    public const string StatusNoDevices = "尚未偵測到任何裝置";
    public const string StatusUpdatedFormat = "更新於 {0}";
    public const string StatusElevationFailed = "無法以系統管理員身分重新啟動。";

    // ---- Battery / devices ----
    public const string LowestBatteryFormat = "{0} 電量最低：{1}%";
    public const string NoBatteryDevices = "沒有回報電量的裝置";
    public const string DeviceDisconnected = "未連線";
    public const string DeviceCharging = "充電中";
    public const string DeviceFull = "已充飽";
    public const string DeviceLow = "電量低";
    public const string DeviceCritical = "電量極低";
    public const string DeviceNotCharging = "未充電";
    public const string DeviceLevelUnavailable = "電量無法取得";
    public const string HideDevice = "隱藏此裝置";
    public const string ShowHiddenDevices = "顯示已隱藏的裝置";
    public const string DevicesEmptyHint = "請連接藍牙、Logitech 或 ASUS ROG 無線裝置後重新整理。";

    // ---- System ----
    public const string Cpu = "CPU";
    public const string Gpu = "GPU";
    public const string Memory = "記憶體";
    public const string Load = "使用率";
    public const string Temperature = "溫度";
    public const string Clock = "時脈";
    public const string Power = "功耗";
    public const string HotSpot = "熱點";
    public const string VideoMemory = "顯示記憶體";
    public const string OtherTemperatures = "其他溫度";
    public const string CpuTempUnavailable = "CPU 溫度需要系統管理員權限";
    public const string NoGpu = "未偵測到顯示卡";

    // ---- Lighting ----
    public const string LightingTitle = "RGB 燈光";
    public const string RgbDisconnected = "尚未連線到 OpenRGB";
    public const string RgbConnecting = "正在連線到 OpenRGB…";
    public const string RgbConnected = "已連線到 OpenRGB";
    public const string RgbError = "OpenRGB 連線失敗";
    public const string RgbHint = "請安裝 OpenRGB 並啟用「設定 › SDK 伺服器」。";
    public const string RgbNoDevices = "OpenRGB 沒有回報任何裝置";
    public const string Mode = "模式";
    public const string Color = "顏色";
    public const string Brightness = "亮度";
    public const string Speed = "速度";
    public const string ApplyToAll = "套用到所有裝置";
    public const string Zones = "區域";

    // ---- Fans ----
    public const string FansTitle = "風扇";
    public const string FanControlUnavailable = "風扇控制需要系統管理員權限";
    public const string NoFans = "未偵測到風扇";
    public const string FanAuto = "交由主機板自動控制";
    public const string FanRpmFormat = "{0} RPM";
    public const string FanPercentFormat = "{0}%";

    // ---- Settings ----
    public const string SettingsGeneral = "一般";
    public const string SettingsAppearance = "外觀";
    public const string SettingsRefresh = "更新頻率";
    public const string SettingsOpenRgb = "OpenRGB";
    public const string SettingsNotifications = "通知";
    public const string SettingsAbout = "關於";
    public const string SettingStartWithSystem = "開機時自動啟動";
    public const string SettingRequestElevation = "啟動時要求系統管理員權限";
    public const string SettingTheme = "主題";
    public const string SettingTrayIcon = "系統匣圖示";
    public const string SettingHardwareRefresh = "硬體更新間隔（秒）";
    public const string SettingBatteryRefresh = "電量更新間隔（秒）";
    public const string SettingOpenRgbHost = "主機";
    public const string SettingOpenRgbPort = "連接埠";
    public const string SettingAutoConnectOpenRgb = "啟動時自動連線";
    public const string SettingUseBundledOpenRgb = "使用內建 OpenRGB";
    public const string SettingBundledOpenRgbPresent = "沒有在執行的 OpenRGB 時，Pulse 會在背景啟動內建的 OpenRGB，結束時一起關閉。以系統管理員身分執行才能控制記憶體等 SMBus 燈光。";
    public const string SettingBundledOpenRgbMissing = "這個版本沒有附帶 OpenRGB（建置前執行 tools\\fetch-openrgb.ps1），請自行安裝 OpenRGB。";
    public const string SettingNotifyLowBattery = "電量低時通知";
    public const string SettingLowBatteryThreshold = "低電量門檻（%）";
    public const string SettingsSaved = "設定已儲存";
    public const string SettingsSaveFailed = "設定儲存失敗";
    public const string AutoStartUnsupported = "此平台不支援自動啟動";
    public const string VersionFormat = "版本 {0}";

    // ---- Tray tooltip ----
    public const string TooltipDeviceFormat = "{0} {1}%";
    public const string TooltipCpuFormat = "CPU {0} · {1}";
    public const string TooltipGpuFormat = "GPU {0} · {1}";

    // ---- Low battery notification ----
    public const string LowBatteryTitle = "電量不足";
    public const string LowBatteryMessageFormat = "{0} 只剩 {1}% 電量";

    // ---- Fans (part 2) ----
    public const string FanManualControl = "手動控制";
    public const string FanMonitorOnly = "僅監測";
    public const string FanManualWarning = "手動設定會持續到應用程式關閉；請留意溫度。";
    public const string FanPresetQuiet = "靜音";
    public const string FanPresetBalanced = "平衡";
    public const string FanPresetPerformance = "效能";
    public const string FanPresetFull = "全速";
    public const string FanPresetQuietLabel = "靜音 30%";
    public const string FanPresetBalancedLabel = "平衡 50%";
    public const string FanPresetPerformanceLabel = "效能 75%";
    public const string FanPresetFullLabel = "全速 100%";

    // ---- Lighting (part 2) ----
    public const string OpenRgbDownloadUrl = "https://openrgb.org/releases.html";
    public const string DownloadOpenRgb = "下載 OpenRGB";
    public const string Reconnect = "重新連線";
    public const string CustomColor = "自訂顏色";
    public const string RgbConnectedFormat = "已連線 OpenRGB（{0} 個裝置）";
    public const string LedCountFormat = "{0} 顆 LED";
    public const string ZonesFormat = "區域（{0}）";

    // ---- Devices (part 2) ----
    public const string DeviceCountFormat = "{0} 個裝置 · 最低 {1}";
    public const string ShowHiddenDevicesFormat = "顯示已隱藏的裝置（{0}）";
    public const string DevicesEmptyTitle = "尚未偵測到可回報電量的裝置";
    public const string DevicesEmptyHintBluetooth = "藍牙裝置需先配對並連線";
    public const string DevicesEmptyHintLogitech = "Logitech 與 ASUS ROG 裝置透過接收器或 USB 連接";
    public const string DevicesEmptyHintOther = "部分裝置不回報電量";
    public const string DeviceGood = "良好";
    public const string DeviceSleeping = "休眠中";

    // ---- Settings (part 2) ----
    public const string SettingAuthorLine = "Pulse · MIT 授權";
    public const string OpenSettingsFolder = "開啟設定檔資料夾";
    public const string OpenLogFile = "開啟記錄檔";
    public const string SettingsTelemetry = "硬體記錄";
    public const string SettingTelemetryEnabled = "每秒記錄溫度、使用率與功耗";
    public const string SettingTelemetryHint = "寫成 CSV（可用 Excel 開啟），每小時清除一次並保留前一小時；開啟時硬體每秒更新。";
    public const string OpenTelemetryFolder = "開啟記錄資料夾";
    public const string TestConnection = "連線測試";
    public const string DemoModeBadge = "示範模式";

    // ---- System (part 2) ----
    public const string SystemElevateBanner = "以系統管理員身分執行以顯示 CPU 溫度並控制風扇";
    public const string NeedsAdmin = "需要系統管理員";
    public const string CoreLoads = "各核心使用率";
    public const string CoresFormat = "{0} 核心";
    public const string HardwareNotReady = "正在等待硬體監控資料…";
    public const string OtherTemperaturesFormat = "其他溫度（{0}）";
    public const string VideoMemoryShort = "VRAM";
    public const string Fan = "風扇";

    // ---- Lighting (part 2, extra) ----
    public const string ApplyToAllShort = "套用到全部";
    public const string HostPortFormat = "{0}:{1}";
    public const string BrightnessNote = "OpenRGB SDK 可能忽略亮度設定";
    public const string RgbLoadingDevices = "正在讀取裝置…";
    public const string RgbServerVersionFormat = "伺服器 {0}";
    public const string ColorAppliedFormat = "已套用 {0}";

    // ---- Lighting: off / restore default ----
    public const string TurnOff = "關燈";
    public const string TurnOffAll = "全部關燈";
    public const string RestoreDefault = "還原預設";
    public const string RestoreAllDefaults = "全部還原預設";
    public const string SaveCurrentAsDefault = "將目前狀態設為預設";
    public const string MoreActions = "更多動作";
    public const string TurnOffTip = "有「Off」模式時切換到該模式，否則把每顆 LED 設為黑色";
    public const string TurnOffAllTip = "關閉所有裝置的燈光";
    public const string RestoreDefaultTip = "還原到 Pulse 第一次偵測到此裝置時的燈光狀態";
    public const string RestoreAllDefaultsTip = "把所有裝置還原到 Pulse 第一次偵測到它們時的燈光狀態";
    public const string RestoreFallbackFormat = "尚未記錄預設狀態，將切換到韌體燈效「{0}」";
    public const string RestoreUnavailable = "尚未記錄預設狀態，也沒有可切換的韌體燈效";
    public const string SaveCurrentAsDefaultTip = "之後「還原預設」會回到現在的燈光狀態";
    public const string SavedAsDefault = "已將目前狀態設為預設";
    public const string TurnedOff = "已關燈";
    public const string RestoredDefault = "已還原預設";

    // ---- Fans (part 2, extra) ----
    public const string FanManualChip = "手動";
    public const string FanAutoChip = "自動";
    public const string FansEmptyHint = "硬體監控就緒後會在此列出風扇。";
    public const string FanDuty = "轉速比";

    // ---- Fan modes ----
    public const string FanModesTitle = "風扇模式";
    public const string FanModeAuto = "自動";
    public const string FanModeQuiet = "靜音";
    public const string FanModeSynced = "全部同步";
    public const string FanModeIndividual = "個別";
    public const string FanModeAutoHint = "所有風扇交給主機板 BIOS 與顯示卡驅動自動控制。";
    public const string FanModeQuietHint = "依 CPU／GPU 溫度低速運轉；過熱時自動交回 BIOS 控制。";
    public const string FanModeSyncedHint = "納入群組的風扇以相同轉速運轉。";
    public const string FanModeIndividualHint = "在下方逐一手動設定每個風扇。";
    public const string FanModeUnavailable = "需要系統管理員權限才能使用風扇模式；目前的選擇會保留。";
    public const string FanQuietCurveLine = "CPU 75°C 以下維持最低轉速，75–80°C 升到 50%；顯示卡 50°C 以下最低，50–70°C 升到 60%";
    public const string FanQuietHandoffLine = "CPU 80°C、顯示卡 75°C 以上交回自動控制，降到 72°C／68°C 後恢復靜音";
    public const string FanQuietWaiting = "等待溫度資料…";
    public const string FanBoardShort = "主機板";
    public const string FanGpuShort = "顯示卡";
    public const string FanSyncedLabel = "同步轉速";
    public const string FanGroupMember = "納入群組控制";
    public const string FanChipHandedOff = "溫度高·自動";
    public const string FanChipNoTemp = "無溫度·自動";
    public const string FanChipExcluded = "已排除";
    public const string FanChipExcludedPump = "已排除：可能是水冷泵";
    public const string FanChipDetecting = "偵測中";
    public const string FanExcludedUser = "已手動排除，維持主機板自動控制。";
    public const string FanExcludedPumpName = "名稱看起來是水冷泵，預設不納入；確定是風扇再開啟。";
    public const string FanExcludedPumpLike = "首次偵測時轉速很高（≥2800 RPM，或 ≥95% 且 ≥1800 RPM），可能是水冷泵，預設不納入。";
    public const string FanHiddenHeadersFormat = "已隱藏 {0} 個未接風扇的接頭";

    public static string FanChipQuiet(double? percent) => $"靜音 {Percent(percent)}";
    public static string FanChipSynced(double? percent) => $"同步 {Percent(percent)}";

    // ---- Settings (part 2, extra) ----
    public const string SettingTrayIconDisplay = "系統匣圖示顯示";
    public const string SettingRequestElevationOnStartup = "啟動時以系統管理員身分執行";
    public const string SecondsFormat = "{0} 秒";
    public const string PercentFormat = "{0}%";
    public const string TestConnectionRunning = "正在測試…";
    public const string TestConnectionOk = "連線成功";
    public const string TestConnectionFailed = "無法連線";
    public const string SettingsFile = "設定檔";
    public const string LogFile = "記錄檔";
    public const string OpenFailed = "無法開啟";
    public const string SettingHardwareRefreshShort = "硬體";
    public const string SettingBatteryRefreshShort = "電池";
    public const string SettingThresholdShort = "門檻";
    public const string SettingNotifyLowBatteryShort = "低電量提醒";
    public const string SettingAutoConnectShort = "自動連線";

    // ---- Low battery (part 2) ----
    public const string LowBatteryToastFormat = "{0} 只剩 {1}%，請盡快充電。";

    // ---- Placeholders (part 2 replaces the views) ----
    public const string PlaceholderDevices = "裝置電量列表";
    public const string PlaceholderSystem = "CPU / GPU / 記憶體監控";
    public const string PlaceholderLighting = "RGB 燈光控制";
    public const string PlaceholderFans = "風扇轉速控制";
    public const string PlaceholderSettings = "應用程式設定";
    public const string PlaceholderComingSoon = "此分頁內容將在下一階段完成";

    // ---- Enum display names ----
    public static string ForEnum(Enum value) => value switch
    {
        DeviceKind k => DeviceKindName(k),
        ConnectionType c => ConnectionTypeName(c),
        BatteryStatus s => BatteryStatusName(s),
        HardwareMonitorState h => HardwareStateName(h),
        RgbConnectionState r => RgbStateName(r),
        AppTheme t => ThemeName(t),
        TrayIconMode m => TrayIconModeName(m),
        _ => value.ToString(),
    };

    public static string DeviceKindName(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "滑鼠",
        DeviceKind.Keyboard => "鍵盤",
        DeviceKind.Headset => "耳機",
        DeviceKind.Speaker => "喇叭",
        DeviceKind.Controller => "控制器",
        DeviceKind.Phone => "手機",
        DeviceKind.Tablet => "平板",
        DeviceKind.Laptop => "筆電",
        DeviceKind.Watch => "手錶",
        DeviceKind.Stylus => "觸控筆",
        DeviceKind.Trackpad => "觸控板",
        DeviceKind.Presenter => "簡報器",
        DeviceKind.Receiver => "接收器",
        DeviceKind.Other => "其他",
        _ => "裝置",
    };

    public static string ConnectionTypeName(ConnectionType type) => type switch
    {
        ConnectionType.Bluetooth => "藍牙",
        ConnectionType.BluetoothLE => "藍牙 LE",
        ConnectionType.UsbReceiver => "USB 接收器",
        ConnectionType.Usb => "USB",
        ConnectionType.Internal => "內建電池",
        _ => Unknown,
    };

    public static string BatteryStatusName(BatteryStatus status) => status switch
    {
        BatteryStatus.Discharging => "使用中",
        BatteryStatus.Charging => DeviceCharging,
        BatteryStatus.Full => DeviceFull,
        BatteryStatus.Low => DeviceLow,
        BatteryStatus.Critical => DeviceCritical,
        BatteryStatus.NotCharging => DeviceNotCharging,
        _ => Unknown,
    };

    public static string HardwareStateName(HardwareMonitorState state) => state switch
    {
        HardwareMonitorState.NotStarted => "尚未啟動",
        HardwareMonitorState.Initializing => "初始化中",
        HardwareMonitorState.Ready => "就緒",
        HardwareMonitorState.Degraded => "部分可用",
        HardwareMonitorState.Failed => "失敗",
        HardwareMonitorState.Unsupported => "不支援",
        _ => Unknown,
    };

    public static string RgbStateName(RgbConnectionState state) => state switch
    {
        RgbConnectionState.Disconnected => "未連線",
        RgbConnectionState.Connecting => "連線中",
        RgbConnectionState.Connected => "已連線",
        RgbConnectionState.Error => "錯誤",
        _ => Unknown,
    };

    public static string ThemeName(AppTheme theme) => theme switch
    {
        AppTheme.System => "跟隨系統",
        AppTheme.Light => "淺色",
        AppTheme.Dark => "深色",
        _ => Unknown,
    };

    public static string TrayIconModeName(TrayIconMode mode) => mode switch
    {
        TrayIconMode.LowestBattery => "最低電量",
        TrayIconMode.CpuTemperature => "CPU 溫度",
        TrayIconMode.GpuTemperature => "GPU 溫度",
        TrayIconMode.CpuLoad => "CPU 使用率",
        TrayIconMode.IconOnly => "僅顯示圖示",
        _ => Unknown,
    };

    /// <summary>OpenRGB device type ("Motherboard", "DRAM", …) → 繁體中文; unknown types pass through.</summary>
    public static string RgbTypeName(string? type)
    {
        var t = (type ?? string.Empty).Trim().ToLowerInvariant();
        return t switch
        {
            "motherboard" => "主機板",
            "dram" or "memory" => "記憶體",
            "gpu" => "顯示卡",
            "cooler" => "散熱器",
            "ledstrip" or "led strip" => "燈條",
            "keyboard" => "鍵盤",
            "mouse" => "滑鼠",
            "mousemat" => "滑鼠墊",
            "headset" => "耳機",
            "headset stand" or "headsetstand" => "耳機架",
            "gamepad" => "控制器",
            "light" => "燈具",
            "speaker" => "喇叭",
            "virtual" => "虛擬",
            "storage" => "儲存裝置",
            "case" => "機殼",
            "microphone" => "麥克風",
            "accessory" => "配件",
            "keypad" => "小鍵盤",
            "laptop" => "筆電",
            "monitor" => "顯示器",
            "" => Unknown,
            _ => type!,
        };
    }

    /// <summary>"87%" or an em dash when the value is unknown.</summary>
    public static string Percent(double? value) => value is { } v ? $"{Math.Round(v):0}%" : NotAvailable;

    /// <summary>"61°C" or an em dash when the value is unknown.</summary>
    public static string Celsius(double? value) => value is { } v ? $"{Math.Round(v):0}°C" : NotAvailable;

    /// <summary>"1 250 RPM" (thin-space thousands) or an em dash when the value is unknown.</summary>
    public static string Rpm(double? value) => value is { } v
        ? string.Format(FanRpmFormat, Math.Round(v).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', ' '))
        : NotAvailable;

    /// <summary>"3.2 / 12 GB" style pair; either side may be unknown.</summary>
    public static string Gigabytes(double? used, double? total)
    {
        var u = used is { } a ? a.ToString("0.0") : NotAvailable;
        var t = total is { } b ? b.ToString("0") : NotAvailable;
        return $"{u} / {t} GB";
    }
}
