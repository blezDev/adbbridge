using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace AdbBridge.Core.Security;

/// <summary>A Host identity and the private client credential shared with its Companion.</summary>
public sealed class BridgeIdentity : IDisposable
{
    public X509Certificate2 ServerCertificate { get; }
    public X509Certificate2 ClientCertificate { get; }
    public BridgeCredentials Credentials => new(Pin(ServerCertificate), Convert.ToBase64String(ClientCertificate.Export(X509ContentType.Pfx)));

    private BridgeIdentity(X509Certificate2 server, X509Certificate2 client)
    {
        ServerCertificate = server;
        ClientCertificate = client;
    }

    public static BridgeIdentity Create() => new(CreateCertificate("AdbBridge Host", false), CreateCertificate("AdbBridge Companion", true));

    private static X509Certificate2 CreateCertificate(string name, bool client)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(client ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        return Import(certificate.Export(X509ContentType.Pfx));
    }

    // Windows Schannel requires a user key container. Do not use PersistKeySet:
    // temporary imported keys are removed when the certificate is disposed.
    public static X509Certificate2 Import(byte[] pfx) => new(pfx, (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    public static string Pin(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    public string Export() => JsonSerializer.Serialize(new[]
    {
        Convert.ToBase64String(ServerCertificate.Export(X509ContentType.Pfx)),
        Convert.ToBase64String(ClientCertificate.Export(X509ContentType.Pfx))
    });

    public static BridgeIdentity FromExport(string json)
    {
        var parts = JsonSerializer.Deserialize<string[]>(json);
        if (parts?.Length != 2) throw new InvalidDataException("Invalid Host identity. Create a new pairing identity.");
        var server = Import(Convert.FromBase64String(parts[0]));
        try { return new BridgeIdentity(server, Import(Convert.FromBase64String(parts[1]))); }
        catch { server.Dispose(); throw; }
    }

    public void Dispose() { ServerCertificate.Dispose(); ClientCertificate.Dispose(); }
    public override string ToString() => "AdbBridge identity (private keys hidden)";
}

public sealed record BridgeCredentials(string ServerPin, string ClientPfx)
{
    public override string ToString() => "AdbBridge pairing credential (hidden)";
}
