using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pulse.App.Localization;
using Pulse.Core.Abstractions;
using Pulse.Core.Ipc;
using Pulse.Core.Models;

namespace Pulse.App.Services;

/// <summary>The agent's tray icon was clicked while this popover is connected (toggle) or it asked for a tab.</summary>
public sealed record PopoverRequest(bool Toggle, string? Tab);

public enum AgentConnectOutcome
{
    Connected,
    /// <summary>No agent answered within the connect window.</summary>
    Unavailable,
    /// <summary>The agent speaks another <see cref="IpcProtocol.Version"/>.</summary>
    ProtocolMismatch,
}

/// <summary>
/// <see cref="IPulseAgent"/> over the agent's named pipe (byte mode, length-prefixed JSON, see <see cref="IpcCodec"/>).
/// One writer task drains an outbound queue (ClientHello always first), one read loop dispatches replies by request id
/// and keeps the pushed state (Hello, then Settings / Hardware / … messages). Requests time out (default 10 s,
/// ConnectRgb 45 s, RefreshNow 30 s, SetFan 15 s) and then answer <see cref="CommandResult.Fail"/>; nothing throws.
/// Events are raised on the read-loop thread, like the engine raises them on its own thread; callers marshal.
/// </summary>
public sealed class IpcPulseClient : IPulseAgent, IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectRgbTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SetFanTimeout = TimeSpan.FromSeconds(15);
    /// <summary>
    /// Pause between connect attempts. Each attempt is immediate (timeout 0): with a longer timeout the framework
    /// busy-waits while the pipe does not exist yet, which would burn a core for the whole window without an agent.
    /// </summary>
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly ILogger<IpcPulseClient> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<IpcMessage> _outbound = Channel.CreateUnbounded<IpcMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<int, TaskCompletionSource<IpcMessage?>> _pending = new();
    private readonly TaskCompletionSource<HelloMessage?> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Completes with true once Hello arrived, false when the connection failed or ended.</summary>
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _visibilityGate = new();

    private NamedPipeClientStream? _pipe;
    private Task? _writer;
    private Task? _reader;
    private int _nextId;
    private int _exitRaised;
    private bool _protocolMismatch;
    private bool _connected;
    private bool? _visible;
    private IReadOnlyList<LowBatteryToast> _pendingToasts = Array.Empty<LowBatteryToast>();

    public IpcPulseClient(ILogger<IpcPulseClient> log) => _log = log;

    // ───────────── state (last pushed by the agent; defaults until Hello) ─────────────

    public AgentInfo Info { get; private set; } = AgentInfo.Empty;
    public SettingsState Settings { get; private set; } = SettingsState.Empty;
    public HardwareMonitorStatus HardwareStatus { get; private set; } = new();
    public RgbStatus RgbStatus { get; private set; } = new();
    public HardwareFrame Hardware { get; private set; } = HardwareFrame.Empty;
    public BatteryState Batteries { get; private set; } = BatteryState.Empty;

    public event EventHandler<AgentInfo>? InfoChanged;
    public event EventHandler<SettingsState>? SettingsChanged;
    public event EventHandler<HardwareMonitorStatus>? HardwareStatusChanged;
    public event EventHandler<RgbStatus>? RgbStatusChanged;
    public event EventHandler<HardwareFrame>? HardwareUpdated;
    public event EventHandler<BatteryState>? BatteriesUpdated;
    public event EventHandler? RgbDevicesChanged;
    public event EventHandler<LowBatteryToast>? LowBattery;
    /// <summary>The agent announced its exit, or the pipe broke after Hello (raised once; reported as Quit then).</summary>
    public event EventHandler<AgentExitReason>? Exiting;
    /// <summary>The agent's tray icon was clicked (or the agent opened a tab) while this client is connected.</summary>
    public event EventHandler<PopoverRequest>? ShowPopoverRequested;

    // ───────────── connection ─────────────

    /// <summary>
    /// Connects (retrying for <paramref name="window"/> while no agent listens), sends ClientHello and waits for Hello.
    /// </summary>
    public async Task<AgentConnectOutcome> ConnectAsync(TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        var ct = _cts.Token;
        NamedPipeClientStream? pipe = null;
        Exception? lastError = null;

        while (pipe is null)
        {
            if (ct.IsCancellationRequested) return Fail(AgentConnectOutcome.Unavailable);
            var candidate = new NamedPipeClientStream(".", IpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await candidate.ConnectAsync(0, ct).ConfigureAwait(false);
                pipe = candidate;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
                lastError = ex;
                if (DateTime.UtcNow >= deadline || ct.IsCancellationRequested)
                {
                    _log.LogWarning("No Pulse agent on pipe {Pipe} after {Seconds:0} s ({Error})", IpcProtocol.PipeName, window.TotalSeconds, lastError.GetType().Name);
                    return Fail(AgentConnectOutcome.Unavailable);
                }
                try { await Task.Delay(ConnectRetryDelay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }

        _pipe = pipe;
        // ClientHello is the first frame on the wire; visibility follows it if the window is already showing.
        _outbound.Writer.TryWrite(new ClientHelloMessage(IpcProtocol.Version, Environment.ProcessId));
        lock (_visibilityGate)
        {
            _connected = true;
            if (_visible is { } visible) _outbound.Writer.TryWrite(new SetVisibilityMessage(visible));
        }
        _writer = Task.Run(() => WriteLoopAsync(pipe, ct));
        _reader = Task.Run(() => ReadLoopAsync(pipe, ct));

        var remaining = deadline - DateTime.UtcNow;
        if (remaining < TimeSpan.FromSeconds(2)) remaining = TimeSpan.FromSeconds(2);
        HelloMessage? hello;
        try
        {
            hello = await _hello.Task.WaitAsync(remaining, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _log.LogWarning("The agent did not answer ClientHello within {Seconds:0} s", remaining.TotalSeconds);
            return Fail(AgentConnectOutcome.Unavailable);
        }

        if (hello is null) return Fail(_protocolMismatch ? AgentConnectOutcome.ProtocolMismatch : AgentConnectOutcome.Unavailable);
        if (hello.Protocol != IpcProtocol.Version)
        {
            _log.LogWarning("Agent protocol {Agent} differs from ours ({Ours})", hello.Protocol, IpcProtocol.Version);
            return Fail(AgentConnectOutcome.ProtocolMismatch);
        }

        if (!_ready.TrySetResult(true))
        {
            // The pipe closed right after Hello (the agent is going away).
            return AgentConnectOutcome.Unavailable;
        }
        _log.LogInformation("Connected to agent pid {Pid} {Version} (elevated={Elevated})", hello.Info.ProcessId, hello.Info.Version, hello.Info.IsElevated);
        return AgentConnectOutcome.Connected;

        AgentConnectOutcome Fail(AgentConnectOutcome outcome)
        {
            _ready.TrySetResult(false);
            return outcome;
        }
    }

    private async Task WriteLoopAsync(Stream pipe, CancellationToken ct)
    {
        try
        {
            await foreach (var message in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await IpcCodec.WriteAsync(pipe, message, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Pipe write failed");
            OnDisconnected();
        }
    }

    private async Task ReadLoopAsync(Stream pipe, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await IpcCodec.ReadAsync(pipe, ct).ConfigureAwait(false);
                if (message is null) break;
                try
                {
                    Dispatch(message);
                }
                catch (Exception ex)
                {
                    // A faulty handler must not end the connection.
                    _log.LogError(ex, "Handling {Type} from the agent failed", message.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return; // Disposed.
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Pipe read ended");
        }

        if (!ct.IsCancellationRequested) OnDisconnected();
    }

    private void Dispatch(IpcMessage message)
    {
        switch (message)
        {
            case HelloMessage hello:
                Info = hello.Info;
                Settings = hello.Settings;
                HardwareStatus = hello.HardwareStatus;
                RgbStatus = hello.RgbStatus;
                Hardware = hello.Hardware;
                Batteries = hello.Batteries;
                _pendingToasts = hello.PendingToasts ?? Array.Empty<LowBatteryToast>();
                _hello.TrySetResult(hello);
                break;
            case InfoMessage m:
                Info = m.Info;
                InfoChanged?.Invoke(this, m.Info);
                break;
            case SettingsMessage m:
                Settings = m.Settings;
                SettingsChanged?.Invoke(this, m.Settings);
                break;
            case HardwareStatusMessage m:
                HardwareStatus = m.Status;
                HardwareStatusChanged?.Invoke(this, m.Status);
                break;
            case RgbStatusMessage m:
                RgbStatus = m.Status;
                RgbStatusChanged?.Invoke(this, m.Status);
                break;
            case HardwareMessage m:
                Hardware = m.Frame;
                HardwareUpdated?.Invoke(this, m.Frame);
                break;
            case BatteriesMessage m:
                Batteries = m.Batteries;
                BatteriesUpdated?.Invoke(this, m.Batteries);
                break;
            case RgbDevicesChangedMessage:
                RgbDevicesChanged?.Invoke(this, EventArgs.Empty);
                break;
            case LowBatteryMessage m:
                LowBattery?.Invoke(this, m.Toast);
                break;
            case ShowPopoverMessage m:
                ShowPopoverRequested?.Invoke(this, new PopoverRequest(m.Toggle, m.Tab));
                break;
            case ExitingMessage m:
                _log.LogInformation("Agent exiting ({Reason})", m.Reason);
                RaiseExiting(m.Reason);
                break;
            case ReplyMessage { Id: 0, Ok: false } m when !_hello.Task.IsCompleted:
                // The agent refused ClientHello (protocol mismatch); it closes the pipe next.
                _log.LogWarning("Agent refused the connection: {Error}", m.Error);
                _protocolMismatch = true;
                _hello.TrySetResult(null);
                break;
            case ReplyMessage m:
                Complete(m.Id, m);
                break;
            case ConnectRgbReplyMessage m:
                Complete(m.Id, m);
                break;
            case RgbDevicesReplyMessage m:
                Complete(m.Id, m);
                break;
            default:
                _log.LogDebug("Skipping unknown agent message {Type}", message.GetType().Name);
                break;
        }
    }

    private void Complete(int id, IpcMessage reply)
    {
        if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(reply);
        else _log.LogDebug("Reply {Id} arrived after its request timed out", id);
    }

    private void OnDisconnected()
    {
        _hello.TrySetResult(null);
        _ready.TrySetResult(false);
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(null);
        }
        if (_ready.Task.IsCompletedSuccessfully && _ready.Task.Result)
        {
            _log.LogInformation("Agent pipe closed");
            RaiseExiting(AgentExitReason.Quit);
        }
    }

    private void RaiseExiting(AgentExitReason reason)
    {
        if (Interlocked.Exchange(ref _exitRaised, 1) == 1) return;
        Exiting?.Invoke(this, reason);
    }

    // ───────────── requests ─────────────

    /// <summary>
    /// Sends a request and waits for its reply; null when it timed out or the connection is gone (the caller turns that
    /// into a failure text). Requests issued before Hello wait for the connection first (within the same timeout).
    /// </summary>
    private async Task<(IpcMessage? Reply, string Error)> SendAsync(Func<int, IpcRequest> build, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        linked.CancelAfter(timeout);
        try
        {
            if (!await _ready.Task.WaitAsync(linked.Token).ConfigureAwait(false))
            {
                return (null, _connected ? UiStrings.AgentDisconnected : UiStrings.AgentUnavailable);
            }
        }
        catch (OperationCanceledException)
        {
            return (null, UiStrings.RequestTimedOut);
        }

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<IpcMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var request = build(id);
        if (!_outbound.Writer.TryWrite(request))
        {
            _pending.TryRemove(id, out _);
            return (null, UiStrings.AgentDisconnected);
        }

        try
        {
            var reply = await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            return (reply, reply is null ? UiStrings.AgentDisconnected : string.Empty);
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            _log.LogWarning("{Request} got no reply within {Seconds:0} s", request.GetType().Name, timeout.TotalSeconds);
            return (null, UiStrings.RequestTimedOut);
        }
    }

    private async Task<CommandResult> CommandAsync(Func<int, IpcRequest> build, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var (reply, error) = await SendAsync(build, timeout, cancellationToken).ConfigureAwait(false);
        return reply switch
        {
            ReplyMessage r => new CommandResult(r.Ok, r.Error),
            ConnectRgbReplyMessage r => new CommandResult(r.Ok, r.Error),
            RgbDevicesReplyMessage r => new CommandResult(r.Ok, r.Error),
            _ => CommandResult.Fail(error),
        };
    }

    private async Task<RgbDevicesResult> DevicesAsync(Func<int, IpcRequest> build, CancellationToken cancellationToken)
    {
        var (reply, error) = await SendAsync(build, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        return reply switch
        {
            RgbDevicesReplyMessage r => new RgbDevicesResult(r.Ok, r.Devices ?? Array.Empty<RgbDevice>(), r.Error),
            ReplyMessage r => new RgbDevicesResult(r.Ok, Array.Empty<RgbDevice>(), r.Error),
            _ => RgbDevicesResult.Failed(error),
        };
    }

    public void SetUiVisible(bool visible)
    {
        lock (_visibilityGate)
        {
            _visible = visible;
            if (_connected) _outbound.Writer.TryWrite(new SetVisibilityMessage(visible));
        }
    }

    /// <summary>The low-battery alerts delivered with Hello (returned once).</summary>
    public IReadOnlyList<LowBatteryToast> TakePendingToasts()
    {
        var toasts = _pendingToasts;
        _pendingToasts = Array.Empty<LowBatteryToast>();
        return toasts;
    }

    public Task<CommandResult> RefreshNowAsync(CancellationToken cancellationToken = default)
        => CommandAsync(id => new RefreshNowRequest(id), RefreshTimeout, cancellationToken);

    public async Task<ConnectRgbResult> ConnectRgbAsync(CancellationToken cancellationToken = default)
    {
        var (reply, error) = await SendAsync(id => new ConnectRgbRequest(id), ConnectRgbTimeout, cancellationToken).ConfigureAwait(false);
        return reply switch
        {
            ConnectRgbReplyMessage r => new ConnectRgbResult(r.Ok, r.Status ?? RgbStatus, r.Error),
            ReplyMessage r => new ConnectRgbResult(r.Ok, RgbStatus, r.Error),
            _ => new ConnectRgbResult(false, RgbStatus, error),
        };
    }

    public Task<RgbDevicesResult> GetRgbDevicesAsync(bool refresh, CancellationToken cancellationToken = default)
        => DevicesAsync(id => new RgbGetDevicesRequest(id, refresh), cancellationToken);

    public Task<RgbDevicesResult> RgbCommandAsync(RgbCommand command, CancellationToken cancellationToken = default)
        => DevicesAsync(id => new RgbCommandRequest(id, command), cancellationToken);

    public Task<CommandResult> SetFanAsync(string fanId, double? percent, CancellationToken cancellationToken = default)
        => CommandAsync(id => new SetFanRequest(id, fanId, percent), SetFanTimeout, cancellationToken);

    public Task<CommandResult> SetFanModeAsync(FanMode mode, CancellationToken cancellationToken = default)
        => CommandAsync(id => new SetFanModeRequest(id, mode), DefaultTimeout, cancellationToken);

    public Task<CommandResult> SetSyncedPercentAsync(double percent, CancellationToken cancellationToken = default)
        => CommandAsync(id => new SetSyncedPercentRequest(id, percent), DefaultTimeout, cancellationToken);

    public Task<CommandResult> SetFanMembershipAsync(string fanId, bool include, CancellationToken cancellationToken = default)
        => CommandAsync(id => new SetFanMembershipRequest(id, fanId, include), DefaultTimeout, cancellationToken);

    public Task<CommandResult> UpdateSettingAsync(SettingsPatch patch, CancellationToken cancellationToken = default)
        => CommandAsync(id => new UpdateSettingRequest(id, patch), DefaultTimeout, cancellationToken);

    public Task<CommandResult> SetAutoStartAsync(bool enabled, CancellationToken cancellationToken = default)
        => CommandAsync(id => new SetAutoStartRequest(id, enabled), DefaultTimeout, cancellationToken);

    public Task<CommandResult> ElevateAsync(bool showUi, CancellationToken cancellationToken = default)
        => CommandAsync(id => new ElevateRequest(id, showUi), DefaultTimeout, cancellationToken);

    /// <summary>Asks the agent to quit; it answers with Exiting, which ends this popover too.</summary>
    public void RequestQuit()
    {
        _ = CommandAsync(id => new QuitRequest(id), DefaultTimeout, CancellationToken.None).ContinueWith(
            t => { if (t.Result is { Ok: false, Error: var e }) _log.LogWarning("Quit request failed: {Error}", e); },
            TaskScheduler.Default);
    }

    // ───────────── teardown ─────────────

    /// <summary>
    /// Lets the writer send what is queued (≤ <paramref name="drain"/>: a setting changed right before the popover hid
    /// must still reach the agent), then closes the pipe. No <see cref="Exiting"/> is raised for our own close.
    /// </summary>
    public async Task CloseAsync(TimeSpan drain)
    {
        Interlocked.Exchange(ref _exitRaised, 1);
        _outbound.Writer.TryComplete();
        if (_writer is { } writer)
        {
            try { await writer.WaitAsync(drain).ConfigureAwait(false); } catch { /* timeout or write failure */ }
        }
        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _exitRaised, 1);
        _outbound.Writer.TryComplete();
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        _hello.TrySetResult(null);
        _ready.TrySetResult(false);
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(null);
        }
        if (_pipe is { } pipe)
        {
            _pipe = null;
            try { await pipe.DisposeAsync().ConfigureAwait(false); } catch { /* already broken */ }
        }
        foreach (var loop in new[] { _reader, _writer })
        {
            if (loop is null) continue;
            try { await loop.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); } catch { /* ended or stuck */ }
        }
        // _cts is not disposed: a late command (e.g. a fire-and-forget Quit) may still link to its token.
    }
}
