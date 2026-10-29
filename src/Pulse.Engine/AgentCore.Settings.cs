using Microsoft.Extensions.Logging;
using Pulse.Core.Abstractions;
using Pulse.Core.Localization;
using Pulse.Core.Models;

namespace Pulse.Engine;

// Settings ownership (the engine is the only writer), auto-start, and persistence of settings.json / state.json.
// Saves are debounced on the engine thread (settings 500 ms, memory 2 s): the engine takes a consistent copy and the
// write itself runs on the pool, serialised through one I/O chain so files never go back in time.
public sealed partial class AgentCore
{
    private static readonly TimeSpan SettingsSaveDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StateSaveDelay = TimeSpan.FromSeconds(2);

    private readonly Timer _settingsTimer;
    private readonly Timer _stateTimer;
    private readonly object _ioGate = new();
    private Task _io = Task.CompletedTask;
    private bool _settingsDirty;
    private bool _stateDirty;

    public Task<CommandResult> UpdateSettingAsync(SettingsPatch patch, CancellationToken cancellationToken = default)
    {
        if (patch is null) return Task.FromResult(CommandResult.Fail("設定內容無效。"));
        return OnEngine(() =>
        {
            var before = SerializeSettings(_settings);
            var error = patch.ApplyTo(_settings);
            if (error is not null)
            {
                _log.LogInformation("Setting {Name} rejected: {Error}", patch.Name, error);
                return CommandResult.Fail(error);
            }
            if (SerializeSettings(_settings) == before) return CommandResult.Success;

            _log.LogInformation("Setting {Name} → {Value}", patch.Name, patch.ValueJson);
            OnSettingChanged(patch.Name);
            return CommandResult.Success;
        });
    }

    public async Task<CommandResult> SetAutoStartAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (_stopping) return CommandResult.Fail(EngineStrings.ShuttingDown);
        var autoStart = _options.AutoStart;
        if (!SafeGet(() => autoStart.IsSupported)) return CommandResult.Fail(Strings.AutoStartUnsupported);

        string? error = null;
        var (actual, viaTask) = await Task.Run(() =>
        {
            // Registry / schtasks: may take a moment, never on the engine thread.
            try
            {
                autoStart.SetEnabled(enabled);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Changing auto-start to {Enabled} failed", enabled);
                error = ex.Message;
            }
            return ReadAutoStart();
        }, CancellationToken.None).ConfigureAwait(false);

        await OnEngine(() =>
        {
            ApplyAutoStart(actual, viaTask);
            return CommandResult.Success;
        }).ConfigureAwait(false);

