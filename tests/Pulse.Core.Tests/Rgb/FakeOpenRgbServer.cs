using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenRGB.NET;

namespace Pulse.Core.Tests.Rgb;

/// <summary>One mode of a <see cref="FakeRgbDevice"/>, serialised exactly as the OpenRGB SDK server does.</summary>
internal sealed class FakeRgbMode
{
    public required string Name { get; init; }
    public int Value { get; init; }
    public ModeFlags Flags { get; init; }
    public uint SpeedMin { get; init; }
    public uint SpeedMax { get; init; }
    public uint Speed { get; init; }
    public uint ColorMin { get; init; }
    public uint ColorMax { get; init; }
    public uint Direction { get; init; }
    public ColorMode ColorMode { get; init; }
    public Color[] Colors { get; init; } = [];

    public static FakeRgbMode Direct() => new() { Name = "Direct", Flags = ModeFlags.HasPerLedColor, ColorMode = ColorMode.PerLed };

    public static FakeRgbMode Static() => new()
    {
        Name = "Static", Flags = ModeFlags.HasModeSpecificColor, ColorMin = 1, ColorMax = 1, ColorMode = ColorMode.ModeSpecific,
        Colors = [new Color(255, 255, 255)],
    };

    public static FakeRgbMode Off() => new() { Name = "Off", Flags = ModeFlags.None, ColorMode = ColorMode.None };

    public static FakeRgbMode Rainbow() => new() { Name = "Rainbow", Flags = ModeFlags.HasSpeed, SpeedMin = 0, SpeedMax = 100, Speed = 50, ColorMode = ColorMode.None };
}

