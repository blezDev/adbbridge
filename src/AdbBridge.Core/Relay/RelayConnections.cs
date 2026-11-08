namespace AdbBridge.Core.Relay;

/// <summary>
/// Owns established streams for a whole Companion session. ADB forwarding rules only
/// control new connections; removing a rule must not cancel streams already using it.
/// </summary>
internal sealed class RelayConnections : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<Task> _tasks = new();
    private readonly object _gate = new();
    private bool _stopping;

    public void Start(Func<CancellationToken, Task> connection, Action rejected)
    {
        lock (_gate)
        {
            if (_stopping) { rejected(); return; }
            var task = connection(_cts.Token);
            _tasks.Add(task);
            _ = ObserveAsync(task);
        }
    }

    private async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { /* the connection handler reports its own errors */ }
        finally { lock (_gate) _tasks.Remove(task); }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            tasks = _tasks.ToArray();
        }
        await _cts.CancelAsync();
        try { await Task.WhenAll(tasks); } catch { /* connection handlers log errors */ }
        _cts.Dispose();
    }
}
