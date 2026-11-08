using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using AdbBridge.Core.Mirroring;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void CreateInstallation(string folder)
{
    Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "scrcpy.exe"), "fixture");
    File.WriteAllText(Path.Combine(folder, "scrcpy-server"), "fixture");
}

var scratch = Path.Combine(Path.GetTempPath(), "adbbridge-scrcpy-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    var configured = Path.Combine(scratch, "configured");
    var onPath = Path.Combine(scratch, "on-path");
    CreateInstallation(configured);
    CreateInstallation(onPath);
    var installer = new ScrcpyInstaller(Path.Combine(scratch, "managed"), scratch, onPath);
    Check(installer.FindExisting(Path.Combine(configured, "scrcpy.exe")) == Path.Combine(configured, "scrcpy.exe"), "Configured path ignored.");
    File.Delete(Path.Combine(configured, "scrcpy-server"));
    Check(installer.FindExisting(Path.Combine(configured, "scrcpy.exe")) == Path.Combine(onPath, "scrcpy.exe"), "Incomplete installation not skipped.");
    using var rejectedDownload = new HttpClient(new FakeDownload());
    Check(await installer.EnsureInstalledAsync(null, client: rejectedDownload) == Path.Combine(onPath, "scrcpy.exe"), "Existing installation was not reused.");

    var emptyRoot = Path.Combine(scratch, "empty");
    installer = new ScrcpyInstaller(emptyRoot, scratch, "");
    try
    {
        await installer.EnsureInstalledAsync(null, client: rejectedDownload);
        throw new Exception("Invalid download checksum was accepted.");
    }
    catch (InvalidDataException) { }
    Check(!Directory.EnumerateDirectories(emptyRoot).Any(), "Failed install left a partial installation.");
    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    try
    {
        await installer.EnsureInstalledAsync(null, ct: cancel.Token, client: rejectedDownload);
        throw new Exception("Cancellation was ignored.");
    }
    catch (OperationCanceledException) { }

    foreach (var windowsPaths in new[] { false, true })
    {
        var zip = Path.Combine(scratch, Guid.NewGuid() + ".zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "scrcpy.exe", "scrcpy-server", "SDL3.dll", "LICENSE" })
            {
                var path = "scrcpy-win64-v5.0.1/" + name;
                using var writer = new StreamWriter(archive.CreateEntry(windowsPaths ? path.Replace('/', '\\') : path).Open());
                writer.Write("fixture");
            }
        }
        var extracted = Path.Combine(scratch, Guid.NewGuid().ToString());
        ScrcpyInstaller.ExtractVerifiedArchive(zip, extracted);
        Check(ScrcpyInstaller.IsInstalled(Path.Combine(extracted, "scrcpy-win64-v5.0.1", "scrcpy.exe")), "Package not installed.");
        Check(File.Exists(Path.Combine(extracted, "scrcpy-win64-v5.0.1", "SDL3.dll")), "Runtime dependency omitted.");
    }
    var unsafeZip = Path.Combine(scratch, "unsafe.zip");
    using (var archive = ZipFile.Open(unsafeZip, ZipArchiveMode.Create))
    {
        using var writer = new StreamWriter(archive.CreateEntry("scrcpy-win64-v5.0.1/../../escape.exe").Open());
        writer.Write("unsafe");
    }
    try
    {
        ScrcpyInstaller.ExtractVerifiedArchive(unsafeZip, Path.Combine(scratch, "unsafe"));
        throw new Exception("Archive traversal accepted.");
    }
    catch (InvalidDataException) { }
    Check(!File.Exists(Path.Combine(scratch, "escape.exe")), "Archive wrote outside installation.");
}
finally { Directory.Delete(scratch, recursive: true); }
Console.WriteLine("All scrcpy installer checks passed.");

if (args.FirstOrDefault() == "--install")
{
    var installer = new ScrcpyInstaller(args.Length > 1 ? Path.GetFullPath(args[1]) : null, searchPath: "");
    var executable = await installer.EnsureInstalledAsync(null, new Progress<string>(Console.WriteLine));
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add("--version");
    using var process = Process.Start(start) ?? throw new Exception("Could not start installed scrcpy.");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    Console.WriteLine(await output);
    Console.WriteLine(await error);
    Check(process.ExitCode == 0, "Installed scrcpy could not start.");
    Console.WriteLine("Official scrcpy download, verification, installation and launch passed: " + executable);
}

sealed class FakeDownload : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
    }
}
