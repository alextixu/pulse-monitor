# Pulse "lite" architecture — design for the lightweight rewrite

Repo: E:\test_think\batterycolorcontrol (branch main). Baseline measured 2026-10-21 on the live install:
resident Pulse.exe = 174 MB working set / 206 MB private, 80 threads, ~1040 handles, 6–9 % of one core
continuously (≈89 ms CPU per 1 Hz tick) with the popover hidden; 15 s from launch to tray icon.
Publish folder 105 MB (24 MB CsWinRT projection used by one Bluetooth provider, 14 MB Avalonia, 11 MB Skia,
49 MB bundled OpenRGB+Qt).

Goal (user request, verbatim intent): make the whole thing as light as possible, rewriting the architecture if
needed, and it must have a "memory" feature (記憶功能): everything the user set up — settings, lighting state,
fan setup, known devices — must survive restarts and be restored automatically.

Targets: resident process < 60 MB working set, < 1 % of one core at 1 Hz telemetry, < 20 threads, tray icon
visible < 2 s after launch; UI opens < 1 s after a tray click (warm) / < 2 s (cold).

## 1. Decision: two processes

| Process | Exe | Runtime footprint | Lifetime |
|---|---|---|---|
| **Agent** | `publish\win-x64\Pulse.exe` (AssemblyName `Pulse`, WinExe, no Avalonia) | .NET + LHM + HidSharp + OpenRGB.NET + System.Drawing (tray glyph) | resident; started by the scheduled task "Pulse" with `--minimized`, elevated |
| **UI** | `publish\win-x64\Pulse.UI.exe` (AssemblyName `Pulse.UI`, Avalonia) | Avalonia + Skia | on demand: started by the agent on tray click; exits `UiIdleExitMinutes` (default 5) after the popover is hidden |

Why not single-process lazy UI: once Avalonia is initialised it cannot be unloaded, so after the first click the
process is back to ~150 MB for the rest of the day. Two processes keep the resident cost constant.

Rejected: in-house OpenRGB client (saves one idle thread; protocol decoder is the main regression risk for a
feature the user relies on daily — deferred), NativeAOT (System.Management/WMI + CsWinRT block it), trimming
(framework-dependent publish; the win is memory, not disk).

Everything the user sees stays the same: same tabs, same cards, same strings, same settings file, same
`Pulse.exe --quit`, same scheduled task, same `tools/install-pulse.ps1`.

## 2. Projects

```
src/Pulse.Core        (net9.0)           models, contracts, FanControl, NEW: Ipc/ (messages + JSON source-gen context)
src/Pulse.Hardware    (net9.0)           unchanged except: Controller/PSU groups opt-in, Memory group throttled to 5 s,
                                         WMI disk query runs concurrently with Computer.Open
src/Pulse.Rgb         (net9.0)           unchanged except: RgbDevice.Key + RgbDevice.HasDefault populated, bounded auto-reconnect
src/Pulse.Logitech    (net9.0)           unchanged except: per-interface back-off after open/write failure (G HUB holds the receiver)
src/Pulse.Asus        (net9.0)           unchanged (keep the endpoint open between polls if cheap; else unchanged)
src/Pulse.Windows     (net9.0-windows)   Bluetooth provider rewritten on CfgMgr32 (drops the 10.0.19041 TFM + 24 MB WinSDK);
                                         autostart becomes task-aware; elevation unchanged
src/Pulse.Engine      (net9.0)  NEW      headless orchestration: EngineScheduler, SettingsService, MonitoringService,
                                         FanModeService, TelemetryLogger, StateStore (記憶), LightingMemory, LowBatteryWatcher,
                                         AgentCore (the API), IpcServer, FileLoggerProvider, AppLogging, SingleInstance,
                                         JsonSettingsStore, AgentOptions. No Avalonia, no CommunityToolkit, no DI container.
src/Pulse.Agent       (net9.0-windows)  NEW  exe "Pulse": Program, Win32 tray (TrayWindow + NotifyIcon + GdiTrayIconRenderer),
                                         UiLauncher, composition of providers, OpenRGB bundle copy block, app.manifest.
src/Pulse.App         (net9.0-windows)  exe "Pulse.UI": views unchanged; view models re-pointed at IPulseClient;
                                         IpcPulseClient (pipe) + InProcPulseClient (demo/screenshot hosts Pulse.Engine
                                         in-process with the Demo providers). Loses every hardware/HID/RGB reference.
tests/Pulse.Core.Tests (xunit) NEW       fan-safety invariants, Quiet curve, tray content rules, tooltip, settings
                                         sanitiser, IPC round-trips, OpenRGB UPDATEMODE golden packets, StateStore merge.
```

