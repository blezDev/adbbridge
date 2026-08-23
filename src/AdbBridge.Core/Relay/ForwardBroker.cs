using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace AdbBridge.Core.Relay;

/// <summary>
/// Host-side counterpart to <see cref="ForwardPortRelay"/> and <see cref="ReversePoolRelay"/>.
/// Sits between the tunnel (ngrok/Pinggy) and the real local adb server, so the tunnel's
/// single public address can reach any of three things depending on what the Companion is
/// asking for:
///
/// - Normal traffic (the adb host protocol itself) is passed straight through to the
///   real adb server on its own port, exactly as if the tunnel pointed at it directly —
///   this is what already worked before this class existed.
/// - A connection that opens with the marker line "FWD:&lt;port&gt;\n" is instead routed
///   to 127.0.0.1:&lt;port&gt; on this machine — the listener that the real adb server just
///   opened here in response to an `adb forward tcp:0 ...` request (see
///   <see cref="ForwardPortRelay"/>).
/// - A connection that opens with "REV:&lt;port&gt;\n" is an idle, pre-opened tunnel
///   connection from Companion's reverse pool (see <see cref="ReversePoolRelay"/>). This
///   broker binds a stand-in listener on 127.0.0.1:&lt;port&gt; itself — the port the real
///   adb server will try to dial when the device opens a reverse-forwarded connection —
///   and pairs whichever arrives first with one of these pooled connections, writing
///   "GO\n" to signal Companion to start bridging to Studio's real listener.
///
/// The markers are unambiguous with zero lookahead: real adb frames always begin with a
/// lowercase-hex length nibble ('0'-'9'/'a'-'f'), and 'F'/'R' are neither, so peeking one
/// byte is enough to tell all three apart.
/// </summary>
public sealed class ForwardBroker : IAsyncDisposable
{
    public const int DefaultPort = 5099;
    private const int MaxMarkerLine = 32;
    private static readonly TimeSpan ReversePairTimeout = TimeSpan.FromSeconds(5);

    private readonly int _brokerPort;
    private readonly int _adbServerPort;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;

    private readonly object _reverseGate = new();
    private readonly Dictionary<int, TcpListener> _reverseListeners = new();
    private readonly Dictionary<int, Channel<TcpClient>> _reversePools = new();

    public event EventHandler<string>? LogMessage;

    public ForwardBroker(int brokerPort = DefaultPort, int adbServerPort = 5037)
    {
        _brokerPort = brokerPort;
        _adbServerPort = adbServerPort;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Loopback, _brokerPort);
        _listener.Start();
        Log($"Forward broker listening on 127.0.0.1:{_brokerPort} (adb server on {_adbServerPort}).");

