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
    // Device Mirroring's screen-sharing-agent opens several reverse connections at once
    // on startup (observed: 3, landing within the same second) — this needs to be an
    // idle-standby target with headroom, not a hard concurrency cap.
    private const int PoolSizePerPort = 4;

    private readonly TcpRelay _relay;
    private readonly Dictionary<string, int> _remoteSpecToPort = new();
    private readonly Dictionary<int, PortPool> _pools = new();
    private readonly object _gate = new();

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

            pool = new PortPool(port, _relay, PoolSizePerPort);
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
        Log($"Tearing down reverse-tunnel pool for 127.0.0.1:{port}.");
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
    }

    private void Log(string message) => LogMessage?.Invoke(this, message);

    /// <summary>
    /// Keeps <see cref="PoolSizePerPort"/> tunnel connections open and idle for one local
    /// port at all times. Replenishment is decoupled from how long a consumed connection's
    /// bridge then runs: the moment a pooled connection is paired (receives "GO"), a fresh
    /// idle replacement is spawned immediately, before dialing the local app and starting
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
        private readonly int _idleTarget;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _tasks = new();
        private readonly object _tasksGate = new();

        public event EventHandler<string>? LogMessage;

        public PortPool(int port, TcpRelay relay, int idleTarget)
        {
            _port = port;
            _relay = relay;
            _idleTarget = idleTarget;
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
        /// upon pairing immediately spawns its own replacement before handling the bridge
        /// itself — so this task's own remaining lifetime never blocks the pool's idle
        /// count from being restored.</summary>
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
                try
                {
                    tunnel = new TcpClient();
                    await tunnel.ConnectAsync(target.Host, target.Port, ct);
                    var stream = tunnel.GetStream();

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

                    // Consumed — backfill the idle count now, independent of how long the
                    // bridge below ends up running.
                    SpawnIdleConnection();

                    await BridgeConsumedConnectionAsync(tunnel, stream, ct);
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
                }
            }
        }

        private async Task BridgeConsumedConnectionAsync(TcpClient tunnel, NetworkStream stream, CancellationToken ct)
        {
            using var _ = tunnel;
            using var local = new TcpClient();
            try
            {
                await local.ConnectAsync(IPAddress.Loopback, _port, ct);
            }
            catch (Exception ex)
            {
                Log($"Reverse-tunnel port {_port}: nothing local is listening ({ex.Message}).");
                return;
            }

            await RelayPump.CopyBothWaysAsync(stream, local.GetStream(), ct);
        }

        private static async Task<bool> WaitForGoAsync(NetworkStream stream, CancellationToken ct)
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