Build flags. Agent: `PublishReadyToRun`, `InvariantGlobalization=true`, `ConcurrentGarbageCollection=false`,
`TieredPGO=false`, `System.GC.ConserveMemory=3` (runtimeconfig), `DebugType=embedded`. UI: `PublishReadyToRun`.
Both stay framework-dependent win-x64. Avalonia.Desktop stays (cross-platform later), Inter font and ColorPicker
stay (UI-only cost).

## 3. Engine threading model

`EngineScheduler`: one dedicated thread ("Pulse.Engine") draining a `Channel<Action>`; `Post(Action)`,
`InvokeAsync(Func<Task>)`, `CheckAccess()`. It replaces every `Dispatcher.UIThread` use in MonitoringService,
FanModeService, SettingsService. Invariant kept: all engine state (LatestHardware, batteries, settings object,
FanPresenceTracker, summary) is mutated and all engine events are raised on this thread. Blocking hardware calls
(LHM Accept, HID polls, OpenRGB) run on the thread pool as today; results are posted back. The 1 Hz hardware loop
calls LHM directly from its own loop task (no Task.Run inside Task.Run).

## 4. AgentCore API (shared by IPC and by the in-proc demo client)

```csharp
public sealed class AgentCore : IAsyncDisposable
{
    // snapshots (safe to read from any thread: volatile references to immutable records / copies)
    AppSettings Settings; HardwareMonitorStatus HardwareStatus; RgbStatus RgbStatus; HardwareSnapshot LatestHardware;
    IReadOnlyList<BatteryDevice> LatestBatteries; string? LastBatteryError; DateTimeOffset? LastBatteryUpdate;
    FanModeResult FanResult; IReadOnlyList<RgbDevice> RgbDevices; TraySummary Summary; MemoryState Memory;
    bool IsElevated; bool CanElevate; AutoStartInfo AutoStart; AgentPaths Paths; bool OpenRgbBundled;

    // events — raised on the engine thread
    event HardwareUpdated(HardwareSnapshot, FanModeResult); BatteriesUpdated(devices, error, at);
    HardwareStatusChanged; RgbStatusChanged; RgbDevicesChanged(devices); FanResultChanged; SettingsChanged(AppSettings, string? name);
    SummaryChanged(TraySummary); LowBattery(BatteryDevice); ShowPopoverRequested(string? tab, bool toggle); Exiting;

    // commands (thread-safe)
    Task StartAsync(); Task ShutdownAsync(TimeSpan budget);
    Task RefreshNowAsync(); void NotifyPopoverOpened();
    Task<bool> ConnectRgbAsync(); Task<IReadOnlyList<RgbDevice>> GetRgbDevicesAsync(bool refresh);
    Task RgbSetDeviceColorAsync(int index, RgbColor c); Task RgbSetZoneColorAsync(int index, int zone, RgbColor c);
    Task RgbSetModeAsync(int index, int mode, RgbColor? c, int? speed, int? brightness);
    Task RgbSetAllAsync(RgbColor c); Task RgbTurnOffAsync(int? index); Task RgbRestoreDefaultAsync(int? index);
    Task RgbSaveDefaultAsync(int index);
    Task SetFanAsync(string fanId, double? percent); void SetFanMode(FanMode m); void SetSyncedPercent(double p);
    void SetFanMembership(string fanId, bool include);
    void UpdateSettings(AppSettings settings, string? changed);   // sanitised, applied live, saved (500 ms debounce), broadcast
    Task<bool> SetAutoStartAsync(bool enabled); bool RelaunchElevated(); void RequestQuit(); void RequestShowPopover(string? tab, bool toggle);
}
```

