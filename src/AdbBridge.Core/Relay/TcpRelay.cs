using System.Net;
using System.Net.Sockets;
using System.Text;
using AdbBridge.Core.Adb;
using AdbBridge.Core.Tunnels;

namespace AdbBridge.Core.Relay;

/// <summary>
/// Owns 127.0.0.1:&lt;port&gt; (5037 by default, the ADB server port) and transparently
/// proxies every byte to the current tunnel target. Because this is a real, protocol-
/// correct listener, any adb client (including Android Studio's bundled adb.exe) that
/// connects here completes the normal adb server handshake and just uses it — it never
/// falls back to spawning its own local server, which is what breaks device detection.
/// </summary>
public sealed class TcpRelay : IAsyncDisposable
{
    private readonly int _localPort;
    private readonly AdbProcessManager _adbProcessManager;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private TunnelInfo? _target;
    private readonly object _targetLock = new();

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private const string HostKillService = "host:kill";
    private static readonly byte[] OkayBytes = Encoding.ASCII.GetBytes("OKAY");

    public bool IsListening => _listener is not null && _cts is { IsCancellationRequested: false };
    public int LocalPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _localPort;
    public DateTime? LastSuccessfulConnection { get; private set; }
    public DateTime? LastFailedConnection { get; private set; }
    public string? LastError { get; private set; }
    public TunnelInfo? Target { get { lock (_targetLock) return _target; } }

    public event EventHandler<string>? LogMessage;

    /// <summary>
    /// Raised when a relayed connection's <c>adb forward</c> request resolves to a local
    /// port (see <see cref="AdbForwardWatcher"/>) — consumed by <see cref="ForwardPortRelay"/>
    /// to open a matching listener here, since Android Studio's Device Mirroring / App
    /// Inspection connect to that port directly rather than through this relay.
    /// </summary>
    public event EventHandler<int>? ForwardEstablished;
    public event EventHandler<int>? ForwardRemoved;
    public event EventHandler? ForwardsCleared;

    /// <summary>See <see cref="AdbForwardWatcher.ReverseForwardEstablished"/> — consumed
    /// by <see cref="ReversePoolRelay"/>.</summary>
    public event EventHandler<(string RemoteSpec, int LocalPort)>? ReverseForwardEstablished;
    public event EventHandler<string>? ReverseForwardRemoved;
    public event EventHandler? ReverseForwardsCleared;

    public TcpRelay(AdbProcessManager adbProcessManager, int localPort = 5037)
    {
        _adbProcessManager = adbProcessManager;
        _localPort = localPort;
    }