/// <summary>A device served by <see cref="FakeOpenRgbServer"/>; mutate it only through <see cref="FakeOpenRgbServer.Mutate"/>.</summary>
internal sealed class FakeRgbDevice
{
    public required string Name { get; init; }
    public int Type { get; init; }
    public string Vendor { get; init; } = "ASUS";
    public string Description { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Serial { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public List<FakeRgbMode> Modes { get; init; } = [FakeRgbMode.Direct(), FakeRgbMode.Static(), FakeRgbMode.Off(), FakeRgbMode.Rainbow()];
    public int ActiveMode { get; set; }
    public required int LedCount { get; init; }
    public Color[] Colors { get; set; } = [];

    /// <summary>Device with <paramref name="leds"/> LEDs, all <paramref name="color"/>, in mode <paramref name="activeMode"/>.</summary>
    public static FakeRgbDevice Create(string name, int type, string location, int leds, Color color, int activeMode = 0, string serial = "")
        => new()
        {
            Name = name, Type = type, Location = location, Serial = serial, LedCount = leds, ActiveMode = activeMode,
            Colors = Enumerable.Repeat(color, leds).ToArray(),
        };
}

/// <summary>
/// Minimal OpenRGB SDK server on a loopback ephemeral port (never the real 6742) speaking protocol v3 — enough for
/// <see cref="Pulse.Rgb.OpenRgbController"/> to connect, enumerate, read back, paint (UPDATELEDS) and switch modes
/// (SETCUSTOMMODE / UPDATEMODE). Protocol v3 is the newest one without zone segments, so the controller-data layout
/// below is the whole format.
/// </summary>
internal sealed class FakeOpenRgbServer : IDisposable
{
    private const uint ServerProtocol = 3;
    private const uint RequestControllerCount = 0;
    private const uint RequestControllerData = 1;
    private const uint RequestProtocolVersion = 40;
    private const uint UpdateLeds = 1050;
    private const uint UpdateZoneLeds = 1051;
    private const uint SetCustomMode = 1100;
    private const uint UpdateMode = 1101;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Socket> _sockets = new();
    private readonly object _sync = new();
    private readonly List<FakeRgbDevice> _devices;

    public FakeOpenRgbServer(params FakeRgbDevice[] devices)
    {
        _devices = devices.ToList();
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>Packet ids received, in order (for asserting what the controller sent).</summary>
    public ConcurrentQueue<uint> Received { get; } = new();

    public void Mutate(Action<List<FakeRgbDevice>> change)
    {
        lock (_sync) change(_devices);
    }

    public T Read<T>(Func<List<FakeRgbDevice>, T> read)
    {
        lock (_sync) return read(_devices);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* best effort */ }
        lock (_sockets)
        {
            foreach (var s in _sockets)
            {
                try { s.Dispose(); } catch { /* best effort */ }
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            lock (_sockets) _sockets.Add(socket);
            new Thread(() => Serve(socket)) { IsBackground = true, Name = "FakeOpenRgbServer" }.Start();
        }
    }

    private void Serve(Socket socket)
    {
        var header = new byte[16];
        try
        {
            while (ReadExactly(socket, header))
            {
                var deviceIndex = BitConverter.ToUInt32(header, 4);
                var command = BitConverter.ToUInt32(header, 8);
                var payload = new byte[BitConverter.ToUInt32(header, 12)];
                if (payload.Length > 0 && !ReadExactly(socket, payload)) return;
                Received.Enqueue(command);
                Handle(socket, deviceIndex, command, payload);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Client went away / server disposed.
        }
    }

    private void Handle(Socket socket, uint deviceIndex, uint command, byte[] payload)
    {
        switch (command)
        {
            case RequestProtocolVersion:
                Reply(socket, 0, command, BitConverter.GetBytes(ServerProtocol));
                break;
            case RequestControllerCount:
                Reply(socket, 0, command, BitConverter.GetBytes((uint)Read(d => d.Count)));
                break;
            case RequestControllerData:
            {
                var protocol = payload.Length >= 4 ? Math.Min(BitConverter.ToUInt32(payload, 0), ServerProtocol) : 0;
                var data = Read(d => Serialize(d[(int)deviceIndex], protocol));
                Reply(socket, deviceIndex, command, data);
                break;
            }
            case UpdateLeds: // [uint32 size][uint16 count][count × RGBx]
                Mutate(d => d[(int)deviceIndex].Colors = ReadColors(payload, 4));
                break;
            case UpdateZoneLeds: // [uint32 size][uint32 zone][uint16 count][count × RGBx]; the fake has one zone
                Mutate(d => d[(int)deviceIndex].Colors = ReadColors(payload, 8));
                break;
            case SetCustomMode:
                Mutate(d =>
                {
                    var device = d[(int)deviceIndex];
                    var custom = device.Modes.FindIndex(m => m.Flags.HasFlag(ModeFlags.HasPerLedColor));
                    if (custom >= 0) device.ActiveMode = custom;
                });
                break;
            case UpdateMode: // [uint32 size][int32 mode index][mode description]
                Mutate(d =>
                {
                    var mode = BitConverter.ToInt32(payload, 4);
                    if (mode >= 0 && mode < d[(int)deviceIndex].Modes.Count) d[(int)deviceIndex].ActiveMode = mode;
                });
                break;
            // SET_CLIENT_NAME and anything else: no reply.
        }
    }

    private static byte[] Serialize(FakeRgbDevice device, uint protocol)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(0u); // data size, patched below
        w.Write(device.Type);
        WriteString(w, device.Name);
        if (protocol >= 1) WriteString(w, device.Vendor);
        WriteString(w, device.Description);
        WriteString(w, device.Version);
        WriteString(w, device.Serial);
        WriteString(w, device.Location);

        w.Write((ushort)device.Modes.Count);
        w.Write(device.ActiveMode);
        foreach (var mode in device.Modes)
        {
            WriteString(w, mode.Name);
            w.Write(mode.Value);
            w.Write((uint)mode.Flags);
            w.Write(mode.SpeedMin);
            w.Write(mode.SpeedMax);
            if (protocol >= 3)
            {
                w.Write(0u); // brightness min
                w.Write(0u); // brightness max
            }
            w.Write(mode.ColorMin);
            w.Write(mode.ColorMax);
            w.Write(mode.Speed);
            if (protocol >= 3) w.Write(0u); // brightness
            w.Write(mode.Direction);
            w.Write((uint)mode.ColorMode);
            WriteColors(w, mode.Colors);
        }

        w.Write((ushort)1); // one zone holding every LED
        WriteString(w, "Main");
        w.Write(1); // linear
        w.Write((uint)device.LedCount);
        w.Write((uint)device.LedCount);
        w.Write((uint)device.LedCount);
        w.Write((ushort)0); // no matrix map

        w.Write((ushort)device.LedCount);
        for (var i = 0; i < device.LedCount; i++)
        {
            WriteString(w, $"LED {i}");
            w.Write((uint)i);
        }

        WriteColors(w, device.Colors);
        w.Flush();
        var bytes = stream.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(0), (uint)bytes.Length);
        return bytes;
    }

    private static void WriteString(BinaryWriter w, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        w.Write((ushort)(bytes.Length + 1));
        w.Write(bytes);
        w.Write((byte)0);
    }

    private static void WriteColors(BinaryWriter w, Color[] colors)
    {
        w.Write((ushort)colors.Length);
        foreach (var c in colors)
        {
            w.Write(c.R);
            w.Write(c.G);
            w.Write(c.B);
            w.Write((byte)0);
        }
    }

    private static Color[] ReadColors(byte[] payload, int offset)
    {
        var count = BitConverter.ToUInt16(payload, offset);
        var colors = new Color[count];
        for (var i = 0; i < count; i++)
        {
            var at = offset + 2 + i * 4;
            colors[i] = new Color(payload[at], payload[at + 1], payload[at + 2]);
        }

        return colors;
    }

    private static void Reply(Socket socket, uint deviceIndex, uint command, byte[] payload)
    {
        var packet = new byte[16 + payload.Length];
        Encoding.ASCII.GetBytes("ORGB").CopyTo(packet, 0);
        BitConverter.TryWriteBytes(packet.AsSpan(4), deviceIndex);
        BitConverter.TryWriteBytes(packet.AsSpan(8), command);
        BitConverter.TryWriteBytes(packet.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(packet, 16);
        var sent = 0;
        while (sent < packet.Length) sent += socket.Send(packet, sent, packet.Length - sent, SocketFlags.None);
    }

    private static bool ReadExactly(Socket socket, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = socket.Receive(buffer, read, buffer.Length - read, SocketFlags.None);
            if (n == 0) return false;
            read += n;
        }

        return true;
    }
}

/// <summary>Logger that keeps every formatted line (for "logged once" assertions and failure diagnostics).</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Enqueue((logLevel, exception is null ? formatter(state, exception) : $"{formatter(state, exception)} | {exception.GetType().Name}: {exception.Message}"));

    public string Dump() => string.Join(Environment.NewLine, Entries.Select(e => $"{e.Level}: {e.Message}"));
}
