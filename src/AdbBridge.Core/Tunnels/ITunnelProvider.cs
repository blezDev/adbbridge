namespace AdbBridge.Core.Tunnels;

/// <summary>
/// A transport that exposes a local TCP port (the ADB server) to the outside world
/// and reports the public address it is currently reachable at.
/// </summary>
public interface ITunnelProvider : IAsyncDisposable
{
    TunnelInfo? Current { get; }
    bool IsRunning { get; }

    event EventHandler<TunnelInfo>? AddressChanged;
    event EventHandler<string>? LogMessage;

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
}
