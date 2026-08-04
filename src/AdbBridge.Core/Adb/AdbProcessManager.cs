using System.Diagnostics;

namespace AdbBridge.Core.Adb;

/// <summary>
/// Wraps the handful of adb.exe operations both apps need: killing every running
/// instance (so nothing else can hold port 5037 out from under the relay/nodaemon
/// server), starting a network-visible server, and listing devices.
/// </summary>
public sealed class AdbProcessManager
{
    private Process? _serverProcess;

    public event EventHandler<string>? LogMessage;

    /// <summary>
    /// Kills every running adb.exe / adb process on this machine, regardless of which
    /// platform-tools install it came from. Safe to call liberally — adb.exe restarts
    /// itself on demand.
    /// </summary>
    public void KillAllAdb()
    {
        foreach (var name in new[] { "adb", "adb.exe" })
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    Log($"Killed adb process (pid {p.Id}).");
                }
                catch (Exception ex)
                {
                    Log($"Could not kill adb process (pid {p.Id}): {ex.Message}");
                }
            }
        }
        _serverProcess = null;
    }

    /// <summary>
    /// Host-side only: starts an adb server bound to 0.0.0.0:5037 (rather than
    /// loopback-only) so it's reachable through the tunnel. Runs in the foreground
    /// (nodaemon) so we hold a process handle and can detect if it dies.
    /// </summary>
    public void StartNodaemonServer(string adbExePath)
    {
        KillAllAdb();

        var psi = new ProcessStartInfo
        {
            FileName = adbExePath,
            Arguments = "-a nodaemon server start",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        _serverProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _serverProcess.OutputDataReceived += (_, e) => { if (e.Data is not null) Log($"[adb] {e.Data}"); };
        _serverProcess.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log($"[adb] {e.Data}"); };
        _serverProcess.Start();
        _serverProcess.BeginOutputReadLine();
        _serverProcess.BeginErrorReadLine();

        Log($"Started nodaemon adb server (pid {_serverProcess.Id}), bound to 0.0.0.0:5037.");
    }

    public bool IsServerRunning => _serverProcess is { HasExited: false };

    public async Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(string adbExePath, CancellationToken ct = default)
    {
        var output = await RunAdbCommandAsync(adbExePath, "devices -l", ct);
        var devices = new List<AdbDevice>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var serial = parts[0];
            var state = parts[1];
            string? model = null;
            foreach (var field in parts.Skip(2))
            {
                if (field.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
                    model = field["model:".Length..];
            }

            devices.Add(new AdbDevice(serial, state, model));
        }

        return devices;
    }

    public async Task<string> RunAdbCommandAsync(string adbExePath, string arguments, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = adbExePath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{adbExePath}'.");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return stdout;
    }

    public void StopServer()
    {
        if (_serverProcess is { HasExited: false })
        {
            try { _serverProcess.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
        _serverProcess = null;
    }

    private void Log(string message) => LogMessage?.Invoke(this, message);
}