Composition (agent Program, by hand — no Microsoft.Extensions.DependencyInjection):
providers (Windows BT, System, Logitech, ASUS) + LibreHardwareMonitorService + OpenRgbController + OpenRgbServerLauncher
+ WindowsElevationService + WindowsAutoStartService → `new AgentCore(...)`. Demo mode (UI process): the same
AgentCore with Demo providers and a `DemoSettingsStore` (never writes) and telemetry off.

Shutdown order (clean exit, --quit, unhandled exception, ProcessExit — identical to today, see
App.axaml.cs:92-106/207-233): tray removed → IPC sends `AgentExiting` and closes → FanModeService.StopApplying
→ MonitoringService stop → TelemetryLogger flush → IHardwareMonitor.Dispose (hands fans back / orphans to 100 %)
→ IRgbController.Dispose → OpenRgbServerLauncher.Dispose (kills the bundled OpenRGB) → SettingsService/StateStore
flush → SingleInstance release → exit 0.

## 5. IPC

Transport: one `NamedPipeServerStream` per client, name `Pulse.Agent.<user SID>` (fallback `Pulse.Agent.<user>`),
message mode, UTF-8 newline-delimited JSON, `System.Text.Json` with a source-generated `IpcJsonContext` in
Pulse.Core (polymorphic `IpcMessage` base with `[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]`, enums as
strings, `IReadOnlyList<double?>` etc. are supported by STJ). Max message 4 MB. Protocol version 1 in both hellos.

