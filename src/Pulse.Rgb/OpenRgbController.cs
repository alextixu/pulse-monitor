using System.Net.Sockets;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;
using OpenRGB.NET;

namespace Pulse.Rgb;

/// <summary>
/// <see cref="IRgbController"/> backed by the OpenRGB SDK server through OpenRGB.NET.
/// The SDK client is synchronous, not thread-safe and blocks forever once the server stops answering, so every call
/// is serialised through one semaphore, runs on the thread pool and is bounded by a timeout; on timeout / IO error the
/// client is disposed and the next <see cref="ConnectAsync"/> creates a fresh one. A watchdog notices when OpenRGB
/// quits so the connection is dropped promptly even while the app is idle.
/// </summary>
public sealed class OpenRgbController : IRgbController
{
    private const int MaxProtocolVersion = 4;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SocketConnectTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan EnumerateTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeSlice = TimeSpan.FromMilliseconds(250);

    private readonly ILogger<OpenRgbController> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusLock = new();
    private readonly Timer _watchdog;

    private RgbStatus _status = new() { Host = OpenRgbInfo.DefaultHost, Port = OpenRgbInfo.DefaultPort };
    private string _host = OpenRgbInfo.DefaultHost;
    private int _port = OpenRgbInfo.DefaultPort;

    // Everything below is only touched while _gate is held.
    private OpenRgbClient? _client;
    private Device[] _rawDevices = [];
    private RgbDevice[]? _devices;
    private volatile bool _disposed;

    public OpenRgbController(ILogger<OpenRgbController> logger)
    {
        _logger = logger;
        _watchdog = new Timer(Watchdog, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public RgbStatus Status
    {
        get { lock (_statusLock) return _status; }
    }

    /// <summary>
    /// Raised synchronously on the thread that changed the status (a thread-pool thread for failures detected by the
    /// watchdog), never while the internal lock is held. Handlers should marshal to the UI thread themselves.
    /// </summary>
    public event EventHandler<RgbStatus>? StatusChanged;

    public void Configure(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host)) host = OpenRgbInfo.DefaultHost;
        if (port is < 1 or > ushort.MaxValue) port = OpenRgbInfo.DefaultPort;
        host = host.Trim();

        lock (_statusLock)
        {
            _host = host;
            _port = port;
        }

        Publish(Status with { Host = host, Port = port });
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return false;
        if (_client is { Connected: true }) return true;

        string host;
        int port;
        lock (_statusLock)
        {
            host = _host;
            port = _port;
        }

        Publish(Status with
        {
            State = RgbConnectionState.Connecting,
            Host = host,
            Port = port,
            ServerVersion = null,
            Message = $"正在連線到 OpenRGB SDK 伺服器（{host}:{port}）…",
        });

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled before the gate was acquired: nothing was touched, but the "Connecting" status must not stick.
            Publish(Status with { State = RgbConnectionState.Disconnected, Message = "OpenRGB 連線已取消。" });
            throw;
        }

