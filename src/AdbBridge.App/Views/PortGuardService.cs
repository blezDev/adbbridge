using AdbBridge.Core.Adb;
using AdbBridge.Core.Relay;

namespace AdbBridge.App.Views;

/// <summary>
/// Background watchdog that keeps the relay owning 127.0.0.1:5037. If anything ever
/// takes the port away from us — Android Studio racing us on its own startup, a manual
/// `adb kill-server`, the relay crashing — this notices within a few seconds, clears
/// competing adb.exe processes, and rebinds the relay. This is what makes the setup
/// survive ADB server restarts instead of needing a manual re-run each time.
/// </summary>
public sealed class PortGuardService : IAsyncDisposable
{
    private readonly TcpRelay _relay;
    private readonly AdbProcessManager _adb;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public event EventHandler<string>? LogMessage;

    public PortGuardService(TcpRelay relay, AdbProcessManager adb, TimeSpan? interval = null)
    {
        _relay = relay;
        _adb = adb;
        _interval = interval ?? TimeSpan.FromSeconds(4);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = LoopAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_relay.IsListening)
                continue;

            try
            {
                Log("Relay is no longer listening on 127.0.0.1:5037 — reclaiming the port.");
                _adb.KillAllAdb();
                await _relay.StartAsync(ct);
            }
            catch (Exception ex)
            {
                Log($"Port reclaim failed: {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask; } catch { /* swallow cancellation */ }
        }
        _cts?.Dispose();
    }

    private void Log(string message) => LogMessage?.Invoke(this, message);
}