Security: `PipeSecurity` grants the current user SID FullControl (and, best effort inside try/catch, an SDDL
mandatory label `S:(ML;;NW;;;ME)` so a medium-integrity UI can write to an elevated agent's pipe). The UI is normally
a child of the agent and therefore has the same token; dev launches from a non-elevated shell may fail to connect
and the UI then shows 「請從托盤圖示開啟 Pulse」.

Agent → UI: `Hello` (everything in §4's snapshot block), `Hardware{Snapshot, FanResult}` (only while a client has
`Visible=true`; coalesced: at most one frame per poll, drop-oldest on a 4-deep outbound queue), `Batteries`,
`HardwareStatus`, `RgbStatus`, `RgbDevices`, `Settings{Settings, Changed}`, `LowBattery{Device}`,
`ShowPopover{Tab, Toggle}`, `AgentExiting`, `Reply{Id, Ok, Error}` / `RgbDevicesReply{Id, Devices}`.
UI → Agent: `ClientHello{Protocol, Pid}`, `SetVisibility{Visible}`, `RefreshNow`, `PopoverOpened`, `ConnectRgb`,
`RgbGetDevices{Refresh}`, `RgbSetDeviceColor`, `RgbSetZoneColor`, `RgbSetMode`, `RgbSetAll`, `RgbTurnOff{Index?}`,
`RgbRestoreDefault{Index?}`, `RgbSaveDefault{Index}`, `SetFan{FanId, Percent?}`, `SetFanMode`, `SetSyncedPercent`,
`SetFanMembership`, `UpdateSettings{Settings, Changed}`, `SetAutoStart{Enabled}`, `Elevate`, `Quit`,
`UiHidden` (lets the agent know the popover closed → stop hardware stream). Every request carries `Id`; the agent
always answers with a Reply (errors as text, never exceptions over the wire).

Model changes needed for the wire: `RgbDevice` gains `Key` (stable identity from OpenRgbController) and `HasDefault`
(so the synchronous `IRgbController.HasDefault` round-trip disappears). `FanModeResult` already carries per-fan
`Presence`, so `FanPresenceTracker` never crosses the pipe. Chinese texts produced in the engine layer
(HardwareMonitorStatus.Message, RecoveryNotice, RgbStatus.Message, FanInfo.Group) stay as they are — the UI shows them
verbatim today and there is only one language.

## 6. UI process

`IPulseClient` (events marshalled to `Dispatcher.UIThread`, same shapes as AgentCore) with two implementations:
`IpcPulseClient` (connect with 10 s retry, request/reply correlation, exits the app if the agent goes away for > 15 s
outside demo mode) and `InProcPulseClient` (wraps an in-process AgentCore built with the Demo providers; used by
`--demo` / `--screenshot <dir>` so screenshots and design work need no agent).

View models keep their public member names (compiled bindings reference them) but take `IPulseClient` instead of
MonitoringService/FanModeService/SettingsService/IRgbController/IHardwareMonitor/IElevationService/IAutoStartService.
Settings: the UI holds the last `AppSettings` received; SettingsViewModel mutates that copy and calls
`client.UpdateSettings(copy, name)`; the agent's `Settings` broadcast is the source of truth (UI re-reads it).
Theme follows `Settings.Theme` from the broadcast; the UI also caches the last theme + last tab in
`%LOCALAPPDATA%\Pulse\ui-state.json` so the window opens in the right theme before the pipe answers.

Lifecycle: `Pulse.UI.exe [--popover] [--tab <name>] [--demo] [--screenshot <dir>]`. Own mutex `Local\Pulse.UI`
(second UI signals the first via the pipe-less route: a `Local\Pulse.UI.Show` event). While connected it sends
`SetVisibility(true)` on show and `UiHidden` on hide. After hide, a timer of `Settings.UiIdleExitMinutes`
(new setting, default 5; 0 = exit at once) ends the process with exit code 0. Agent exit → UI exits.
Per-tick work while hidden: none (the agent stops the hardware stream).

Tray-click toggle semantics preserved: the agent sends `ShowPopover{Toggle=true}`; the UI applies the existing
350 ms deactivate guard.

## 7. Agent tray (Win32)

`TrayWindow`: hidden top-level window + `GetMessage` loop on the main thread; `Shell_NotifyIconW`
NIF_ICON|NIF_MESSAGE|NIF_TIP|NIF_SHOWTIP, NOTIFYICON_VERSION_4, re-add on `TaskbarCreated`; left click → `UiLauncher`;
right click → `TrackPopupMenuEx` with exactly today's items: 開啟 / 重新整理 / [以系統管理員身分重新啟動 when
!IsElevated && CanElevate] / — / 設定 / — / 結束. Updates coalesced 400 ms (System.Threading.Timer posting a custom
message). Tooltip = `TrayService.BuildTooltip` rules verbatim (127 chars, cut at a line boundary, "…").
`GdiTrayIconRenderer`: System.Drawing (or raw GDI if the spike shows it is cheaper) implementing the exact ring /
battery glyph geometry and colours of TrayIconRenderer.cs (documented in the services map "mustPreserve"); icons
rendered at `GetSystemMetrics(SM_CXSMICON)`; previous HICON destroyed after `NIM_MODIFY`; tiny LRU of ≤ 32 HICONs by
content. Low-battery balloon via NIF_INFO when no UI is visible (the UI still shows its in-window toast when open).