        _acceptLoopTask = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        if (_listener is null) return;

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }

            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient inbound, CancellationToken ct)
    {
        try
        {
            var stream = inbound.GetStream();
            var first = new byte[1];
            var read = await stream.ReadAsync(first, ct);
            if (read <= 0) { inbound.Dispose(); return; }

            if (first[0] == (byte)'F')
            {
                using var _ = inbound;
                await HandleForwardMarkerAsync(stream, first[0], ct);
            }
            else if (first[0] == (byte)'R')
            {
                // Ownership of `inbound` transfers into the reverse pool from here — it
                // must NOT be disposed on this path. It's disposed later, whenever a
                // reverse-connect actually consumes it (or the broker shuts down).
                await HandleReverseMarkerAsync(inbound, stream, first[0], ct);
            }
            else
            {
                using var _ = inbound;
                await RelayToAsync(stream, _adbServerPort, first, ct);
            }
        }
        catch (Exception ex)
        {
            inbound.Dispose();
            Log($"Broker connection error: {ex.Message}");
        }
    }

    private async Task HandleForwardMarkerAsync(NetworkStream stream, byte firstByte, CancellationToken ct)
    {
        var line = new List<byte> { firstByte };
        var oneByte = new byte[1];
        while (line.Count < MaxMarkerLine)
        {
            var read = await stream.ReadAsync(oneByte, ct);
            if (read <= 0) break;
            line.Add(oneByte[0]);
            if (oneByte[0] == (byte)'\n') break;
        }

        var text = Encoding.ASCII.GetString(line.ToArray());
        if (text.StartsWith("FWD:", StringComparison.Ordinal) &&
            int.TryParse(text["FWD:".Length..].Trim(), out var port) && port is > 0 and <= 65535)
        {
            await RelayToAsync(stream, port, null, ct);
        }
        else
        {
            // Didn't actually match the marker format — treat what we buffered as the
            // start of ordinary adb traffic rather than dropping it on the floor.
            await RelayToAsync(stream, _adbServerPort, line.ToArray(), ct);
        }
    }

    private async Task HandleReverseMarkerAsync(TcpClient inbound, NetworkStream stream, byte firstByte, CancellationToken ct)
    {
        var line = new List<byte> { firstByte };
        var oneByte = new byte[1];
        while (line.Count < MaxMarkerLine)
        {
            var read = await stream.ReadAsync(oneByte, ct);
            if (read <= 0) { inbound.Dispose(); return; }
            line.Add(oneByte[0]);
            if (oneByte[0] == (byte)'\n') break;
        }

        var text = Encoding.ASCII.GetString(line.ToArray());
        if (!text.StartsWith("REV:", StringComparison.Ordinal) ||
            !int.TryParse(text["REV:".Length..].Trim(), out var port) || port is <= 0 or > 65535)
        {
            Log($"Discarding malformed reverse-pool marker: {text.Trim()}");
            inbound.Dispose();
            return;
        }

        EnsureReverseListener(port);

        Channel<TcpClient> channel;
        lock (_reverseGate)
        {
            if (!_reversePools.TryGetValue(port, out channel!))
            {
                channel = Channel.CreateUnbounded<TcpClient>();
                _reversePools[port] = channel;
            }
        }

        await channel.Writer.WriteAsync(inbound, ct);
    }

    private void EnsureReverseListener(int port)
    {
        lock (_reverseGate)
        {
            if (_reverseListeners.ContainsKey(port)) return;

            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            _reverseListeners[port] = listener;
            Log($"Reverse-tunnel stand-in listener bound on 127.0.0.1:{port} for adb reverse.");
            _ = ReverseAcceptLoopAsync(port, listener);
        }
    }

    private async Task ReverseAcceptLoopAsync(int port, TcpListener listener)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        while (!ct.IsCancellationRequested)
        {
            TcpClient real;
            try
            {
                real = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception) { break; } // listener stopped — broker shutting down or port torn down

            _ = PairReverseConnectionAsync(port, real, ct);
        }
    }

    private async Task PairReverseConnectionAsync(int port, TcpClient real, CancellationToken ct)
    {
        using var _ = real;

        Channel<TcpClient>? channel;
        lock (_reverseGate) { _reversePools.TryGetValue(port, out channel); }

        if (channel is null)
        {
            Log($"Reverse connection on port {port} arrived with no pooled tunnel connection registered.");
            return;
        }

        TcpClient tunnelClient;
        try
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(ReversePairTimeout);
            tunnelClient = await channel.Reader.ReadAsync(waitCts.Token);
        }
        catch (OperationCanceledException)
        {
            Log($"Reverse connection on port {port} timed out waiting for a pooled tunnel connection.");
            return;
        }

        using var __ = tunnelClient;
        try
        {
            var tunnelStream = tunnelClient.GetStream();
            await tunnelStream.WriteAsync(Encoding.ASCII.GetBytes("GO\n"), ct);
            await RelayPump.CopyBothWaysAsync(real.GetStream(), tunnelStream, ct);
        }
        catch (Exception ex)
        {
            Log($"Reverse relay for port {port} failed: {ex.Message}");
        }
    }

    private static async Task RelayToAsync(NetworkStream inboundStream, int targetPort, byte[]? prebuffered, CancellationToken ct)
    {
        using var outbound = new TcpClient();
        await outbound.ConnectAsync(IPAddress.Loopback, targetPort, ct);
        var outboundStream = outbound.GetStream();

        if (prebuffered is { Length: > 0 })
            await outboundStream.WriteAsync(prebuffered, ct);

        await RelayPump.CopyBothWaysAsync(inboundStream, outboundStream, ct);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;

        lock (_reverseGate)
        {
            foreach (var listener in _reverseListeners.Values)
            {
                try { listener.Stop(); } catch { /* already gone */ }
            }
            _reverseListeners.Clear();

            foreach (var channel in _reversePools.Values)
            {
                channel.Writer.TryComplete();
                while (channel.Reader.TryRead(out var pending))
                    pending.Dispose();
            }
            _reversePools.Clear();
        }

        if (_acceptLoopTask is not null)
        {
            try { await _acceptLoopTask; } catch { /* swallow cancellation */ }
            _acceptLoopTask = null;
        }

        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void Log(string message) => LogMessage?.Invoke(this, message);
}
