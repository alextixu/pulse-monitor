# Pulse lite rewrite — implementation spec (work packages)

Read first: `decisions-v2.md` (the contracts; it overrides `architecture.md`), then the Pulse.Core contracts that are
**frozen** (do not edit anything under `src/Pulse.Core` — if you need a change there, stop and report it):

- `src/Pulse.Core/Abstractions/IPulseAgent.cs` — the engine as seen by every client (tray, pipe server, UI). Payload
  records `AgentInfo`, `SettingsState`, `HardwareFrame`, `BatteryState`, `LowBatteryToast`, `CommandResult`,
  `ConnectRgbResult`, `RgbDevicesResult`, `RgbCommand(Kind)`, `AgentExitReason`.
- `src/Pulse.Core/Ipc/IpcMessages.cs`, `IpcJsonContext.cs` (source-generated JSON for messages, AppSettings, PulseState;
  `IpcJsonContext.FileOptions` = indented), `IpcCodec.cs` (`IpcProtocol.PipeName`, `Version`, length-prefixed framing).
- `src/Pulse.Core/Models/SettingsPatch.cs` (single-property settings change; `ApplyTo` sanitises), `PulseState.cs`
  (state.json model), `AppSettings` (deep `Clone()`, `Sanitize()`, new `RestoreLightingOnStartup`), `RgbDevice.Key` /
  `HasDefault`, `BatteryDevice.LastConnectedAt`.
- `src/Pulse.Core/Abstractions/IStateStore.cs` (+ `InMemoryStateStore`), `IRgbServerLauncher.cs` (+ `RgbLaunchResult`,
  `NullRgbServerLauncher`), `IDeviceChangeAware.cs`, `IHardwareMonitor.GetSnapshot` (synchronous).
- `src/Pulse.Core/Tray/TrayModels.cs` — `TraySummary(.From)`, `BatterySummary`, `TrayIconContent`, `TrayTone`,
  `TrayGlyphStyle`, `TrayContent.Build/BuildTooltip/ColorOf` (pure tray rules).
- `src/Pulse.Core/Localization/Strings.cs` — every user-facing zh-TW string (namespace `Pulse.Core.Localization`).
- `src/Pulse.Core/FanControl/*` — unchanged fan logic; `FanPresenceTracker.Seed/Export` added.

Public API skeletons that other packages compile against (keep these signatures; you may add members):
`src/Pulse.Engine/AgentCore.cs` (`AgentCoreOptions`, `AgentCore` incl. host-facing members), `src/Pulse.Engine/Ipc/IpcServer.cs`,
`src/Pulse.Engine/Infrastructure/JsonStateStore.cs`. Already moved into Pulse.Engine: `FileLoggerProvider`, `AppLogging`
(`AppLogging.CreateFactory`), `JsonSettingsStore`, `Demo/*` (Demo providers, `DemoSettingsStore`, `DemoElevationService`,
`DemoAutoStartService`). Old Avalonia-coupled sources to port live in `src/Pulse.Engine/_port/` (excluded from compile).

## Rules for every package

- Build only your own project with `dotnet build <your csproj> -c Debug -m:1 -nodeReuse:false --no-dependencies`
  (Pulse.Core and the other libraries are already built; other agents are building their projects at the same time).
  If a build fails with "being used by another process" / file lock, wait 20 s and retry (up to 3 times).
- Never touch the running user's Pulse (`publish\win-x64\Pulse.exe`, scheduled task "Pulse"): no `--quit`, no kill, no
  launch, no publish into `publish\win-x64`. Never read/write the user's real `%APPDATA%`/`%LOCALAPPDATA%` Pulse files
  except read-only through `\\localhost\C$\Users\alex\AppData\...` when you need examples.
- Nullable warnings are errors. Keep comments in the style of the surrounding code (English, explain why). User-facing
  text: Traditional Chinese, add new strings to a `Strings` partial in your own project if Pulse.Core lacks them (Core is
  frozen) — prefer existing `Strings` members.
- Do not commit. Report: files added/changed, build result, test result, deviations from this spec, open issues.

