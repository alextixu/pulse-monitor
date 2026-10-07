using System.Diagnostics;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pulse.Logitech;

/// <summary>
/// Battery levels of Logitech devices reachable through HID++: devices paired to Unifying / LIGHTSPEED / Bolt
/// receivers and devices connected directly over USB or Bluetooth. HID handles are kept open between polls and
/// dropped as soon as they fail; each call is bounded to a few seconds and never throws for "nothing found".
/// </summary>
public sealed class LogitechHidppBatteryProvider : IBatteryProvider, IDisposable
{
    public const string ProviderName = "Logitech";

    private static readonly TimeSpan PollBudget = TimeSpan.FromSeconds(8);

    private readonly ILogger<LogitechHidppBatteryProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, HidppInterface> _interfaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _disposed;

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

        var result = new List<BatteryDevice>();
        foreach (var iface in _interfaces.Values.ToList())
        {
            if (token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                result.AddRange(iface.Snapshot());
                continue;
            }

            try
            {
                result.AddRange(await iface.PollAsync(token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!_shutdown.IsCancellationRequested)
                {
                    _logger.LogWarning("Logitech HID++ poll exceeded {Budget} s on {Key}; returning cached values", PollBudget.TotalSeconds, iface.Info.Key);
                }
                result.AddRange(iface.Snapshot());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Polling HID++ interface {Key} failed", iface.Info.Key);
            }

            if (iface.Transport.IsFaulted)
            {
                _logger.LogInformation("Dropping HID++ interface {Key}: {Reason}", iface.Info.Key, iface.Transport.Fault);
                iface.Dispose();
                _interfaces.Remove(iface.Info.Key);
            }
        }

        result = Deduplicate(result);
        _logger.LogDebug("Logitech HID++ poll: {Count} device(s) from {Interfaces} interface(s) in {Elapsed} ms",
            result.Count, _interfaces.Count, stopwatch.ElapsedMilliseconds);
        return result;
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

    /// <summary>Re-enumerates HID++ collections: opens new interfaces, drops unplugged or faulted ones.</summary>
    private void SyncInterfaces()
    {
        var found = HidInterfaceEnumerator.Enumerate(_logger);
        var present = new HashSet<string>(found.Select(f => f.Key), StringComparer.OrdinalIgnoreCase);

        foreach (var (key, iface) in _interfaces.ToList())
        {
            if (present.Contains(key) && !iface.Transport.IsFaulted) continue;
            _logger.LogInformation("HID++ interface {Key} gone ({Reason})", key, iface.Transport.Fault ?? "unplugged");
            iface.Dispose();
            _interfaces.Remove(key);
        }

        foreach (var info in found)
        {
            if (_interfaces.ContainsKey(info.Key)) continue;
            var transport = HidppTransport.TryOpen(info, _logger);
            if (transport is null) continue;
            _interfaces[info.Key] = new HidppInterface(info, transport, _logger);
            _logger.LogInformation("Opened HID++ interface {Info} short={Short}", info, transport.HasShortReports);
        }
    }
}