`UiLauncher`: if a UI client is connected → `ShowPopover{Toggle}` over the pipe; else start
`<agent dir>\Pulse.UI.exe --popover` (inherits the agent's token), 3 s cold-start guard against double launches.
Second agent instance (double-click Pulse.exe) → signals `Global\Pulse.ShowPopover` as today → the running agent
launches/toggles the UI.

## 8. Memory (記憶) — StateStore `%LOCALAPPDATA%\Pulse\state.json`

Owned by the agent, atomic writes (tmp + move), debounced 2 s, flushed on shutdown, corrupt → `.corrupt` + fresh.

```json
{ "Version": 1,
  "Batteries": [ { ...BatteryDevice fields..., "LastSeenAt": "…" } ],          // last reading per device id
  "Lighting": { "<device Key>": { "Kind": "Off|Color|Mode|Default", "Color": "#RRGGBB", "ModeName": "…",
                                   "Speed": 50, "Brightness": 100, "UpdatedAt": "…" } },
  "FanFirstSight": { "<fan id>": { "Percent": 2.0, "Rpm": 0, "EverSpun": false } },
  "LowBatteryNotified": [ "<device id>" ] }
```

Behaviours:
- **Known devices**: at start, remembered batteries are published immediately as `IsConnected=false` with their last
  percent and `Detail = "上次 HH:mm"`. Each poll merges: a device reported by a provider replaces its memory; a
  remembered device not reported stays listed as disconnected with its last value (this also stops the list from
  flapping 2↔3↔4 entries when the Logitech receiver fails). Entries unseen for 30 days are dropped.
- **Lighting**: every successful RGB command records the intent per device Key (TurnOff → Off, SetDeviceColor/SetAll →
  Color, SetMode → Mode by name + speed/brightness, RestoreDefault → Default; zone colours are not remembered).
  After each successful connect and after each `RgbDevicesChanged` the agent re-applies intents to devices that have
  one and have not been applied in this connection (`RestoreLightingOnStartup` setting, default true). The user's
  "all off except the keyboard" therefore survives reboots.
- **Fan first sight**: `FanPresenceTracker` is seeded from memory so pump/hidden-header decisions are stable across
  agent restarts; the uncommitted "only named pumps run flat out" rule is kept.
- **Low-battery notified set**: persisted; an id is removed when the device charges or rises ≥ 5 % above the threshold.
- Existing stores unchanged: `settings.json` (same schema + new keys `UiIdleExitMinutes`, `RestoreLightingOnStartup`,
  `ProbeUsbCoolers`), `rgb-defaults.json`, `fan-recovery.json`.

## 9. Resource rules in the engine

- Hardware cadence unchanged (1 s while TelemetryEnabled — the user asked for per-second telemetry), but per tick:
  LHM Controller/PSU groups off unless `ProbeUsbCoolers`; Memory hardware updated every 5 s; storage every 10 s (as now);
  FanControlSettings/presence snapshots cached until a change; TelemetryLogger pre-indexes the snapshot per row and
  flushes every 5 s; no IPC traffic when no UI is visible; tray update only on content change.
- Logging: Information; persistent `StreamWriter` (flush per line, 2 MiB roll as now); agent → `app.log`,
  UI → `ui.log`; fan duty writes at Debug (hand-off/resume/mode switch stay Information); Logitech open/drop churn
  replaced by exponential back-off per interface (30 s → 1 → 2 → 5 → 15 min) with one Information line per state change.
- OpenRGB: bounded auto-reconnect after watchdog Error (5 s, 15 s, 60 s, then every 5 min; relaunch bundled copy when
  `UseBundledOpenRgb`), logged once per state change.
- Startup order in the agent: tray icon first (static glyph, < 1 s), then settings/state, hardware init
  (WMI disk query concurrent with Computer.Open), providers, RGB connect; the UI is never started at boot.

## 10. Install / update / dev

- Scheduled task and `tools/install-pulse.ps1` unchanged (`Pulse.exe --minimized`).
- `Pulse.exe --quit` now also tells a running UI to exit (and waits ≤ 3 s) so the publish folder can be replaced.
- New `tools/publish.ps1 [-Deploy]`: publishes Agent and UI into `publish\win-x64` (shared DLL set), optional
  `--quit` → publish → `schtasks /run /tn Pulse`.
- `run.ps1` builds both and starts the agent unelevated (tray) or the UI in demo mode (`-Demo`); `run-admin.ps1`
  starts the agent elevated. `--screenshot` keeps working through the in-proc demo client.
- README: architecture section rewritten (two processes, pipe, memory), install unchanged, screenshots unchanged.

## 11. What must not change (contracts)

Fan safety (orphan journal, stand-in curve, hand-off thresholds, SetDefault semantics, dispose ordering, elevation
gate), OpenRGB 1.0 fixes (SETCUSTOMMODE for Direct, hand-built UPDATEMODE with direction rules, read-back verify, Off
fallback), telemetry CSV columns/rotation/encoding, settings.json schema (additive only), `--quit` protocol and the
three Global\ object names, exit codes 0/1/2/3, tray content rules and tooltip format, every user-facing string.
