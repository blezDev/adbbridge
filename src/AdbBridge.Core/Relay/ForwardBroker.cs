using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AdbBridge.Core.Relay;

/// <summary>
/// Host-side counterpart to <see cref="ForwardPortRelay"/>. Sits between the tunnel
/// (ngrok/Pinggy) and the real local adb server, so the tunnel's single public address
/// can reach either one depending on what the Companion is asking for:
///
/// - Normal traffic (the adb host protocol itself) is passed straight through to the
///   real adb server on its own port, exactly as if the tunnel pointed at it directly —
///   this is what already worked before this class existed.
/// - A connection that opens with the marker line "FWD:&lt;port&gt;\n" is instead routed
///   to 127.0.0.1:&lt;port&gt; on this machine — the listener that the real adb server just
///   opened here in response to an `adb forward tcp:0 ...` request. Without this, Android
///   Studio's Device Mirroring / App Inspection features (which rely on connecting
///   directly to a forwarded port rather than going through the adb server protocol)
///   silently fail to attach, even though `adb forward` itself reports success.
///
/// The marker is unambiguous with zero lookahead: real adb frames always begin with a
/// lowercase-hex length nibble ('0'-'9'/'a'-'f'), and 'F' is neither, so peeking one byte
/// is enough to tell the two apart.
/// </summary>
public sealed class ForwardBroker : IAsyncDisposable
{
    public const int DefaultPort = 5099;
    private const int MaxMarkerLine = 32;

    private readonly int _brokerPort;
    private readonly int _adbServerPort;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;

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
        using var _ = inbound;
        var stream = inbound.GetStream();

        try
        {
            var first = new byte[1];
            var read = await stream.ReadAsync(first, ct);
            if (read <= 0) return;

            if (first[0] == (byte)'F')
            {
                await HandleForwardMarkerAsync(stream, first[0], ct);
            }
            else
            {
                await RelayToAsync(stream, _adbServerPort, first, ct);
            }
        }
        catch (Exception ex)
        {
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
