using System.Net;
using System.Net.Sockets;
using System.Text;
using AdbBridge.Core.Tunnels;

namespace AdbBridge.Core.Relay;

/// <summary>
/// Companion-side counterpart to <see cref="ForwardBroker"/>. Watches the given
/// <see cref="TcpRelay"/> for adb <c>forward:</c> requests (via its
/// <see cref="TcpRelay.ForwardEstablished"/>/<see cref="TcpRelay.ForwardRemoved"/>/
/// <see cref="TcpRelay.ForwardsCleared"/> events) and, for each one, opens a matching
/// local TCP listener on this machine at the same port number the real adb server bound
/// on the Host. Android Studio's Device Mirroring and App Inspection tools connect
/// directly to that port (bypassing the adb server protocol entirely) — without a local
/// listener here, that connection attempt finds nothing and the feature fails silently
/// even though `adb forward` itself succeeded.
///
/// Each accepted connection is tunneled through the exact same tunnel address the relay
/// already uses, prefixed with a short marker line the Host's <see cref="ForwardBroker"/>
/// uses to route it to the right local port there.
/// </summary>
public sealed class ForwardPortRelay : IAsyncDisposable
{
    private readonly TcpRelay _relay;
    private readonly Dictionary<int, PortListener> _listeners = new();
    private readonly object _gate = new();
    private readonly RelayConnections _connections = new();

    public event EventHandler<string>? LogMessage;

    public ForwardPortRelay(TcpRelay relay)
    {
        _relay = relay;
        _relay.ForwardEstablished += OnForwardEstablished;
        _relay.ForwardRemoved += OnForwardRemoved;
        _relay.ForwardsCleared += OnForwardsCleared;
    }

    private void OnForwardEstablished(object? sender, int port) => _ = StartListenerAsync(port);
    private void OnForwardRemoved(object? sender, int port) => _ = StopListenerAsync(port);
    private void OnForwardsCleared(object? sender, EventArgs e) => _ = StopAllAsync();

    private async Task StartListenerAsync(int port)
    {
        lock (_gate)
        {
            if (_listeners.ContainsKey(port)) return;
        }

        var listener = new PortListener(port, _relay, _connections);
        listener.LogMessage += (_, msg) => Log(msg);

        try
        {
            await listener.StartAsync();
        }
        catch (Exception ex)
        {
            Log($"Could not open local forward listener on 127.0.0.1:{port}: {ex.Message}");
            return;
        }

        lock (_gate) { _listeners[port] = listener; }
        Log($"Relaying 127.0.0.1:{port} through the tunnel for App Inspection / Device Mirroring.");
    }

    private async Task StopListenerAsync(int port)
    {
        PortListener? listener;
        lock (_gate)
        {
            if (!_listeners.Remove(port, out listener)) return;
        }
        Log($"Stopping forward listener on 127.0.0.1:{port}; established streams remain connected.");
        await listener!.DisposeAsync();
    }

    private async Task StopAllAsync()
    {
        List<PortListener> all;
        lock (_gate)
        {
            all = _listeners.Values.ToList();
            _listeners.Clear();
        }
        if (all.Count > 0)
            Log($"Tearing down all {all.Count} forward listener(s) (adb cleared all forwards).");
        foreach (var l in all)
            await l.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _relay.ForwardEstablished -= OnForwardEstablished;
        _relay.ForwardRemoved -= OnForwardRemoved;
        _relay.ForwardsCleared -= OnForwardsCleared;
        await StopAllAsync();
        await _connections.DisposeAsync();
    }

    private void Log(string message) => LogMessage?.Invoke(this, message);

    /// <summary>Owns one local port's listener and relays each accepted connection to the
    /// tunnel, tagged with the marker line <see cref="ForwardBroker"/> expects.</summary>
    private sealed class PortListener : IAsyncDisposable
    {
        private readonly int _port;
        private readonly TcpRelay _relay;
        private readonly RelayConnections _connections;
        private readonly CancellationTokenSource _cts = new();
        private TcpListener? _listener;
        private Task? _acceptLoopTask;

        public event EventHandler<string>? LogMessage;

        public PortListener(int port, TcpRelay relay, RelayConnections connections)
        {
            _port = port;
            _relay = relay;
            _connections = connections;
        }

        public Task StartAsync()
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
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

                _connections.Start(sessionToken => HandleClientAsync(client, sessionToken), client.Dispose);
            }
        }

        private async Task HandleClientAsync(TcpClient inbound, CancellationToken ct)
        {
            using var _ = inbound;

            var target = _relay.Target;
            if (target is null)
            {
                Log($"Rejected forwarded connection on port {_port}: no tunnel target configured.");
                return;
            }

            var acceptedAt = DateTime.UtcNow;
            try
            {
                using var outbound = new TcpClient();
                await outbound.ConnectAsync(target.Host, target.Port, ct);
                Log($"Forwarded connection on port {_port} reached the tunnel in {(DateTime.UtcNow - acceptedAt).TotalMilliseconds:0}ms.");

                var inboundStream = inbound.GetStream();
                var outboundStream = outbound.GetStream();

                var marker = Encoding.ASCII.GetBytes($"FWD:{_port}\n");
                await outboundStream.WriteAsync(marker, ct);

                await RelayPump.CopyBothWaysAsync(inboundStream, outboundStream, ct);
            }
            catch (Exception ex)
            {
                Log($"Forwarded connection on port {_port} failed: {ex.Message}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener?.Stop();
            _listener = null;

            if (_acceptLoopTask is not null)
            {
                try { await _acceptLoopTask; } catch { /* swallow cancellation */ }
            }
            _cts.Dispose();
        }

        private void Log(string message) => LogMessage?.Invoke(this, message);
    }
}
