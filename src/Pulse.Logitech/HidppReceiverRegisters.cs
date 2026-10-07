using Pulse.Core.Models;

namespace Pulse.Logitech;

internal sealed record ReceiverPairingInfo(string Wpid, DeviceKind Kind, string? Serial);

/// <summary>HID++ 1.0 register 0xB5 reads on a receiver (index 0xFF): serial, slot count, pairing info and names.</summary>
internal static class HidppReceiverRegisters
{
    private const int TimeoutMs = HidppDevice.RequestTimeoutMs;

    /// <summary>Register 0xB5/0x03: receiver serial (4 bytes) and the number of pairing slots. Serial is null when unsupported.</summary>
    public static async Task<(string? Serial, int MaxSlots, HidppResultKind Kind)> ReadReceiverInfoAsync(HidppTransport transport, CancellationToken cancellationToken)
    {
        var result = await ReadAsync(transport, Hidpp.ReceiverInfoSerial, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Frame.Length < 11) return (null, Hidpp.MaxReceiverSlots, result.Kind);

        var serial = Convert.ToHexString(result.Frame.AsSpan(5, 4));
        int slots = result.Frame[10];
        if (slots is < 1 or > Hidpp.MaxReceiverSlots) slots = Hidpp.MaxReceiverSlots;
        return (serial, slots, result.Kind);
    }

    /// <summary>Pairing info of <paramref name="slot"/> (1-based). Null when the slot is empty; <c>Kind</c> tells why.</summary>
    public static async Task<(ReceiverPairingInfo? Info, HidppResultKind Kind, Hidpp1Error Error)> ReadPairingAsync(
        HidppTransport transport, ReceiverFamily family, int slot, CancellationToken cancellationToken)
    {
        if (family == ReceiverFamily.Bolt)
        {
            var bolt = await ReadAsync(transport, (byte)(Hidpp.BoltPairingInfo + slot), cancellationToken).ConfigureAwait(false);
            if (!bolt.IsSuccess || bolt.Frame.Length < 12) return (null, bolt.Kind, bolt.Hidpp1Code);
            var wpid = $"{bolt.Frame[7]:X2}{bolt.Frame[6]:X2}";
            var kind = HidppProtocol.KindFromPairingNibble(bolt.Frame[5] & 0x0F);
            var serial = Convert.ToHexString(bolt.Frame.AsSpan(8, 4));
            return (new ReceiverPairingInfo(wpid, kind, serial), bolt.Kind, Hidpp1Error.None);
        }

        var result = await ReadAsync(transport, (byte)(Hidpp.PairingInfo + slot - 1), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Frame.Length < 12) return (null, result.Kind, result.Hidpp1Code);

        var pairing = new ReceiverPairingInfo(
            $"{result.Frame[7]:X2}{result.Frame[8]:X2}",
            HidppProtocol.KindFromPairingNibble(result.Frame[11] & 0x0F),
            null);

        var extended = await ReadAsync(transport, (byte)(Hidpp.ExtendedPairingInfo + slot - 1), cancellationToken).ConfigureAwait(false);
        if (extended.IsSuccess && extended.Frame.Length >= 9)
        {
            var serial = Convert.ToHexString(extended.Frame.AsSpan(5, 4));
            if (serial != "00000000") pairing = pairing with { Serial = serial };
        }
        return (pairing, result.Kind, Hidpp1Error.None);
    }

    /// <summary>Device name stored by the receiver for <paramref name="slot"/>; null when unavailable.</summary>
    public static async Task<string?> ReadDeviceNameAsync(HidppTransport transport, ReceiverFamily family, int slot, CancellationToken cancellationToken)
    {
        if (family == ReceiverFamily.Bolt)
        {
            var bolt = await transport.RequestAsync(
                HidppProtocol.Short(Hidpp.ReceiverIndex, Hidpp.GetRegisterLong, Hidpp.RegisterReceiverInfo, (byte)(Hidpp.BoltDeviceName + slot), 0x01),
                TimeoutMs, cancellationToken, matchParam: 0).ConfigureAwait(false);
            if (!bolt.IsSuccess || bolt.Frame.Length < 8) return null;
            var boltName = HidppProtocol.Ascii(bolt.Frame, 7, Math.Min(13, (int)bolt.Frame[6]));
            return string.IsNullOrWhiteSpace(boltName) ? null : boltName;
        }

        var result = await ReadAsync(transport, (byte)(Hidpp.DeviceName + slot - 1), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Frame.Length < 7) return null;
        var name = HidppProtocol.Ascii(result.Frame, 6, Math.Min(14, (int)result.Frame[5]));
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static Task<HidppResult> ReadAsync(HidppTransport transport, byte subAddress, CancellationToken cancellationToken)
        => transport.RequestAsync(
            HidppProtocol.Short(Hidpp.ReceiverIndex, Hidpp.GetRegisterLong, Hidpp.RegisterReceiverInfo, subAddress),
            TimeoutMs, cancellationToken, matchParam: 0);
}
