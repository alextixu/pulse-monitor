using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;

namespace Pulse.Core.Ipc;

/// <summary>Pipe name, protocol version and the wire format shared by the agent and its clients.</summary>
public static class IpcProtocol
{
    /// <summary>Bumped on any incompatible message change; a client that sees another version asks the user to reopen Pulse.</summary>
    public const int Version = 1;

    /// <summary>Larger frames are rejected before allocating.</summary>
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    /// <summary>
    /// One pipe per user and session; an elevated agent and an unelevated client of the same user get the same name.
    /// </summary>
    public static string PipeName
    {
        get
        {
            using var process = Process.GetCurrentProcess();
            return $"Pulse.Agent.{Sanitize(Environment.UserDomainName)}.{Sanitize(Environment.UserName)}.{process.SessionId}";
        }
    }

    private static string Sanitize(string value) => new(value.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}

/// <summary>
/// Framing: a 4-byte little-endian length followed by that many bytes of UTF-8 JSON (byte-mode pipe). One writer per
/// stream; readers stitch partial reads.
/// </summary>
public static class IpcCodec
{
    public static byte[] Encode(IpcMessage message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, IpcJsonContext.Default.IpcMessage);
        if (json.Length > IpcProtocol.MaxMessageBytes) throw new InvalidOperationException($"IPC message too large ({json.Length} bytes).");
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame, 4);
        return frame;
    }

    public static async Task WriteAsync(Stream stream, IpcMessage message, CancellationToken cancellationToken = default)
    {
        var frame = Encode(message);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The next message, or null when the peer closed the stream. Throws <see cref="InvalidDataException"/> on a bad frame.</summary>
    public static async Task<IpcMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > IpcProtocol.MaxMessageBytes) throw new InvalidDataException($"Bad IPC frame length {length}.");

        var payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false)) return null;
        try
        {
            return JsonSerializer.Deserialize(payload, IpcJsonContext.Default.IpcMessage) ?? new IpcMessage();
        }
        catch (JsonException)
        {
            // A message this side does not understand (or a malformed one): skip it, keep the connection.
            return new IpcMessage();
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
