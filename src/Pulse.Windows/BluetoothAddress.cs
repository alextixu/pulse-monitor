namespace Pulse.Windows;

/// <summary>Helpers for the 48-bit Bluetooth address in its various textual forms. Canonical form is 12 upper-case hex digits.</summary>
internal static class BluetoothAddress
{
    /// <summary>Accepts "aa:bb:cc:dd:ee:ff", "AA-BB-..." or "AABBCCDDEEFF".</summary>
    public static bool TryNormalize(string? text, out string hex12)
    {
        hex12 = string.Empty;
        if (string.IsNullOrEmpty(text)) return false;

        Span<char> buffer = stackalloc char[12];
        var n = 0;
        foreach (var c in text)
        {
            if (c is ':' or '-' or ' ') continue;
            if (n == 12 || !IsHex(c)) return false;
            buffer[n++] = char.ToUpperInvariant(c);
        }

        if (n != 12) return false;
        hex12 = new string(buffer);
        return true;
    }

    /// <summary>"AABBCCDDEEFF" → "aa:bb:cc:dd:ee:ff" (the form used in <c>BatteryDevice.Id</c>).</summary>
    public static string ToDisplay(string hex12)
    {
        Span<char> buffer = stackalloc char[17];
        var j = 0;
        for (var i = 0; i < 12; i++)
        {
            if (i > 0 && i % 2 == 0) buffer[j++] = ':';
            buffer[j++] = char.ToLowerInvariant(hex12[i]);
        }

        return new string(buffer);
    }

    /// <summary>
    /// Extracts the address from a BTHENUM / BTHLE PnP instance id. Device nodes ("BTHLE\Dev_686CE645E1AD\...") get
    /// priority 2, service children ("BTHENUM\{uuid}_LOCALMFG&amp;0002\7&amp;...&amp;FC039F42E11F_C00000000") priority 1.
    /// BTHLEDevice\ GATT service nodes and the BTH\ bus nodes are rejected.
    /// </summary>
    public static bool TryExtractFromInstanceId(string? instanceId, out string hex12, out int priority)
    {
        hex12 = string.Empty;
        priority = 0;
        if (string.IsNullOrEmpty(instanceId)) return false;
        if (!instanceId.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase)
            && !instanceId.StartsWith(@"BTHLE\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var dev = instanceId.IndexOf(@"\DEV_", StringComparison.OrdinalIgnoreCase);
        if (dev >= 0 && dev + 5 + 12 <= instanceId.Length && TryNormalize(instanceId.AsSpan(dev + 5, 12).ToString(), out hex12))
        {
            priority = 2;
            return true;
        }

        var lastSegment = instanceId[(instanceId.LastIndexOf('\\') + 1)..];
        foreach (var token in lastSegment.Split('&', '_'))
        {
            if (token.Length == 12 && TryNormalize(token, out hex12))
            {
                priority = 1;
                return true;
            }
        }

        return false;
    }

    private static bool IsHex(char c) => char.IsAsciiHexDigit(c);
}
