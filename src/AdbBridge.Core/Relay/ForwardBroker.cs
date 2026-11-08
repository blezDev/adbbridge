using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using AdbBridge.Core.Security;
using AdbBridge.Core.Tunnels;

namespace AdbBridge.Core.Relay;

/// <summary>Authenticated Host gateway for ADB and broker-managed forwarding ports.</summary>
public sealed class ForwardBroker : IAsyncDisposable
{
    public const int DefaultPort = 5099;
    private readonly int _brokerPort;
    private readonly int _adbServerPort;
    private readonly BridgeIdentity _identity;
    private readonly bool _ownsIdentity;
    private readonly RelayConnections _connections = new();
    private readonly SemaphoreSlim _slots = new(128, 128);
    private readonly object _gate = new();
    private readonly HashSet<int> _forwardPorts = new();
    private readonly Dictionary<string, int> _reverseMappings = new();
    private readonly Dictionary<int, TcpListener> _reverseListeners = new();
    private readonly Dictionary<int, Channel<BrokerConnection>> _reversePools = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    private sealed record BrokerConnection(TcpClient Client, Stream Stream) : IDisposable
    {
        public void Dispose() { Stream.Dispose(); Client.Dispose(); }
    }

    public event EventHandler<string>? LogMessage;
    public int LocalPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _brokerPort;
    public TunnelInfo SecureAddress(TunnelInfo endpoint) => endpoint with { Credentials = _identity.Credentials };

