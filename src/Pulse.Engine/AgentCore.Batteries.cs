using Microsoft.Extensions.Logging;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Pulse.Core.Tray;

namespace Pulse.Engine;

// Battery polling: an async loop on the thread pool, every provider in parallel with its own timeout; the results are
// merged with the battery memory and published on the engine thread (low-battery alerts follow from there).
public sealed partial class AgentCore
{
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PopoverBatteryThrottle = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DeviceChangeRefreshDelay = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _batteryGate = new(1, 1);
    /// <summary>Released to make the loop re-read its period (BatteryRefreshSeconds changed).</summary>
    private readonly SemaphoreSlim _batteryWake = new(0, 1);
    private Task? _batteryLoop;
    private long _lastBatteryPollTicks;
    private int _deviceRefreshPending;

    private TimeSpan BatteryPeriod => TimeSpan.FromSeconds(Math.Clamp(_settingsState.Settings.BatteryRefreshSeconds, 5, 86400));

    private TimeSpan TimeSinceBatteryPoll()
    {
        var last = Interlocked.Read(ref _lastBatteryPollTicks);
        return last == 0 ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(Environment.TickCount64 - last);
    }

    private void WakeBatteryLoop()
    {
        try { _batteryWake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task BatteryLoopAsync(CancellationToken ct)
    {
        try
        {
            await RefreshBatteriesAsync(waitIfBusy: true, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var wait = BatteryPeriod - TimeSinceBatteryPoll();
                if (wait > TimeSpan.Zero)
                {
                    // Woken early when the period changed: loop to recompute the due time.
                    await _batteryWake.WaitAsync(wait, ct).ConfigureAwait(false);
                    continue;
                }

                if (!await RefreshBatteriesAsync(waitIfBusy: false, ct).ConfigureAwait(false))
                {
                    // Another refresh (user / popover) is still running past the due time: do not spin.
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Battery loop terminated unexpectedly");
        }
    }

    /// <summary>
    /// One poll of every provider. Timer-driven polls never queue behind a running one (returns false); user-triggered
    /// ones wait. Completes after the result was published on the engine thread.
    /// </summary>
    private async Task<bool> RefreshBatteriesAsync(bool waitIfBusy, CancellationToken ct)
    {
        if (_stopping) return true;
        if (waitIfBusy)
        {
            try { await _batteryGate.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return true; }
        }
        else if (!_batteryGate.Wait(0))
        {
            return false;
        }

        try
        {
            Interlocked.Exchange(ref _lastBatteryPollTicks, Environment.TickCount64);
            var results = await Task.WhenAll(_providers.Select(p => PollProviderAsync(p, ct))).ConfigureAwait(false);
            if (ct.IsCancellationRequested || _stopping) return true;

            var devices = results.SelectMany(r => r.Devices).ToList();
            var error = results.Select(r => r.Error).FirstOrDefault(e => e is not null);
            await _scheduler.InvokeAsync(() => OnBatteryPoll(devices, error)).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return true;
        }
        catch (ObjectDisposedException) when (_stopping)
        {
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Battery refresh failed");
            return true;
        }
        finally
        {
            _batteryGate.Release();
        }
    }

    private async Task<(IReadOnlyList<BatteryDevice> Devices, string? Error)> PollProviderAsync(IBatteryProvider provider, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProviderTimeout);
        try
        {
            // Task.Run: a provider may block synchronously before its first await; WaitAsync bounds one that ignores the token.
            var devices = await Task.Run(() => provider.GetDevicesAsync(timeout.Token), timeout.Token)
                .WaitAsync(ProviderTimeout + TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            _log.LogDebug("{Provider}: {Count} device(s)", provider.Name, devices.Count);
            return (devices, null);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException && !ct.IsCancellationRequested)
        {
            _log.LogWarning("{Provider} timed out after {Timeout}s", provider.Name, ProviderTimeout.TotalSeconds);
            return (Array.Empty<BatteryDevice>(), $"{provider.Name}: timeout");
        }
        catch (OperationCanceledException)
        {
            return (Array.Empty<BatteryDevice>(), null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Provider} failed", provider.Name);
            return (Array.Empty<BatteryDevice>(), $"{provider.Name}: {ex.Message}");
        }
    }

    /// <summary>Engine thread: merge with the memory, publish, alert.</summary>
    private void OnBatteryPoll(IReadOnlyList<BatteryDevice> reported, string? error)
    {
        if (_stopping) return;
        var now = DateTimeOffset.Now;
        var merged = _batteryMemory.Merge(reported, now, out var memoryChanged);
        LogBatteries(merged);
        PublishBatteries(merged, now, error);

        var toasts = _lowBattery.Evaluate(merged, _settings, out var notifiedChanged);
        foreach (var toast in toasts) DeliverToast(toast);
        if (memoryChanged || notifiedChanged) MarkStateDirty();
    }

    /// <summary>Engine thread: stores the battery state (visible list and lowest device derived from the settings) and raises the events.</summary>
    private void PublishBatteries(IReadOnlyList<BatteryDevice> all, DateTimeOffset? lastUpdate, string? error)
    {
        var visible = VisibleOf(all);
        TraySummary.From(visible, _hardwareFrame.Snapshot, out var lowest);
        _batteries = new BatteryState { All = all, Visible = visible, Lowest = lowest, LastUpdate = lastUpdate, LastError = error };
        BatteriesUpdated?.Invoke(this, _batteries);
        UpdateSummary();
    }

    /// <summary>Windows reported a device change: poll the batteries a little later (coalesced), so a new device shows up promptly.</summary>
    private void ScheduleDeviceChangeRefresh()
    {
        if (!_started || Interlocked.Exchange(ref _deviceRefreshPending, 1) != 0) return;
        var ct = _lifetimeCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DeviceChangeRefreshDelay, ct).ConfigureAwait(false);
                Volatile.Write(ref _deviceRefreshPending, 0);
                await RefreshBatteriesAsync(waitIfBusy: false, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                Volatile.Write(ref _deviceRefreshPending, 0);
            }
        });
    }

    /// <summary>Information-level device list, written only when a device appears/disappears or changes level/status.</summary>
    private void LogBatteries(IReadOnlyList<BatteryDevice> devices)
    {
        var signature = string.Join(";", devices.Select(d => $"{d.Id}|{d.Percent}|{d.Status}|{d.IsConnected}"));
        if (signature == _lastBatteryLogSignature) return;
        _lastBatteryLogSignature = signature;
        if (devices.Count == 0)
        {
            _log.LogInformation("Batteries: no devices reported");
            return;
        }
        var list = string.Join(", ", devices.Select(d =>
            $"{d.Name} {(d.Percent is { } p ? p + "%" : "n/a")} [{d.Source}/{d.Connection}, {d.Status}{(d.IsConnected ? "" : ", disconnected")}]"));
        _log.LogInformation("Batteries ({Count}): {Devices}", devices.Count, list);
    }
}
