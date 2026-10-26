using System.IO.Pipes;
using Microsoft.Extensions.Logging;

namespace Pulse.Engine.Ipc;

/// <summary>
/// The agent side of the pipe: accepts clients (one server stream per client, created by <c>createPipe</c> so the host can
/// apply its security descriptor), answers <c>ClientHelloMessage</c> with a Hello, forwards engine events, dispatches each
/// request to <see cref="AgentCore"/> as its own task and replies through a single writer per client with two lanes
/// (latest-only Hardware/Batteries frames; a never-dropped FIFO for everything else). See decisions-v2.md §15–16.
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    public IpcServer(AgentCore agent, Func<NamedPipeServerStream> createPipe, ILogger<IpcServer> log) => throw new NotImplementedException();

    /// <summary>Starts listening; sets <see cref="AgentCore.ShowPopoverInClient"/>.</summary>
    public void Start() => throw new NotImplementedException();

    public int ClientCount => throw new NotImplementedException();

    /// <summary>Sends Exiting to every client (bounded wait), closes the pipes and stops listening.</summary>
    public ValueTask DisposeAsync() => throw new NotImplementedException();
}
