namespace AdbBridge.Core.Tunnels;

/// <summary>
/// Placeholder for a future Cloudflare Tunnel transport. Raw TCP through Cloudflare
/// (as opposed to HTTP-only quick tunnels) requires Cloudflare Zero Trust plus a domain
/// added to your Cloudflare account, neither of which is available yet. This type exists
/// so <see cref="ITunnelProvider"/> consumers can add Cloudflare support later without
/// changing anything else — it is intentionally not wired into either app's UI.
///
/// When a domain is available, implement this by:
///  1. Host: `cloudflared tunnel run &lt;name&gt;` with an ingress rule of
///     `tcp://localhost:5037` mapped to a chosen hostname.
///  2. Companion: `cloudflared access tcp --hostname &lt;hostname&gt; --url 127.0.0.1:5037`
///     (requires a Cloudflare Access "self-hosted" application for that hostname,
///     typically authorized via a service token so it can run headless).
/// </summary>
public sealed class CloudflareTunnelProvider : ITunnelProvider
{
    public TunnelInfo? Current => null;
    public bool IsRunning => false;

    public event EventHandler<TunnelInfo>? AddressChanged { add { } remove { } }
    public event EventHandler<string>? LogMessage { add { } remove { } }

    public Task StartAsync(CancellationToken ct = default) =>
        throw new NotImplementedException("Cloudflare Tunnel support requires a Cloudflare Zero Trust setup with a domain — see class remarks.");

    public Task StopAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
