using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdbBridge.Core.Tunnels;

namespace AdbBridge.Core.Security;

public static class SecureTunnel
{
    private static bool Matches(X509Certificate? certificate, string pin)
    {
        if (certificate is null || pin.Length != 64 || !pin.All(Uri.IsHexDigit)) return false;
        using var parsed = new X509Certificate2(certificate);
        return DateTime.UtcNow >= parsed.NotBefore.ToUniversalTime() && DateTime.UtcNow <= parsed.NotAfter.ToUniversalTime() &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), Convert.FromHexString(pin));
    }

    public static async Task<SslStream> ConnectAsync(TcpClient client, TunnelInfo target, CancellationToken ct)
    {
        var credentials = target.Credentials ?? throw new AuthenticationException("Paste the secure connection code from an updated Host. Unauthenticated connections are disabled.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(target.Host, target.Port, deadline.Token);
        var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        try
        {
            using var certificate = BridgeIdentity.Import(Convert.FromBase64String(credentials.ClientPfx));
            if (!certificate.HasPrivateKey) throw new AuthenticationException("The connection code has no client private key.");
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "adbbridge",
                ClientCertificates = new X509CertificateCollection { certificate },
                RemoteCertificateValidationCallback = (_, remote, _, _) => Matches(remote, credentials.ServerPin),
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                AllowRenegotiation = false
            }, deadline.Token);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    public static async Task<SslStream> AcceptAsync(Stream network, BridgeIdentity identity, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var clientPin = BridgeIdentity.Pin(identity.ClientCertificate);
        var stream = new SslStream(network, leaveInnerStreamOpen: false);
        try
        {
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = identity.ServerCertificate,
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = (_, remote, _, _) => Matches(remote, clientPin),
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                AllowRenegotiation = false
            }, deadline.Token);
            // Explicitly verify the peer even when the platform reports a resumed session.
            if (!stream.IsMutuallyAuthenticated || !Matches(stream.RemoteCertificate, clientPin))
                throw new AuthenticationException("Unpaired Companion rejected.");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
}
