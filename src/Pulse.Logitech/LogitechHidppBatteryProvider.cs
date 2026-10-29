using System.Diagnostics;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pulse.Logitech;

/// <summary>
/// Battery levels of Logitech devices reachable through HID++: devices paired to Unifying / LIGHTSPEED / Bolt
/// receivers and devices connected directly over USB or Bluetooth. HID handles are kept open between polls. When a
/// handle cannot be opened or its transport faults (typically G HUB holding the collection, or a device that is off),
/// the handle is closed at once but the interface and its last device snapshots are kept; the interface is then
/// reopened on a back-off (30 s, 1 min, then every 2 min; see <see cref="HidppBackoff"/>) while its devices keep being
/// reported as disconnected with their last known level. A Windows device change (<see cref="OnDevicesChanged"/>) lets
/// every backing-off interface retry on the next poll. Each call is bounded to a few seconds and never throws for
/// "nothing found".
/// </summary>
public sealed class LogitechHidppBatteryProvider : IBatteryProvider, IDeviceChangeAware, IDisposable
{
    public const string ProviderName = "Logitech";

    private static readonly TimeSpan PollBudget = TimeSpan.FromSeconds(8);

    private readonly ILogger<LogitechHidppBatteryProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, HidppInterface> _interfaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _disposed;
    /// <summary>Set by <see cref="OnDevicesChanged"/> (any thread), consumed by the next poll under <see cref="_gate"/>.</summary>
    private volatile bool _devicesChanged;

    public LogitechHidppBatteryProvider(ILogger<LogitechHidppBatteryProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<LogitechHidppBatteryProvider>.Instance;
    }

    public string Name => ProviderName;

    public bool IsSupported => true;