## E1 — Pulse.Engine (the engine) — `src/Pulse.Engine`, tests in a NEW `tests/Pulse.Engine.Tests`

Implement `AgentCore`, `IpcServer`, `JsonStateStore` and everything behind them. Port from `_port/` (MonitoringService,
FanModeService, SettingsService, TelemetryLogger) then delete `_port/` and its csproj entries.

Threading (decisions-v2 §3): one **engine thread** (`EngineScheduler`: dedicated thread draining a `Channel<Action>`,
`Post`, `InvokeAsync<T>`, `CheckAccess`, every action wrapped in try/catch + LogError) owns all mutable state and raises
every IPulseAgent/AgentCore event. A dedicated **`Pulse.Hardware` thread** runs the loop: wait for the period (1 s while
`IsUiVisible`, else 2 s; wake early on a command) → `IHardwareMonitor.GetSnapshot()` → `FanPresenceTracker.Observe` →
`FanModeController.UpdateAsync(...).GetAwaiter().GetResult()` with the cached `FanControlSettings` (rebuilt only when
settings or HardwareStatus change) → Post the `HardwareFrame` to the engine thread (publish, telemetry enqueue, tray
summary, low-battery check is battery-side). Manual `SetFanAsync` and the fan commands are queued to the hardware thread
and executed there before the next poll (all fan writes stay on one thread). Watchdog: no successful snapshot for 30 s →
HardwareStatus Failed (message) so FanModeResult goes Unavailable; recover when snapshots resume. Battery loop: async on
the pool, every `BatteryRefreshSeconds` (clamp 5..86400), providers in parallel with the 20 s timeout per provider,
results posted to the engine thread. `SetUiVisible(true)` triggers a battery refresh if the last one is > 5 s old.

Shutdown (decisions-v2 §1): `ShutdownAsync` = Stop controller → wait hardware thread (≤ 3 s) → `IHardwareMonitor.Dispose()`
(on the calling thread, never skipped) → raise `Exiting(reason)` → stop battery loop (≤ 2 s) → telemetry flush (≤ 2 s) →
`IRgbController.Dispose` → `IRgbServerLauncher.Dispose` → settings + state flush → done. `RestoreFansOnAbnormalExit`:
`controller.Stop(); hardware.Dispose();` synchronously, nothing else, idempotent, no-op after ShutdownAsync.
`RequestQuit()` raises `ShutdownRequested(Quit)`; `ElevateAsync(showUi)` calls `options.RelaunchElevated(showUi)` and on
success raises `ShutdownRequested(Relaunch)`; failure → `CommandResult.Fail(Strings.StatusElevationFailed)`.

Settings (§17): the engine owns the live `AppSettings`; `Settings` returns `SettingsState(clone, Revision, Changed)`.
`UpdateSettingAsync(patch)` → `patch.ApplyTo(live)`; error → Fail. Fan commands are the only writers of FanMode /
SyncedFanPercent (round, clamp 0..100, ignore < 0.5 change) / FanGroupExcluded/IncludedIds (SetMembership semantics of
the old FanModeService). Every change: Revision++, debounced save 500 ms (serialise on the engine thread, write
off-thread), broadcast `SettingsChanged`, re-derive tray summary when HiddenDeviceIds/TrayIconMode change, re-evaluate
fans when fan fields change, restart timers when cadence fields change. First run: save defaults when the file is missing
(not demo). JsonSettingsStore: switch to `IpcJsonContext` (`AppSettings` type info for read, `FileOptions` for write),
`AppSettings.Sanitize()`. `SetAutoStartAsync` → `IAutoStartService.SetEnabled`, then re-read and update
`StartWithSystem` + Info. Reconcile StartWithSystem with `IAutoStartService.IsEnabled` at start.

Hardware/fans: same behaviour as `_port/FanModeService.cs` + `MonitoringService` (FanControlSettings.From(settings,
HardwareStatus), ControlAvailable = (Ready|Degraded) && FanControlAvailable). Manual SetFan validation (§4): reject
unless FanMode == Individual, fan exists & CanControl, not in OrphanedFanIds, its last status is not StandIn /
NotControllable, control available; messages in zh-TW. After a successful manual write run an evaluation.

