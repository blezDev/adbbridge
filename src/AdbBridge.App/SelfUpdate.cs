using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AdbBridge.Core.Update;
using AdbBridge.Core.Security;

namespace AdbBridge.App;

internal sealed record UpdateHandoff(int ParentId, string Target, string Package, string Hash, string? Role);

internal static class SelfUpdate
{
    public static string UpdateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbBridge", "updates");

    public static async Task<string> PrepareAsync(UpdateInfo update, IProgress<int> progress, CancellationToken ct)
    {
        var target = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the running application.");
        SafeArchive.RejectLinks(target);
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "AdbBridge.dll")))
            throw new InvalidOperationException("In-app updating requires the single-file release build. Download and run the published AdbBridge.exe.");
        // Fail before downloading or disconnecting if this portable installation isn't writable.
        var probe = Path.Combine(Path.GetDirectoryName(target)!, $".adbbridge-write-test-{Guid.NewGuid():N}");
        try { using (File.Create(probe)) { } }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("AdbBridge cannot update in this folder. Move it to a writable folder, such as Downloads, and try again.", ex);
        }
        finally { if (File.Exists(probe)) File.Delete(probe); }

        var staging = Path.Combine(UpdateRoot, Guid.NewGuid().ToString("N"));
        PrivateFiles.EnsureDirectory(UpdateRoot);
        try
        {
            var package = await UpdatePackage.DownloadAsync(update, staging, progress, ct);
            var version = FileVersionInfo.GetVersionInfo(package).FileVersion;
            if (!Version.TryParse(version, out var downloaded) ||
                !Version.TryParse(update.Version.TrimStart('v', 'V'), out var expected) ||
                downloaded.Major != expected.Major || downloaded.Minor != expected.Minor || downloaded.Build != Math.Max(0, expected.Build))
                throw new InvalidDataException("The downloaded executable does not match the release version.");
            return package;
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    public static async Task StartHelperAsync(string package, string? role)
    {
        var staging = Path.GetDirectoryName(package)!;
        var target = Environment.ProcessPath!;
        var helper = Path.Combine(staging, "AdbBridge.Updater.exe");
        File.Copy(target, helper);
        var manifest = Path.Combine(staging, "handoff.json");
        var handoff = new UpdateHandoff(Environment.ProcessId, target, package, await UpdatePackage.HashAsync(package), role);
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(handoff));
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(manifest);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the update installer.");
        var ready = Path.Combine(staging, "ready");
        for (var i = 0; i < 150; i++)
        {
            if (File.Exists(ready)) return;
            if (process.HasExited) throw new IOException("The update installer could not initialize. Your installed application has not been changed.");
            await Task.Delay(100);
        }
        try { process.Kill(); } catch { /* already exited */ }
        throw new TimeoutException("The update installer did not start in time. Your installed application has not been changed.");
    }

    public static async Task ApplyAsync(string manifestPath)
    {
        var staging = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        SafeArchive.RejectLinks(manifestPath);
        var root = Path.GetFullPath(UpdateRoot) + Path.DirectorySeparatorChar;
        if (!staging.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Environment.ProcessPath, Path.Combine(staging, "AdbBridge.Updater.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid updater staging location.");
        var handoff = JsonSerializer.Deserialize<UpdateHandoff>(await File.ReadAllTextAsync(manifestPath))
            ?? throw new InvalidDataException("Invalid update handoff.");
        SafeArchive.RejectLinks(handoff.Package);
        SafeArchive.RejectLinks(handoff.Target);
        if (!string.Equals(handoff.Package, Path.Combine(staging, "AdbBridge.new.exe"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(await UpdatePackage.HashAsync(handoff.Package), handoff.Hash, StringComparison.OrdinalIgnoreCase) ||
            handoff.Role is not (null or "host" or "companion"))
            throw new InvalidDataException("The staged update could not be verified.");
        using var parent = Process.GetProcessById(handoff.ParentId);
        if (!string.Equals(parent.MainModule?.FileName, handoff.Target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update target is not the running AdbBridge application.");
        await File.WriteAllTextAsync(Path.Combine(staging, "ready"), "ready");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await parent.WaitForExitAsync(timeout.Token);

        var pending = handoff.Target + ".update-" + Guid.NewGuid().ToString("N");
        var backup = handoff.Target + ".previous";
        var replaced = false;
        try
        {
            // Recheck the exact locked bytes copied after the parent exits. The
            // staged package may have changed since the initial readiness check.
            await using var source = new FileStream(handoff.Package, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var pendingFile = new FileStream(pending, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
            await source.CopyToAsync(pendingFile);
            await pendingFile.FlushAsync();
            pendingFile.Position = 0;
            var copiedHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(pendingFile));
            if (!string.Equals(copiedHash, handoff.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The staged executable changed before installation. Update rejected.");
            await pendingFile.DisposeAsync(); // ReplaceFile on Windows requires the source handle closed.
            SafeArchive.RejectLinks(backup);
            // Atomic replacement on the installation volume, retaining the previous binary.
            for (var attempt = 0; ; attempt++)
            {
                try { File.Replace(pending, handoff.Target, backup); replaced = true; break; }
                catch (IOException) when (attempt < 30) { await Task.Delay(500); }
            }
            Restart(handoff.Target, handoff.Role);
        }
        catch (Exception ex)
        {
            // Restore and relaunch the previous binary when replacement or launch fails.
            if (replaced) File.Copy(backup, handoff.Target, overwrite: true);
            try { Restart(handoff.Target, handoff.Role); } catch { /* report original failure */ }
            throw new IOException("Could not install the update. The previous executable was retained. " + ex.Message, ex);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
        // Cleanup must never roll back an update that has already been launched.
        try
        {
            File.Delete(handoff.Package);
            File.Delete(manifestPath);
            File.Delete(Path.Combine(staging, "ready"));
        }
        catch { /* antivirus may still hold staged files; the new app is already running */ }
    }

    private static void Restart(string target, string? role)
    {
        var start = new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target)! };
        if (role is not null) start.ArgumentList.Add(role);
        using var process = Process.Start(start) ?? throw new IOException("Could not restart AdbBridge.");
    }
}
