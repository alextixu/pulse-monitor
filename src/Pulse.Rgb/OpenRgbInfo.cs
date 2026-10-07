namespace Pulse.Rgb;

/// <summary>Static facts about OpenRGB the UI can show next to the connection status.</summary>
public static class OpenRgbInfo
{
    public const string DownloadUrl = "https://openrgb.org/releases.html";

    public const string DefaultHost = "127.0.0.1";

    public const int DefaultPort = 6742;

    /// <summary>Name under which this app registers with the SDK server (shown in OpenRGB › SDK Server › clients).</summary>
    public const string ClientName = "Pulse";

    /// <summary>Step-by-step hint (繁體中文) shown when the SDK server cannot be reached.</summary>
    public const string SetupHint =
        "1. 前往 openrgb.org 下載並安裝 OpenRGB。\n" +
        "2. 開啟 OpenRGB，到「設定 › SDK Server」勾選啟用伺服器，並確認連接埠為 6742。\n" +
        "3. 建議以「OpenRGB.exe --server --startminimized」啟動（可加入開機自動執行），SDK 伺服器就會隨 OpenRGB 自動開啟。\n" +
        "4. Armoury Crate、iCUE、G HUB 等廠商軟體可能與 OpenRGB 爭奪裝置控制權；若燈光無法變更，請先關閉這些程式。";
}