Memory (§5, §19–22) — `state.json` via IStateStore (debounced 2 s, flushed on shutdown):
- **Batteries** (`BatteryMemory`): merge provider results with remembered devices field-wise; a provider entry with
  IsConnected=true replaces the memory and sets LastConnectedAt=now; IsConnected=false keeps the remembered
  Percent/Voltage when the provider's are null. A device missing from a poll or flipping to disconnected stays shown as
  connected with Status Unknown and Detail 「休眠中」 until 3 consecutive polls (hysteresis), then becomes disconnected with
  Detail 「上次 HH:mm」 (today) / 「上次 M/d HH:mm」 using LastConnectedAt (BatteryDevice.LastConnectedAt from the provider wins
  when set). Remembered devices are published immediately at start as disconnected. Expire entries not reported for 30
  days. Sorting/visible/lowest exactly as today (connected first, lowest %, name OrdinalIgnoreCase); `BatteryState`.
- **Low battery** (`LowBatteryWatcher`): same rule as old MainViewModel (NotifyOnLowBattery, connected, Percent ≤
  threshold, not hidden, not Charging/Full); once per device until it charges or rises ≥ 5 % above the threshold
  (persisted set). Toast text `Strings.LowBatteryTitle` / `string.Format(Strings.LowBatteryToastFormat, name, pct)`.
  If a UI is visible → raise `LowBattery`; else → raise `NotificationRequested` and queue it for `TakePendingToasts`.
- **Fans** (`FanMemory`): seed `FanPresenceTracker` from `PulseState.Fans` only when `FanBootTime` matches the current
  boot (UtcNow − TickCount64, 2-minute tolerance); never record a fan that is manual, orphaned or in the recovery journal
  at capture time (use snapshot `IsManual` and HardwareStatus.OrphanedFanIds); export after the first 5 polls and then on
  change.
- **Lighting** (`LightingMemory`): every successful RGB command records an intent by device Key (SetColor → Color,
  SetZoneColor → Zones (merge zone, clear device colour), SetMode → Mode{name, colour if SupportsColor, speed if
  SupportsSpeed, brightness if SupportsBrightness}, TurnOff → Off, RestoreDefault → Default, Forget → remove; Key null =
  every device). Keys containing a `#n` duplicate suffix are not remembered. When `RestoreLightingOnStartup`: during the 2
  minutes after the first successful connect of this process, apply each intent once per Key (Off → TurnOff, Color →
  SetDeviceColor, Mode → SetMode with the index resolved by name, Default → RestoreDefault, Zones → per-zone colour),
  re-reading devices every 10 s in that window for late devices; then raise RgbDevicesChanged. Never re-apply after a
  reconnect.

RGB orchestration (`RgbService`): at start, if AutoConnectOpenRgb: `UseBundledOpenRgb` → `launcher.EnsureRunningAsync`
(remember the first result: AlreadyRunning ⇒ never launch the bundled server later in this session), `Configure`,
`ConnectAsync`. After a Started launch watch the device count every 2 s for 30 s (raise RgbDevicesChanged on change).
Reconnect with back-off 5 s, 15 s, 60 s, then every 5 min after the status goes Error/Disconnected unexpectedly;
log once per state change. Commands: resolve `RgbCommand.Key` → current index under the device list (refresh once if
missing); absent → `RgbDevicesResult.Failed("裝置已變更，請重新整理")`; map Kind to IRgbController calls (ModeName → index;
fall back to ModeIndex), Key null for SetColor/TurnOff/RestoreDefault → SetAll/TurnOffAll/RestoreAllDefaults; reply with
`GetDevicesAsync(refresh: false)` afterwards; raise RgbDevicesChanged. `ConnectRgbAsync` reply carries the status.
`DemoRgbController` must fill `Key` (e.g. Name|demo|index) and `HasDefault`.

Telemetry: port `_port/TelemetryLogger.cs` unchanged in format (columns, rotation, BOM, sharing); one row per published
frame while TelemetryEnabled; pre-index fans/temps/drives per row instead of FirstOrDefault per cell; directory from
options (null = off).

