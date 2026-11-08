using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdbBridge.App;
using AdbBridge.Core.Update;

if (args.FirstOrDefault() == "--test-helper")
{
    try { await SelfUpdate.ApplyAsync(args[1]); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}
if (args.FirstOrDefault() == "--test-parent")
{
    await File.WriteAllTextAsync(Path.Combine(args[1], "parent-started"), "started");
    for (var i = 0; i < 300 && (!File.Exists(Path.Combine(args[1], "ready")) || File.Exists(Path.Combine(args[1], "hold-parent"))); i++) await Task.Delay(100);
    return;
}
if (Path.GetFileName(Environment.ProcessPath) == "AdbBridge.exe")
{
    await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "restarted"), "restarted");
    return;
}

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Reject(Action action, string message)
{
    try { action(); } catch (InvalidDataException) { return; }
    throw new Exception(message);
}
static Process Start(string exe, params string[] arguments)
{
    var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    return Process.Start(start) ?? throw new Exception("Could not start updater test process.");
}

var prefix = "https://github.com/blezDev/adbbridge/releases/download/v9.0.0/";
string Metadata(string assetPrefix, bool prerelease = false) => JsonSerializer.Serialize(new
{
    tag_name = "v9.0.0", html_url = "https://github.com/blezDev/adbbridge/releases/tag/v9.0.0", prerelease,
    assets = new[] {
        new { name = UpdatePackage.PackageName, browser_download_url = assetPrefix + UpdatePackage.PackageName },
        new { name = "SHA256SUMS.txt", browser_download_url = assetPrefix + "SHA256SUMS.txt" }
    }
});
Check(UpdateChecker.ParseRelease(Metadata(prefix), new Version(1, 0, 2, 0))?.DownloadUrl == prefix + UpdatePackage.PackageName, "Release assets not found.");
Check(UpdateChecker.ParseRelease(Metadata(prefix), new Version(9, 0, 0, 0)) is null, "Same version offered as update.");
Check(UpdateChecker.ParseRelease(Metadata(prefix, true), new Version(1, 0, 0)) is null, "Prerelease offered.");
Check(UpdateChecker.ParseRelease(Metadata("https://example.com/"), new Version(1, 0, 0))?.DownloadUrl is null, "Foreign download accepted.");
Reject(() => UpdatePackage.ParseChecksum("bad " + UpdatePackage.PackageName), "Invalid checksum accepted.");
Reject(() => UpdatePackage.ParseChecksum(new string('a', 64) + " other.zip"), "Wrong asset checksum accepted.");

var scratch = Path.Combine(Path.GetTempPath(), "AdbBridge-updater-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    foreach (var entryName in new[] { "AdbBridge-win-x64/AdbBridge.exe", "AdbBridge-win-x64\\AdbBridge.exe" })
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryName).Open());
            writer.Write("MZtest-executable");
        }
        var bytes = memory.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        using var client = new HttpClient(new FakeRelease(bytes, hash));
        var staging = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        var exe = await UpdatePackage.DownloadAsync(new("v9.0.0", "", prefix + UpdatePackage.PackageName, prefix + "SHA256SUMS.txt"), staging, client: client);
        Check(await File.ReadAllTextAsync(exe) == "MZtest-executable", "Verified executable not extracted.");
        using var corrupt = new HttpClient(new FakeRelease(bytes, new string('0', 64)));
        var corruptStage = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        try
        {
            await UpdatePackage.DownloadAsync(new("v9.0.0", "", prefix + UpdatePackage.PackageName, prefix + "SHA256SUMS.txt"), corruptStage, client: corrupt);
            throw new Exception("Corrupt download accepted.");
        }
        catch (InvalidDataException) { Check(!File.Exists(Path.Combine(corruptStage, "AdbBridge.new.exe")), "Corrupt binary extracted."); }
        using var oversizedMetadata = new HttpClient(new FakeRelease(bytes, new string('a', 16385)));
        try
        {
            await UpdatePackage.DownloadAsync(new("v9.0.0", "", prefix + UpdatePackage.PackageName, prefix + "SHA256SUMS.txt"), Path.Combine(scratch, Guid.NewGuid().ToString("N")), client: oversizedMetadata);
            throw new Exception("Oversized checksum metadata accepted.");
        }
        catch (InvalidDataException) { }
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try
        {
            await UpdatePackage.DownloadAsync(new("v9.0.0", "", prefix + UpdatePackage.PackageName, prefix + "SHA256SUMS.txt"), staging, ct: cancel.Token, client: client);
            throw new Exception("Cancellation ignored.");
        }
        catch (OperationCanceledException) { }
    }
    var badZip = Path.Combine(scratch, "bad.zip");
    using (var archive = ZipFile.Open(badZip, ZipArchiveMode.Create))
    {
        using var writer = new StreamWriter(archive.CreateEntry("../AdbBridge.exe").Open());
        writer.Write("MZinvalid-path");
    }
    Reject(() => UpdatePackage.ExtractExecutable(badZip, scratch), "Unexpected archive path accepted.");
}
finally { Directory.Delete(scratch, recursive: true); }