    public void UpdateTarget(TunnelInfo target)
    {
        lock (_targetLock)
        {
            _target = target;
        }
        Log($"Relay target set to {target}");
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await StopAsync();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, _localPort);
                _listener.Start();
                Log($"Relay listening on 127.0.0.1:{_localPort} (attempt {attempt}).");
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                Log($"Port {_localPort} is in use — killing any adb.exe holding it and retrying.");
                _adbProcessManager.KillAllAdb();
                await Task.Delay(500, ct);
                if (attempt == maxAttempts)
                    throw;
            }
        }

        _acceptLoopTask = AcceptLoopAsync(_cts.Token);
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
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient inbound, CancellationToken ct)
    {
        using var _ = inbound;

        TunnelInfo? target;
        lock (_targetLock) { target = _target; }

        if (target is null)
        {
            Log("Rejected connection: no tunnel target configured yet.");
            return;
        }

        try
        {
            var inboundStream = inbound.GetStream();

            var (isKill, prebuffered) = await TryInterceptKillAsync(inboundStream, ct);
            if (isKill)
            {
                await inboundStream.WriteAsync(OkayBytes, ct);
                Log("Blocked a host:kill request from the local adb client — this relay's port " +
                    "isn't the Host's real adb server, so a local `adb kill-server` can't be allowed " +
                    "to tear down the remote one.");
                return;
            }

            using var outbound = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await outbound.ConnectAsync(target.Host, target.Port, connectCts.Token);

            LastSuccessfulConnection = DateTime.UtcNow;
            LastError = null;

            var outboundStream = outbound.GetStream();

            var watcher = new AdbForwardWatcher();
            watcher.ForwardEstablished += (_, port) => ForwardEstablished?.Invoke(this, port);
            watcher.ForwardRemoved += (_, port) => ForwardRemoved?.Invoke(this, port);
            watcher.ForwardsCleared += (_, _) => ForwardsCleared?.Invoke(this, EventArgs.Empty);
            watcher.ReverseForwardEstablished += (_, e) => ReverseForwardEstablished?.Invoke(this, e);
            watcher.ReverseForwardRemoved += (_, remoteSpec) => ReverseForwardRemoved?.Invoke(this, remoteSpec);
            watcher.ReverseForwardsCleared += (_, _) => ReverseForwardsCleared?.Invoke(this, EventArgs.Empty);
            watcher.ServiceObserved += (_, service) => Log($"adb service request: {service}");

            if (prebuffered.Length > 0)
            {
                watcher.OnClientToServer(prebuffered);
                await outboundStream.WriteAsync(prebuffered, ct);
            }

            await RelayPump.CopyBothWaysAsync(
                inboundStream, outboundStream,
                (buf, n) => watcher.OnClientToServer(buf.AsSpan(0, n)),
                (buf, n) => watcher.OnServerToClient(buf.AsSpan(0, n)),
                ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LastFailedConnection = DateTime.UtcNow;
            LastError = $"Timed out reaching {target} within {ConnectTimeout.TotalSeconds:0}s.";
            Log($"Relay connection error: {LastError}");
        }
        catch (Exception ex)
        {
            LastFailedConnection = DateTime.UtcNow;
            LastError = ex.Message;
            Log($"Relay connection error: {ex.Message}");
        }
    }

    /// <summary>
    /// Peeks the very first request frame on a fresh connection to check whether it's the
    /// adb host service <c>host:kill</c> — the wire form of `adb kill-server` — before any
    /// bytes touch the tunnel. This relay's local port is a stand-in for the Host's real
    /// adb server; a plain byte-for-byte proxy would forward a local `adb kill-server`
    /// straight through and actually kill the Host's real server, taking the phone's USB
    /// connection down with it. Android Studio's own adb housekeeping issues this
    /// routinely as part of its normal reconnect logic — harmless on a real local adb
    /// server, destructive here. Every other request is left completely alone: the bytes
    /// read here are handed back to the caller as <c>Prebuffered</c> to replay onward.
    /// </summary>
    private static async Task<(bool IsKill, byte[] Prebuffered)> TryInterceptKillAsync(NetworkStream inboundStream, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        var lenRead = await ReadFullyAsync(inboundStream, lenBuf, ct);
        if (lenRead < lenBuf.Length)
            return (false, lenBuf[..lenRead]);

        if (!RelayPump.TryParseHexLength(lenBuf[0], lenBuf[1], lenBuf[2], lenBuf[3], out var bodyLen) ||
            bodyLen != HostKillService.Length)
        {
            return (false, lenBuf);
        }

        var bodyBuf = new byte[HostKillService.Length];
        var bodyRead = await ReadFullyAsync(inboundStream, bodyBuf, ct);
        if (bodyRead < bodyBuf.Length ||
            !Encoding.ASCII.GetString(bodyBuf).Equals(HostKillService, StringComparison.Ordinal))
        {
            var combined = new byte[lenBuf.Length + bodyRead];
            lenBuf.CopyTo(combined, 0);
            bodyBuf.AsSpan(0, bodyRead).CopyTo(combined.AsSpan(lenBuf.Length));
            return (false, combined);
        }

        return (true, Array.Empty<byte>());
    }

    /// <summary>Reads until <paramref name="buffer"/> is full or the connection closes early,
    /// returning how many bytes actually arrived.</summary>
    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read <= 0) break;
            total += read;
        }
        return total;
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
