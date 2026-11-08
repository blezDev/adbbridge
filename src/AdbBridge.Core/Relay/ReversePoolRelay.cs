using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AdbBridge.Core.Relay;

/// <summary>
/// Companion-side counterpart to the reverse-tunnel side of <see cref="ForwardBroker"/>.
/// adb reverse is the mirror image of adb forward: Studio opens its own local listener
/// (e.g. for Device Mirroring's screen-sharing-agent) and asks the real adb server to
/// deliver device-originated connections to it. That "local" port is only meaningful on
/// whichever machine runs the real adb server — the Host, in this bridge — so a plain
/// relay can't help: there's no listener on Companion to wrap, and the Host's real adb
/// server dials <em>out</em> to the local port itself, which can't be intercepted the way
/// a listener can.
///
/// The fix mirrors how a reverse SSH tunnel solves the same problem: instead of the Host
/// waiting for an inbound connection it can forward, Companion proactively keeps a small
/// pool of already-open, marker-tagged tunnel connections sitting idle on the Host side
/// (see <see cref="ForwardBroker"/>'s reverse handling). When the real adb server finally
/// dials the Host's stand-in listener, the Host pairs that connection with one from the
/// pool and signals "GO" — only then does Companion dial Studio's real local listener and
/// start bridging bytes, so Studio never sees a connection attempt that isn't backed by a
/// genuine device-originated one.
/// </summary>
public sealed class ReversePoolRelay : IAsyncDisposable
{
    // Keep one idle slot and replace it after its local connection succeeds. This
    // preserves arrival order for scrcpy's video/control sockets across the WAN.
    // Established streams have a separate lifetime, so this is not a concurrency cap.
    private const int PoolSizePerPort = 1;

    private readonly TcpRelay _relay;
    private readonly Dictionary<string, int> _remoteSpecToPort = new();
    private readonly Dictionary<int, PortPool> _pools = new();
    private readonly object _gate = new();
    private readonly RelayConnections _connections = new();

    public event EventHandler<string>? LogMessage;

    public ReversePoolRelay(TcpRelay relay)
    {
        _relay = relay;
        _relay.ReverseForwardEstablished += OnEstablished;
        _relay.ReverseForwardRemoved += OnRemoved;
        _relay.ReverseForwardsCleared += OnCleared;
    }

    private void OnEstablished(object? sender, (string RemoteSpec, int LocalPort) e) => StartPool(e.RemoteSpec, e.LocalPort);
    private void OnRemoved(object? sender, string remoteSpec) => _ = StopPoolAsync(remoteSpec);
    private void OnCleared(object? sender, EventArgs e) => _ = StopAllAsync();

    private void StartPool(string remoteSpec, int port)
    {
        PortPool pool;
        lock (_gate)
        {
            _remoteSpecToPort[remoteSpec] = port;
            if (_pools.ContainsKey(port)) return; // already pooling this port

            pool = new PortPool(port, _relay, PoolSizePerPort, _connections);
            pool.LogMessage += (_, msg) => Log(msg);
            _pools[port] = pool;
        }

        Log($"Pooling reverse-tunnel connections for 127.0.0.1:{port} (adb reverse).");
        pool.Start();
    }

    private async Task StopPoolAsync(string remoteSpec)
    {
        PortPool? pool = null;
        int port;
        lock (_gate)
        {
            if (!_remoteSpecToPort.Remove(remoteSpec, out port)) return;

            // Other reverse mappings can share a local port in principle — only tear the
            // pool down once nothing else still references it.
            if (!_remoteSpecToPort.ContainsValue(port))
                _pools.Remove(port, out pool);
        }

        if (pool is null) return;
        Log($"Stopping reverse-tunnel pool for 127.0.0.1:{port}; established streams remain connected.");
        await pool.DisposeAsync();
    }

    private async Task StopAllAsync()
    {
        List<PortPool> all;
        lock (_gate)
        {
            all = _pools.Values.ToList();
            _pools.Clear();
            _remoteSpecToPort.Clear();
        }

        if (all.Count > 0)
            Log($"Tearing down all {all.Count} reverse-tunnel pool(s) (adb cleared all reverse forwards).");
        foreach (var pool in all)
            await pool.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _relay.ReverseForwardEstablished -= OnEstablished;
        _relay.ReverseForwardRemoved -= OnRemoved;
        _relay.ReverseForwardsCleared -= OnCleared;
        await StopAllAsync();
        await _connections.DisposeAsync();
    }

    private void Log(string message) => LogMessage?.Invoke(this, message);

