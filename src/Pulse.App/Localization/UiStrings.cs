namespace Pulse.App.Localization;

/// <summary>
/// User-facing texts that only the popover process needs (talking to the agent). Everything else lives in
/// <see cref="Pulse.Core.Localization.Strings"/>, which is frozen for this package.
/// </summary>
public static class UiStrings
{
    /// <summary>The agent speaks another protocol version (it was updated while this UI was starting).</summary>
    public const string ProtocolMismatch = "Pulse 已更新，請重新開啟";

    /// <summary>No agent answered on the pipe (Pulse.UI.exe started by hand, or the agent is not running).</summary>
    public const string AgentUnavailable = "請從托盤圖示開啟 Pulse";

    /// <summary>A request got no reply within its timeout.</summary>
    public const string RequestTimedOut = "Pulse 沒有回應，請稍後再試。";

    /// <summary>The pipe broke while a request was waiting for its reply.</summary>
    public const string AgentDisconnected = "與 Pulse 的連線已中斷。";
}