    public async Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return Array.Empty<BatteryDevice>();

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return Array.Empty<BatteryDevice>();
        }

        try
        {
            if (_disposed) return Array.Empty<BatteryDevice>();
            // HidSharp I/O is synchronous; keep the caller (UI timer) off the blocking path.
            return await Task.Run(() => PollAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Logitech HID++ poll failed");
            return Array.Empty<BatteryDevice>();
        }
        finally
        {
            try { _gate.Release(); } catch (ObjectDisposedException) { /* disposed while polling */ }
        }
    }

    /// <summary>
    /// A USB / Bluetooth device arrived or left (WM_DEVICECHANGE, from the agent's tray window): a receiver that was
    /// re-plugged, a mouse that woke up or G HUB letting go is worth trying at once. The interfaces are only touched by
    /// the poll (under the gate), so this just flags the next poll, which re-enumerates the HID collections anyway and then
    /// lets every backing-off interface retry immediately (keeping its failure count).
    /// </summary>
    public void OnDevicesChanged()
    {
        if (_disposed) return;
        _devicesChanged = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Abort an in-flight poll (it returns cached values) so the gate is released quickly, then take it ourselves.
        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
        var acquired = false;
        try { acquired = _gate.Wait(TimeSpan.FromSeconds(5)); } catch (ObjectDisposedException) { }
        if (!acquired) _logger.LogWarning("Logitech HID++ poll did not finish before dispose; closing handles anyway");
        try
        {
            foreach (var iface in _interfaces.Values.ToList()) iface.Dispose();
            _interfaces.Clear();
        }
        finally
        {
            if (acquired) _gate.Release();
            _gate.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task<IReadOnlyList<BatteryDevice>> PollAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        budget.CancelAfter(PollBudget);
        var token = budget.Token;

        SyncInterfaces();
        if (_devicesChanged)
        {
            _devicesChanged = false;
            RetryBackingOffInterfaces();
        }

        var result = new List<BatteryDevice>();
        foreach (var iface in _interfaces.Values.ToList())
        {
            if (token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                result.AddRange(iface.Snapshot());
                continue;
            }

            result.AddRange(await PollInterfaceAsync(iface, token, cancellationToken).ConfigureAwait(false));
        }

        result = Deduplicate(result);
        _logger.LogDebug("Logitech HID++ poll: {Count} device(s) from {Interfaces} interface(s) ({Closed} closed / backing off) in {Elapsed} ms",
            result.Count, _interfaces.Count, _interfaces.Values.Count(i => !i.IsOpen), stopwatch.ElapsedMilliseconds);
        return result;
    }

    /// <summary>
    /// Polls one interface: opens (or reopens) its transport when the back-off allows, runs the poll, and on a transport
    /// fault closes the handles again and schedules the next attempt. A closed interface yields its cached devices,
    /// marked disconnected; an interface that never completed discovery yields nothing.
    /// </summary>
    private async Task<List<BatteryDevice>> PollInterfaceAsync(HidppInterface iface, CancellationToken token, CancellationToken cancellationToken)
    {
        // A reader thread may have died while idle between polls; that is a fault like any other.
        if (CloseIfFaulted(iface)) return iface.Snapshot();

        if (iface.Transport is null)
        {
            if (!iface.Backoff.IsDue(DateTimeOffset.Now))
            {
                _logger.LogTrace("HID++ interface {Key} closed; next attempt at {RetryAt:HH:mm:ss}", iface.Info.Key, iface.Backoff.RetryAt);
                return iface.Snapshot();
            }

            if (!TryOpen(iface))
            {
                RecordFailure(iface, "open failed");
                return iface.Snapshot();
            }
        }

        List<BatteryDevice>? devices = null;
        try
        {
            devices = await iface.PollAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!_shutdown.IsCancellationRequested)
            {
                _logger.LogWarning("Logitech HID++ poll exceeded {Budget} s on {Key}; returning cached values", PollBudget.TotalSeconds, iface.Info.Key);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Polling HID++ interface {Key} failed", iface.Info.Key);
        }

        if (CloseIfFaulted(iface)) return iface.Snapshot();

        if (devices is not null && iface.Backoff.IsActive)
        {
            _logger.LogInformation("HID++ interface {Key} recovered after {Failures} failed attempt(s)", iface.Info.Key, iface.Backoff.Failures);
            iface.Backoff.Reset();
        }

        return devices ?? iface.Snapshot();
    }

    /// <summary>
    /// When the transport has faulted, closes it (disposing both HidStreams, which ends the reader threads), keeps the
    /// interface with its devices marked offline, and advances the back-off. Returns true when it did so.
    /// </summary>
    private bool CloseIfFaulted(HidppInterface iface)
    {
        if (iface.Transport is not { IsFaulted: true } faulted) return false;

        var reason = faulted.Fault ?? "transport fault";
        iface.CloseTransport();
        RecordFailure(iface, reason);
        return true;
    }

    /// <summary>Opens the interface's HID collections. The first open of an interface is news; retries are not.</summary>
    private bool TryOpen(HidppInterface iface)
    {
        var transport = HidppTransport.TryOpen(iface.Info, _logger);
        if (transport is null) return false;

        iface.Attach(transport);
        if (iface.Backoff.IsActive)
        {
            _logger.LogDebug("Reopened HID++ interface {Key} (attempt {Attempt}) short={Short}", iface.Info.Key, iface.Backoff.Failures + 1, transport.HasShortReports);
        }
        else
        {
            _logger.LogInformation("Opened HID++ interface {Info} short={Short}", iface.Info, transport.HasShortReports);
        }
        return true;
    }

    /// <summary>Advances the interface's back-off; the first failure is logged at Information, later ones at Debug.</summary>
    private void RecordFailure(HidppInterface iface, string reason)
    {
        var delay = iface.Backoff.Fail(DateTimeOffset.Now);
        if (iface.Backoff.Failures == 1)
        {
            _logger.LogInformation("HID++ interface {Key}: backing off for {Delay} after {Reason}", iface.Info.Key, HidppBackoff.Describe(delay), reason);
        }
        else
        {
            _logger.LogDebug("HID++ interface {Key}: attempt {Attempt} failed ({Reason}); next attempt in {Delay}",
                iface.Info.Key, iface.Backoff.Failures, reason, HidppBackoff.Describe(delay));
        }
    }

    /// <summary>
    /// A device reachable twice (e.g. charging over its USB cable while still paired to the receiver, which then reports
    /// it asleep) shares one id; keep the entry that is online and has a reading.
    /// </summary>
    private static List<BatteryDevice> Deduplicate(List<BatteryDevice> devices)
    {
        if (devices.Count < 2) return devices;

        var best = new Dictionary<string, BatteryDevice>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>(devices.Count);
        foreach (var device in devices)
        {
            if (!best.TryGetValue(device.Id, out var existing))
            {
                best[device.Id] = device;
                order.Add(device.Id);
            }
            else if (Rank(device) > Rank(existing))
            {
                best[device.Id] = device;
            }
        }
        return order.Select(id => best[id]).ToList();

        static int Rank(BatteryDevice d) => (d.IsConnected ? 2 : 0) + (d.Percent is null ? 0 : 1);
    }

    /// <summary>Makes every interface that is backing off due for an attempt in this poll (device change).</summary>
    private void RetryBackingOffInterfaces()
    {
        var retried = 0;
        foreach (var iface in _interfaces.Values)
        {
            if (!iface.Backoff.IsActive) continue;
            iface.Backoff.RetrySoon();
            retried++;
        }
        if (retried > 0) _logger.LogDebug("Device change: retrying {Count} backing-off HID++ interface(s) now", retried);
    }

    /// <summary>
    /// Re-enumerates HID++ collections: registers new interfaces (opened lazily by the poll, subject to their back-off)
    /// and forgets unplugged ones. A faulted-but-present interface is kept so its devices stay in the list.
    /// </summary>
    private void SyncInterfaces()
    {
        var found = HidInterfaceEnumerator.Enumerate(_logger);
        var present = new HashSet<string>(found.Select(f => f.Key), StringComparer.OrdinalIgnoreCase);

        foreach (var (key, iface) in _interfaces.ToList())
        {
            if (present.Contains(key)) continue;
            _logger.LogInformation("HID++ interface {Key} gone (unplugged)", key);
            iface.Dispose();
            _interfaces.Remove(key);
        }

        foreach (var info in found)
        {
            if (_interfaces.ContainsKey(info.Key)) continue;
            _interfaces[info.Key] = new HidppInterface(info, _logger);
        }
    }
}