Pipe server (`IpcServer`, §15–16): accept loop creating a new server stream per client via the injected factory
(`PipeTransmissionMode.Byte`, `PipeOptions.Asynchronous` — the host supplies them); per client: read loop
(`IpcCodec.ReadAsync`), single writer task with a latest-only slot for Hardware/Batteries frames and a FIFO for the rest;
disconnect a client whose FIFO has not drained for 5 s. On `ClientHelloMessage` with the wrong protocol reply
Reply(0,false,"protocol") and close; else send Hello (PendingToasts = `TakePendingToasts()` only if the client is visible
— otherwise when it first becomes visible). Requests → AgentCore methods (each as its own task) → Reply /
ConnectRgbReply / RgbDevicesReply. SetVisibility → per-client flag; AgentCore visibility = any client visible
(SetUiVisible on change). Forward events: Settings, Info, HardwareStatus, RgbStatus, Batteries (always), Hardware (only to
visible clients; send the latest frame immediately when a client becomes visible), RgbDevicesChanged, LowBattery (visible
clients), Exiting. `ShowPopoverInClient` = send ShowPopover to the most recent client; false when none. Log connects /
disconnects at Debug.

Tests (`tests/Pulse.Engine.Tests`, xunit, net9.0, refs Pulse.Engine + Pulse.Core; add to Pulse.sln under "tests"): battery
merge + hysteresis + expiry + detail text; low-battery watcher reset rules; fan memory boot scope and capture guard;
lighting intents (zone after off, duplicate keys, apply-once window with a fake IRgbController); settings patch + fan
command ownership + revision; manual SetFan validation; shutdown order with a fake IHardwareMonitor (Dispose reached even
when the hardware thread is stuck); RestoreFansOnAbnormalExit idempotence; IpcServer end-to-end over a real named pipe
with a demo AgentCore (Hello, a request/reply, visibility gating of Hardware frames, Exiting). Demo AgentCore never
writes files (assert on a temp dir).

## E2 — Pulse.Rgb identity — `src/Pulse.Rgb`, tests in `tests/Pulse.Core.Tests/Rgb`

In `OpenRgbController`: populate `RgbDevice.Key` (normalised key) and `HasDefault` in `Map`; normalise HID locations by
stripping the Windows instance segment (keep `VID_xxxx&PID_xxxx&MI_xx` and the serial; example live keys in
`\\localhost\C$\Users\alex\AppData\Roaming\Pulse\rgb-defaults.json` — two "ASUS ROG Falchion (Wireless)" entries differ
only in `8&1b8da785&0&0000` vs `8&1ba891e3&0&0000`); keep the `#n` duplicate suffix rule. Migrate stored defaults: when a
computed key is unknown but exactly one stored entry has the same normalised key, or the same Name + Type + LED count, use
that entry (rewrite it under the new key). Do not capture a first-seen default when the device currently shows an Off mode
or a per-LED mode with every LED #000000 (log once; HasDefault stays false). After `SaveCurrentAsDefaultAsync` update the
cached device (`StoreDevice`) so `GetDevicesAsync(refresh:false)` returns HasDefault = true. Keep every OpenRGB 1.0
protocol behaviour exactly (SETCUSTOMMODE, hand-built UPDATEMODE, read-back, Off fallback, watchdog, probe). Golden tests
for key normalisation (both Falchion keys → one key; I2C DRAM / GPU / motherboard keys unchanged), migration, capture
guard, HasDefault after save (use a fake / the existing test seams; if OpenRgbController cannot be tested without a
server, extract the pure key/migration logic into an internal static class and test that). Build: `src/Pulse.Rgb` then
`tests/Pulse.Core.Tests` (only you build that test project).

## D1 — Pulse.Agent executable + Win32 tray — NEW `src/Pulse.Agent`; also `src/Pulse.Windows`, `src/Pulse.Logitech`

