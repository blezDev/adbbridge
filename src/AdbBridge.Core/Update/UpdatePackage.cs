using System.IO.Compression;
using System.Security.Cryptography;

namespace AdbBridge.Core.Update;

public static class UpdatePackage
{
    public const string PackageName = "AdbBridge-win-x64.zip";
    private const long MaxDownloadBytes = 300L * 1024 * 1024;

    public static string ParseChecksum(string text)
    {
        var matches = text.Split('\n').Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[1].TrimStart('*') == PackageName).ToArray();
        if (matches.Length != 1 || matches[0][0].Length != 64 || !matches[0][0].All(Uri.IsHexDigit))
            throw new InvalidDataException("The release does not contain a valid package checksum.");
        return matches[0][0];
    }

    public static async Task<string> HashAsync(string file, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    public static async Task<string> DownloadAsync(UpdateInfo update, string stagingDirectory,
        IProgress<int>? progress = null, CancellationToken ct = default, HttpClient? client = null)
    {
        if (update.DownloadUrl is null || update.ChecksumUrl is null)
            throw new InvalidOperationException("This release is missing its Windows package or SHA256SUMS.txt.");
        if (!UpdateChecker.IsAssetUrl(update.DownloadUrl, update.Version, PackageName) ||
            !UpdateChecker.IsAssetUrl(update.ChecksumUrl, update.Version, "SHA256SUMS.txt"))
            throw new InvalidDataException("Updates must use this repository's exact HTTPS release assets.");
        AdbBridge.Core.Security.SafeArchive.RejectLinks(stagingDirectory);
        var ownsClient = client is null;
        client ??= new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        try
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AdbBridge-Updater");
            using var checksumResponse = await client.GetAsync(update.ChecksumUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            checksumResponse.EnsureSuccessStatusCode();
            await using var checksumInput = await checksumResponse.Content.ReadAsStreamAsync(ct);
            using var checksumBytes = new MemoryStream();
            var checksumBuffer = new byte[4096];
            int checksumRead;
            while ((checksumRead = await checksumInput.ReadAsync(checksumBuffer, ct)) > 0)
            {
                if (checksumBytes.Length + checksumRead > 16384) throw new InvalidDataException("The checksum file is too large.");
                checksumBytes.Write(checksumBuffer, 0, checksumRead);
            }
            var checksum = ParseChecksum(System.Text.Encoding.ASCII.GetString(checksumBytes.ToArray()));
            Directory.CreateDirectory(stagingDirectory);
            var zipPath = Path.Combine(stagingDirectory, PackageName);
            AdbBridge.Core.Security.SafeArchive.RejectLinks(zipPath);
            using var response = await client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            if (length > MaxDownloadBytes) throw new InvalidDataException("The update package is too large.");
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var destination = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes) throw new InvalidDataException("The update package is too large.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress?.Report(length is > 0 ? (int)(total * 100 / length.Value) : -1);
                }
            }
            if (!string.Equals(await HashAsync(zipPath, ct), checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update checksum verification failed. Your installed application has not been changed.");
            var exePath = ExtractExecutable(zipPath, stagingDirectory);
            File.Delete(zipPath);
            return exePath;
        }
        finally { if (ownsClient) client.Dispose(); }
    }

    public static string ExtractExecutable(string zipPath, string stagingDirectory)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        // Releases made with PowerShell may use backslashes in ZIP entry names.
        var entries = archive.Entries.Where(entry =>
            entry.FullName.Replace('\\', '/') is "AdbBridge-win-x64/AdbBridge.exe" or "AdbBridge.exe").ToArray();
        if (entries.Length != 1 || entries[0].Length is < 2 or > MaxDownloadBytes)
            throw new InvalidDataException("The update package must contain exactly one AdbBridge.exe.");
        // Extract only the expected file, never archive-controlled paths or settings.
        var exePath = Path.Combine(stagingDirectory, "AdbBridge.new.exe");
        AdbBridge.Core.Security.SafeArchive.RejectLinks(exePath);
        AdbBridge.Core.Security.SafeArchive.Extract(entries[0], exePath, MaxDownloadBytes);
        using var file = File.OpenRead(exePath);
        if (file.ReadByte() != 'M' || file.ReadByte() != 'Z')
            throw new InvalidDataException("The update does not contain a Windows executable.");
        return exePath;
    }
}
