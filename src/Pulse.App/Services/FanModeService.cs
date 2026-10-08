using System.ComponentModel;
using Avalonia.Threading;
using Pulse.Core.Abstractions;
using Pulse.Core.FanControl;
using Pulse.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Pulse.App.Services;

/// <summary>
/// Runs the fan modes (自動 / 靜音 / 全部同步 / 個別). Driven on the UI thread by <see cref="MonitoringService.HardwareUpdated"/>
/// and settings changes; each evaluation runs <see cref="FanModeController.UpdateAsync"/> on the thread pool, never two at
/// once (the latest request wins while one is in flight). Also owns the session's <see cref="FanPresenceTracker"/> so the
/// Fans tab and the controller agree on which headers have a fan.
/// Disposed before <see cref="IHardwareMonitor"/> (it depends on it), so it stops writing before the monitor restores fans.
/// </summary>
public sealed partial class FanModeService : ObservableObject, IDisposable
{
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(3);

    private readonly MonitoringService _monitoring;
    private readonly SettingsService _settings;
    private readonly ILogger<FanModeService> _log;
    private readonly FanModeController _controller;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _queueGate = new();
    private Request? _pending;
    private bool _running;
    private volatile bool _disposed;

    private sealed record Request(HardwareSnapshot Snapshot, FanControlSettings Settings, bool ControlAvailable, FanPresenceSnapshot Presence);

    /// <summary>Result of the most recent evaluation (UI thread).</summary>
    [ObservableProperty] private FanModeResult _current = FanModeResult.Empty;

    public FanModeService(MonitoringService monitoring, SettingsService settings, IHardwareMonitor hardware, ILogger<FanModeService> log)
    {
        _monitoring = monitoring;
        _settings = settings;
        _log = log;
        _controller = new FanModeController(hardware, log);

        _monitoring.HardwareUpdated += OnHardwareUpdated;
        _monitoring.PropertyChanged += OnMonitoringPropertyChanged;
        _settings.Changed += OnSettingsChanged;
        _log.LogInformation("Fan mode at startup: {Mode} (synced {Percent:0}%, excluded {Excluded}, included {Included})",
            settings.Settings.FanMode, settings.Settings.SyncedFanPercent,
            settings.Settings.FanGroupExcludedIds.Count, settings.Settings.FanGroupIncludedIds.Count);
    }

    /// <summary>Which fan headers have shown a fan this session (UI thread only).</summary>
    public FanPresenceTracker Presence { get; } = new();

    /// <summary>Raised on the UI thread after a hardware snapshot was fed to <see cref="Presence"/>.</summary>
    public event EventHandler<HardwareSnapshot>? HardwareObserved;

    /// <summary>Raised on the UI thread after each evaluation.</summary>
    public event EventHandler<FanModeResult>? ResultChanged;

    public AppSettings Settings => _settings.Settings;

    /// <summary>True when fans can be written (monitor running and at least one controllable fan, i.e. elevated on Windows).</summary>
    public bool ControlAvailable
    {
        get
        {
            var s = _monitoring.HardwareStatus;
            return s.State is HardwareMonitorState.Ready or HardwareMonitorState.Degraded && s.FanControlAvailable;
        }
    }

    public void SetMode(FanMode mode)
    {
        if (Settings.FanMode == mode) return;
        _log.LogInformation("Fan mode → {Mode}", mode);
        Settings.FanMode = mode;
        _settings.NotifyChanged(nameof(AppSettings.FanMode));
    }

    public void SetSyncedPercent(double percent)
    {
        percent = Math.Clamp(Math.Round(percent), 0, 100);
        if (Math.Abs(Settings.SyncedFanPercent - percent) < 0.5) return;
        Settings.SyncedFanPercent = percent;
        _settings.NotifyChanged(nameof(AppSettings.SyncedFanPercent));
    }

    /// <summary>"納入群組控制" switch: an explicit choice either way (include overrides the pump heuristic).</summary>
    public void SetMembership(string fanId, bool include)
    {
        var excluded = Settings.FanGroupExcludedIds;
        var included = Settings.FanGroupIncludedIds;
        excluded.Remove(fanId);
        included.Remove(fanId);
        (include ? included : excluded).Add(fanId);
        _log.LogInformation("Fan {FanId} {Action} the fan group", fanId, include ? "added to" : "removed from");
        _settings.NotifyChanged(nameof(AppSettings.FanGroupExcludedIds));
    }

    private void OnHardwareUpdated(object? sender, HardwareSnapshot snapshot)
    {
        if (_disposed) return;
        Presence.Observe(snapshot.Fans);
        HardwareObserved?.Invoke(this, snapshot);
        Schedule();
    }

    private void OnMonitoringPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitoringService.HardwareStatus)) Schedule();
    }

    private void OnSettingsChanged(object? sender, string? name)
    {
        if (name is null or nameof(AppSettings.FanMode) or nameof(AppSettings.SyncedFanPercent)
            or nameof(AppSettings.FanGroupExcludedIds) or nameof(AppSettings.FanGroupIncludedIds))
        {
            Schedule();
        }
    }

    /// <summary>UI thread: queues an evaluation of the latest snapshot with the current settings.</summary>
    private void Schedule()
    {
        if (_disposed) return;
        var snapshot = _monitoring.LatestHardware;
        if (snapshot.Fans.Count == 0) return;

        var request = new Request(snapshot, FanControlSettings.From(Settings), ControlAvailable, Presence.Capture());
        lock (_queueGate)
        {
            _pending = request;
            if (_running) return;
            _running = true;
        }
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (true)
        {
            Request? request;
            lock (_queueGate)
            {
                request = _pending;
                _pending = null;
                if (request is null || _disposed)
                {
                    _running = false;
                    return;
                }
            }

            try
            {
                var result = await _controller.UpdateAsync(request.Snapshot, request.Settings, request.ControlAvailable, request.Presence, _cts.Token)
                    .ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed) return;
                    Current = result;
                    ResultChanged?.Invoke(this, result);
                });
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Fan mode evaluation failed");
            }
        }
    }

    /// <summary>Stops all further fan writes (also used by the abnormal-exit path before the monitor restores fans).</summary>
    public void StopApplying()
    {
        _disposed = true;
        _controller.Stop();
    }

    public void Dispose()
    {
        if (_controller.IsStopped && _cts.IsCancellationRequested) return;
        StopApplying();
        _monitoring.HardwareUpdated -= OnHardwareUpdated;
        _monitoring.PropertyChanged -= OnMonitoringPropertyChanged;
        _settings.Changed -= OnSettingsChanged;
        _cts.Cancel();
        if (!_controller.WaitIdle(DisposeWait))
        {
            _log.LogWarning("Fan mode evaluation still running at shutdown; the hardware monitor restores fans anyway");
        }
        _log.LogInformation("Fan mode service stopped (held {Count} fan(s); the hardware monitor hands them back)", _controller.Controlled.Count);
    }
}