    public ForwardBroker(int brokerPort = DefaultPort, int adbServerPort = 5037, BridgeIdentity? identity = null)
    {
        _brokerPort = brokerPort;
        _adbServerPort = adbServerPort;
        _identity = identity ?? BridgeIdentity.Create();
        _ownsIdentity = identity is null;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Loopback, _brokerPort);
        _listener.Start();
        Log($"Authenticated TLS broker listening on 127.0.0.1:{LocalPort}.");
        _acceptLoop = AcceptAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                if (!_slots.Wait(0)) { client.Dispose(); continue; }
                _connections.Start(token => HandleAsync(client, token), () => { client.Dispose(); _slots.Release(); });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        Stream? stream = null;
        var queued = false;
        try
        {
            stream = await SecureTunnel.AcceptAsync(client.GetStream(), _identity, ct);
            using var header = CancellationTokenSource.CreateLinkedTokenSource(ct);
            header.CancelAfter(TimeSpan.FromSeconds(10));
            var first = new byte[1];
            await stream.ReadExactlyAsync(first, header.Token);
            if (first[0] is (byte)'F' or (byte)'R')
            {
                var kind = first[0];
                var bytes = new List<byte> { kind };
                while (bytes.Count < 32 && bytes[^1] != '\n')
                {
                    await stream.ReadExactlyAsync(first, header.Token);
                    bytes.Add(first[0]);
                }
                var marker = Encoding.ASCII.GetString(bytes.ToArray());
                var prefix = kind == 'F' ? "FWD:" : "REV:";
                if (bytes[^1] != '\n' || !marker.StartsWith(prefix, StringComparison.Ordinal) ||
                    !int.TryParse(marker[prefix.Length..].Trim(), out var port) || port is <= 0 or > 65535)
                    throw new InvalidDataException("Invalid broker marker.");
                if (kind == 'F')
                {
                    lock (_gate) if (!_forwardPorts.Contains(port)) throw new InvalidDataException("Forward port is not registered by ADB.");
                    await RelayForwardAsync(stream, port, ct);
                }
                else
                {
                    lock (_gate)
                    {
                        if (!_reverseMappings.ContainsValue(port)) throw new InvalidDataException("Reverse port is not registered by ADB.");
                        EnsureReverseListener(port);
                        queued = _reversePools[port].Writer.TryWrite(new BrokerConnection(client, stream));
                    }
                }
            }
            else await RelayAdbAsync(stream, first, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { Log("Broker rejected or closed a connection. Check pairing and forwarding registration."); }
        finally
        {
            if (!queued) { stream?.Dispose(); client.Dispose(); }
            _slots.Release();
        }
    }

    private async Task RelayAdbAsync(Stream stream, byte[] first, CancellationToken ct)
    {
        using var adb = new TcpClient();
        await adb.ConnectAsync(IPAddress.Loopback, _adbServerPort, ct);
        var watcher = new AdbForwardWatcher();
        watcher.ForwardEstablished += (_, port) => { lock (_gate) _forwardPorts.Add(port); };
        watcher.ForwardRemoved += (_, port) => { lock (_gate) _forwardPorts.Remove(port); };
        watcher.ForwardsCleared += (_, _) => { lock (_gate) _forwardPorts.Clear(); };
        watcher.ReverseForwardEstablished += (_, mapping) => { lock (_gate) _reverseMappings[mapping.RemoteSpec] = mapping.LocalPort; };
        watcher.ReverseForwardRemoved += (_, spec) =>
        {
            lock (_gate)
                if (_reverseMappings.Remove(spec, out var port) && !_reverseMappings.ContainsValue(port)) RemoveReverse(port);
        };
        watcher.ReverseForwardsCleared += (_, _) =>
        {
            lock (_gate)
            {
                _reverseMappings.Clear();
                foreach (var port in _reverseListeners.Keys.ToArray()) RemoveReverse(port);
            }
        };
        watcher.OnClientToServer(first);
        await adb.GetStream().WriteAsync(first, ct);
        await RelayPump.CopyBothWaysAsync(stream, adb.GetStream(),
            (bytes, count) => watcher.OnClientToServer(bytes.AsSpan(0, count)),
            (bytes, count) => watcher.OnServerToClient(bytes.AsSpan(0, count)), ct);
    }

    private static async Task RelayForwardAsync(Stream stream, int port, CancellationToken ct)
    {
        using var target = new TcpClient();
        await target.ConnectAsync(IPAddress.Loopback, port, ct);
        await RelayPump.CopyBothWaysAsync(stream, target.GetStream(), ct);
    }

    private void EnsureReverseListener(int port)
    {
        if (_reverseListeners.ContainsKey(port)) return;
        if (_reverseListeners.Count >= 64) throw new InvalidOperationException("Reverse listener limit reached.");
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _reversePools[port] = Channel.CreateBounded<BrokerConnection>(8);
        _reverseListeners[port] = listener;
        _ = AcceptReverseAsync(port, listener, _cts!.Token);
    }

    private async Task AcceptReverseAsync(int port, TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                Channel<BrokerConnection>? pool;
                lock (_gate) _reversePools.TryGetValue(port, out pool);
                if (pool is null) { client.Dispose(); continue; }
                _connections.Start(token => PairAsync(client, pool, token), client.Dispose);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private static async Task PairAsync(TcpClient device, Channel<BrokerConnection> pool, CancellationToken ct)
    {
        using (device)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                using var tunnel = await pool.Reader.ReadAsync(deadline.Token);
                await tunnel.Stream.WriteAsync(Encoding.ASCII.GetBytes("GO\n"), ct);
                await RelayPump.CopyBothWaysAsync(device.GetStream(), tunnel.Stream, ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException or IOException or SocketException) { }
        }
    }

    private void RemoveReverse(int port)
    {
        if (_reverseListeners.Remove(port, out var listener)) listener.Stop();
        if (_reversePools.Remove(port, out var pool))
        {
            pool.Writer.TryComplete();
            while (pool.Reader.TryRead(out var pending)) pending.Dispose();
        }
        // Already-paired streams belong to _connections and continue until they close.
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync();
        _listener?.Stop();
        if (_acceptLoop is not null) await _acceptLoop;
        await _connections.DisposeAsync();
        lock (_gate)
        {
            foreach (var port in _reverseListeners.Keys.ToArray()) RemoveReverse(port);
            _reverseMappings.Clear();
            _forwardPorts.Clear();
        }
        _listener = null;
        _cts.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_ownsIdentity) _identity.Dispose();
    }

    private void Log(string message) => LogMessage?.Invoke(this, message);
}
