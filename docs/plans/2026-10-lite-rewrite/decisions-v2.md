# Pulse lite rewrite — v2 decisions (overrides architecture.md where they differ)

Inputs: architecture.md (v1), review/review0..3.md (fan-safety, memory/product, IPC/UI, performance; the
Windows-security lens did not run), and the product owner's answers of 2026-10-27.

## Product decisions (owner)

| Topic | Decision |
|---|---|
| UI lifetime | **Pulse.UI exits as soon as the popover is hidden.** Every open is a cold start, so the UI cold path must be lean (ReadyToRun, window shown before data, no per-LED payloads). Pending debounced writes are flushed (≤ 1 s) before exit. |
| Lighting memory | **Re-apply once per agent start**: during the first 2 minutes after the agent's first successful OpenRGB connect, each remembered device is applied once (late-detected devices included). Never re-applied after a reconnect. |
| Low battery while the panel is closed | **Windows notification** (Shell_NotifyIcon NIF_INFO balloon) **and** the in-panel toast the next time the panel opens (the agent keeps a pending-toast queue, delivered in Hello). |
| Polling cadence | **1 s while the panel is visible, 2 s while hidden.** Telemetry rows follow the actual cadence. Fan evaluation runs every poll (all fan timing is time-based, so 2 s is safe). |

## Engineering decisions (from the review; defaults chosen without asking)

### Fan safety
1. **Two shutdown paths.** Clean path (tray 結束, `--quit`, IPC Quit, WM_ENDSESSION): `FanModeController.Stop()` → `IHardwareMonitor.Dispose()` FIRST (never budget-limited), then IPC `AgentExiting`, telemetry flush, RGB dispose, OpenRGB launcher dispose, settings/state flush, SingleInstance release, exit 0. Abnormal path (UnhandledException IsTerminating, ProcessExit): static, synchronous `controller.Stop(); hardware.Dispose();` on the calling thread — no Post, no pipe, no other I/O. Idempotent via a flag set at the end of the clean path. `--quit` timeout raised to 20 s.
2. **WM_QUERYENDSESSION / WM_ENDSESSION** handled in the tray window: QUERY → `ShutdownBlockReasonCreate(hwnd, "正在把風扇交回 BIOS…")`, start the clean shutdown on a pool thread, return TRUE; ENDSESSION(TRUE) → join it (≤ 6 s), `ShutdownBlockReasonDestroy`.
3. **Threads.** Dedicated `Pulse.Hardware` thread runs LHM Accept + SnapshotBuilder synchronously and then `FanModeController.UpdateAsync` (single-flight, latest wins — same as today). It posts results to the engine thread. A watchdog marks the monitor Failed when no successful poll for 30 s. The engine thread's drain loop wraps each action in try/catch.
4. **Manual fan writes are validated by the agent**: rejected unless FanMode == Individual, the fan is controllable, not orphaned, not StandIn, and control is available. After a successful write the controller is re-evaluated.
5. **Fan first-sight memory is boot-scoped**: stored with the journal's boot identity, discarded on a new boot; never recorded for fans that are manual / orphaned / in the journal at capture time. Purpose: stable pump/hidden-header decisions across agent restarts within one boot only.
6. **Demo isolation**: demo/screenshot AgentCore uses an in-memory state store, no journal path, no telemetry, no IPC server, no single-instance guard.
7. Every `[UnmanagedCallersOnly]` entry point catches everything (an escaping exception is a FailFast that skips the fan hooks).

### Performance
8. **LHM sensor history disabled**: `ValuesTimeWindow = TimeSpan.Zero` for every sensor after Open and on SensorAdded/HardwareAdded (default keeps one day of samples ≈ 3 MB/h). Applied to the current app too.
9. **No WMI**: DiskMediaTypes uses `IOCTL_STORAGE_QUERY_PROPERTY` (StorageDeviceProperty for bus type + product id, StorageDeviceSeekPenaltyProperty for HDD) on `\\.\PhysicalDriveN` opened with zero access rights; runs synchronously before the first Accept; on failure **no** drive is SMART-polled (not all).
10. **DIMM SPD sub-hardware throttled to 10 s** (generic memory load still every poll). Controller/PSU groups stay enabled (journal recovery for USB coolers), revisit after measurement.
11. **Instrumentation**: per-node Accept timing, build/fan/telemetry/IPC/tray timing, CPU per wall second, WS, private, GC heap, allocations, threads, handles, GDI/USER — one Information line every 5 min and a `Stats` IPC message. Startup deltas from Process.StartTime (Main, tray NIM_ADD, first snapshot) and UI (Main, window shown, Hello).
12. **Tray renderer = raw GDI** (spike run 3), no icon cache: render on content change, NIM_MODIFY, DestroyIcon(old). Rendered at 32 px (same look as today).
13. Runtime knobs for the agent: InvariantGlobalization, ConcurrentGC off, TieredPGO off, ConserveMemory 3, EventSourceSupport off, MetadataUpdaterSupport off; the two CurrentCultureIgnoreCase sorts become OrdinalIgnoreCase explicitly. Logger keeps one open stream. Fan duty writes log at Debug.

