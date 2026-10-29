using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Pulse.Core.Abstractions;
using Pulse.Core.FanControl;
using Pulse.Core.Localization;
using Pulse.Core.Models;
using Pulse.Engine.Memory;
using FanMemoryEntry = Pulse.Core.Models.FanMemory;

namespace Pulse.Engine;

// Hardware polling, fan modes, manual fan writes and the hardware watchdog.
//
// The "Pulse.Hardware" thread owns everything that touches the monitor: GetSnapshot, the presence tracker, the fan-mode
// controller and every fan write (manual ones included), so writes never interleave. It reads two volatile inputs
// published by the engine thread (_fanInputs, _uiVisible) and posts every result back to the engine thread.
public sealed partial class AgentCore
{
    private static readonly TimeSpan VisiblePeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HiddenPeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ManualFanTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);
    /// <summary>Every Nth hardware poll is summarised at Information level (the first one always is).</summary>
    private const int HardwareLogEvery = 30;

    private readonly Thread _hwThread;
    private readonly AutoResetEvent _hwWake = new(false);
    private readonly ConcurrentQueue<ManualFanCommand> _hwCommands = new();
    private readonly CancellationTokenSource _hwCts = new();
    private readonly List<TaskCompletionSource<bool>> _pollWaiters = new();
    private readonly FanPresenceTracker _fanTracker = new();
    private readonly FanPresenceMemory _fanMemory;
    private readonly Timer _watchdogTimer;
    private volatile bool _hwStop;
    private volatile bool _hwRunning;
    private volatile FanInputs _fanInputs;
    private int _fansRestored;

    // Hardware-thread only.
    private HardwareSnapshot? _hwLastSnapshot;
    private FanModeResult _hwLastResult = FanModeResult.Empty;
    private bool _hwSnapshotFailing;

    // Watchdog (engine thread, except the tick stamp written by the hardware thread).
    private long _lastFreshPollTicks;
    private long _readySinceTicks;
    private bool _watchdogTripped;
    /// <summary>The monitor's own status (the published <see cref="HardwareStatus"/> may be overridden by the watchdog).</summary>
    private HardwareMonitorStatus _monitorStatus;

    /// <summary>No successful snapshot for this long while the monitor claims to run → reported Failed (fan modes pause).</summary>
    internal TimeSpan WatchdogTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long shutdown waits for the hardware thread before disposing the monitor anyway.</summary>
    internal TimeSpan HardwareStopTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>What the hardware thread evaluates fans with; rebuilt only when the settings or the status change.</summary>
    private sealed record FanInputs(FanControlSettings Settings, bool ControlAvailable);

    private sealed record ManualFanCommand(string FanId, double? Percent)
    {
        public TaskCompletionSource<CommandResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static bool IsRunning(HardwareMonitorState state) => state is HardwareMonitorState.Ready or HardwareMonitorState.Degraded;

    private static bool IsControlAvailable(HardwareMonitorStatus status) => IsRunning(status.State) && status.FanControlAvailable;

    /// <summary>Engine thread (or the constructor).</summary>
    private FanInputs BuildFanInputs() => new(FanControlSettings.From(_settings, _hardwareStatus), IsControlAvailable(_hardwareStatus));

    private void RebuildFanInputs() => _fanInputs = BuildFanInputs();

    private void WakeHardware()
    {
        try { _hwWake.Set(); } catch (ObjectDisposedException) { }
    }

    // ───────────── fan commands (IPulseAgent) ─────────────

    public async Task<CommandResult> SetFanAsync(string fanId, double? percent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fanId)) return CommandResult.Fail(EngineStrings.FanUnknownId);
        if (percent is { } p && !double.IsFinite(p)) return CommandResult.Fail(EngineStrings.FanInvalidPercent);
        if (_stopping || _controller.IsStopped) return CommandResult.Fail(EngineStrings.ShuttingDown);
        if (!_hwRunning) return CommandResult.Fail(Strings.FanControlUnavailable);

        var command = new ManualFanCommand(fanId, percent is { } v ? Math.Clamp(v, 0, 100) : null);
        _hwCommands.Enqueue(command);
        WakeHardware();
        try
        {
            return await command.Done.Task.WaitAsync(ManualFanTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.LogWarning("Manual fan write for {FanId} not executed within {Timeout}s (hardware thread busy)", fanId, ManualFanTimeout.TotalSeconds);
            return CommandResult.Fail(EngineStrings.FanTimeout);
        }
    }

    public Task<CommandResult> SetFanModeAsync(FanMode mode, CancellationToken cancellationToken = default)
        => OnEngine(() =>
        {
            if (!Enum.IsDefined(mode)) return CommandResult.Fail(EngineStrings.FanInvalidMode);
            if (_settings.FanMode == mode) return CommandResult.Success;
            _log.LogInformation("Fan mode → {Mode}", mode);
            _settings.FanMode = mode;
            OnSettingChanged(nameof(AppSettings.FanMode));
            return CommandResult.Success;
        });

    public Task<CommandResult> SetSyncedPercentAsync(double percent, CancellationToken cancellationToken = default)
        => OnEngine(() =>
        {
            if (!double.IsFinite(percent)) return CommandResult.Fail(EngineStrings.FanInvalidPercent);
            percent = Math.Clamp(Math.Round(percent), 0, 100);
            if (Math.Abs(_settings.SyncedFanPercent - percent) < 0.5) return CommandResult.Success;
            _settings.SyncedFanPercent = percent;
            OnSettingChanged(nameof(AppSettings.SyncedFanPercent));
            return CommandResult.Success;
        });

    public Task<CommandResult> SetFanMembershipAsync(string fanId, bool include, CancellationToken cancellationToken = default)
        => OnEngine(() =>
        {
            if (string.IsNullOrWhiteSpace(fanId)) return CommandResult.Fail(EngineStrings.FanUnknownId);
            var excluded = _settings.FanGroupExcludedIds;
            var included = _settings.FanGroupIncludedIds;
            var target = include ? included : excluded;
            var other = include ? excluded : included;
            if (target.Contains(fanId) && !other.Contains(fanId)) return CommandResult.Success;

            // An explicit choice either way: include overrides the pump heuristic, exclude overrides everything.
            excluded.RemoveAll(id => id == fanId);
            included.RemoveAll(id => id == fanId);
            target.Add(fanId);
            _log.LogInformation("Fan {FanId} {Action} the fan group", fanId, include ? "added to" : "removed from");
            OnSettingChanged(nameof(AppSettings.FanGroupExcludedIds));
            return CommandResult.Success;
        });

    /// <summary>Runs a settings writer on the engine thread; fails cleanly once shutdown started.</summary>
    private async Task<CommandResult> OnEngine(Func<CommandResult> action)
    {
        if (_stopping) return CommandResult.Fail(EngineStrings.ShuttingDown);
        try
        {
            return await _scheduler.InvokeAsync(() => _stopping ? CommandResult.Fail(EngineStrings.ShuttingDown) : action()).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return CommandResult.Fail(EngineStrings.ShuttingDown);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Engine command failed");
            return CommandResult.Fail(ex.Message);
        }
    }

    /// <summary>Waits for the next published hardware frame (or a skipped poll when the monitor is not running).</summary>
    private async Task RefreshHardwareAsync(CancellationToken cancellationToken)
    {
        if (!_hwRunning) return;
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pollWaiters) _pollWaiters.Add(waiter);
        WakeHardware();
        try
        {
            await waiter.Task.WaitAsync(RefreshTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.LogWarning("Hardware refresh did not complete within {Timeout}s", RefreshTimeout.TotalSeconds);
        }
    }

    // ───────────── the hardware thread ─────────────

    private void StartHardwareThread()
    {
        _hwRunning = true;
        _hwThread.Start();
    }

    private void HardwareThreadMain()
    {
        long nextDue = 0;
        try
        {
            while (!_hwStop)
            {
                var wait = nextDue - Environment.TickCount64;
                if (wait > 0 && _hwCommands.IsEmpty) _hwWake.WaitOne((int)Math.Min(wait, int.MaxValue));
                if (_hwStop) break;

                // Fan commands run before the next poll; the poll that follows re-evaluates the fans.
                while (_hwCommands.TryDequeue(out var command))
                {
                    command.Done.TrySetResult(ExecuteManualFan(command));
                }
                if (_hwStop) break;

                nextDue = Environment.TickCount64 + (long)(_uiVisible ? VisiblePeriod : HiddenPeriod).TotalMilliseconds;
                try
                {
                    PollOnce();
                }
                catch (OperationCanceledException) when (_hwCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Hardware poll failed");
                }
                ReleasePollWaiters();
            }
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Hardware thread terminated unexpectedly");
        }
        finally
        {
            _hwRunning = false;
            while (_hwCommands.TryDequeue(out var command)) command.Done.TrySetResult(CommandResult.Fail(EngineStrings.ShuttingDown));
            ReleasePollWaiters();
        }
    }

    /// <summary>Hardware thread: one snapshot → presence → fan-mode evaluation → frame posted to the engine thread.</summary>
    private void PollOnce()
    {
        if (!IsRunning(_hardware.Status.State)) return;

        HardwareSnapshot snapshot;
        try
        {
            snapshot = _hardware.GetSnapshot(_hwCts.Token);
        }
        catch (OperationCanceledException) when (_hwCts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!_hwSnapshotFailing) _log.LogWarning(ex, "Hardware snapshot failed");
            else _log.LogDebug(ex, "Hardware snapshot failed again");
            _hwSnapshotFailing = true;
            return;
        }

        // The LHM monitor answers a failed update with its previous snapshot: that is not a successful poll.
        if (_hwStop || ReferenceEquals(snapshot, HardwareSnapshot.Empty) || ReferenceEquals(snapshot, _hwLastSnapshot)) return;
        if (_hwSnapshotFailing)
        {
            _hwSnapshotFailing = false;
            _log.LogInformation("Hardware snapshots recovered");
        }
        _hwLastSnapshot = snapshot;
        Volatile.Write(ref _lastFreshPollTicks, Environment.TickCount64);

        var inputs = _fanInputs;
        var orphaned = inputs.Settings.OrphanedIds;
        _fanMemory.BeforeObserve(_fanTracker, snapshot.Fans, orphaned);
        _fanTracker.Observe(snapshot.Fans);

        FanModeResult result;
        if (snapshot.Fans.Count == 0)
        {
            result = new FanModeResult
            {
                Mode = inputs.Settings.Mode,
                ControlAvailable = inputs.ControlAvailable,
                CpuTempC = snapshot.Cpu?.PackageTempC,
                GpuTempC = snapshot.Gpus.Select(g => g.CoreTempC).FirstOrDefault(t => t is not null),
            };
        }
        else
        {
            result = _controller.UpdateAsync(snapshot, inputs.Settings, inputs.ControlAvailable, _fanTracker.Capture(), _hwCts.Token)
                .GetAwaiter().GetResult();
        }
        _hwLastResult = result;

        var export = _fanMemory.AfterObserve(_fanTracker, snapshot.Fans, orphaned);
        _scheduler.Post(() => OnHardwareFrame(snapshot, result, export));
    }

    /// <summary>Hardware thread: validates and executes one manual fan write (the per-fan cards in 個別 mode).</summary>
    private CommandResult ExecuteManualFan(ManualFanCommand command)
    {
        if (_hwStop || _controller.IsStopped) return CommandResult.Fail(EngineStrings.ShuttingDown);
        var inputs = _fanInputs;
        if (inputs.Settings.Mode != FanMode.Individual) return CommandResult.Fail(EngineStrings.FanNotIndividual);
        if (!inputs.ControlAvailable) return CommandResult.Fail(Strings.FanControlUnavailable);

        var fan = _hwLastSnapshot?.Fans.FirstOrDefault(f => f.Id == command.FanId);
        if (fan is null) return CommandResult.Fail(EngineStrings.FanUnknown);
        if (!fan.CanControl) return CommandResult.Fail(EngineStrings.FanNotControllable);
        if (inputs.Settings.OrphanedIds.Contains(fan.Id)) return CommandResult.Fail(EngineStrings.FanOrphaned);
        if (_hwLastResult.Fans.TryGetValue(fan.Id, out var status))
        {
            if (status.State == FanModeState.StandIn) return CommandResult.Fail(EngineStrings.FanOrphaned);
            if (status.State == FanModeState.NotControllable) return CommandResult.Fail(EngineStrings.FanNotControllable);
        }

        try
        {
            _hardware.SetFanAsync(fan.Id, command.Percent, _hwCts.Token).GetAwaiter().GetResult();
            _log.LogInformation("Manual fan {Fan} → {Percent}", fan.Name, command.Percent is { } p ? $"{p:0}%" : "firmware");
            return CommandResult.Success;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Manual fan write {FanId} → {Percent} failed", fan.Id, command.Percent);
            return CommandResult.Fail(string.Format(EngineStrings.FanWriteFailedFormat, ex.Message));
        }
    }

    private void ReleasePollWaiters()
    {
        TaskCompletionSource<bool>[] waiters;
        lock (_pollWaiters)
        {
            if (_pollWaiters.Count == 0) return;
            waiters = _pollWaiters.ToArray();
            _pollWaiters.Clear();
        }
        // Through the engine queue, so the waiters resume after the frame of this poll was published.
        if (!_scheduler.Post(() => Release(waiters))) Release(waiters);

        static void Release(TaskCompletionSource<bool>[] list)
        {
            foreach (var w in list) w.TrySetResult(true);
        }
    }

    /// <summary>Stops the loop and waits (bounded) for it; true when the thread ended.</summary>
    private bool StopHardwareThread()
    {
        _hwStop = true;
        try { _hwCts.Cancel(); } catch (ObjectDisposedException) { }
        WakeHardware();
        if (!_hwThread.IsAlive) return true;
        if (Environment.CurrentManagedThreadId == _hwThread.ManagedThreadId) return false;
        return _hwThread.Join(HardwareStopTimeout);
    }

    // ───────────── engine-thread side ─────────────

    /// <summary>Engine thread: publishes one poll (frame, summary, telemetry row, fan memory).</summary>
    private void OnHardwareFrame(HardwareSnapshot snapshot, FanModeResult fans, IReadOnlyList<FanMemoryEntry>? fanExport)
    {
        // A frame polled just before the shutdown stopped the thread: the fans are already handed back, drop it.
        if (_stopping) return;
        if (_watchdogTripped)
        {
            _watchdogTripped = false;
            _log.LogInformation("Hardware watchdog: snapshots resumed");
            SetEffectiveStatus();
        }

        var frame = new HardwareFrame(snapshot, fans);
        _hardwareFrame = frame;
        LogHardware(snapshot);
        HardwareUpdated?.Invoke(this, frame);

        if (_telemetry is not null && _settings.TelemetryEnabled)
        {
            _telemetry.Enqueue(DateTime.Now, snapshot, fans, _hardwareStatus.State);
        }
        UpdateSummary();

        if (fanExport is not null)
        {
            _stateFans = fanExport;
            _stateFanBoot = _fanMemory.BootTime;
            MarkStateDirty();
        }
    }

    /// <summary>Raised by the monitor on any thread.</summary>
    private void OnMonitorStatusChanged(object? sender, HardwareMonitorStatus status) => _scheduler.Post(() => ApplyMonitorStatus(status));

    private void ApplyMonitorStatus(HardwareMonitorStatus status)
    {
        if (status == _monitorStatus) return;
        _log.LogInformation("Hardware status → {State}: {Message}", status.State, status.Message);
        var wasRunning = IsRunning(_monitorStatus.State);
        _monitorStatus = status;
        if (!wasRunning && IsRunning(status.State))
        {
            _readySinceTicks = Environment.TickCount64;
            WakeHardware(); // first snapshot right away
        }
        SetEffectiveStatus();
    }

    /// <summary>Engine thread: publishes the monitor status, overridden to Failed while the watchdog is tripped.</summary>
    private void SetEffectiveStatus()
    {
        var effective = _watchdogTripped
            ? _monitorStatus with { State = HardwareMonitorState.Failed, Message = EngineStrings.HardwareStalled }
            : _monitorStatus;
        if (effective == _hardwareStatus) return;
        _hardwareStatus = effective;
        RebuildFanInputs();
        WakeHardware();
        HardwareStatusChanged?.Invoke(this, effective);
    }

    /// <summary>Engine thread, every 5 s: the monitor claims to run but no fresh snapshot arrived for <see cref="WatchdogTimeout"/>.</summary>
    private void CheckWatchdog()
    {
        if (_watchdogTripped || _stopping || !IsRunning(_monitorStatus.State)) return;
        var last = Math.Max(Volatile.Read(ref _lastFreshPollTicks), _readySinceTicks);
        if (last == 0 || Environment.TickCount64 - last < WatchdogTimeout.TotalMilliseconds) return;

        _watchdogTripped = true;
        _log.LogWarning("Hardware watchdog: no snapshot for {Seconds:0}s; reporting the monitor as failed until it recovers",
            (Environment.TickCount64 - last) / 1000.0);
        SetEffectiveStatus();

        // The hardware thread may be stuck, so it cannot report Unavailable itself: publish it from here.
        var frame = _hardwareFrame;
        var statuses = new Dictionary<string, FanModeFanStatus>(StringComparer.Ordinal);
        foreach (var fan in frame.Snapshot.Fans)
        {
            var presence = frame.Fans.Fans.TryGetValue(fan.Id, out var s) ? s.Presence : FanPresence.Visible;
            statuses[fan.Id] = new FanModeFanStatus(fan.Id, FanModeState.Unavailable, null, FanExclusion.None,
                FanModeController.IsGpuFan(fan, frame.Snapshot), null, presence);
        }
        _hardwareFrame = new HardwareFrame(frame.Snapshot, frame.Fans with { ControlAvailable = false, Fans = statuses });
        HardwareUpdated?.Invoke(this, _hardwareFrame);
    }

    private async Task InitializeHardwareAsync()
    {
        try
        {
            _log.LogInformation("Initializing hardware monitor ({Type})", _hardware.GetType().Name);
            await _hardware.InitializeAsync(_lifetimeCts.Token).ConfigureAwait(false);
            var status = _hardware.Status;
            _log.LogInformation("Hardware monitor state: {State} ({Message})", status.State, status.Message);
            // In case the monitor changed its status without raising StatusChanged (or before we subscribed).
            _scheduler.Post(() => ApplyMonitorStatus(status));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Hardware monitor initialization failed");
        }
    }

    /// <summary>Information-level hardware summary on the first snapshot and then every <see cref="HardwareLogEvery"/> polls.</summary>
    private void LogHardware(HardwareSnapshot snapshot)
    {
        var n = ++_hardwarePolls;
        if (n != 1 && n % HardwareLogEvery != 0) return;
        if (!_log.IsEnabled(LogLevel.Information)) return;
        var cpu = snapshot.Cpu is { } c
            ? $"CPU {c.Name}: load {Fmt(c.LoadPercent, "%")}, temp {Fmt(c.PackageTempC, "°C")}, clock {Fmt(c.ClockMhz, " MHz")}, power {Fmt(c.PowerWatts, " W")}"
            : "CPU n/a";
        var gpus = snapshot.Gpus.Count == 0
            ? "GPU n/a"
            : string.Join(" | ", snapshot.Gpus.Select(g =>
                $"GPU {g.Name}: load {Fmt(g.LoadPercent, "%")}, temp {Fmt(g.CoreTempC, "°C")}, hotspot {Fmt(g.HotSpotTempC, "°C")}, vram {Fmt(g.MemoryUsedMb, " MB")}/{Fmt(g.MemoryTotalMb, " MB")}, fan {Fmt(g.FanPercent, "%")}/{Fmt(g.FanRpm, " RPM")}, power {Fmt(g.PowerWatts, " W")}"));
        var mem = snapshot.Memory is { } m ? $"memory {Fmt(m.UsedGb, " GB")}/{Fmt(m.TotalGb, " GB")}" : "memory n/a";
        var fans = $"fans {snapshot.Fans.Count} ({snapshot.Fans.Count(f => f.CanControl)} controllable)";
        _log.LogInformation("Hardware #{Poll}: {Cpu} | {Gpus} | {Memory} | {Fans} | other temps {Other}", n, cpu, gpus, mem, fans, snapshot.OtherTemperatures.Count);

        static string Fmt(double? v, string unit) => v is { } x ? $"{Math.Round(x, 1)}{unit}" : "n/a";
    }

    /// <summary>Fan safety first: stop the controller, end the hardware thread (bounded), hand the fans back.</summary>
    private void StopFansAndHardware()
    {
        try { _controller.Stop(); } catch (Exception ex) { _log.LogWarning(ex, "Stopping the fan controller failed"); }
        if (!StopHardwareThread())
        {
            _log.LogWarning("Hardware thread still busy after {Timeout}s; disposing the monitor anyway", HardwareStopTimeout.TotalSeconds);
        }
        if (Interlocked.Exchange(ref _fansRestored, 1) != 0) return;
        try
        {
            _hardware.Dispose();
            _log.LogInformation("Hardware monitor disposed (fans handed back; held {Count} fan(s))", _controller.Controlled.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Disposing the hardware monitor failed");
        }
    }

    /// <summary>
    /// Last-chance fan restore for UnhandledException / ProcessExit: stops the fan controller and disposes the hardware
    /// monitor synchronously on the calling thread; nothing else (no posts, no pipe, no logging). A no-op after
    /// <see cref="ShutdownAsync"/> and when called again.
    /// </summary>
    public void RestoreFansOnAbnormalExit(string reason)
    {
        if (Volatile.Read(ref _fansRestored) != 0) return;
        _hwStop = true;
        try { _controller.Stop(); } catch { /* keep going: the monitor restores the fans */ }
        if (Interlocked.Exchange(ref _fansRestored, 1) != 0) return;
        try { _hardware.Dispose(); } catch { /* nothing else can be done on this path */ }
    }
}
