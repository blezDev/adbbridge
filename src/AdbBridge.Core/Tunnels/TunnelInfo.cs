namespace AdbBridge.Core.Tunnels;

public sealed record TunnelInfo(string Host, int Port)
{
    public override string ToString() => $"{Host}:{Port}";

    public static bool TryParse(string? text, out TunnelInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();
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
        var portText = text[(idx + 1)..];
        if (!int.TryParse(portText, out var port) || port is <= 0 or > 65535)
            return false;

        info = new TunnelInfo(host, port);
        return true;
    }
}