`Pulse.Agent.csproj`: net9.0-windows, OutputType WinExe, **AssemblyName Pulse**, RootNamespace Pulse.Agent,
ApplicationManifest (copy `src/Pulse.App/app.manifest`, identity name Pulse.Agent, keep asInvoker/longPathAware,
PerMonitorV2), `AllowUnsafeBlocks`, references Pulse.Engine, Pulse.Hardware, Pulse.Rgb, Pulse.Logitech, Pulse.Asus,
Pulse.Windows; the OpenRGB bundle copy block from `src/Pulse.App/Pulse.App.csproj` (third_party\OpenRGB → OpenRGB\ + NOTICE
+ GPL text); runtime knobs: InvariantGlobalization, ConcurrentGarbageCollection=false, TieredPGO=false,
EventSourceSupport=false, MetadataUpdaterSupport=false, `System.GC.ConserveMemory=3` (runtimeconfig.template.json),
PublishReadyToRun for publish, DebugType embedded. Add to Pulse.sln. No Avalonia, no System.Drawing.

- `AgentOptions`: `--minimized`, `--elevated-relaunch`, `--quit`, `--show-ui`, `--tab <name>`; unknown ignored.
- `SingleInstance` (move `src/Pulse.App/Services/SingleInstance.cs` here, namespace Pulse.Agent): same Global\ names,
  ACLs and `--quit` protocol; `RequestQuit` timeout 20 s; after the agent released the mutex also wait ≤ 3 s for any
  `Pulse.UI` process to exit (it exits on the agent's Exiting message).
- `Program.Main` ([STAThread]): options → `FileLoggerProvider(%LOCALAPPDATA%\Pulse\logs\app.log)` +
  `AppLogging.CreateFactory` → `--quit` → single instance (10 s wait on --elevated-relaunch; second instance signals Show
  and exits 0) → load settings (`JsonSettingsStore`) → RequestElevationOnStartup relaunch (WindowsElevationService,
  forwards args + `--elevated-relaunch`; skip when already elevated / --elevated-relaunch) → crash hooks
  (UnhandledException terminating / ProcessExit → `agent.RestoreFansOnAbnormalExit`) → tray window + icon (static glyph)
  → compose providers (`WindowsSystemBatteryProvider`, `WindowsBluetoothBatteryProvider`, `LogitechHidppBatteryProvider`,
  `AsusRogBatteryProvider`, `LibreHardwareMonitorService`, `OpenRgbController`, `OpenRgbServerLauncher`,
  `WindowsElevationService`, autostart service) into `AgentCoreOptions` (TelemetryDirectory
  `%LOCALAPPDATA%\Pulse\logs`, StateStore `JsonStateStore(JsonStateStore.DefaultPath)`, Version from the assembly
  informational version, RelaunchElevated = elevation relaunch with `--show-ui` when asked) → `IpcServer` with the pipe
  factory → `agent.StartAsync()` → message loop. `ShutdownRequested` / tray 結束 / Global quit event → leave the loop →
  `await agent.ShutdownAsync(reason)` → dispose IPC server, tray → release SingleInstance → exit 0. Without `--minimized`
  (or with `--show-ui`) open the UI once the pipe is listening. Log startup deltas from `Process.StartTime` (Main, tray
  added, first snapshot via HardwareUpdated).
- Pipe factory: `NamedPipeServerStreamAcl.Create(IpcProtocol.PipeName, InOut, maxInstances 8, Byte, Asynchronous, 0, 0,
  security)` with PipeSecurity: current user FullControl + Authenticated Users ReadWrite|Synchronize; best effort add the
  medium-integrity mandatory label (SDDL `S:(ML;;NW;;;ME)` via `SetSecurityDescriptorSddlForm` on the existing
  descriptor) inside try/catch (log once at Debug if unsupported).
- Tray (port `docs/plans/2026-10-lite-rewrite/spikes/tray/Program.cs`, read its REPORT.md "Gotchas"): `TrayWindow`
  (hidden top-level window, `[UnmanagedCallersOnly]` WndProc that catches everything, `GetMessage` loop on the main thread,
  thread-safe `Post(Action)` via a queue + PostMessage(WM_APP+n), TaskbarCreated re-add, WM_QUERYENDSESSION →
  ShutdownBlockReasonCreate(「正在把風扇交回 BIOS…」) + start `ShutdownAsync(Quit)` on a pool thread, return TRUE;
  WM_ENDSESSION(TRUE) → join (≤ 6 s) + ShutdownBlockReasonDestroy; WM_DEVICECHANGE(DBT_DEVNODES_CHANGED, debounced 2 s)
  → `agent.NotifyDevicesChanged()`), `NotifyIcon` (Shell_NotifyIconW, NOTIFYICON_VERSION_4, NIF_SHOWTIP, uID 1, tooltip ≤
  127, balloon NIF_INFO with NIIF_WARNING for `NotificationRequested`), `GdiTrayIconRenderer` (pure GDI port of the spike's
  `--gdi` renderer: ring / round-cap arc / centred bold digits (Segoe UI Bold, 0.5×S or 0.36×S for 3 chars,
  ANTIALIASED_QUALITY) and the battery glyph with bolt, colours from `TrayContent.ColorOf`/`Track`, rendered at 32 px,
  CreateIconIndirect; `DestroyIcon` the previous HICON after NIM_MODIFY; never throw: fall back to the static glyph, then a
  blank icon), `TrayController` (subscribes `agent.SummaryChanged` + `SettingsChanged`(TrayIconMode) → 400 ms coalesce →
  `TrayContent.Build` / `BuildTooltip` → update only on change), menu via TrackPopupMenuEx (SetForegroundWindow before,
  WM_NULL after) with exactly: 開啟 (`Strings.MenuOpen`) / 重新整理 (`MenuRefresh` → RefreshNowAsync) / 以系統管理員身分重新啟動
  (`MenuElevate`, only when !IsElevated && CanElevate → ElevateAsync(false)) / separator / 設定 (`MenuSettings` → open UI on
  the Settings tab) / separator / 結束 (`MenuQuit` → RequestQuit).
- `UiLauncher`: left click (NIN_SELECT / WM_LBUTTONUP) → if `agent.LastUiHiddenAt` is < 350 ms ago do nothing (toggle
  close); else if `agent.ShowPopoverInClient(true, tab)` returns true done; else start `<agent dir>\Pulse.UI.exe --popover
  [--tab X]` (UseShellExecute=false, inherits the token) with a 3 s guard against double launches; log a warning when the
  exe is missing.
- `src/Pulse.Windows`: make `WindowsAutoStartService` task-aware (decisions-v2 §23): IsEnabled = Run value present OR
  scheduled task "Pulse" exists (`schtasks /query /tn Pulse` exit code); when the task exists, `SetEnabled(true)` removes
  the Run value (no double launch) and `SetEnabled(false)` deletes the task (`schtasks /delete /tn Pulse /f`) and the Run
  value; expose `IsViaTask`. Keep the test-seam constructor.
- `src/Pulse.Logitech`: `LogitechHidppBatteryProvider` implements `IDeviceChangeAware` → `RetrySoon()` on every backing-off
  interface (and re-enumerate on the next poll).
- No tests required beyond a renderer smoke test if cheap (render all tones/3-digit text to HICON and destroy); build must
  succeed. You cannot run the agent end-to-end until E1 lands (AgentCore throws NotImplemented) — make sure it compiles.

## U1 — Pulse.UI (the popover) — `src/Pulse.App` (folder/namespace stay `Pulse.App`, AssemblyName becomes `Pulse.UI`)

Goal: the Avalonia app becomes a thin client of `IPulseAgent`. Remove project references to Pulse.Hardware, Pulse.Logitech,
Pulse.Asus, Pulse.Rgb, Pulse.Windows; reference Pulse.Core and Pulse.Engine (only for the in-process demo agent and
FileLoggerProvider). Change TargetFramework to `net9.0-windows` (drops the 24 MB WinRT projection; remove
`SupportedOSPlatformVersion` if NETSDK1135 complains) and add PublishReadyToRun. Delete the OpenRGB bundle copy block
(the agent owns it), `Services/TrayService.cs`, `Services/TrayIconRenderer.cs`, `Services/SingleInstance.cs` (D1 moves it —
do not delete it before D1 copied it: leave it and remove it from compile with `<Compile Remove>` instead), the Skia usage.

- `Services/IpcPulseClient` : `IPulseAgent` over the pipe (`NamedPipeClientStream(".", IpcProtocol.PipeName, InOut,
  Asynchronous)`; connect retry for 10 s; send `ClientHelloMessage`; wait for Hello; one writer; read loop; request ids;
  per-request timeouts (default 10 s, ConnectRgb 45 s, RefreshNow 30 s, SetFan 15 s) → `CommandResult.Fail(Strings.…)`;
  extra event `ShowPopoverRequested(bool toggle, string? tab)`; Exiting / pipe broken → raise `Exiting`). Protocol mismatch
  → show 「Pulse 已更新，請重新開啟」 and exit.
- `Services/AgentConnection` (ObservableObject, UI thread): wraps an `IPulseAgent` (IPC or the demo `AgentCore`),
  marshals every event to `Dispatcher.UIThread`, exposes observable `Info`, `Settings` (a mutable **copy** of
  AppSettings for bindings; replaced on every SettingsChanged with a newer Revision), `HardwareStatus`, `RgbStatus`,
  `Hardware` (frame), `Batteries`, and the command methods. This replaces MonitoringService / FanModeService /
  SettingsService / IHardwareMonitor / IRgbController / IElevationService / IAutoStartService in every view model.
- View models (keep public member names — compiled bindings use them): Settings setters send
  `SettingsPatch.Of(nameof(...), value)` via UpdateSettingAsync (keep the UI-side clamps); StartWithSystem →
  SetAutoStartAsync (revert on failure); Devices hide/show → patch HiddenDeviceIds; Lighting uses `RgbCommand` with
  `device.Key` and reads `device.HasDefault` (no synchronous HasDefault), loads devices lazily, reloads on
  RgbDevicesChanged; Fans: SetFanModeAsync / SetSyncedPercentAsync / SetFanMembershipAsync / SetFanAsync (revert IsManual on
  failure), presence from `FanModeResult.Fans[id].Presence`, add a `_syncingMode` guard so a settings broadcast never echoes
  back as SetFanMode; Elevate → ElevateAsync(showUi: true); Quit (popover 結束) → RequestQuit; low-battery toasts come from
  the agent's LowBattery event and Hello's pending toasts (no detection in the UI); elevation / autostart / paths / bundled
  flags come from `Info` and must raise PropertyChanged when Info changes. Drop the IsRefreshing* status mix (keep
  `MainViewModel.IsRefreshing` for the user's own refresh). Hidden-window per-tick work disappears (the agent only streams
  hardware while visible).
- Lifecycle (`Program`/`App`): `Pulse.UI.exe [--popover] [--tab <name>] [--demo] [--screenshot <dir>]`; UI log
  `%LOCALAPPDATA%\Pulse\logs\ui.log`; single instance `Local\Pulse.UI` with the SingleInstance ACL pattern (second launch
  signals the first to show and exits; skipped in demo/screenshot); show the popover immediately (theme from a small
  `%LOCALAPPDATA%\Pulse\ui-state.json` cache {Theme, LastTab}) and fill it when Hello arrives (VMs must tolerate defaults
  before Hello); `SetVisibility(true)` on show; when the popover hides (deactivate / Escape / toggle): flush armed
  debounce timers (≤ 1 s for their replies), `SetVisibility(false)`, write ui-state.json, then shut the process down (the
  user chose "exit immediately"). Agent `Exiting` → exit. `ShowPopoverRequested(toggle)` while visible → hide (→ exit).
  Demo/screenshot: in-process `AgentCore` built with the Demo providers from Pulse.Engine.Demo, `DemoSettingsStore`,
  `InMemoryStateStore`, `TelemetryDirectory = null`, `IsDemo = true` — compile against it now; it throws
  NotImplementedException until E1 lands, so run screenshot verification only after E1 reports done. `ScreenshotService`
  keeps working through AgentConnection (fan modes via SetFanModeAsync).
- Remove now-dead code the UI map flagged (unused converters / strings are optional; do not touch Pulse.Core/Strings).
- Build `src/Pulse.App` with `--no-dependencies` (Pulse.Engine skeleton is built). Report the member-level change map.
