using Microsoft.Extensions.Logging;
using Pulse.Core.Abstractions;

namespace Pulse.Engine;

// Start and the clean shutdown path (decisions-v2 §1). The abnormal path is RestoreFansOnAbnormalExit (Hardware partial).
public sealed partial class AgentCore
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Starts hardware initialisation, the hardware thread, the battery loop, the watchdog and (when
    /// <see cref="Pulse.Core.Models.AppSettings.AutoConnectOpenRgb"/>) the RGB connection. Settings and memory were already
    /// loaded by the constructor. Returns at once; nothing here waits for the hardware or a provider. Idempotent.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate)
        {
            if (_started || _stopping) return Task.CompletedTask;
            _started = true;
        }

        var settings = _settingsState.Settings;
        _log.LogInformation("Fan mode at startup: {Mode} (synced {Percent:0}%, excluded {Excluded}, included {Included})",
            settings.FanMode, settings.SyncedFanPercent, settings.FanGroupExcludedIds.Count, settings.FanGroupIncludedIds.Count);
        if (_telemetry is not null)
        {
            _log.LogInformation("Telemetry log {State}: {Path}", settings.TelemetryEnabled ? "enabled" : "disabled", _telemetry.CurrentPath);
        }

        _scheduler.Post(() =>
        {
            // A monitor that was already running when we were built never goes through the Ready transition.
            if (IsRunning(_monitorStatus.State)) _readySinceTicks = Environment.TickCount64;
            if (!_settingsFileExisted && !_options.IsDemo)
            {
                // First run: write the defaults so the file exists for the user to find.
                _settingsDirty = true;
                SaveSettingsNow();
            }
        });

        StartHardwareThread();
        _ = Task.Run(InitializeHardwareAsync, CancellationToken.None);
        _batteryLoop = Task.Run(() => BatteryLoopAsync(_lifetimeCts.Token), CancellationToken.None);
        var watchdogEvery = WatchdogTimeout / 3 < WatchdogInterval ? WatchdogTimeout / 3 : WatchdogInterval;
        _watchdogTimer.Change(watchdogEvery, watchdogEvery);
        _ = Task.Run(ReconcileAutoStart, CancellationToken.None);
        if (settings.AutoConnectOpenRgb)
        {
            _ = Task.Run(() => _rgbService.ConnectAsync(_lifetimeCts.Token), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Clean shutdown, idempotent (every call returns the same task): fan controller stopped, hardware thread ended
    /// (≤ 3 s) and the monitor disposed — fans handed back — synchronously on the calling thread before anything else;
    /// then clients told (<see cref="Exiting"/>), the battery loop stopped, telemetry flushed, RGB and the bundled
    /// OpenRGB stopped, settings and memory flushed, the engine thread ended.
    /// </summary>
    public Task ShutdownAsync(AgentExitReason reason)
    {
        TaskCompletionSource owner;
        lock (_lifecycleGate)
        {
            if (_shutdownTask is not null) return _shutdownTask;
            _stopping = true;
            owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownTask = owner.Task;
        }

        // Runs the fan hand-back synchronously right here (outside the lock), the rest asynchronously.
        Task core;
        try
        {
            core = ShutdownCore(reason);
        }
        catch (Exception ex)
        {
            core = Task.FromException(ex);
        }
        core.ContinueWith(t =>
        {
            if (t.Exception is { } ex)
            {
                _log.LogError(ex, "Engine shutdown failed");
                owner.TrySetException(ex.InnerExceptions);
            }
            else
            {
                owner.TrySetResult();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return owner.Task;
    }

    private Task ShutdownCore(AgentExitReason reason)
    {
        _log.LogInformation("Engine shutting down ({Reason})", reason);
        try
        {
            StopFansAndHardware();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Fan hand-back during shutdown failed");
        }
        return ShutdownRestAsync(reason);
    }

    private async Task ShutdownRestAsync(AgentExitReason reason)
    {
        _hardware.StatusChanged -= OnMonitorStatusChanged;
        try { _watchdogTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }

        // Clients first hear it here (the pipe server forwards Exiting); the fans are already back with the firmware.
        try
        {
            await _scheduler.InvokeAsync(() => Exiting?.Invoke(this, reason)).WaitAsync(StepTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Raising Exiting failed");
        }

        try { _lifetimeCts.Cancel(); } catch (ObjectDisposedException) { }
        if (_batteryLoop is { } loop)
        {
            try { await loop.WaitAsync(StepTimeout).ConfigureAwait(false); }
            catch (TimeoutException) { _log.LogWarning("Battery loop still busy after {Seconds}s", StepTimeout.TotalSeconds); }
            catch (Exception ex) { _log.LogDebug(ex, "Battery loop ended with an error"); }
        }

        if (_telemetry is { } telemetry)
        {
            var flushed = await Task.Run(() => telemetry.Flush(StepTimeout)).ConfigureAwait(false);
            if (!flushed) _log.LogWarning("Telemetry flush did not finish within {Seconds}s", StepTimeout.TotalSeconds);
        }

        _rgb.StatusChanged -= OnRgbStatusChanged;
        _rgbService.Dispose();
        try { _rgb.Dispose(); } catch (Exception ex) { _log.LogWarning(ex, "Disposing the RGB controller failed"); }
        try { _options.RgbLauncher.Dispose(); } catch (Exception ex) { _log.LogWarning(ex, "Stopping the bundled OpenRGB failed"); }

        await FlushPersistenceAsync().ConfigureAwait(false);

        if (!_scheduler.Stop(StepTimeout)) _log.LogWarning("Engine thread did not stop within {Seconds}s", StepTimeout.TotalSeconds);
        _watchdogTimer.Dispose();
        _settingsTimer.Dispose();
        _stateTimer.Dispose();
        _log.LogInformation("Engine stopped ({Reason})", reason);
    }
}
