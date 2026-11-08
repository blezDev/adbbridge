using System.Reflection;
using System.Text.Json;

namespace AdbBridge.Core.Update;

public sealed record UpdateInfo(string Version, string ReleaseUrl, string? DownloadUrl = null, string? ChecksumUrl = null);

/// <summary>
/// Finds a newer stable release and its Windows package and checksum assets.
/// </summary>
public static class UpdateChecker
{
    private const string ApiUrl = "https://api.github.com/repos/blezDev/adbbridge/releases/latest";

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default, bool throwOnError = false)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AdbBridge-UpdateChecker");

            using var response = await http.GetAsync(ApiUrl, ct);
            response.EnsureSuccessStatusCode();

            var current = ParseVersionCore(Assembly.GetEntryAssembly()?.GetName().Version?.ToString());
            return ParseRelease(await response.Content.ReadAsStringAsync(ct), current ?? new Version(0, 0, 0));
        }
        catch when (!throwOnError)
        {
            // Offline, rate-limited, or the API shape changed — this is a nice-to-have
            // check, so fail silently rather than bothering the user about it.
            return null;
        }
    }

    public static UpdateInfo? ParseRelease(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var release = doc.RootElement;
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
            release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString();
        var url = release.GetProperty("html_url").GetString();
        var latest = ParseVersionCore(tag);
        var normalizedCurrent = ParseVersionCore(current.ToString());
        if (latest is null || string.IsNullOrWhiteSpace(url)) throw new InvalidDataException("Invalid release metadata.");
        if (latest <= normalizedCurrent) return null;
        string? download = null, checksum = null;
        if (release.TryGetProperty("assets", out var assets))
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var assetUrl = asset.GetProperty("browser_download_url").GetString();
                // Only accept assets attached to this exact release in our repository.
                var prefix = $"https://github.com/blezDev/adbbridge/releases/download/{Uri.EscapeDataString(tag!)}/";
                if (assetUrl?.StartsWith(prefix, StringComparison.Ordinal) != true) continue;
                if (name == UpdatePackage.PackageName) download = assetUrl;
                if (name == "SHA256SUMS.txt") checksum = assetUrl;
            }
        return new UpdateInfo(tag!, url!, download, checksum);
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
