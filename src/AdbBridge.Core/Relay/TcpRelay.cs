using System.Net;
using System.Net.Sockets;
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

    public bool IsListening => _listener is not null && _cts is { IsCancellationRequested: false };
    public DateTime? LastSuccessfulConnection { get; private set; }
    public DateTime? LastFailedConnection { get; private set; }
    public string? LastError { get; private set; }

    public event EventHandler<string>? LogMessage;

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
            using var outbound = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await outbound.ConnectAsync(target.Host, target.Port, connectCts.Token);

            LastSuccessfulConnection = DateTime.UtcNow;
            LastError = null;

            var inboundStream = inbound.GetStream();
            var outboundStream = outbound.GetStream();

            var toRemote = inboundStream.CopyToAsync(outboundStream, ct);
            var toLocal = outboundStream.CopyToAsync(inboundStream, ct);

            await Task.WhenAny(toRemote, toLocal);
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