### Architecture / IPC
14. **Pulse.Engine references only Pulse.Core.** New Core abstractions: `IRgbServerLauncher`, `IStateStore`. `Strings.cs` (no Avalonia dependency) moves to `Pulse.Core/Localization`. TraySummary/BatterySummary and the pure tray content rules move to Pulse.Core. Demo providers move to Pulse.Engine. Pulse.UI therefore carries no LHM/HidSharp/OpenRGB.NET.
15. **IPC framing**: byte-mode named pipe, 4-byte little-endian length prefix + UTF-8 JSON (source-generated context, string enums, `AllowNamedFloatingPointLiterals`, RgbColor as "#RRGGBB", `IgnoreUnrecognizedTypeDiscriminators` with an Unknown fallback). One writer task per client; two outbound lanes: latest-only slots (Hardware, Batteries, Summary, Stats) and a never-dropped FIFO (replies, settings, status, AgentExiting); a client that does not drain for 5 s is disconnected. Each request is dispatched as its own task; replies carry `Ok/Error`. Client timeouts: default 10 s, ConnectRgb 45 s, RefreshNow 30 s, SetFan 15 s. Protocol mismatch → UI shows 「Pulse 已更新，請重新開啟」 and exits.
16. **Hello** carries AgentInfo (IsElevated, CanElevate, AutoStart, paths, OpenRgbBundled, protocol), Settings (+Revision), HardwareStatus, RgbStatus, latest Hardware frame, batteries (all + visible + lowest), FanModeResult, pending low-battery toasts. **Not** RgbDevices: the Lighting tab loads them lazily with `RgbGetDevices`, whose reply is the only message carrying per-LED colours.
17. **Settings**: agent is the only writer. UI sends `UpdateSetting{Name, ValueJson}` patches; FanMode/SyncedFanPercent/FanGroupExcludedIds/FanGroupIncludedIds change only through the fan commands. Broadcast `Settings{Settings, Changed, Revision}`; UI ignores older revisions. `AppSettings.Clone()` becomes deep. Agent recomputes TraySummary/tray icon when HiddenDeviceIds or TrayIconMode change.
18. **RGB identity**: `RgbDevice.Key` (normalised: HID instance segment stripped) and `HasDefault` on the model; old keys in rgb-defaults.json migrate when exactly one stored entry matches Name+Type+LedCount. Never capture a first-seen default while the device shows Off/all-black and an Off intent exists. RGB requests carry Key (index is a hint); unknown Key → `裝置已變更，請重新整理`. Commands that change state reply with the updated RgbDevice.
19. **Lighting intents**: Off / Color / Mode(name, colour, speed, brightness) / Default / Zones{index→colour}. A zone paint converts the intent to Zones (device colour cleared). Per-device 「不要記住這個裝置」 and a Lighting-tab switch 「啟動時還原燈光」 (RestoreLightingOnStartup). Duplicate keys with `#n` suffix are not remembered.
20. **OpenRGB reconnect**: bounded back-off after watchdog Error; relaunch the bundled server only if Pulse launched it this session (AlreadyRunning at start → reconnect only).
21. **Battery memory**: field-wise merge (a disconnected provider report keeps the remembered Percent), `LastConnectedAt` (from DEVPKEY_Bluetooth_LastConnectedTime for Bluetooth) and `LastReportedAt` (30-day expiry), connection hysteresis 90 s (a flip to disconnected/missing shows 休眠中 until 3 consecutive polls). Detail for remembered devices: 「上次 HH:mm」 today, 「上次 M/d HH:mm」 otherwise. Logitech back-off capped at 2 min and reset on WM_DEVICECHANGE.
22. **Low-battery notified set** persisted; reset when the device charges or rises ≥ 5 % above the threshold.
23. **Autostart is task-aware**: if the scheduled task "Pulse" exists, StartWithSystem = true and the HKCU Run value is removed (stops the duplicate launch seen 7/7 boots); turning it off deletes the task (agent is elevated) and the Run value. Reconciled at agent start.
24. **Startup order**: settings → RequestElevationOnStartup relaunch → tray icon → IPC server → state → hardware/providers → RGB. `Pulse.exe` without `--minimized` opens the UI once the pipe is up. Elevation relaunch passes `--show-ui` when a UI was connected.
25. **Tray click while the popover is open**: the UI hides on deactivation and exits; the agent records the hide time and ignores a tray click within 350 ms of it (toggle-close), otherwise launches `Pulse.UI.exe --popover`.
26. UI single instance: `Local\Pulse.UI` with the SingleInstance ACL pattern; skipped in demo. `Pulse.exe --quit` also signals the UI to exit; `tools/publish.ps1` refuses to copy while Pulse.UI.exe is alive.
27. FansViewModel gets a `_syncingMode` guard so a Settings broadcast never echoes back as SetFanMode.

## Implementation phases

A. In-place improvements (current app, deployable): 8, 9, 10, 11 (log line only), logger stream, fan-write logs at Debug, Logitech cap 2 min.
B. Pulse.Core contracts: IPC messages + JSON context, Strings move, tray rules move, IRgbServerLauncher, IStateStore, RgbDevice.Key/HasDefault, AppSettings deep clone + new keys + patch apply, BatteryDevice.LastConnectedAt.
C. Pulse.Engine: scheduler, AgentCore (ports of MonitoringService/FanModeService/SettingsService/TelemetryLogger), memory (battery merge, lighting, first sight, low battery), IpcServer, Demo providers.
D. Pulse.Agent: Program, SingleInstance, Win32 tray + GDI renderer, session end, UiLauncher, autostart, composition, OpenRGB bundle.
E. Pulse.UI: IPulseClient (Ipc + InProc), VM refactor, lifecycle (exit on hide), theme cache.
F. Tests, tools/publish.ps1, deploy, soak measurement, README.
