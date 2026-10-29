using System.IO.Pipes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pulse.Core.Abstractions;
using Pulse.Core.Ipc;
using Pulse.Core.Models;
using Pulse.Engine.Demo;
using Pulse.Engine.Ipc;

namespace Pulse.Engine.Tests.Ipc;

/// <summary>The agent pipe end to end: a real named pipe, the demo engine, stores in a temp directory that must stay empty.</summary>
public sealed class IpcServerTests : IAsyncLifetime
{
    private readonly string _pipeName = $"Pulse.Test.{Guid.NewGuid():N}";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pulse-engine-tests", Guid.NewGuid().ToString("N"));
    private AgentCore _agent = null!;
    private IpcServer _server = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        ILoggerFactory loggers = NullLoggerFactory.Instance;
        _agent = new AgentCore(new AgentCoreOptions
        {
            LoggerFactory = loggers,
            SettingsStore = new JsonSettingsStore(loggers.CreateLogger<JsonSettingsStore>(), Path.Combine(_dir, "settings.json")),
            StateStore = new JsonStateStore(Path.Combine(_dir, "state.json"), loggers.CreateLogger<JsonStateStore>()),
            Hardware = new DemoHardwareMonitor(loggers.CreateLogger<DemoHardwareMonitor>()),
            Rgb = new DemoRgbController(loggers.CreateLogger<DemoRgbController>()),
            BatteryProviders = new IBatteryProvider[] { new DemoBatteryProvider(loggers.CreateLogger<DemoBatteryProvider>()) },
            Elevation = new DemoElevationService(loggers.CreateLogger<DemoElevationService>()),
            AutoStart = new DemoAutoStartService(),
            TelemetryDirectory = Path.Combine(_dir, "logs"),
            IsDemo = true,
        });
        _server = new IpcServer(_agent,
            () => new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous),
            NullLogger<IpcServer>.Instance);
        _server.Start();
        await _agent.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _agent.ShutdownAsync(AgentExitReason.Quit);
        await _server.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task HelloRequestsVisibilityGatingAndExiting()
    {
        await using var client = await TestClient.ConnectAsync(_pipeName);
        await client.SendAsync(new ClientHelloMessage(IpcProtocol.Version, Environment.ProcessId));

        var hello = Assert.IsType<HelloMessage>(await client.NextAsync());
        Assert.Equal(IpcProtocol.Version, hello.Protocol);
        Assert.True(hello.Info.IsDemo);
        Assert.Empty(hello.PendingToasts); // not visible
        Assert.True(await Eventually(() => _server.ClientCount == 1));

        // A request and its reply (plus the settings broadcast it causes).
        await client.SendAsync(new UpdateSettingRequest(7, SettingsPatch.Of(nameof(AppSettings.Theme), AppTheme.Dark)));
        var reply = await client.WaitForAsync<ReplyMessage>(r => r.Id == 7);
        Assert.True(reply.Ok, reply.Error);
        var settings = await client.WaitForAsync<SettingsMessage>(s => s.Settings.Settings.Theme == AppTheme.Dark);
        Assert.Equal(nameof(AppSettings.Theme), settings.Settings.Changed);

        // RGB devices travel only in replies; the demo devices carry their stable key.
        await client.SendAsync(new RgbGetDevicesRequest(8, Refresh: false));
        Assert.True(await Eventually(() => _agent.RgbStatus.State == RgbConnectionState.Connected));
        await client.SendAsync(new RgbGetDevicesRequest(9, Refresh: true));
        var devices = await client.WaitForAsync<RgbDevicesReplyMessage>(r => r.Id == 9);
        Assert.True(devices.Ok);
        Assert.NotEmpty(devices.Devices);
        Assert.All(devices.Devices, d => Assert.False(string.IsNullOrEmpty(d.Key)));
        Assert.All(devices.Devices, d => Assert.True(d.HasDefault));

        // Hidden: no hardware frames while the demo monitor polls (every 2 s).
        Assert.True(await Eventually(() => _agent.Hardware.Snapshot.Cpu is not null, TimeSpan.FromSeconds(10)));
        var hiddenPeriod = await client.CollectAsync(TimeSpan.FromSeconds(2.5));
        Assert.DoesNotContain(hiddenPeriod, m => m is HardwareMessage);
        Assert.False(_agent.IsUiVisible);

        // Visible: the latest frame at once, then one per poll.
        await client.SendAsync(new SetVisibilityMessage(true));
        await client.WaitForAsync<HardwareMessage>(_ => true, TimeSpan.FromSeconds(3));
        Assert.True(_agent.IsUiVisible);
        await client.WaitForAsync<HardwareMessage>(_ => true, TimeSpan.FromSeconds(3));

        await client.SendAsync(new SetVisibilityMessage(false));
        Assert.True(await Eventually(() => !_agent.IsUiVisible));
        Assert.NotNull(_agent.LastUiHiddenAt);

        // The tray can reach this client.
        Assert.True(_agent.ShowPopoverInClient!(true, "fans"));
        var show = await client.WaitForAsync<ShowPopoverMessage>(_ => true);
        Assert.True(show.Toggle);
        Assert.Equal("fans", show.Tab);

        // Shutdown: the client hears Exiting, then the pipe closes.
        await _agent.ShutdownAsync(AgentExitReason.Quit);
        var exiting = await client.WaitForAsync<ExitingMessage>(_ => true);
        Assert.Equal(AgentExitReason.Quit, exiting.Reason);
        await _server.DisposeAsync();
        Assert.True(await client.ClosedAsync(TimeSpan.FromSeconds(5)));

        // The demo engine wrote nothing at all.
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));
    }

    [Fact]
    public async Task AVisibilityRightAfterTheHelloBringsThePendingToasts()
    {
        // The demo stylus is at 7 %: an alert is pending because nobody was visible.
        Assert.True(await Eventually(() => _agent.Batteries.All.Count > 0, TimeSpan.FromSeconds(10)));
        await Task.Delay(300);

        await using var client = await TestClient.ConnectAsync(_pipeName);
        await client.SendAsync(new ClientHelloMessage(IpcProtocol.Version, Environment.ProcessId));
        await client.SendAsync(new SetVisibilityMessage(true));

        var hello = Assert.IsType<HelloMessage>(await client.NextAsync());
        Assert.Contains(hello.PendingToasts, t => t.Device.Id == "demo:stylus");
        Assert.Empty(_agent.TakePendingToasts());
        await client.WaitForAsync<HardwareMessage>(_ => true, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AnotherProtocolIsToldSoAndClosed()
    {
        await using var client = await TestClient.ConnectAsync(_pipeName);
        await client.SendAsync(new ClientHelloMessage(IpcProtocol.Version + 41, Environment.ProcessId));

        var reply = Assert.IsType<ReplyMessage>(await client.NextAsync());
        Assert.Equal(0, reply.Id);
        Assert.False(reply.Ok);
        Assert.Equal("protocol", reply.Error);
        Assert.True(await client.ClosedAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ADisconnectedClientIsForgottenAndNoLongerVisible()
    {
        var client = await TestClient.ConnectAsync(_pipeName);
        await client.SendAsync(new ClientHelloMessage(IpcProtocol.Version, Environment.ProcessId));
        await client.SendAsync(new SetVisibilityMessage(true));
        Assert.IsType<HelloMessage>(await client.NextAsync());
        Assert.True(await Eventually(() => _agent.IsUiVisible));

        await client.DisposeAsync();
        Assert.True(await Eventually(() => _server.ClientCount == 0 && !_agent.IsUiVisible));
        Assert.False(_agent.ShowPopoverInClient!(false, null));
    }

    private static Task<bool> Eventually(Func<bool> condition, TimeSpan? timeout = null) => Fakes.TestAgent.Eventually(condition, timeout);

    /// <summary>The UI side of the pipe, reduced to what the tests need: a reader pumping messages into a channel.</summary>
    private sealed class TestClient : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly Channel<IpcMessage> _inbox = Channel.CreateUnbounded<IpcMessage>();
        private readonly Task _reader;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly List<IpcMessage> _backlog = new();

        private TestClient(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _reader = Task.Run(ReadLoopAsync);
        }

        public static async Task<TestClient> ConnectAsync(string name)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            return new TestClient(pipe);
        }

        public async Task SendAsync(IpcMessage message)
        {
            await _writeGate.WaitAsync();
            try { await IpcCodec.WriteAsync(_pipe, message); }
            finally { _writeGate.Release(); }
        }

        public async Task<IpcMessage> NextAsync(TimeSpan? timeout = null)
            => await _inbox.Reader.ReadAsync().AsTask().WaitAsync(timeout ?? TimeSpan.FromSeconds(10));

        /// <summary>The first matching message, from the backlog of earlier unmatched ones or the pipe (others are kept).</summary>
        public async Task<T> WaitForAsync<T>(Func<T, bool> match, TimeSpan? timeout = null) where T : IpcMessage
        {
            var earlier = _backlog.OfType<T>().FirstOrDefault(match);
            if (earlier is not null)
            {
                _backlog.Remove(earlier);
                return earlier;
            }

            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            while (true)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) throw new TimeoutException($"No {typeof(T).Name} arrived.");
                var message = await NextAsync(left);
                if (message is T typed && match(typed)) return typed;
                _backlog.Add(message);
            }
        }

        /// <summary>Everything received during <paramref name="duration"/> (the backlog is cleared first).</summary>
        public async Task<List<IpcMessage>> CollectAsync(TimeSpan duration)
        {
            _backlog.Clear();
            var list = new List<IpcMessage>();
            var deadline = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < deadline)
            {
                try { list.Add(await NextAsync(deadline - DateTime.UtcNow)); }
                catch (TimeoutException) { break; }
            }
            return list;
        }

        /// <summary>True once the agent closed its end (the reader saw end of stream).</summary>
        public async Task<bool> ClosedAsync(TimeSpan timeout)
        {
            try
            {
                await _reader.WaitAsync(timeout);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (await IpcCodec.ReadAsync(_pipe) is { } message) await _inbox.Writer.WriteAsync(message);
            }
            catch (Exception)
            {
                // Broken / closed pipe: the reader just ends (ClosedAsync reports it).
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _pipe.DisposeAsync();
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* closing */ }
        }
    }
}
