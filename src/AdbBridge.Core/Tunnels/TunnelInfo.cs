namespace AdbBridge.Core.Tunnels;

public sealed record TunnelInfo(string Host, int Port)
{
    public AdbBridge.Core.Security.BridgeCredentials? Credentials { get; init; }
    public override string ToString() => $"{Host}:{Port}";

    public string ConnectionCode => Credentials is null ? ToString() :
        $"adbbridge://{Host}:{Port}/#{Credentials.ServerPin}.{Credentials.ClientPfx.Replace('+', '-').Replace('/', '_').TrimEnd('=')}";

    public static bool TryParse(string? text, out TunnelInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();
        if (text.Length > 32768) return false;
        if (text.StartsWith("adbbridge://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "adbbridge" ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || uri.AbsolutePath != "/") return false;
            var parts = uri.Fragment.TrimStart('#').Split('.');
            if (parts.Length != 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit) || uri.Port is <= 0 or > 65535) return false;
            try
            {
                var base64 = parts[1].Replace('-', '+').Replace('_', '/');
                base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
                var pfx = Convert.FromBase64String(base64);
                using var certificate = AdbBridge.Core.Security.BridgeIdentity.Import(pfx);
                if (!certificate.HasPrivateKey) return false;
                info = new TunnelInfo(uri.Host, uri.Port) { Credentials = new(parts[0], Convert.ToBase64String(pfx)) };
                return true;
            }
            catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException) { return false; }
        }
        foreach (var prefix in new[] { "tcp://", "http://", "https://" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..];
                break;
            }
        }
        text = text.TrimEnd('/');

        var idx = text.LastIndexOf(':');
        if (idx <= 0 || idx == text.Length - 1)
            return false;

        var host = text[..idx];
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown) return false;
        var portText = text[(idx + 1)..];
        if (!int.TryParse(portText, out var port) || port is <= 0 or > 65535)
            return false;

        info = new TunnelInfo(host, port);
        return true;
    }
}