        _log.LogInformation("Auto-start → {Actual} (requested {Requested}, via task {ViaTask})", actual, enabled, viaTask);
        return actual == enabled ? CommandResult.Success : CommandResult.Fail(error is null ? EngineStrings.AutoStartFailed : $"{EngineStrings.AutoStartFailed}（{error}）");
    }

    /// <summary>Pool thread: the real registration (Run value or scheduled task).</summary>
    private (bool Enabled, bool ViaTask) ReadAutoStart()
    {
        var autoStart = _options.AutoStart;
        var enabled = SafeGet(() => autoStart.IsEnabled);
        var viaTask = enabled && SafeGet(() => _options.AutoStartViaTask is { } via
            ? via()
            : autoStart.GetType().GetProperty("IsViaTask")?.GetValue(autoStart) is true);
        return (enabled, viaTask);
    }

    /// <summary>Engine thread: the setting follows the registration (the registration is the truth).</summary>
    private void ApplyAutoStart(bool enabled, bool viaTask)
    {
        if (_settings.StartWithSystem != enabled)
        {
            _settings.StartWithSystem = enabled;
            OnSettingChanged(nameof(AppSettings.StartWithSystem));
        }
        UpdateInfo(_info with { AutoStartEnabled = enabled, AutoStartViaTask = viaTask });
    }

    /// <summary>Pool thread, at start: reconcile StartWithSystem with the real registration.</summary>
    private void ReconcileAutoStart()
    {
        if (!SafeGet(() => _options.AutoStart.IsSupported)) return;
        var (enabled, viaTask) = ReadAutoStart();
        _scheduler.Post(() =>
        {
            if (_settings.StartWithSystem != enabled)
            {
                _log.LogInformation("Auto-start is {State} (via task {ViaTask}); StartWithSystem was {Setting} — following the registration",
                    enabled ? "on" : "off", viaTask, _settings.StartWithSystem);
            }
            ApplyAutoStart(enabled, viaTask);
        });
    }

    /// <summary>
    /// Engine thread, after the live settings changed: new revision, debounced save, broadcast, then the reactions
    /// (battery visibility + tray summary, fan re-evaluation, battery cadence).
    /// </summary>
    private void OnSettingChanged(string name)
    {
        _revision++;
        _settingsState = new SettingsState(_settings.Clone(), _revision, name);
        ScheduleSettingsSave();
        SettingsChanged?.Invoke(this, _settingsState);

        switch (name)
        {
            case nameof(AppSettings.HiddenDeviceIds):
                PublishBatteries(_batteries.All, _batteries.LastUpdate, _batteries.LastError);
                break;
            case nameof(AppSettings.TrayIconMode):
                UpdateSummary(force: true);
                break;
            case nameof(AppSettings.FanMode):
            case nameof(AppSettings.SyncedFanPercent):
            case nameof(AppSettings.FanGroupExcludedIds):
            case nameof(AppSettings.FanGroupIncludedIds):
                RebuildFanInputs();
                WakeHardware();
                break;
            case nameof(AppSettings.BatteryRefreshSeconds):
                WakeBatteryLoop();
                break;
        }
    }

    private void ScheduleSettingsSave()
    {
        if (_options.IsDemo) return;
        _settingsDirty = true;
        try { _settingsTimer.Change(SettingsSaveDelay, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    /// <summary>Engine thread: takes a copy of the settings and writes it off-thread.</summary>
    private void SaveSettingsNow()
    {
        if (!_settingsDirty || _options.IsDemo) return;
        _settingsDirty = false;
        var copy = _settings.Clone();
        QueueIo(() => SaveSettingsCore(copy));
    }

    private void SaveSettingsCore(AppSettings copy)
    {
        try
        {
            _options.SettingsStore.Save(copy);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Saving settings to {Path} failed", _options.SettingsStore.FilePath);
        }
    }

    /// <summary>Engine thread: the memory changed; written 2 s later (coalesced).</summary>
    private void MarkStateDirty()
    {
        if (_options.IsDemo || _stopping) return;
        _stateDirty = true;
        try { _stateTimer.Change(StateSaveDelay, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    private void SaveStateNow()
    {
        if (!_stateDirty || _options.IsDemo) return;
        _stateDirty = false;
        var state = ComposeState();
        QueueIo(() => _options.StateStore.Save(state));
    }

    /// <summary>Engine thread: the whole memory as one immutable state.json document.</summary>
    private PulseState ComposeState() => new()
    {
        Batteries = _batteryMemory.Export(),
        Lighting = _rgbService.ExportMemory(),
        FanBootTime = _stateFanBoot,
        Fans = _stateFans,
        LowBatteryNotified = _lowBattery.Notified.OrderBy(id => id, StringComparer.Ordinal).ToList(),
    };

    private void QueueIo(Action write)
    {
        lock (_ioGate)
        {
            _io = _io.ContinueWith(_ =>
            {
                try
                {
                    write();
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Background write failed");
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Shutdown: waits for queued writes, then writes the settings (when changed) and the memory (always: exact
    /// timestamps) synchronously. Demo engines write nothing.
    /// </summary>
    private async Task FlushPersistenceAsync()
    {
        try { _settingsTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }
        try { _stateTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }
        if (_options.IsDemo) return;

        (AppSettings? Settings, PulseState State) snapshot;
        try
        {
            snapshot = await _scheduler.InvokeAsync(Capture).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The engine thread is gone or blocked; the loops are stopped by now, so a direct read is consistent enough.
            _log.LogDebug(ex, "Engine thread unavailable for the final flush; reading the state directly");
            snapshot = Capture();
        }

        Task pending;
        lock (_ioGate) pending = _io;
        try { await pending.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* best effort */ }

        if (snapshot.Settings is { } settings) SaveSettingsCore(settings);
        try
        {
            _options.StateStore.Save(snapshot.State);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Saving the memory failed");
        }

        (AppSettings?, PulseState) Capture()
        {
            var dirty = _settingsDirty;
            _settingsDirty = false;
            _stateDirty = false;
            return (dirty ? _settings.Clone() : null, ComposeState());
        }
    }

    /// <summary>Raised by the RGB controller on any thread.</summary>
    private void OnRgbStatusChanged(object? sender, RgbStatus status) => _scheduler.Post(() =>
    {
        if (status == _rgbStatus) return;
        _log.LogInformation("RGB status → {State}: {Message}", status.State, status.Message);
        _rgbStatus = status;
        RgbStatusChanged?.Invoke(this, status);
    });
}
