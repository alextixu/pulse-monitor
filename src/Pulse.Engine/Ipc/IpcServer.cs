using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Pulse.Core.Abstractions;
using Pulse.Core.FanControl;
using Pulse.Core.Ipc;
using Pulse.Core.Models;

namespace Pulse.Engine.Ipc;

/// <summary>
/// The agent side of the pipe: accepts clients (one server stream per client, created by <c>createPipe</c> so the host can
/// apply its security descriptor), answers <c>ClientHelloMessage</c> with a Hello, forwards engine events, dispatches each
/// request to <see cref="AgentCore"/> as its own task and replies through a single writer per client with two lanes
/// (latest-only Hardware/Batteries frames; a never-dropped FIFO for everything else). See decisions-v2.md §15–16.
/// Event handlers only enqueue (they run on the engine thread and must never block it).
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    /// <summary>A client whose outbound FIFO has not moved for this long is disconnected.</summary>
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly AgentCore _agent;
    private readonly Func<NamedPipeServerStream> _createPipe;
    private readonly ILogger<IpcServer> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<Client> _clients = new();
    private Func<bool, string?, bool>? _showPopover;
    private Task? _acceptLoop;
    private Timer? _stallTimer;
    private int _nextId;
    private long _helloSequence;
    private bool _anyVisible;
    private AgentExitReason? _exitReason;
    private int _started;
    private int _disposed;

    public IpcServer(AgentCore agent, Func<NamedPipeServerStream> createPipe, ILogger<IpcServer> log)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _createPipe = createPipe ?? throw new ArgumentNullException(nameof(createPipe));
        _log = log;
    }

    /// <summary>Starts listening; sets <see cref="AgentCore.ShowPopoverInClient"/>.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0 || Volatile.Read(ref _disposed) != 0) return;

        _agent.InfoChanged += OnInfoChanged;
        _agent.SettingsChanged += OnSettingsChanged;
        _agent.HardwareStatusChanged += OnHardwareStatusChanged;
        _agent.RgbStatusChanged += OnRgbStatusChanged;
        _agent.HardwareUpdated += OnHardwareUpdated;
        _agent.BatteriesUpdated += OnBatteriesUpdated;
        _agent.RgbDevicesChanged += OnRgbDevicesChanged;
        _agent.LowBattery += OnLowBattery;
        _agent.Exiting += OnExiting;
        _showPopover = ShowPopover;
        _agent.ShowPopoverInClient = _showPopover;

        _stallTimer = new Timer(_ => CheckStalled(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _log.LogInformation("Pipe server listening");
    }

    public int ClientCount
    {
        get { lock (_gate) return _clients.Count; }
    }

    /// <summary>Sends Exiting to every client (bounded wait), closes the pipes and stops listening.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        if (_started != 0)
        {
            _agent.InfoChanged -= OnInfoChanged;
            _agent.SettingsChanged -= OnSettingsChanged;
            _agent.HardwareStatusChanged -= OnHardwareStatusChanged;
            _agent.RgbStatusChanged -= OnRgbStatusChanged;
            _agent.HardwareUpdated -= OnHardwareUpdated;
            _agent.BatteriesUpdated -= OnBatteriesUpdated;
            _agent.RgbDevicesChanged -= OnRgbDevicesChanged;
            _agent.LowBattery -= OnLowBattery;
            _agent.Exiting -= OnExiting;
            if (ReferenceEquals(_agent.ShowPopoverInClient, _showPopover)) _agent.ShowPopoverInClient = null;
        }
        _stallTimer?.Dispose();

        var clients = Snapshot();
        foreach (var client in clients)
        {
            client.Send(new ExitingMessage(_exitReason ?? AgentExitReason.Quit), beforeHello: true, onlyIfNoExiting: true);
            client.CloseWhenDrained();
        }
        try
        {
            await Task.WhenAll(clients.Select(c => c.Completion)).WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Not every client drained before the pipe server closed");
        }
        foreach (var client in clients) client.Close();

        if (_acceptLoop is { } loop)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { /* shutting down */ }
        }
        _log.LogInformation("Pipe server stopped");
    }

    // ───────────── accept loop ─────────────

    private async Task AcceptLoopAsync()
    {
        var failures = 0;
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = _createPipe();
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                failures = 0;
                var client = new Client(this, pipe, Interlocked.Increment(ref _nextId));
                pipe = null;
                lock (_gate) _clients.Add(client);
                _log.LogDebug("Pipe client {Id} connected", client.Id);
                client.Start();
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                if (failures == 1 || failures % 20 == 0) _log.LogWarning(ex, "Pipe accept failed ({Count} in a row)", failures);
                try { await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Min(failures, 20)), _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    // ───────────── engine events → clients ─────────────

    private void OnInfoChanged(object? sender, AgentInfo info) => Broadcast(new InfoMessage(info));
    private void OnSettingsChanged(object? sender, SettingsState settings) => Broadcast(new SettingsMessage(settings));
    private void OnHardwareStatusChanged(object? sender, HardwareMonitorStatus status) => Broadcast(new HardwareStatusMessage(status));
    private void OnRgbStatusChanged(object? sender, RgbStatus status) => Broadcast(new RgbStatusMessage(status));
    private void OnRgbDevicesChanged(object? sender, EventArgs e) => Broadcast(new RgbDevicesChangedMessage());

    private void OnBatteriesUpdated(object? sender, BatteryState batteries)
    {
        var message = new BatteriesMessage(batteries);
        foreach (var client in Snapshot()) client.SetLatest(Lane.Batteries, message);
    }

    private void OnHardwareUpdated(object? sender, HardwareFrame frame)
    {
        HardwareMessage? message = null;
        foreach (var client in Snapshot())
        {
            if (client.IsVisible) client.SetLatest(Lane.Hardware, message ??= new HardwareMessage(frame));
        }
    }

    private void OnLowBattery(object? sender, LowBatteryToast toast)
    {
        var message = new LowBatteryMessage(toast);
        foreach (var client in Snapshot())
        {
            if (client.IsVisible) client.Send(message);
        }
    }

    private void OnExiting(object? sender, AgentExitReason reason)
    {
        _exitReason = reason;
        var message = new ExitingMessage(reason);
        foreach (var client in Snapshot()) client.Send(message, beforeHello: true, onlyIfNoExiting: true);
    }

    private void Broadcast(IpcMessage message)
    {
        foreach (var client in Snapshot()) client.Send(message);
    }

    /// <summary>The tray asks a connected popover to show / toggle: the most recent client that said hello.</summary>
    private bool ShowPopover(bool toggle, string? tab)
    {
        Client? target;
        lock (_gate) target = _clients.Where(c => c.HelloSequence > 0 && !c.IsClosed).MaxBy(c => c.HelloSequence);
        if (target is null) return false;
        target.Send(new ShowPopoverMessage(toggle, tab));
        return true;
    }

    private Client[] Snapshot()
    {
        lock (_gate) return _clients.ToArray();
    }

    private void Remove(Client client)
    {
        lock (_gate)
        {
            if (!_clients.Remove(client)) return;
        }
        _log.LogDebug("Pipe client {Id} disconnected", client.Id);
        UpdateVisibility();
    }

    /// <summary>The engine's visibility is "any client visible"; told only on a change.</summary>
    private void UpdateVisibility()
    {
        lock (_gate)
        {
            var any = _clients.Any(c => c.IsVisible);
            if (any == _anyVisible) return;
            _anyVisible = any;
            _agent.SetUiVisible(any);
        }
    }

    private void CheckStalled()
    {
        var now = Environment.TickCount64;
        foreach (var client in Snapshot())
        {
            if (!client.IsStalled(now, StallLimit)) continue;
            _log.LogWarning("Pipe client {Id} has not read anything for {Seconds}s; disconnecting it", client.Id, StallLimit.TotalSeconds);
            client.Close();
        }
    }

    private enum Lane
    {
        Hardware,
        Batteries,
    }

    /// <summary>One connected client: a read loop, a single writer task and the two outbound lanes.</summary>
    private sealed class Client
    {
        /// <summary>
        /// A UI sends SetVisibility right after ClientHello; waiting this long for it lets the Hello carry the pending
        /// low-battery toasts (they go only to a visible client).
        /// </summary>
        private static readonly TimeSpan HelloVisibilityGrace = TimeSpan.FromMilliseconds(50);

        /// <summary>FIFO placeholder: the Hello is built by the writer when it is its turn, from the state at that moment.</summary>
        private static readonly IpcMessage HelloPending = new();

        private readonly IpcServer _server;
        private readonly NamedPipeServerStream _pipe;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _gate = new();
        private readonly Queue<(IpcMessage Message, long EnqueuedAt)> _fifo = new();
        private readonly SemaphoreSlim _signal = new(0, 1);
        private TaskCompletionSource? _visibilityWaiter;
        private IpcMessage? _hardwareSlot;
        private IpcMessage? _batteriesSlot;
        private long _writeStartedAt;
        private bool _helloQueued;
        private bool _helloSent;
        private bool _visible;
        private bool _toastsDelivered;
        private bool _exitingQueued;
        private bool _closeWhenDrained;
        private bool _closed;

        public Client(IpcServer server, NamedPipeServerStream pipe, int id)
        {
            _server = server;
            _pipe = pipe;
            Id = id;
            Completion = Task.CompletedTask;
        }

        public int Id { get; }

        /// <summary>Order of the Hello (0 = none yet): the most recent client gets ShowPopover.</summary>
        public long HelloSequence { get; private set; }

        /// <summary>Completes when the writer ended (drained and closed, or failed).</summary>
        public Task Completion { get; private set; }

        public bool IsVisible
        {
            get { lock (_gate) return _visible && !_closed; }
        }

        public bool IsClosed
        {
            get { lock (_gate) return _closed; }
        }

        private AgentCore Agent => _server._agent;
        private ILogger Log => _server._log;

        public void Start()
        {
            Completion = Task.Run(WriteLoopAsync);
            _ = Task.Run(ReadLoopAsync);
        }

        /// <summary>FIFO lane (never dropped). Before the Hello only replies and Exiting go out.</summary>
        public void Send(IpcMessage message, bool beforeHello = false, bool onlyIfNoExiting = false)
        {
            lock (_gate)
            {
                if (_closed || !_helloSent && !beforeHello) return;
                if (message is ExitingMessage)
                {
                    if (onlyIfNoExiting && _exitingQueued) return;
                    _exitingQueued = true;
                }
                _fifo.Enqueue((message, Environment.TickCount64));
            }
            Signal();
        }

        /// <summary>Latest-only lane: an unsent frame is replaced by the newer one.</summary>
        public void SetLatest(Lane lane, IpcMessage message)
        {
            lock (_gate)
            {
                if (_closed || !_helloSent) return;
                if (lane == Lane.Hardware) _hardwareSlot = message;
                else _batteriesSlot = message;
            }
            Signal();
        }

        public void CloseWhenDrained()
        {
            lock (_gate) _closeWhenDrained = true;
            Signal();
        }

        public bool IsStalled(long now, TimeSpan limit)
        {
            lock (_gate)
            {
                if (_closed) return false;
                var started = Interlocked.Read(ref _writeStartedAt);
                if (started != 0 && now - started > limit.TotalMilliseconds) return true;
                return _fifo.Count > 0 && now - _fifo.Peek().EnqueuedAt > limit.TotalMilliseconds;
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                _fifo.Clear();
                _hardwareSlot = null;
                _batteriesSlot = null;
            }
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            try { _pipe.Dispose(); } catch { /* already broken */ }
            Signal();
            _server.Remove(this);
        }

        private void Signal()
        {
            try { _signal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        private bool TryTake(out IpcMessage message)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    message = null!;
                    return false;
                }
                if (_fifo.Count > 0)
                {
                    message = _fifo.Dequeue().Message;
                    return true;
                }
                if (_hardwareSlot is { } hardware)
                {
                    _hardwareSlot = null;
                    message = hardware;
                    return true;
                }
                if (_batteriesSlot is { } batteries)
                {
                    _batteriesSlot = null;
                    message = batteries;
                    return true;
                }
                message = null!;
                return false;
            }
        }

        private async Task WriteLoopAsync()
        {
            var ct = _cts.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _signal.WaitAsync(ct).ConfigureAwait(false);
                    while (TryTake(out var message))
                    {
                        if (ReferenceEquals(message, HelloPending))
                        {
                            await WaitForVisibilityAsync(ct).ConfigureAwait(false);
                            message = BuildHello();
                        }

                        byte[] frame;
                        try
                        {
                            frame = IpcCodec.Encode(message);
                        }
                        catch (Exception ex)
                        {
                            Log.LogWarning(ex, "Pipe client {Id}: dropping an unencodable {Type}", Id, message.GetType().Name);
                            continue;
                        }

                        Interlocked.Exchange(ref _writeStartedAt, Environment.TickCount64);
                        await _pipe.WriteAsync(frame, ct).ConfigureAwait(false);
                        Interlocked.Exchange(ref _writeStartedAt, 0);
                    }

                    bool close;
                    lock (_gate) close = _closeWhenDrained && _fifo.Count == 0 && _hardwareSlot is null && _batteriesSlot is null;
                    if (close)
                    {
                        // Let the client read what is buffered before the handle goes away.
                        try { await Task.Run(() => _pipe.WaitForPipeDrain(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                        catch { /* best effort */ }
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException ex)
            {
                Log.LogDebug("Pipe client {Id}: write failed ({Error})", Id, ex.Message);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Pipe client {Id}: writer failed", Id);
            }
            finally
            {
                Close();
            }
        }

        private async Task ReadLoopAsync()
        {
            var ct = _cts.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var message = await IpcCodec.ReadAsync(_pipe, ct).ConfigureAwait(false);
                    if (message is null) break;
                    Handle(message);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (InvalidDataException ex)
            {
                Log.LogWarning("Pipe client {Id}: bad frame ({Error}); disconnecting", Id, ex.Message);
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Pipe client {Id}: reader failed", Id);
            }
            finally
            {
                // A client that hung up gets nothing more; one we are closing deliberately is closed by its writer.
                bool closing;
                lock (_gate) closing = _closeWhenDrained;
                if (!closing) Close();
            }
        }

        private void Handle(IpcMessage message)
        {
            switch (message)
            {
                case ClientHelloMessage hello:
                    OnHello(hello);
                    break;
                case SetVisibilityMessage visibility:
                    OnVisibility(visibility.Visible);
                    break;
                case IpcRequest request:
                    _ = Task.Run(() => DispatchAsync(request));
                    break;
                default:
                    Log.LogDebug("Pipe client {Id}: ignoring a message this agent does not understand", Id);
                    break;
            }
        }

        private void OnHello(ClientHelloMessage hello)
        {
            if (hello.Protocol != IpcProtocol.Version)
            {
                Log.LogInformation("Pipe client {Id} (pid {Pid}) speaks protocol {Protocol}, this agent {Version}; closing",
                    Id, hello.ProcessId, hello.Protocol, IpcProtocol.Version);
                Send(new ReplyMessage(0, false, "protocol"), beforeHello: true);
                CloseWhenDrained();
                return;
            }

            lock (_gate)
            {
                if (_helloQueued || _closed) return;
                _helloQueued = true;
                _fifo.Enqueue((HelloPending, Environment.TickCount64));
            }
            Log.LogDebug("Pipe client {Id}: hello from pid {Pid}", Id, hello.ProcessId);
            Signal();
        }

        private async Task WaitForVisibilityAsync(CancellationToken ct)
        {
            Task wait;
            lock (_gate)
            {
                if (_visible) return;
                wait = (_visibilityWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            try { await wait.WaitAsync(HelloVisibilityGrace, ct).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }

        /// <summary>
        /// Writer: the Hello from the current state. Built under the lock that event forwarding takes, so every event
        /// raised before it is reflected in it (and dropped), every later one is queued after it.
        /// </summary>
        private HelloMessage BuildHello()
        {
            lock (_gate)
            {
                var agent = Agent;
                IReadOnlyList<LowBatteryToast> toasts = Array.Empty<LowBatteryToast>();
                if (_visible)
                {
                    toasts = agent.TakePendingToasts();
                    _toastsDelivered = true;
                }
                var helloMessage = new HelloMessage(IpcProtocol.Version, agent.Info, agent.Settings, agent.HardwareStatus,
                    agent.RgbStatus, agent.Hardware, agent.Batteries, toasts);
                _hardwareSlot = null;
                _batteriesSlot = null;
                _helloSent = true;
                HelloSequence = Interlocked.Increment(ref _server._helloSequence);
                return helloMessage;
            }
        }

        private void OnVisibility(bool visible)
        {
            bool becameVisible;
            lock (_gate)
            {
                becameVisible = visible && !_visible;
                _visible = visible;
                if (becameVisible && _visibilityWaiter is { } waiter)
                {
                    _visibilityWaiter = null;
                    waiter.TrySetResult();
                }
                if (becameVisible && _helloSent)
                {
                    // The popover needs a frame now, not at the next poll.
                    _hardwareSlot = new HardwareMessage(Agent.Hardware);
                    if (!_toastsDelivered)
                    {
                        _toastsDelivered = true;
                        foreach (var toast in Agent.TakePendingToasts()) _fifo.Enqueue((new LowBatteryMessage(toast), Environment.TickCount64));
                    }
                }
            }
            Log.LogDebug("Pipe client {Id}: {State}", Id, visible ? "visible" : "hidden");
            _server.UpdateVisibility();
            if (becameVisible) Signal();
        }

        private async Task DispatchAsync(IpcRequest request)
        {
            IpcMessage reply;
            try
            {
                reply = await HandleRequestAsync(request, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return; // the client is gone
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Pipe client {Id}: request {Type} failed", Id, request.GetType().Name);
                reply = new ReplyMessage(request.Id, false, ex.Message);
            }
            Send(reply, beforeHello: true);
        }

        private async Task<IpcMessage> HandleRequestAsync(IpcRequest request, CancellationToken ct)
        {
            var agent = Agent;
            switch (request)
            {
                case RefreshNowRequest r:
                    return Reply(r.Id, await agent.RefreshNowAsync(ct).ConfigureAwait(false));
                case ConnectRgbRequest r:
                {
                    var result = await agent.ConnectRgbAsync(ct).ConfigureAwait(false);
                    return new ConnectRgbReplyMessage(r.Id, result.Ok, result.Error, result.Status);
                }
                case RgbGetDevicesRequest r:
                    return Devices(r.Id, await agent.GetRgbDevicesAsync(r.Refresh, ct).ConfigureAwait(false));
                case RgbCommandRequest r:
                    return Devices(r.Id, await agent.RgbCommandAsync(r.Command, ct).ConfigureAwait(false));
                case SetFanRequest r:
                    return Reply(r.Id, await agent.SetFanAsync(r.FanId, r.Percent, ct).ConfigureAwait(false));
                case SetFanModeRequest r:
                    return Reply(r.Id, await agent.SetFanModeAsync(r.Mode, ct).ConfigureAwait(false));
                case SetSyncedPercentRequest r:
                    return Reply(r.Id, await agent.SetSyncedPercentAsync(r.Percent, ct).ConfigureAwait(false));
                case SetFanMembershipRequest r:
                    return Reply(r.Id, await agent.SetFanMembershipAsync(r.FanId, r.Include, ct).ConfigureAwait(false));
                case UpdateSettingRequest r:
                    return Reply(r.Id, await agent.UpdateSettingAsync(r.Patch, ct).ConfigureAwait(false));
                case SetAutoStartRequest r:
                    return Reply(r.Id, await agent.SetAutoStartAsync(r.Enabled, ct).ConfigureAwait(false));
                case ElevateRequest r:
                    return Reply(r.Id, await agent.ElevateAsync(r.ShowUi, ct).ConfigureAwait(false));
                case QuitRequest r:
                    Log.LogInformation("Pipe client {Id} asked the agent to quit", Id);
                    agent.RequestQuit();
                    return new ReplyMessage(r.Id, true, null);
                default:
                    return new ReplyMessage(request.Id, false, "unknown request");
            }

            static ReplyMessage Reply(int id, CommandResult result) => new(id, result.Ok, result.Error);
            static RgbDevicesReplyMessage Devices(int id, RgbDevicesResult result) => new(id, result.Ok, result.Error, result.Devices);
        }
    }
}
