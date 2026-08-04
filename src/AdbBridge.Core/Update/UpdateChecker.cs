using System.Reflection;
using System.Text.Json;

namespace AdbBridge.Core.Update;

public sealed record UpdateInfo(string Version, string ReleaseUrl);

/// <summary>
/// Checks GitHub Releases for a version newer than the one currently running. Read-only
/// (no download/self-replace) — surfaces a link to the release page and lets the user
/// decide whether to grab it, rather than silently replacing a running executable.
/// </summary>
public static class UpdateChecker
{
    private const string ApiUrl = "https://api.github.com/repos/blezDev/adbbridge/releases/latest";

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AdbBridge-UpdateChecker");

            using var response = await http.GetAsync(ApiUrl, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagProp) ||
                !doc.RootElement.TryGetProperty("html_url", out var urlProp))
                return null;

            var tag = tagProp.GetString();
            var url = urlProp.GetString();
            if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(url)) return null;

            var latest = ParseVersionCore(tag);
            var current = ParseVersionCore(Assembly.GetEntryAssembly()?.GetName().Version?.ToString());
            if (latest is null || current is null || latest <= current) return null;

            return new UpdateInfo(tag, url);
        }
        catch
        {
            // Offline, rate-limited, or the API shape changed — this is a nice-to-have
            // check, so fail silently rather than bothering the user about it.
            return null;
        }
    }

    /// <summary>Normalizes to Major.Minor.Build only (drops Revision), since a git tag
    /// like "v1.0.2" parses with an unset (-1) fourth component that would otherwise
    /// compare as "less than" an assembly version's explicit ".0" revision.</summary>
    private static Version? ParseVersionCore(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.TrimStart('v', 'V');
        return Version.TryParse(trimmed, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }
}
