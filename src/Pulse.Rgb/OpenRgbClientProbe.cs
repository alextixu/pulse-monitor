using System.Net.Sockets;
using System.Reflection;
using OpenRGB.NET;

namespace Pulse.Rgb;

/// <summary>
/// Liveness probe for the OpenRGB.NET 3.1.1 client. Its read loop never notices a peer close: <c>ReceiveAllAsync</c>
/// returns silently on a zero-byte receive, the loop condition is <c>Socket.Connected</c> (which only turns false after
/// a failed send) and the stale header buffer is re-parsed, so the loop spins at full CPU and queues garbage replies
/// until the client is disposed. The socket is therefore inspected directly. Field names (<c>OpenRgbClient._connection</c>,
/// <c>OpenRgbConnection._socket</c>) are those of the pinned package version; when they cannot be resolved the probe
/// reports "not closed" and the request timeouts take over.
/// </summary>
internal static class OpenRgbClientProbe
{
    private static readonly FieldInfo? ConnectionField =
        typeof(OpenRgbClient).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? SocketField =
        ConnectionField?.FieldType.GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic);

    public static bool IsSupported => SocketField is not null;

    /// <summary>True when the server has closed (or reset) the TCP connection underneath the client.</summary>
    public static bool IsPeerClosed(OpenRgbClient client)
    {
        if (ConnectionField is null || SocketField is null) return false;

        try
        {
            if (ConnectionField.GetValue(client) is not { } connection || SocketField.GetValue(connection) is not Socket socket)
                return false;
            if (!socket.Connected) return true;

            // Readable with nothing to read means EOF: the peer sent FIN (or the socket errored). An unsolicited packet
            // consumed by the read loop between the two calls looks the same, so a positive is confirmed once more.
            if (!LooksClosed(socket)) return false;
            Thread.Yield();
            return LooksClosed(socket);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
            return true;
        }
    }

    private static bool LooksClosed(Socket socket) => socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0;
}
