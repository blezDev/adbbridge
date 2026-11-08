using System.IO.Compression;
using AdbBridge.Core.Update;

namespace AdbBridge.Core.Mirroring;

/// <summary>Finds an existing scrcpy installation or installs a verified portable release per user.</summary>
public sealed class ScrcpyInstaller
{
    // Official Windows release and SHA-256 published in Genymobile/scrcpy/doc/windows.md.
    public const string Version = "5.0.1";
    public const string ArchiveHash = "b12a2c4ee8be317422451fc7dcf8ee20a71b5ea7ef9ad73ddd825a25316227a5";
    public const string DownloadUrl = "https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-win64-v5.0.1.zip";
    private const string PackageFolder = "scrcpy-win64-v5.0.1";
    private const long MaximumArchiveBytes = 150L * 1024 * 1024;
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private readonly string _installRoot;
    private readonly string _applicationDirectory;
    private readonly string _searchPath;

    public ScrcpyInstaller(string? installRoot = null, string? applicationDirectory = null, string? searchPath = null)
    {
        _installRoot = installRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbBridge", "tools", "scrcpy");
        _applicationDirectory = applicationDirectory ?? AppContext.BaseDirectory;
        _searchPath = searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "";
    }

    public static bool IsInstalled(string? executable) => !string.IsNullOrWhiteSpace(executable) &&
        File.Exists(executable) && File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "scrcpy-server"));

    public string? FindExisting(string? configuredPath)
    {
        var candidates = new List<string?>
        {
            configuredPath,
            Path.Combine(_applicationDirectory, "scrcpy.exe"),
            Path.Combine(_applicationDirectory, "scrcpy", "scrcpy.exe"),
            Path.Combine(_installRoot, Version, PackageFolder, "scrcpy.exe")
        };
        foreach (var directory in _searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(directory.Trim().Trim('"'), "scrcpy.exe")); }
            catch (ArgumentException) { /* skip malformed PATH entry */ }
        }
        return candidates.FirstOrDefault(IsInstalled);
    }

    public async Task<string> EnsureInstalledAsync(string? configuredPath, IProgress<string>? progress = null,
        CancellationToken ct = default, HttpClient? client = null)
    {
        var existing = FindExisting(configuredPath);
        if (existing is not null) return existing;
        await InstallGate.WaitAsync(ct);
        string? staging = null;
        var ownsClient = client is null;
        try
        {
            existing = FindExisting(configuredPath);
            if (existing is not null) return existing;
            progress?.Report($"Downloading scrcpy {Version}...");
            Directory.CreateDirectory(_installRoot);
            staging = Path.Combine(_installRoot, ".install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            client ??= new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AdbBridge-ScrcpyInstaller");
            using var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            if (length > MaximumArchiveBytes) throw new InvalidDataException("The scrcpy download is too large.");
            var archive = Path.Combine(staging, "scrcpy.zip");
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(archive))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read, lastPercent = -1;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaximumArchiveBytes) throw new InvalidDataException("The scrcpy download is too large.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    var percent = length is > 0 ? (int)(total * 100 / length.Value) : -1;
                    if (percent != lastPercent) { progress?.Report($"Downloading scrcpy... {percent}%"); lastPercent = percent; }
                }
            }
            progress?.Report("Verifying scrcpy download...");
            if (!string.Equals(await UpdatePackage.HashAsync(archive, ct), ArchiveHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("scrcpy download verification failed. Please try again.");
            progress?.Report("Installing scrcpy...");
            var extracted = Path.Combine(staging, "extracted");
            await Task.Run(() => ExtractVerifiedArchive(archive, extracted, ct), ct);
            ct.ThrowIfCancellationRequested();
            var destination = Path.Combine(_installRoot, Version);
            if (Directory.Exists(destination))
            {
                // Move aside an incomplete installation without disturbing a running executable.
                Directory.Move(destination, Path.Combine(_installRoot, ".incomplete-" + Guid.NewGuid().ToString("N")));
            }
            Directory.Move(extracted, destination);
            return Path.Combine(destination, PackageFolder, "scrcpy.exe");
        }
        finally
        {
            if (ownsClient) client?.Dispose();
            try
            {
                if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch (IOException) { /* temporary files may still be held by antivirus */ }
            catch (UnauthorizedAccessException) { /* installation is complete; cleanup is best effort */ }
            finally { InstallGate.Release(); }
        }
    }

    public static void ExtractVerifiedArchive(string archivePath, string destination, CancellationToken ct = default)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Sum(entry => entry.Length) > 500L * 1024 * 1024)
            throw new InvalidDataException("The scrcpy installation is too large.");
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(PackageFolder + "/", StringComparison.Ordinal) ||
                name.Split('/').Any(part => part is ".." or "." || part.Contains(':')))
                throw new InvalidDataException("The scrcpy archive contains an unexpected path.");
            var path = Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar));
            if (name.EndsWith('/')) Directory.CreateDirectory(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: false);
            }
        }
        if (!IsInstalled(Path.Combine(destination, PackageFolder, "scrcpy.exe")))
            throw new InvalidDataException("The scrcpy installation is missing its executable or server.");
    }
}
