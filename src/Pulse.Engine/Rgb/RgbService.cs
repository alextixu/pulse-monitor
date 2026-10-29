using Microsoft.Extensions.Logging;
using Pulse.Core.Abstractions;
using Pulse.Core.Localization;
using Pulse.Core.Models;
using Pulse.Engine.Memory;

namespace Pulse.Engine.Rgb;

/// <summary>
/// RGB orchestration around <see cref="IRgbController"/>: connecting (with the bundled OpenRGB when allowed), the
/// post-launch device-detection watch, bounded reconnects after an unexpected drop, key-addressed commands and the
/// lighting memory (record every successful command; re-apply once after the first connect of this process).
/// Runs on the thread pool; it reports back through <see cref="DevicesChanged"/> / <see cref="MemoryChanged"/>, which
/// the agent marshals to the engine thread. The controller serialises its own calls.
/// </summary>
internal sealed class RgbService : IDisposable
{
    public static readonly TimeSpan RestoreWindow = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan RestoreRescan = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DetectionInterval = TimeSpan.FromSeconds(2);
    private const int DetectionRounds = 15;
    private static readonly TimeSpan[] ReconnectDelays = { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60) };
    private static readonly TimeSpan ReconnectSteady = TimeSpan.FromMinutes(5);

    private readonly IRgbController _rgb;
    private readonly IRgbServerLauncher _launcher;
    private readonly Func<AppSettings> _settings;
    private readonly ILogger _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private readonly LightingMemory _memory;
    /// <summary>Keys already restored (or changed by the user) in this process: never restored (again).</summary>
    private readonly HashSet<string> _restored = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private RgbLaunchResult? _firstLaunch;
    private bool _everConnected;
    private RgbConnectionState _lastState;
    private int _reconnecting;
    private volatile bool _disposed;

    public RgbService(
        IRgbController rgb,
        IRgbServerLauncher launcher,
        LightingMemory memory,
        Func<AppSettings> settings,
        ILogger log,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _rgb = rgb;
        _launcher = launcher;
        _memory = memory;
        _settings = settings;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _delay = delay ?? Task.Delay;
        _lastState = rgb.Status.State;
        _rgb.StatusChanged += OnStatusChanged;
    }

    /// <summary>The device list or a device's state changed (pool thread).</summary>
    public event Action? DevicesChanged;

    /// <summary>The lighting memory changed and should be persisted (pool thread).</summary>
    public event Action? MemoryChanged;

    /// <summary>The startup re-apply, while (or after) it runs; for tests and diagnostics.</summary>
    public Task? RestoreTask { get; private set; }

    public bool IsBundled => _launcher.IsBundled;

    public IReadOnlyDictionary<string, LightingIntent> ExportMemory()
    {
        lock (_gate) return _memory.Export();
    }

    /// <summary>
    /// Connects to the configured server, starting the bundled OpenRGB first when allowed: only when
    /// <see cref="AppSettings.UseBundledOpenRgb"/> and the first launch attempt of this session did not find a server
    /// already running (then that one is the user's and Pulse only reconnects to it).
    /// </summary>
    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) => ConnectCoreAsync(quiet: false, cancellationToken);

    /// <param name="quiet">Reconnect attempts log their outcome at Debug (the loop logs the state changes).</param>
    private async Task<bool> ConnectCoreAsync(bool quiet, CancellationToken cancellationToken)
    {
        if (_disposed) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        var ct = linked.Token;
        try
        {
            await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            var settings = _settings();
            var launched = false;
            if (settings.UseBundledOpenRgb && _firstLaunch != RgbLaunchResult.AlreadyRunning)
            {
                var launch = await _launcher.EnsureRunningAsync(settings.OpenRgbHost, settings.OpenRgbPort, ct).ConfigureAwait(false);
                _firstLaunch ??= launch;
                launched = launch == RgbLaunchResult.Started;
                _log.LogInformation("Bundled OpenRGB: {Result} (bundled copy present: {Bundled})", launch, _launcher.IsBundled);
            }

            _rgb.Configure(settings.OpenRgbHost, settings.OpenRgbPort);
            var ok = await Task.Run(() => _rgb.ConnectAsync(ct), ct).ConfigureAwait(false);
            _log.Log(quiet && !ok ? LogLevel.Debug : LogLevel.Information, "RGB connect to {Host}:{Port} → {Result}",
                settings.OpenRgbHost, settings.OpenRgbPort, ok ? "connected" : _rgb.Status.Message);
            if (!ok) return false;

            if (launched) _ = WatchDeviceDetectionAsync(_cts.Token);
            if (!_everConnected)
            {
                _everConnected = true;
                if (settings.RestoreLightingOnStartup && HasIntents())
                {
                    RestoreTask = RestoreLightingAsync(_clock(), _cts.Token);
                }
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RGB connect failed");
            return false;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task<RgbDevicesResult> GetDevicesAsync(bool refresh, CancellationToken cancellationToken = default)
    {
        try
        {
            var devices = await _rgb.GetDevicesAsync(refresh, cancellationToken).ConfigureAwait(false);
            return new RgbDevicesResult(true, devices);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Reading the RGB devices failed");
            return RgbDevicesResult.Failed(ex.Message);
        }
    }

    /// <summary>Runs one lighting command addressed by key; records its intent when it succeeded.</summary>
    public async Task<RgbDevicesResult> ExecuteAsync(RgbCommand command, CancellationToken cancellationToken = default)
    {
        if (_disposed) return RgbDevicesResult.Failed(EngineStrings.ShuttingDown);
        try
        {
            if (command.Kind == RgbCommandKind.Forget) return await ForgetAsync(command.Key, cancellationToken).ConfigureAwait(false);
            if (_rgb.Status.State != RgbConnectionState.Connected) return RgbDevicesResult.Failed(Strings.RgbDisconnected);

            IReadOnlyList<RgbDevice> devices;
            RgbDevice? target = null;
            if (command.Key is { } key)
            {
                // Indexes change with every enumeration: resolve the key against the current list (refresh once).
                devices = await _rgb.GetDevicesAsync(false, cancellationToken).ConfigureAwait(false);
                target = devices.FirstOrDefault(d => d.Key == key);
                if (target is null)
                {
                    devices = await _rgb.GetDevicesAsync(true, cancellationToken).ConfigureAwait(false);
                    target = devices.FirstOrDefault(d => d.Key == key);
                }
                if (target is null) return RgbDevicesResult.Failed(EngineStrings.RgbDeviceChanged);
            }
            else if (command.Kind is RgbCommandKind.SetColor or RgbCommandKind.TurnOff or RgbCommandKind.RestoreDefault)
            {
                devices = await _rgb.GetDevicesAsync(false, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return RgbDevicesResult.Failed(EngineStrings.RgbNeedsDevice);
            }

            var before = _rgb.Status;
            var error = await RunAsync(command, target, cancellationToken).ConfigureAwait(false);
            if (error is not null) return RgbDevicesResult.Failed(error);

            // The controller reports rejected operations through its status message instead of throwing.
            var after = _rgb.Status;
            var list = await _rgb.GetDevicesAsync(false, cancellationToken).ConfigureAwait(false);
            if (after.State != RgbConnectionState.Connected)
            {
                return new RgbDevicesResult(false, list, after.Message ?? Strings.RgbError);
            }
            if (!ReferenceEquals(before, after) && !string.IsNullOrEmpty(after.Message) && after.Message != before.Message)
            {
                DevicesChanged?.Invoke();
                return new RgbDevicesResult(false, list, after.Message);
            }

            var targets = target is not null ? new[] { target } : devices;
            bool changed;
            lock (_gate)
            {
                foreach (var d in targets) _restored.Add(d.Key);
                changed = _memory.Record(command, targets, _clock());
            }
            if (changed) MemoryChanged?.Invoke();
            DevicesChanged?.Invoke();
            return new RgbDevicesResult(true, list);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _log.LogWarning(ex, "RGB command {Kind} on {Key} failed", command.Kind, command.Key ?? "(all)");
            return RgbDevicesResult.Failed(ex.Message);
        }
    }

    /// <summary>Maps one command to the controller; returns an error text for a command that cannot run.</summary>
    private async Task<string?> RunAsync(RgbCommand command, RgbDevice? target, CancellationToken ct)
    {
        switch (command.Kind)
        {
            case RgbCommandKind.SetColor:
                if (command.Color is not { } color) return EngineStrings.RgbNeedsColor;
                if (target is null) await _rgb.SetAllAsync(color, ct).ConfigureAwait(false);
                else await _rgb.SetDeviceColorAsync(target.Index, color, ct).ConfigureAwait(false);
                return null;

            case RgbCommandKind.SetZoneColor:
                if (command.Color is not { } zoneColor) return EngineStrings.RgbNeedsColor;
                if (command.ZoneIndex is not { } zone) return EngineStrings.RgbNeedsZone;
                await _rgb.SetZoneColorAsync(target!.Index, zone, zoneColor, ct).ConfigureAwait(false);
                return null;

            case RgbCommandKind.SetMode:
                var mode = LightingMemory.ResolveMode(target!, command.ModeName, command.ModeIndex);
                if (mode is null) return string.Format(EngineStrings.RgbUnknownModeFormat, command.ModeName ?? command.ModeIndex?.ToString());
                await _rgb.SetModeAsync(target!.Index, mode.Index, command.Color, command.Speed, command.Brightness, ct).ConfigureAwait(false);
                return null;

            case RgbCommandKind.TurnOff:
                if (target is null) await _rgb.TurnOffAllAsync(ct).ConfigureAwait(false);
                else await _rgb.TurnOffAsync(target.Index, ct).ConfigureAwait(false);
                return null;

            case RgbCommandKind.RestoreDefault:
                if (target is null) await _rgb.RestoreAllDefaultsAsync(ct).ConfigureAwait(false);
                else await _rgb.RestoreDefaultAsync(target.Index, ct).ConfigureAwait(false);
                return null;

            case RgbCommandKind.SaveAsDefault:
                await _rgb.SaveCurrentAsDefaultAsync(target!.Index, ct).ConfigureAwait(false);
                return null;

            default:
                return EngineStrings.RgbNeedsDevice;
        }
    }

    private async Task<RgbDevicesResult> ForgetAsync(string? key, CancellationToken ct)
    {
        bool changed;
        lock (_gate) changed = _memory.Forget(key);
        if (changed)
        {
            _log.LogInformation("Lighting memory: forgot {Key}", key ?? "every device");
            MemoryChanged?.Invoke();
        }
        var devices = _rgb.Status.State == RgbConnectionState.Connected
            ? await _rgb.GetDevicesAsync(false, ct).ConfigureAwait(false)
            : Array.Empty<RgbDevice>();
        return new RgbDevicesResult(true, devices);
    }

    private bool HasIntents()
    {
        lock (_gate) return _memory.Count > 0;
    }

    /// <summary>
    /// The once-per-process re-apply: during <see cref="RestoreWindow"/> after the first successful connect, every
    /// remembered device is applied once (devices that show up late are caught by a re-read every
    /// <see cref="RestoreRescan"/>). Never repeated after a reconnect.
    /// </summary>
    private async Task RestoreLightingAsync(DateTimeOffset start, CancellationToken ct)
    {
        var deadline = start + RestoreWindow;
        var first = true;
        var total = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_settings().RestoreLightingOnStartup) break;
                if (_rgb.Status.State == RgbConnectionState.Connected)
                {
                    var devices = await _rgb.GetDevicesAsync(refresh: !first, ct).ConfigureAwait(false);
                    first = false;
                    var applied = 0;
                    foreach (var device in devices)
                    {
                        LightingIntent? intent;
                        lock (_gate)
                        {
                            if (!LightingMemory.IsRememberable(device.Key) || _restored.Contains(device.Key)) continue;
                            intent = _memory.Get(device.Key);
                            if (intent is null) continue;
                            _restored.Add(device.Key);
                        }
                        if (await ApplyAsync(device, intent, ct).ConfigureAwait(false)) applied++;
                    }

                    if (applied > 0)
                    {
                        total += applied;
                        _log.LogInformation("Lighting memory: restored {Count} device(s)", applied);
                        DevicesChanged?.Invoke();
                    }

                    bool pending;
                    lock (_gate) pending = _memory.Export().Keys.Any(k => !_restored.Contains(k));
                    if (!pending) break;
                }

                await _delay(RestoreRescan, ct).ConfigureAwait(false);
                if (_clock() > deadline) break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Restoring the remembered lighting failed");
        }
        _log.LogDebug("Lighting restore window closed ({Count} device(s) restored)", total);
    }

    private async Task<bool> ApplyAsync(RgbDevice device, LightingIntent intent, CancellationToken ct)
    {
        try
        {
            switch (intent.Kind)
            {
                case LightingIntentKind.Off:
                    await _rgb.TurnOffAsync(device.Index, ct).ConfigureAwait(false);
                    break;
                case LightingIntentKind.Color when intent.Color is { } hex:
                    await _rgb.SetDeviceColorAsync(device.Index, RgbColor.FromHex(hex), ct).ConfigureAwait(false);
                    break;
                case LightingIntentKind.Mode:
                    var mode = LightingMemory.ResolveMode(device, intent.ModeName, null);
                    if (mode is null)
                    {
                        _log.LogInformation("Lighting memory: {Device} has no mode {Mode} any more; skipped", device.Name, intent.ModeName);
                        return false;
                    }
                    RgbColor? color = mode.SupportsColor && intent.Color is { } c ? RgbColor.FromHex(c) : null;
                    await _rgb.SetModeAsync(device.Index, mode.Index, color,
                        mode.SupportsSpeed ? intent.Speed : null, mode.SupportsBrightness ? intent.Brightness : null, ct).ConfigureAwait(false);
                    break;
                case LightingIntentKind.Default:
                    await _rgb.RestoreDefaultAsync(device.Index, ct).ConfigureAwait(false);
                    break;
                case LightingIntentKind.Zones when intent.Zones is { } zones:
                    foreach (var (zone, hex) in zones.OrderBy(z => z.Key))
                    {
                        if (device.Zones.All(z => z.Index != zone)) continue;
                        await _rgb.SetZoneColorAsync(device.Index, zone, RgbColor.FromHex(hex), ct).ConfigureAwait(false);
                    }
                    break;
                default:
                    return false;
            }
            _log.LogDebug("Lighting memory: {Device} → {Kind}", device.Name, intent.Kind);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Lighting memory: restoring {Device} ({Kind}) failed", device.Name, intent.Kind);
            return false;
        }
    }

    /// <summary>
    /// A freshly started OpenRGB opens its SDK port before it has finished detecting devices, so the first list can be
    /// short. Re-read it for up to 30 s and tell clients whenever it grows or shrinks.
    /// </summary>
    private async Task WatchDeviceDetectionAsync(CancellationToken ct)
    {
        try
        {
            var last = (await _rgb.GetDevicesAsync(refresh: false, ct).ConfigureAwait(false)).Count;
            for (var i = 0; i < DetectionRounds && !ct.IsCancellationRequested; i++)
            {
                await _delay(DetectionInterval, ct).ConfigureAwait(false);
                var count = (await _rgb.GetDevicesAsync(refresh: true, ct).ConfigureAwait(false)).Count;
                if (count == last) continue;
                _log.LogInformation("Bundled OpenRGB detection: {Count} RGB device(s)", count);
                last = count;
                DevicesChanged?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Watching OpenRGB device detection failed");
        }
    }

    private void OnStatusChanged(object? sender, RgbStatus status)
    {
        RgbConnectionState previous;
        lock (_gate)
        {
            previous = _lastState;
            _lastState = status.State;
        }
        if (_disposed || previous != RgbConnectionState.Connected) return;
        if (status.State is RgbConnectionState.Error or RgbConnectionState.Disconnected) StartReconnect();
    }

    private void StartReconnect()
    {
        if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0) return;
        _ = Task.Run(() => ReconnectLoopAsync(_cts.Token));
    }

    /// <summary>Back-off 5 s, 15 s, 60 s, then every 5 min until connected (by us or by the user).</summary>
    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        _log.LogInformation("OpenRGB connection lost; reconnecting after 5 s, 15 s, 60 s, then every 5 minutes");
        try
        {
            for (var attempt = 0; !ct.IsCancellationRequested; attempt++)
            {
                await _delay(attempt < ReconnectDelays.Length ? ReconnectDelays[attempt] : ReconnectSteady, ct).ConfigureAwait(false);
                if (_rgb.Status.State == RgbConnectionState.Connected)
                {
                    _log.LogInformation("OpenRGB connected again");
                    break;
                }
                if (await ConnectCoreAsync(quiet: true, ct).ConfigureAwait(false))
                {
                    _log.LogInformation("OpenRGB reconnected after {Attempts} attempt(s)", attempt + 1);
                    DevicesChanged?.Invoke();
                    break;
                }
                if (attempt == ReconnectDelays.Length - 1) _log.LogInformation("OpenRGB still unreachable; retrying every 5 minutes");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OpenRGB reconnect loop failed");
        }
        finally
        {
            Volatile.Write(ref _reconnecting, 0);
        }
    }

    /// <summary>Stops the background work (reconnects, detection watch, restore). The controller is disposed by the agent.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rgb.StatusChanged -= OnStatusChanged;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }
}
