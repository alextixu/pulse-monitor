using System.Threading.Channels;
using HidSharp;
using Microsoft.Extensions.Logging;

namespace Pulse.Logitech;

/// <summary>
/// Request/response channel to one HID++ interface. Owns the long-report stream (all HID++ 2.0 traffic) and, when the
/// OS exposes it separately, the short-report stream of the same interface (HID++ 1.0 error replies and receiver
/// register traffic arrive there). Both are read on background threads into one inbox; traffic is serialized by a gate.
/// On Windows every open handle on a collection receives every input report, so the inbox also sees replies to other
/// software (G HUB) and device notifications; replies are therefore matched by header and software id, never by order.
/// </summary>
internal sealed class HidppTransport : IDisposable
{
    private const int ReaderTimeoutMs = 250;
    private const int WriteTimeoutMs = 1000;
    private const int InboxCapacity = 256;
    private const int BusyRetryDelayMs = 50;

    private readonly ILogger _logger;
    private readonly HidStream _longStream;
    private readonly HidStream? _shortStream;
    private readonly int _longOutputLength;
    private readonly int _shortOutputLength;
    private readonly Channel<byte[]> _inbox = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(InboxCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Thread> _readers = new();
    private volatile string? _fault;
    private int _disposed;

    private HidppTransport(ILogger logger, string key, HidStream longStream, HidStream? shortStream)
    {
        _logger = logger;
        Key = key;
        _longStream = longStream;
        _shortStream = shortStream;
        _longOutputLength = SafeLength(longStream.Device.GetMaxOutputReportLength, Hidpp.LongLength);
        _shortOutputLength = shortStream is null ? 0 : SafeLength(shortStream.Device.GetMaxOutputReportLength, Hidpp.ShortLength);

        StartReader(longStream, "long");
        if (shortStream is not null && !ReferenceEquals(shortStream, longStream)) StartReader(shortStream, "short");
    }

    public string Key { get; }

    public bool HasShortReports => _shortStream is not null;

    /// <summary>Non-null once the handle failed (unplugged / access lost); the owner should dispose and reopen later.</summary>
    public string? Fault => _fault;

    public bool IsFaulted => _fault is not null;

    public static HidppTransport? TryOpen(HidppInterfaceInfo info, ILogger logger)
    {
        HidStream? longStream = null;
        HidStream? shortStream = null;
        try
        {
            if (!info.LongDevice.TryOpen(out longStream) || longStream is null)
            {
                logger.LogDebug("Cannot open long collection {Path}", info.LongDevice.DevicePath);
                return null;
            }
            Configure(longStream);

            if (info.ShortDevice is not null)
            {
                if (ReferenceEquals(info.ShortDevice, info.LongDevice)
                    || string.Equals(info.ShortDevice.DevicePath, info.LongDevice.DevicePath, StringComparison.OrdinalIgnoreCase))
                {
                    shortStream = longStream; // single hidraw node declaring both report ids (Linux)
                }
                else if (info.ShortDevice.TryOpen(out shortStream) && shortStream is not null)
                {
                    Configure(shortStream);
                }
                else
                {
                    logger.LogDebug("Cannot open short collection {Path}; continuing with long reports only", info.ShortDevice.DevicePath);
                    shortStream = null;
                }
            }

            return new HidppTransport(logger, info.Key, longStream, shortStream);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to open HID++ interface {Key}", info.Key);
            if (shortStream is not null && !ReferenceEquals(shortStream, longStream)) SafeDispose(shortStream);
            if (longStream is not null) SafeDispose(longStream);
            return null;
        }
    }

    /// <summary>
    /// Sends one frame (short 0x10 or long 0x11) and waits for the matching reply or error frame. Never throws for
    /// protocol or I/O problems (see <see cref="HidppResultKind"/>); only cancellation propagates.
    /// <paramref name="matchParam"/> additionally requires that parameter byte of the reply to echo the request
    /// (0 for sub-addressed register reads such as 0xB5, 2 for the ping byte); -1 matches on the header only.
    /// A HID++ 2.0 <see cref="Hidpp2Error.Busy"/> answer is retried once.
    /// </summary>
    public async Task<HidppResult> RequestAsync(byte[] request, int timeoutMs, CancellationToken cancellationToken, int matchParam = -1)
    {
        if (request.Length < 4) throw new ArgumentException("HID++ frame must be at least 4 bytes.", nameof(request));
        if (_disposed != 0 || IsFaulted) return HidppResult.IoError();

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return HidppResult.IoError();
        }

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var result = await SendAndWaitAsync(request, timeoutMs, cancellationToken, matchParam).ConfigureAwait(false);
                if (attempt == 0 && result.Kind == HidppResultKind.Hidpp2Error && result.Hidpp2Code == Hidpp2Error.Busy)
                {
                    await Task.Delay(BusyRetryDelayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                return result;
            }
        }
        finally
        {
            try { _gate.Release(); } catch (ObjectDisposedException) { /* disposed while waiting */ }
        }
    }

