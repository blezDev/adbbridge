namespace AdbBridge.Core.Relay;

/// <summary>
/// Shared plumbing for "copy raw bytes both ways between two streams until either side
/// closes" — the pattern every relay component in this namespace needs at its core.
/// </summary>
internal static class RelayPump
{
    /// <summary>Plain bidirectional copy with no visibility into the bytes.</summary>
    public static Task CopyBothWaysAsync(Stream a, Stream b, CancellationToken ct) =>
        CopyBothWaysAsync(a, b, null, null, ct);

    /// <summary>
    /// Bidirectional copy where each chunk is also handed to an observer before being
    /// written on to the destination — the bytes themselves are never altered. Used by
    /// <see cref="TcpRelay"/> to snoop the adb host protocol for <c>forward:</c> requests
    /// without disturbing the raw proxying every other command relies on.
    /// </summary>
    public static async Task CopyBothWaysAsync(
        Stream a, Stream b,
        Action<byte[], int>? observeAToB, Action<byte[], int>? observeBToA,
        CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var aToB = PumpAsync(a, b, observeAToB, session.Token);
        var bToA = PumpAsync(b, a, observeBToA, session.Token);
        var completed = await Task.WhenAny(aToB, bToA);
        try { await completed; }
        finally
        {
            await session.CancelAsync();
            try { await Task.WhenAll(aToB, bToA); } catch { /* observe the other direction before releasing its streams */ }
        }
    }

    private static async Task PumpAsync(Stream source, Stream destination, Action<byte[], int>? observe, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read <= 0) break;

            observe?.Invoke(buffer, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    /// <summary>Parses the 4-byte lowercase-hex length prefix every adb host request/data
    /// frame starts with. Shared by <see cref="AdbForwardWatcher"/> and <see cref="TcpRelay"/>.</summary>
    internal static bool TryParseHexLength(byte a, byte b, byte c, byte d, out int value)
    {
        value = 0;
        int d0 = HexDigit(a), d1 = HexDigit(b), d2 = HexDigit(c), d3 = HexDigit(d);
        if (d0 < 0 || d1 < 0 || d2 < 0 || d3 < 0) return false;

        value = (d0 << 12) | (d1 << 8) | (d2 << 4) | d3;
        return true;
    }

    private static int HexDigit(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10, // adb emits lowercase; tolerate uppercase too
        _ => -1
    };
}
