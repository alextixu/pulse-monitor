using System.Text.Json.Serialization;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;

namespace Pulse.Core.Ipc;

/// <summary>
/// Base of every message on the agent pipe. Serialised through <see cref="IpcJsonContext.IpcMessage"/> with a "t"
/// discriminator; an unknown discriminator (a newer peer) deserialises as a plain <see cref="IpcMessage"/>, which the
/// reader logs and skips.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t", IgnoreUnrecognizedTypeDiscriminators = true,
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
// agent → client
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(InfoMessage), "info")]
[JsonDerivedType(typeof(SettingsMessage), "settings")]
[JsonDerivedType(typeof(HardwareStatusMessage), "hwStatus")]
[JsonDerivedType(typeof(RgbStatusMessage), "rgbStatus")]
[JsonDerivedType(typeof(HardwareMessage), "hardware")]
[JsonDerivedType(typeof(BatteriesMessage), "batteries")]
[JsonDerivedType(typeof(RgbDevicesChangedMessage), "rgbChanged")]
[JsonDerivedType(typeof(LowBatteryMessage), "lowBattery")]
[JsonDerivedType(typeof(ShowPopoverMessage), "show")]
[JsonDerivedType(typeof(ExitingMessage), "exiting")]
[JsonDerivedType(typeof(ReplyMessage), "reply")]
[JsonDerivedType(typeof(ConnectRgbReplyMessage), "rgbConnectReply")]
[JsonDerivedType(typeof(RgbDevicesReplyMessage), "rgbDevicesReply")]
// client → agent
[JsonDerivedType(typeof(ClientHelloMessage), "clientHello")]
[JsonDerivedType(typeof(SetVisibilityMessage), "visibility")]
[JsonDerivedType(typeof(RefreshNowRequest), "refresh")]
[JsonDerivedType(typeof(ConnectRgbRequest), "rgbConnect")]
[JsonDerivedType(typeof(RgbGetDevicesRequest), "rgbDevices")]
[JsonDerivedType(typeof(RgbCommandRequest), "rgb")]
[JsonDerivedType(typeof(SetFanRequest), "setFan")]
[JsonDerivedType(typeof(SetFanModeRequest), "fanMode")]
[JsonDerivedType(typeof(SetSyncedPercentRequest), "syncedPercent")]
[JsonDerivedType(typeof(SetFanMembershipRequest), "fanMembership")]
[JsonDerivedType(typeof(UpdateSettingRequest), "setting")]
[JsonDerivedType(typeof(SetAutoStartRequest), "autoStart")]
[JsonDerivedType(typeof(ElevateRequest), "elevate")]
[JsonDerivedType(typeof(QuitRequest), "quit")]
public record IpcMessage;

/// <summary>A client request; the agent always answers with a reply carrying the same <see cref="Id"/>.</summary>
public abstract record IpcRequest(int Id) : IpcMessage;

// ───────────── agent → client ─────────────

/// <summary>First message after <see cref="ClientHelloMessage"/>: everything a fresh popover needs (no RGB device list).</summary>
public sealed record HelloMessage(
    int Protocol,
    AgentInfo Info,
    SettingsState Settings,
    HardwareMonitorStatus HardwareStatus,
    RgbStatus RgbStatus,
    HardwareFrame Hardware,
    BatteryState Batteries,
    IReadOnlyList<LowBatteryToast> PendingToasts) : IpcMessage;

public sealed record InfoMessage(AgentInfo Info) : IpcMessage;
public sealed record SettingsMessage(SettingsState Settings) : IpcMessage;
public sealed record HardwareStatusMessage(HardwareMonitorStatus Status) : IpcMessage;
public sealed record RgbStatusMessage(RgbStatus Status) : IpcMessage;
/// <summary>Sent only while the client reported itself visible; latest-only (an unsent frame is replaced).</summary>
public sealed record HardwareMessage(HardwareFrame Frame) : IpcMessage;
public sealed record BatteriesMessage(BatteryState Batteries) : IpcMessage;
public sealed record RgbDevicesChangedMessage : IpcMessage;
public sealed record LowBatteryMessage(LowBatteryToast Toast) : IpcMessage;
/// <summary>The tray icon was clicked while this client is connected: show the popover, or toggle it.</summary>
public sealed record ShowPopoverMessage(bool Toggle, string? Tab) : IpcMessage;
public sealed record ExitingMessage(AgentExitReason Reason) : IpcMessage;

public sealed record ReplyMessage(int Id, bool Ok, string? Error) : IpcMessage;
public sealed record ConnectRgbReplyMessage(int Id, bool Ok, string? Error, RgbStatus Status) : IpcMessage;
public sealed record RgbDevicesReplyMessage(int Id, bool Ok, string? Error, IReadOnlyList<RgbDevice> Devices) : IpcMessage;

// ───────────── client → agent ─────────────

public sealed record ClientHelloMessage(int Protocol, int ProcessId) : IpcMessage;
/// <summary>The popover became visible (true) or was hidden (false). Not a request: no reply.</summary>
public sealed record SetVisibilityMessage(bool Visible) : IpcMessage;

public sealed record RefreshNowRequest(int Id) : IpcRequest(Id);
public sealed record ConnectRgbRequest(int Id) : IpcRequest(Id);
public sealed record RgbGetDevicesRequest(int Id, bool Refresh) : IpcRequest(Id);
public sealed record RgbCommandRequest(int Id, RgbCommand Command) : IpcRequest(Id);
public sealed record SetFanRequest(int Id, string FanId, double? Percent) : IpcRequest(Id);
public sealed record SetFanModeRequest(int Id, FanMode Mode) : IpcRequest(Id);
public sealed record SetSyncedPercentRequest(int Id, double Percent) : IpcRequest(Id);
public sealed record SetFanMembershipRequest(int Id, string FanId, bool Include) : IpcRequest(Id);
public sealed record UpdateSettingRequest(int Id, SettingsPatch Patch) : IpcRequest(Id);
public sealed record SetAutoStartRequest(int Id, bool Enabled) : IpcRequest(Id);
public sealed record ElevateRequest(int Id, bool ShowUi) : IpcRequest(Id);
public sealed record QuitRequest(int Id) : IpcRequest(Id);
