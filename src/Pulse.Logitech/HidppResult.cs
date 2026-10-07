namespace Pulse.Logitech;

internal enum HidppResultKind
{
    Success,
    /// <summary>HID++ 2.0 error frame (0xFF marker).</summary>
    Hidpp2Error,
    /// <summary>HID++ 1.0 error frame (0x8F marker), also what receivers answer for absent / sleeping devices.</summary>
    Hidpp1Error,
    Timeout,
    /// <summary>The underlying HID handle failed (device unplugged, access lost).</summary>
    IoError,
}

/// <summary>Outcome of one HID++ request. Never throws; callers switch on <see cref="Kind"/>.</summary>
internal readonly struct HidppResult
{
    private static readonly byte[] Empty = Array.Empty<byte>();

    private HidppResult(HidppResultKind kind, byte[] frame, byte errorCode)
    {
        Kind = kind;
        Frame = frame;
        ErrorCode = errorCode;
    }

    public HidppResultKind Kind { get; }

    /// <summary>Full raw reply including report id; empty for <see cref="HidppResultKind.Timeout"/> / <see cref="HidppResultKind.IoError"/>.</summary>
    public byte[] Frame { get; }

    public byte ErrorCode { get; }

    public bool IsSuccess => Kind == HidppResultKind.Success;

    public Hidpp1Error Hidpp1Code => Kind == HidppResultKind.Hidpp1Error ? (Hidpp1Error)ErrorCode : Hidpp1Error.None;

    public Hidpp2Error Hidpp2Code => Kind == HidppResultKind.Hidpp2Error ? (Hidpp2Error)ErrorCode : Hidpp2Error.None;

    /// <summary>Parameter byte <paramref name="i"/> (0-based, i.e. frame[4 + i]); 0 when out of range.</summary>
    public byte Param(int i) => IsSuccess && Frame.Length > 4 + i ? Frame[4 + i] : (byte)0;

    public ReadOnlySpan<byte> Params => IsSuccess && Frame.Length > 4 ? Frame.AsSpan(4) : ReadOnlySpan<byte>.Empty;

    public static HidppResult Success(byte[] frame) => new(HidppResultKind.Success, frame, 0);

    public static HidppResult Error2(byte[] frame) => new(HidppResultKind.Hidpp2Error, frame, frame.Length > 5 ? frame[5] : (byte)0);

    public static HidppResult Error1(byte[] frame) => new(HidppResultKind.Hidpp1Error, frame, frame.Length > 5 ? frame[5] : (byte)0);

    public static HidppResult Timeout() => new(HidppResultKind.Timeout, Empty, 0);

    public static HidppResult IoError() => new(HidppResultKind.IoError, Empty, 0);

    public override string ToString() => Kind switch
    {
        HidppResultKind.Success => $"OK [{HidppProtocol.Hex(Frame)}]",
        HidppResultKind.Hidpp2Error => $"HID++2 error {Hidpp2Code} [{HidppProtocol.Hex(Frame)}]",
        HidppResultKind.Hidpp1Error => $"HID++1 error {Hidpp1Code} [{HidppProtocol.Hex(Frame)}]",
        _ => Kind.ToString(),
    };
}