    private async Task<HidppResult> SendAndWaitAsync(byte[] request, int timeoutMs, CancellationToken cancellationToken, int matchParam)
    {
        if (_disposed != 0 || IsFaulted) return HidppResult.IoError();

        // Late replies to earlier (timed-out) requests and stale notifications are irrelevant now.
        while (_inbox.Reader.TryRead(out _)) { }

        var frame = request;
        HidStream stream;
        int outputLength;
        if (request[0] == Hidpp.ShortReportId)
        {
            if (_shortStream is null)
            {
                frame = HidppProtocol.ShortToLong(request);
                stream = _longStream;
                outputLength = _longOutputLength;
            }
            else
            {
                stream = _shortStream;
                outputLength = _shortOutputLength;
            }
        }
        else
        {
            stream = _longStream;
            outputLength = _longOutputLength;
        }

        if (frame.Length < outputLength)
        {
            var padded = new byte[outputLength];
            Array.Copy(frame, padded, frame.Length);
            frame = padded;
        }

        _logger.LogTrace("{Key} --> {Frame}", Key, HidppProtocol.Hex(frame));
        try
        {
            stream.Write(frame);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException or TimeoutException)
        {
            SetFault($"write failed: {ex.Message}");
            return HidppResult.IoError();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        while (true)
        {
            byte[] reply;
            try
            {
                reply = await _inbox.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogTrace("{Key} <-- timeout after {Timeout} ms for {Frame}", Key, timeoutMs, HidppProtocol.Hex(request));
                return HidppResult.Timeout();
            }
            catch (ChannelClosedException)
            {
                SetFault("reader stopped");
                return HidppResult.IoError();
            }

            if (HidppProtocol.IsReplyTo(request, reply, matchParam))
            {
                var result = HidppProtocol.Classify(reply);
                _logger.LogTrace("{Key} <-- {Result}", Key, result);
                return result;
            }

            _logger.LogTrace("{Key} <-- (unsolicited) {Frame}", Key, HidppProtocol.Hex(reply));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cts.Cancel();
        _inbox.Writer.TryComplete();
        // Closing the handles aborts the pending overlapped reads, so the reader threads exit promptly.
        if (_shortStream is not null && !ReferenceEquals(_shortStream, _longStream)) SafeDispose(_shortStream);
        SafeDispose(_longStream);

        foreach (var reader in _readers)
        {
            try { reader.Join(1000); } catch { /* best effort */ }
        }
        _cts.Dispose();
        _gate.Dispose();
    }

    private void StartReader(HidStream stream, string name)
    {
        var thread = new Thread(() => ReadLoop(stream, name))
        {
            IsBackground = true,
            Name = $"hidpp-{name}-reader",
        };
        _readers.Add(thread);
        thread.Start();
    }

    private void ReadLoop(HidStream stream, string name)
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var report = stream.Read();
                if (report is { Length: >= 4 }) _inbox.Writer.TryWrite(report);
            }
            catch (TimeoutException)
            {
                // No report within ReaderTimeoutMs; loop to re-check cancellation.
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested) SetFault($"{name} read failed: {ex.Message}");
                break;
            }
        }
        // A dead reader makes the whole interface unusable (its replies would never arrive); the owner reopens it later.
        if (!token.IsCancellationRequested) SetFault($"{name} reader stopped");
        _inbox.Writer.TryComplete();
    }

    private void SetFault(string reason)
    {
        if (Interlocked.CompareExchange(ref _fault, reason, null) is null)
        {
            _logger.LogDebug("HID++ interface {Key} faulted: {Reason}", Key, reason);
        }
    }

    private static void Configure(HidStream stream)
    {
        stream.ReadTimeout = ReaderTimeoutMs;
        stream.WriteTimeout = WriteTimeoutMs;
    }

    private static int SafeLength(Func<int> getter, int fallback)
    {
        try
        {
            var value = getter();
            return value > 0 ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void SafeDispose(IDisposable disposable)
    {
        try { disposable.Dispose(); } catch { /* handle already gone */ }
    }
}