    /// <summary>
    /// Keeps <see cref="PoolSizePerPort"/> tunnel connections open and idle for one local
    /// port at all times. Replenishment is decoupled from how long a consumed connection's
    /// bridge then runs: when a paired connection has connected to the local app, a fresh
    /// idle replacement is spawned immediately, before starting
    /// the (potentially long-lived — a live mirroring session) bridge. Tying replenishment
    /// to the bridge's own lifetime, as an earlier version of this did, silently exhausts
    /// the pool the moment concurrently-consumed connections outlive a few seconds — which
    /// Device Mirroring's screen-sharing-agent does immediately, opening several reverse
    /// connections at once and holding at least one open for the whole session.
    /// </summary>
    private sealed class PortPool : IAsyncDisposable
    {
        private readonly int _port;
        private readonly TcpRelay _relay;
        private readonly RelayConnections _connections;
        private readonly int _idleTarget;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _tasks = new();
        private readonly object _tasksGate = new();

        public event EventHandler<string>? LogMessage;

        public PortPool(int port, TcpRelay relay, int idleTarget, RelayConnections connections)
        {
            _port = port;
            _relay = relay;
            _idleTarget = idleTarget;
            _connections = connections;
        }

        public void Start()
        {
            for (var i = 0; i < _idleTarget; i++)
                SpawnIdleConnection();
        }

        private void SpawnIdleConnection()
        {
            if (_cts.IsCancellationRequested) return;

            var task = MaintainIdleConnectionAsync(_cts.Token);
            lock (_tasksGate) { _tasks.Add(task); }
        }

        /// <summary>Owns one idle pooled connection at a time: opens it, waits for GO, and
        /// upon pairing transfers ownership to a session stream. That stream replenishes
        /// the idle slot after connecting locally, preserving connection order without
        /// waiting for the stream itself to end.</summary>
        private async Task MaintainIdleConnectionAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var target = _relay.Target;
                if (target is null)
                {
                    try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
                    continue;
                }

                TcpClient? tunnel = null;
                Stream? securedStream = null;
                var handedOff = false;
                try
                {
                    tunnel = new TcpClient();
                    var stream = securedStream = await AdbBridge.Core.Security.SecureTunnel.ConnectAsync(tunnel, target, ct);

                    var marker = Encoding.ASCII.GetBytes($"REV:{_port}\n");
                    await stream.WriteAsync(marker, ct);
                    Log($"Reverse-tunnel port {_port}: pooled connection reached the Host, waiting for GO.");

                    // Sits here — potentially for a long time — until the Host pairs this
                    // connection with a genuine device-originated reverse-connect.
                    if (!await WaitForGoAsync(stream, ct))
                    {
                        tunnel.Dispose();
                        continue; // dead before pairing, or pool torn down — retry this slot
                    }
                    Log($"Reverse-tunnel port {_port}: received GO, dialing the local app.");

                    // Removing the reverse rule cancels only idle slots. A consumed
                    // connection belongs to the whole session, not this pool anymore.
                    var consumed = tunnel;
                    _connections.Start(sessionToken => BridgeConsumedConnectionAsync(consumed, stream, sessionToken), consumed.Dispose);
                    handedOff = true;
                    return; // this task's one job (this one connection) is done
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Reverse-tunnel pool slot for port {_port} failed: {ex.Message}");
                    tunnel?.Dispose();
                    try { await Task.Delay(250, ct); } catch (OperationCanceledException) { break; }
                }
                finally { if (!handedOff) { securedStream?.Dispose(); tunnel?.Dispose(); } }
            }
        }

        private async Task BridgeConsumedConnectionAsync(TcpClient tunnel, Stream stream, CancellationToken ct)
        {
            using var _ = tunnel;
            await using var ownedStream = stream;
            using var local = new TcpClient();
            try
            {
                await local.ConnectAsync(IPAddress.Loopback, _port, ct);
                // Do not offer the next slot until this connection has entered the
                // local listener's queue. Video and control must arrive in order.
                SpawnIdleConnection();
            }
            catch (Exception ex)
            {
                Log($"Reverse-tunnel port {_port}: nothing local is listening ({ex.Message}).");
                SpawnIdleConnection();
                return;
            }

            try { await RelayPump.CopyBothWaysAsync(stream, local.GetStream(), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { Log($"Reverse-tunnel port {_port}: stream ended ({ex.Message})."); }
        }

        private static async Task<bool> WaitForGoAsync(Stream stream, CancellationToken ct)
        {
            var buf = new byte[3];
            var total = 0;
            while (total < buf.Length)
            {
                int n;
                try { n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct); }
                catch { return false; }
                if (n <= 0) return false;
                total += n;
            }
            return buf[0] == (byte)'G' && buf[1] == (byte)'O' && buf[2] == (byte)'\n';
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            List<Task> snapshot;
            lock (_tasksGate) { snapshot = _tasks.ToList(); }
            try { await Task.WhenAll(snapshot); } catch { /* swallow cancellation */ }
            _cts.Dispose();
        }

        private void Log(string message) => LogMessage?.Invoke(this, message);
    }
}