// Publish this test project as a single file for real process-exit, replacement,
// restart and rollback checks. All targets are disposable copies of this harness.
if (args.Contains("--integration"))
{
    var actualHelperIndex = Array.IndexOf(args, "--actual-helper");
    var actualHelper = actualHelperIndex >= 0 ? Path.GetFullPath(args[actualHelperIndex + 1]) : null;
    foreach (var scenario in actualHelper is null ? new[] { "success", "rollback", "tamper" } : new[] { "success" })
    {
        var failRestart = scenario == "rollback";
        var tamper = scenario == "tamper";
        var staging = Path.Combine(SelfUpdate.UpdateRoot, "test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(staging, "install"));
        var target = Path.Combine(staging, "install", "AdbBridge.exe");
        var helper = Path.Combine(staging, "AdbBridge.Updater.exe");
        var package = Path.Combine(staging, "AdbBridge.new.exe");
        File.Copy(Environment.ProcessPath!, target);
        File.Copy(actualHelper ?? Environment.ProcessPath!, helper);
        if (failRestart) await File.WriteAllTextAsync(package, "MZnot-a-real-executable");
        else File.Copy(Environment.ProcessPath!, package);
        var originalHash = await UpdatePackage.HashAsync(target);
        if (tamper) await File.WriteAllTextAsync(Path.Combine(staging, "hold-parent"), "hold");
        using var parent = Start(target, "--test-parent", staging);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            while (!File.Exists(Path.Combine(staging, "parent-started"))) await Task.Delay(100, timeout.Token);
            var manifest = Path.Combine(staging, "handoff.json");
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new UpdateHandoff(parent.Id, target, package, await UpdatePackage.HashAsync(package), null)));
            using var installer = Start(helper, actualHelper is null ? "--test-helper" : "--apply-update", manifest);
            if (tamper)
            {
                while (!File.Exists(Path.Combine(staging, "ready"))) await Task.Delay(100, timeout.Token);
                await File.WriteAllTextAsync(package, "MZmodified-after-verification");
                File.Delete(Path.Combine(staging, "hold-parent"));
            }
            await installer.WaitForExitAsync(timeout.Token);
            if (installer.ExitCode != (failRestart || tamper ? 1 : 0)) Console.WriteLine(await installer.StandardError.ReadToEndAsync());
            Check(installer.ExitCode == (failRestart || tamper ? 1 : 0), "Unexpected installer result.");
            while (!File.Exists(Path.Combine(staging, "install", "restarted"))) await Task.Delay(100, timeout.Token);
            Check(await UpdatePackage.HashAsync(target) == originalHash, "Installed or restored executable differs.");
            Check(File.Exists(target + ".previous") == !tamper, "Unexpected backup state.");
            Console.WriteLine(tamper ? "Installer rejected staged-package tampering and restarted the original." : failRestart ? "Installer rollback and restart passed." : "Installer replacement and restart passed.");
        }
        finally
        {
            if (!parent.HasExited) { parent.Kill(); await parent.WaitForExitAsync(); }
            // The restarted fixture exits immediately after writing its marker.
            await Task.Delay(500);
            Directory.Delete(staging, recursive: true);
        }
    }
}
Console.WriteLine("All updater checks passed.");

sealed class FakeRelease(byte[] package, string hash) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt")
                ? new StringContent(hash + "  " + UpdatePackage.PackageName, Encoding.ASCII)
                : new ByteArrayContent(package)
        });
    }
}