        RgbStatus? next = null;
        try
        {
            if (_client is { Connected: true })
            {
                next = Status with { State = RgbConnectionState.Connected };
                return true;
            }

            DropClient();
            var client = new OpenRgbClient(host, port, OpenRgbInfo.ClientName, autoConnect: false,
                timeoutMs: (int)SocketConnectTimeout.TotalMilliseconds, protocolVersionNumber: MaxProtocolVersion);
            _client = client;

            try
            {
                await RunAsync(() => { client.Connect(); return true; }, ConnectTimeout, cancellationToken, probePeer: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DropClient();
                next = Status with { State = RgbConnectionState.Disconnected, Message = "OpenRGB 連線已取消。" };
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogInformation("OpenRGB SDK server not reachable at {Host}:{Port}: {Error}", host, port, ex.Message);
                DropClient();
                next = Status with { State = RgbConnectionState.Disconnected, ServerVersion = null, Message = CannotConnectMessage(host, port) };
                return false;
            }

            var protocol = client.CommonProtocolVersion.Number;
            _logger.LogInformation("Connected to OpenRGB SDK server at {Host}:{Port} (protocol v{Protocol})", host, port, protocol);

            int deviceCount;
            try
            {
                deviceCount = (await RefreshDevicesCoreAsync(client, cancellationToken).ConfigureAwait(false)).Count;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DropClient();
                next = Status with { State = RgbConnectionState.Disconnected, Message = "OpenRGB 連線已取消。" };
                throw;
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                _logger.LogWarning("OpenRGB connection established but device enumeration failed: {Error}", ex.Message);
                DropClient();
                next = Status with { State = RgbConnectionState.Disconnected, ServerVersion = null, Message = CannotConnectMessage(host, port) };
                return false;
            }
            catch (Exception ex)
            {
                // The server answered but the payload was rejected by the SDK client: stay connected, retry on refresh.
                _logger.LogWarning(ex, "OpenRGB device enumeration failed after connecting");
                _devices = null;
                deviceCount = 0;
            }

            _watchdog.Change(WatchdogInterval, WatchdogInterval);
            next = Status with
            {
                State = RgbConnectionState.Connected,
                ServerVersion = $"SDK protocol v{protocol}",
                Message = $"已連線到 OpenRGB（SDK 通訊協定 v{protocol}），偵測到 {deviceCount} 個 RGB 裝置。",
            };
            return true;
        }
        finally
        {
            _gate.Release();
            if (next is not null) Publish(next);
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        RgbStatus? next = null;
        try
        {
            var hadClient = _client is not null;
            DropClient();
            if (hadClient) _logger.LogInformation("Disconnected from OpenRGB SDK server");
            next = Status with { State = RgbConnectionState.Disconnected, ServerVersion = null, Message = "已中斷與 OpenRGB 的連線。" };
        }
        finally
        {
            _gate.Release();
            if (next is not null) Publish(next);
        }
    }

    public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(bool refresh = false, CancellationToken cancellationToken = default)
        => ExecuteAsync("GetDevices", (IReadOnlyList<RgbDevice>)Array.Empty<RgbDevice>(), async (client, ct) =>
        {
            if (!refresh && _devices is { } cached) return cached;
            return await RefreshDevicesCoreAsync(client, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task SetDeviceColorAsync(int deviceIndex, RgbColor color, CancellationToken cancellationToken = default)
        => ExecuteAsync("SetDeviceColor", false, async (client, ct) =>
        {
            var device = await RequireDeviceAsync(client, deviceIndex, ct).ConfigureAwait(false);
            await SetDeviceColorCoreAsync(client, device, color, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task SetZoneColorAsync(int deviceIndex, int zoneIndex, RgbColor color, CancellationToken cancellationToken = default)
        => ExecuteAsync("SetZoneColor", false, async (client, ct) =>
        {
            var device = await RequireDeviceAsync(client, deviceIndex, ct).ConfigureAwait(false);
            if (zoneIndex < 0 || zoneIndex >= device.Zones.Count)
                throw new BadIndexException(nameof(zoneIndex), zoneIndex, $"Device {deviceIndex} has {device.Zones.Count} zones.");

            var zone = device.Zones[zoneIndex];
            var (perLed, _) = ResolveColorMode(device);
            if (perLed is null)
            {
                // Without a per-LED mode the zone cannot be addressed on its own; paint the whole device instead.
                _logger.LogDebug("OpenRGB device {Device} has no per-LED mode; painting the whole device instead of zone {Zone}", deviceIndex, zoneIndex);
                await SetDeviceColorCoreAsync(client, device, color, ct).ConfigureAwait(false);
                return true;
            }

            device = await EnsureModeAsync(client, device, perLed, ct).ConfigureAwait(false);
            if (zone.LedCount <= 0)
            {
                _logger.LogDebug("Zone {Zone} of OpenRGB device {Device} has no LEDs; nothing to paint", zoneIndex, deviceIndex);
                return true;
            }

            var colors = Repeat(color, zone.LedCount);
            await RunAsync(() => { client.UpdateZoneLeds(device.Index, zoneIndex, colors); return true; }, RequestTimeout, ct).ConfigureAwait(false);

            var start = device.Zones.Take(zoneIndex).Sum(z => z.LedCount);
            var painted = device.Colors.ToArray();
            for (var i = start; i < Math.Min(start + zone.LedCount, painted.Length); i++) painted[i] = color;
            StoreDevice(device with { Colors = painted });
            return true;
        }, cancellationToken);

    public Task SetModeAsync(int deviceIndex, int modeIndex, RgbColor? color = null, int? speed = null, int? brightness = null, CancellationToken cancellationToken = default)
        => ExecuteAsync("SetMode", false, async (client, ct) =>
        {
            var device = await RequireDeviceAsync(client, deviceIndex, ct).ConfigureAwait(false);
            if (modeIndex < 0 || modeIndex >= device.Modes.Count)
                throw new BadIndexException(nameof(modeIndex), modeIndex, $"Device {deviceIndex} has {device.Modes.Count} modes.");

            var mode = device.Modes[modeIndex];
            if (device.Index >= _rawDevices.Length || modeIndex >= _rawDevices[device.Index].Modes.Length)
            {
                // Cached snapshot no longer matches the raw list (should not happen: both are replaced together).
                _devices = null;
                throw new InvalidOperationException("Cached OpenRGB device list is stale; refresh and retry.");
            }

            var raw = _rawDevices[device.Index].Modes[modeIndex];

            uint? sdkSpeed = null;
            if (speed is { } s)
            {
                if (mode.SupportsSpeed)
                {
                    var lo = Math.Min(raw.SpeedMin, raw.SpeedMax);
                    var hi = Math.Max(raw.SpeedMin, raw.SpeedMax);
                    sdkSpeed = (uint)Math.Clamp(s, (long)lo, hi);
                }
                else
                {
                    _logger.LogDebug("Mode {Mode} of OpenRGB device {Device} has no speed; ignoring speed={Speed}", mode.Name, deviceIndex, s);
                }
            }

            if (brightness is { } b)
            {
                // OpenRGB.NET 3.1.1's UpdateMode re-reads the mode from the server and has no brightness parameter,
                // so brightness cannot be changed through this client version.
                _logger.LogWarning("Brightness={Brightness} for mode {Mode} of OpenRGB device {Device} ignored: not supported by the OpenRGB.NET client", b, mode.Name, deviceIndex);
            }

            Color[]? sdkColors = null;
            var paintLeds = false;
            if (color is { } c)
            {
                if (mode.IsPerLed)
                {
                    paintLeds = device.LedCount > 0;
                }
                else if (mode.SupportsColor && raw.Colors.Length > 0)
                {
                    // The SDK client requires exactly as many colours as the mode currently reports.
                    sdkColors = Repeat(c, raw.Colors.Length);
                    if (raw.ColorMode == ColorMode.Random)
                        _logger.LogDebug("Mode {Mode} of OpenRGB device {Device} is in random-colour mode; the colour may be ignored by the device", mode.Name, deviceIndex);
                }
                else
                {
                    _logger.LogDebug("Mode {Mode} of OpenRGB device {Device} takes no colour; ignoring colour {Color}", mode.Name, deviceIndex, c);
                }
            }

            await RunAsync(() => { client.UpdateMode(device.Index, modeIndex, sdkSpeed, null, sdkColors); return true; }, RequestTimeout, ct).ConfigureAwait(false);
            if (sdkSpeed is { } applied) raw.SetSpeed(applied);
            if (sdkColors is not null) raw.SetColors(sdkColors);

            var updated = device with { ActiveModeIndex = modeIndex };
            if (paintLeds && color is { } led)
            {
                var colors = Repeat(led, device.LedCount);
                await RunAsync(() => { client.UpdateLeds(device.Index, colors); return true; }, RequestTimeout, ct).ConfigureAwait(false);
                updated = updated with { Colors = ToModel(colors) };
            }
            else if (sdkColors is not null && color is { } whole)
            {
                updated = updated with { Colors = ToModel(Repeat(whole, device.LedCount)) };
            }

            StoreDevice(updated);
            _logger.LogDebug("OpenRGB device {Device} switched to mode {Mode} (speed={Speed}, colours={Colours})", deviceIndex, mode.Name, sdkSpeed, sdkColors?.Length);
            return true;
        }, cancellationToken);

    public Task SetAllAsync(RgbColor color, CancellationToken cancellationToken = default)
        => ExecuteAsync("SetAll", false, async (client, ct) =>
        {
            var devices = _devices ?? await RefreshDevicesCoreAsync(client, ct).ConfigureAwait(false);
            foreach (var device in devices.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await SetDeviceColorCoreAsync(client, device, color, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!IsConnectionFailure(ex) && ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Painting OpenRGB device {Index} ({Name}) failed; continuing with the next device", device.Index, device.Name);
                }
            }

            return true;
        }, cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watchdog.Dispose();

        var acquired = _gate.Wait(TimeSpan.FromSeconds(3));
        try
        {
            DropClient();
        }
        finally
        {
            if (acquired) _gate.Release();
        }

        lock (_statusLock)
        {
            _status = _status with { State = RgbConnectionState.Disconnected, ServerVersion = null };
        }
    }

    // ----- core operations (caller holds _gate) -------------------------------------------------------------

    private async Task<IReadOnlyList<RgbDevice>> RefreshDevicesCoreAsync(OpenRgbClient client, CancellationToken ct)
    {
        var raw = await RunAsync(client.GetAllControllerData, EnumerateTimeout, ct).ConfigureAwait(false);
        _rawDevices = raw;
        _devices = raw.Select(Map).ToArray();
        _logger.LogDebug("OpenRGB reported {Count} devices", raw.Length);
        return _devices;
    }

    private async Task<RgbDevice> RequireDeviceAsync(OpenRgbClient client, int deviceIndex, CancellationToken ct)
    {
        var devices = _devices ?? await RefreshDevicesCoreAsync(client, ct).ConfigureAwait(false);
        if (deviceIndex < 0 || deviceIndex >= devices.Count)
            throw new BadIndexException(nameof(deviceIndex), deviceIndex, $"OpenRGB reports {devices.Count} devices.");
        return devices[deviceIndex];
    }

    private async Task SetDeviceColorCoreAsync(OpenRgbClient client, RgbDevice device, RgbColor color, CancellationToken ct)
    {
        var (perLed, staticMode) = ResolveColorMode(device);
        if (perLed is not null)
        {
            device = await EnsureModeAsync(client, device, perLed, ct).ConfigureAwait(false);
            if (device.LedCount <= 0)
            {
                _logger.LogDebug("OpenRGB device {Device} has no LEDs; nothing to paint", device.Index);
                return;
            }

            var colors = Repeat(color, device.LedCount);
            await RunAsync(() => { client.UpdateLeds(device.Index, colors); return true; }, RequestTimeout, ct).ConfigureAwait(false);
            StoreDevice(device with { Colors = ToModel(colors) });
            return;
        }

        if (staticMode is not null)
        {
            var raw = _rawDevices[device.Index].Modes[staticMode.Index];
            var colors = Repeat(color, raw.Colors.Length);
            await RunAsync(() => { client.UpdateMode(device.Index, staticMode.Index, null, null, colors); return true; }, RequestTimeout, ct).ConfigureAwait(false);
            raw.SetColors(colors);
            StoreDevice(device with { ActiveModeIndex = staticMode.Index, Colors = ToModel(Repeat(color, device.LedCount)) });
            return;
        }

        if (device.LedCount <= 0)
        {
            _logger.LogDebug("OpenRGB device {Device} has no LEDs; nothing to paint", device.Index);
            return;
        }

        var fallback = Repeat(color, device.LedCount);
        await RunAsync(() => { client.UpdateLeds(device.Index, fallback); return true; }, RequestTimeout, ct).ConfigureAwait(false);
        StoreDevice(device with { Colors = ToModel(fallback) });
    }

    /// <summary>Switches the device to <paramref name="mode"/> unless it is already active; returns the updated cache entry.</summary>
    private async Task<RgbDevice> EnsureModeAsync(OpenRgbClient client, RgbDevice device, RgbMode mode, CancellationToken ct)
    {
        if (device.ActiveModeIndex == mode.Index) return device;

        await RunAsync(() => { client.UpdateMode(device.Index, mode.Index); return true; }, RequestTimeout, ct).ConfigureAwait(false);
        _logger.LogDebug("OpenRGB device {Device} switched to mode {Mode}", device.Index, mode.Name);
        var updated = device with { ActiveModeIndex = mode.Index };
        StoreDevice(updated);
        return updated;
    }

    /// <summary>Picks the per-LED mode ("Direct" preferred) or, failing that, a "Static" mode with a mode-specific colour.</summary>
    private (RgbMode? PerLed, RgbMode? Static) ResolveColorMode(RgbDevice device)
    {
        var perLed = device.Modes.FirstOrDefault(m => m.IsPerLed && m.Name.Equals("Direct", StringComparison.OrdinalIgnoreCase))
                     ?? device.Modes.FirstOrDefault(m => m.IsPerLed && m.Name.Equals("Custom", StringComparison.OrdinalIgnoreCase))
                     ?? device.Modes.FirstOrDefault(m => m.IsPerLed);
        if (perLed is not null) return (perLed, null);

        var rawModes = device.Index < _rawDevices.Length ? _rawDevices[device.Index].Modes : [];
        var staticMode = device.Modes.FirstOrDefault(m =>
            m.Name.Equals("Static", StringComparison.OrdinalIgnoreCase)
            && m.Index < rawModes.Length
            && rawModes[m.Index].Flags.HasFlag(ModeFlags.HasModeSpecificColor)
            && rawModes[m.Index].Colors.Length > 0);
        return (null, staticMode);
    }

    /// <summary>Copy-on-write replacement so lists already handed out stay immutable snapshots.</summary>
    private void StoreDevice(RgbDevice device)
    {
        if (_devices is null) return;
        var slot = Array.FindIndex(_devices, d => d.Index == device.Index);
        if (slot < 0) return;
        var copy = _devices.ToArray();
        copy[slot] = device;
        _devices = copy;
    }

    // ----- plumbing --------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="body"/> with the gate held and a live client. Connection failures drop the client and
    /// set <see cref="RgbConnectionState.Error"/>; SDK rejections are logged and leave the connection alone.
    /// Only <see cref="ArgumentOutOfRangeException"/> (bad index) and cancellation reach the caller.
    /// </summary>
    private async Task<T> ExecuteAsync<T>(string operation, T fallback, Func<OpenRgbClient, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        if (_disposed) return fallback;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        RgbStatus? next = null;
        try
        {
            var client = _client;
            if (client is null)
            {
                var current = Status;
                if (current.State == RgbConnectionState.Disconnected && string.IsNullOrEmpty(current.Message))
                    next = current with { Message = "尚未連線到 OpenRGB SDK 伺服器。" };
                return fallback;
            }

            if (OpenRgbClientProbe.IsPeerClosed(client))
                throw new IOException("The OpenRGB SDK server closed the connection.");

            return await body(client, cancellationToken).ConfigureAwait(false);
        }
        catch (BadIndexException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // RunAsync drops the client only when a blocked SDK call had to be abandoned; a cancellation that landed
            // between calls (or after the call finished) leaves the connection intact.
            if (_client is null)
            {
                _logger.LogDebug("OpenRGB {Operation} cancelled while a request was in flight; the connection was reset", operation);
                next = Status with { State = RgbConnectionState.Disconnected, ServerVersion = null, Message = "OpenRGB 作業已取消，連線已重設。" };
            }
            else
            {
                _logger.LogDebug("OpenRGB {Operation} cancelled", operation);
            }

            throw;
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            _logger.LogWarning("OpenRGB {Operation} failed, dropping the connection: {Error}", operation, ex.Message);
            DropClient();
            next = Status with { State = RgbConnectionState.Error, ServerVersion = null, Message = ConnectionLostMessage };
            return fallback;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            _logger.LogWarning(ex, "OpenRGB {Operation} rejected by the SDK client", operation);
            _devices = null; // the server-side device list may have changed; re-query next time
            next = Status with { Message = "OpenRGB 拒絕了這次燈光操作（裝置清單可能已變更），請重新整理裝置清單後再試。" };
            return fallback;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenRGB {Operation} failed unexpectedly", operation);
            next = Status with { Message = "OpenRGB 發生未預期的錯誤，請查看記錄檔。" };
            return fallback;
        }
        finally
        {
            _gate.Release();
            if (next is not null) Publish(next);
        }
    }

    /// <summary>
    /// Runs a blocking SDK call on the thread pool bounded by <paramref name="timeout"/> / the token. If the bound is
    /// hit the client is dropped (disposing it also unblocks the SDK's internal wait).
    /// </summary>
    private async Task<T> RunAsync<T>(Func<T> call, TimeSpan timeout, CancellationToken cancellationToken, bool probePeer = true)
    {
        var task = Task.Run(call, CancellationToken.None);
        try
        {
            // Wait in slices so a server that dies mid-request is noticed by the probe long before the timeout;
            // the SDK's own wait never returns on its own once the peer is gone.
            var deadline = DateTime.UtcNow + timeout;
            while (!task.IsCompleted)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException($"OpenRGB SDK call did not complete within {timeout}.");
                await Task.WhenAny(task, Task.Delay(remaining < ProbeSlice ? remaining : ProbeSlice, cancellationToken)).ConfigureAwait(false);
                if (task.IsCompleted) break;
                cancellationToken.ThrowIfCancellationRequested();
                if (probePeer && _client is { } live && OpenRgbClientProbe.IsPeerClosed(live))
                    throw new IOException("The OpenRGB SDK server closed the connection.");
            }

            return await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
        {
            if (!task.IsCompleted)
            {
                // The abandoned call is unblocked by disposing the client; observe its (expected) failure quietly.
                _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                _logger.LogDebug("OpenRGB SDK call abandoned ({Reason}); disposing the client", ex is TimeoutException ? $"timeout {timeout}" : ex.GetType().Name);
                DropClient();
            }

            throw;
        }
    }

    /// <summary>Periodic liveness check so a closed OpenRGB is noticed (and the SDK's spinning read loop stopped) while idle.</summary>
    private void Watchdog(object? state)
    {
        // Wait(0) rather than blocking: a request in progress will notice a dead peer on its own.
        if (_disposed || !_gate.Wait(0)) return;

        RgbStatus? next = null;
        try
        {
            var client = _client;
            if (client is null || !OpenRgbClientProbe.IsPeerClosed(client)) return;

            _logger.LogWarning("OpenRGB SDK server closed the connection; dropping the client");
            DropClient();
            next = Status with { State = RgbConnectionState.Error, ServerVersion = null, Message = ConnectionLostMessage };
        }
        finally
        {
            _gate.Release();
            if (next is not null) Publish(next);
        }
    }

    private void DropClient()
    {
        if (!_disposed) _watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var client = _client;
        _client = null;
        _devices = null;
        _rawDevices = [];
        if (client is null) return;

        try
        {
            client.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing the OpenRGB client threw");
        }
    }

    private void Publish(RgbStatus next)
    {
        lock (_statusLock)
        {
            if (next == _status) return;
            _status = next;
        }

        try
        {
            StatusChanged?.Invoke(this, next);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A StatusChanged handler threw");
        }
    }

    private static bool IsConnectionFailure(Exception ex) => ex switch
    {
        BadIndexException => false,
        SocketException or IOException or ObjectDisposedException or TimeoutException => true,
        // Raised by the SDK's internal token once the client has been disposed underneath a blocked call.
        OperationCanceledException => true,
        // The SDK's span readers throw these on a truncated / stale packet, i.e. a corrupted stream.
        ArgumentOutOfRangeException or IndexOutOfRangeException => true,
        AggregateException agg => agg.InnerExceptions.Count > 0 && agg.InnerExceptions.All(IsConnectionFailure),
        _ => false,
    };

    private const string ConnectionLostMessage = "與 OpenRGB 的連線已中斷（OpenRGB 可能已關閉）。請確認 OpenRGB 正在執行且已啟用 SDK Server，然後重新連線。";

    private static string CannotConnectMessage(string host, int port)
        => $"無法連線到 OpenRGB SDK 伺服器（{host}:{port}）。請安裝 OpenRGB，並在 設定 › SDK Server 啟用伺服器（或以 --server 參數啟動）。";

    /// <summary>Bad device / zone / mode index supplied by the caller; the only failure that propagates.</summary>
    private sealed class BadIndexException(string paramName, int actualValue, string message)
        : ArgumentOutOfRangeException(paramName, actualValue, message);

    // ----- mapping ---------------------------------------------------------------------------------------------

    private static RgbDevice Map(Device device) => new()
    {
        Index = device.Index,
        Name = string.IsNullOrWhiteSpace(device.Name) ? $"OpenRGB 裝置 {device.Index}" : device.Name,
        Type = HumanizeType((int)device.Type),
        Vendor = string.IsNullOrWhiteSpace(device.Vendor) ? null : device.Vendor,
        Description = string.IsNullOrWhiteSpace(device.Description) ? null : device.Description,
        Zones = device.Zones.Select(z => new RgbZone { Index = z.Index, Name = z.Name, LedCount = (int)z.LedCount }).ToArray(),
        Modes = device.Modes.Select(MapMode).ToArray(),
        ActiveModeIndex = device.ActiveModeIndex,
        LedCount = device.Leds.Length,
        Colors = ToModel(device.Colors),
    };

    private static RgbMode MapMode(Mode mode) => new()
    {
        Index = mode.Index,
        Name = mode.Name,
        SupportsColor = mode.Flags.HasFlag(ModeFlags.HasPerLedColor) || mode.Flags.HasFlag(ModeFlags.HasModeSpecificColor),
        SupportsSpeed = mode.SupportsSpeed,
        SupportsBrightness = mode.SupportsBrightness,
        IsPerLed = mode.Flags.HasFlag(ModeFlags.HasPerLedColor),
        MinSpeed = mode.SupportsSpeed ? (int)Math.Min(mode.SpeedMin, int.MaxValue) : null,
        MaxSpeed = mode.SupportsSpeed ? (int)Math.Min(mode.SpeedMax, int.MaxValue) : null,
        MinBrightness = mode.SupportsBrightness ? (int)Math.Min(mode.BrightnessMin, int.MaxValue) : null,
        MaxBrightness = mode.SupportsBrightness ? (int)Math.Min(mode.BrightnessMax, int.MaxValue) : null,
    };

    /// <summary>Maps the raw OpenRGB device_type value (OpenRGB 1.0 enum; 3.1.1's <see cref="DeviceType"/> stops at 14).</summary>
    private static string HumanizeType(int type) => type switch
    {
        0 => "Motherboard",
        1 => "DRAM",
        2 => "GPU",
        3 => "Cooler",
        4 => "LED Strip",
        5 => "Keyboard",
        6 => "Mouse",
        7 => "Mousemat",
        8 => "Headset",
        9 => "Headset Stand",
        10 => "Gamepad",
        11 => "Light",
        12 => "Speaker",
        13 => "Virtual",
        14 => "Storage",
        15 => "Case",
        16 => "Microphone",
        17 => "Accessory",
        18 => "Keypad",
        19 => "Laptop",
        20 => "Monitor",
        _ => "Unknown",
    };

    private static Color[] Repeat(RgbColor color, int count)
    {
        var colors = new Color[Math.Max(count, 0)];
        Array.Fill(colors, new Color(color.R, color.G, color.B));
        return colors;
    }

    private static RgbColor[] ToModel(Color[] colors)
    {
        var result = new RgbColor[colors.Length];
        for (var i = 0; i < colors.Length; i++) result[i] = new RgbColor(colors[i].R, colors[i].G, colors[i].B);
        return result;
    }
}
