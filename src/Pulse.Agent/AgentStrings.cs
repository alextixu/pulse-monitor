namespace Pulse.Agent;

/// <summary>User-facing strings that only the agent shows and that Pulse.Core's (frozen) <c>Strings</c> lacks. Traditional Chinese.</summary>
internal static class AgentStrings
{
    /// <summary>Shown by Windows next to Pulse while logoff / shutdown waits for the clean exit (ShutdownBlockReasonCreate).</summary>
    public const string ShutdownBlockReason = "正在把風扇交回 BIOS…";
}
