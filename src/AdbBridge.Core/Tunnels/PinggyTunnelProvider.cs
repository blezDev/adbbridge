using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AdbBridge.Core.Tunnels;

/// <summary>
/// Exposes the local ADB server through a Pinggy TCP tunnel. Pinggy has no bundled
/// client binary to install — it works over a plain SSH reverse tunnel, and Windows
/// 10/11 ships an OpenSSH client already, so this only needs ssh.exe plus a Pinggy
/// token. With a Pinggy Pro token the tunnel host:port is persistent across restarts,
/// which free-tier ngrok can't offer.
///
/// Command shape: `ssh -p 443 -R0:localhost:&lt;port&gt; &lt;token&gt;+tcp@a.pinggy.io`. Pinggy's
/// server requires a real password-auth round trip (any password works — it's not
/// actually checked) that ssh cannot complete non-interactively over a redirected pipe
/// on its own, so this sets SSH_ASKPASS/SSH_ASKPASS_REQUIRE=force with a trivial helper
/// script that supplies a blank password. Once authenticated, Pinggy renders a full
/// ANSI terminal UI (box-drawing, cursor positioning) rather than plain banner lines —
/// there's no local status API like ngrok's — so the address is scraped by scanning the
/// raw accumulated output for a `tcp://host:port` substring rather than parsing it line
/// by line, which a full-screen redraw with no newlines would defeat.
///
/// Host key checking uses `accept-new` against a real, persisted known_hosts file
/// (not "no" / NUL) — trusts a.pinggy.io's key the first time without an interactive
/// prompt, but still rejects a *different* key later, so a MITM after the first
/// connection is still caught. Arguments are built via ArgumentList rather than a raw
/// interpolated string so the token can't be misparsed into extra flags.
/// </summary>
public sealed class PinggyTunnelProvider : ITunnelProvider
{
    private static readonly Regex AddressPattern = new(@"tcp://[a-zA-Z0-9.-]+:\d+", RegexOptions.Compiled);
    private static readonly Regex AnsiPattern =
        new(@"\x1b(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1b]*(?:\x07|\x1b\\)|[@-Z\\-_])", RegexOptions.Compiled);
    private static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(45);
    private const int MaxBufferChars = 8000;

    private readonly string _sshExePath;
    private readonly string _token;
    private readonly int _localPort;
    private readonly string _pinggyHost;

    private Process? _process;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private TaskCompletionSource<TunnelInfo>? _addressTcs;
    private readonly StringBuilder _outputBuffer = new();

    public TunnelInfo? Current { get; private set; }
    public bool IsRunning => _process is { HasExited: false };

    public event EventHandler<TunnelInfo>? AddressChanged;
    public event EventHandler<string>? LogMessage;

    public PinggyTunnelProvider(string sshExePath, string token, int localPort = 5037, string pinggyHost = "a.pinggy.io")
    {
        _sshExePath = sshExePath;
        _token = token;
        _localPort = localPort;
        _pinggyHost = pinggyHost;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_sshExePath))
            throw new FileNotFoundException($"ssh.exe not found at '{_sshExePath}'.", _sshExePath);

        _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _monitorTask = MonitorLoopAsync(_monitorCts.Token);
        return Task.CompletedTask;
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Log("Starting Pinggy tunnel over SSH...");
                _addressTcs = new TaskCompletionSource<TunnelInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
                LaunchProcess();

                await WaitForAddressOrExitAsync(ct);

                if (_process is not null && !_process.HasExited)
                {
                    // Address arrived and the process is still alive — sit here until
                    // it actually dies (crash, network drop, kill) before restarting.
                    await _process.WaitForExitAsync(ct);
                }

                Log("Pinggy SSH process exited, restarting...");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Pinggy monitor error: {ex.Message}");
            }

            try
            {
                await Task.Delay(backoff, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }
    }

    /// <summary>
    /// Waits for either the address to be parsed out of ssh's output or the process to
    /// exit — whichever comes first. Pinggy's banner over port 443 can take well over
    /// the old fixed 20s window depending on network conditions, so this doesn't give up
    /// on a short clock the way an earlier version did (which silently and permanently
    /// dropped a late-arriving address even though the tunnel was working fine).
    ///
    /// It does still give up after <see cref="StuckAfter"/> with no progress at all —
    /// covering the case where the underlying TCP connection dies without ssh noticing
    /// or exiting (observed: a CLOSE_WAIT socket sitting there indefinitely, e.g. from a
    /// flaky IPv6 path) — by killing the process outright so the outer loop retries.
    /// </summary>
    private async Task WaitForAddressOrExitAsync(CancellationToken ct)
    {
        if (_addressTcs is null || _process is null) return;

        var addressTask = _addressTcs.Task;
        var exitTask = _process.WaitForExitAsync(ct);
        var waited = TimeSpan.Zero;

        while (waited < StuckAfter)
        {
            var heartbeat = Task.Delay(TimeSpan.FromSeconds(10), ct);
            var completed = await Task.WhenAny(addressTask, exitTask, heartbeat);

            if (completed == addressTask)
            {
                var info = await addressTask;
                Current = info;
                AddressChanged?.Invoke(this, info);
                Log($"Pinggy tunnel live: {info}");
                return;
            }

            if (completed == exitTask)
            {
                Log("Pinggy SSH process exited before reporting an address.");
                return;
            }

            waited += TimeSpan.FromSeconds(10);
            Log($"Still waiting for Pinggy to report a public address ({waited.TotalSeconds:0}s so far)...");
        }

        Log($"No response after {StuckAfter.TotalSeconds:0}s — the connection may have died without ssh " +
            "noticing. Restarting the tunnel.");
        if (_process is { HasExited: false })
        {
            try { _process.Kill(true); } catch { /* already gone */ }
        }
    }

    private void LaunchProcess()
    {
        _outputBuffer.Clear();

        var psi = new ProcessStartInfo
        {
            FileName = _sshExePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        // ArgumentList (rather than a single interpolated Arguments string) makes .NET
        // quote each entry correctly, so a token containing a space or quote can't be
        // reinterpreted as extra ssh flags.
        psi.ArgumentList.Add("-4"); // forces IPv4: on networks with partial/broken IPv6
                                     // routing, ssh can complete a TCP handshake over
                                     // IPv6 and then have the connection silently die
                                     // (observed as CLOSE_WAIT with the process never
                                     // noticing or exiting), stalling forever with no
                                     // address and no error. IPv4 avoids that entirely.
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("443");
        // accept-new (not "no") trusts a host key the first time it's seen and pins it
        // to a real known_hosts file — so a later MITM presenting a *different* key for
        // a.pinggy.io still gets rejected. Skips the interactive first-connect prompt
        // (which would hang forever with piped stdin) without disabling verification.
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("StrictHostKeyChecking=accept-new");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add($"UserKnownHostsFile={EnsureKnownHostsFile()}");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ServerAliveInterval=15");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ServerAliveCountMax=3");
        psi.ArgumentList.Add($"-R0:localhost:{_localPort}");
        psi.ArgumentList.Add($"{_token}+tcp@{_pinggyHost}");

        // Pinggy's server requires completing a password prompt (any password, even
        // blank, is accepted — it's not actually checked) before it does anything.
        // OpenSSH's password prompt reads from a real controlling terminal, not from
        // redirected stdin, so a piped process hangs here forever with no error and no
        // way to feed it a password by writing to the pipe. SSH_ASKPASS_REQUIRE=force
        // (OpenSSH 8.4+) makes ssh invoke a helper program for the password instead,
        // which works fine non-interactively.
        psi.EnvironmentVariables["SSH_ASKPASS"] = EnsureAskPassHelper();
        psi.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
        psi.EnvironmentVariables["DISPLAY"] = "dummy:0"; // some OpenSSH builds also gate askpass on DISPLAY being set

        _process = new Process { StartInfo = psi };
        _process.Start();

        _ = PumpStreamAsync(_process.StandardOutput);
        _ = PumpStreamAsync(_process.StandardError);
    }

    private static string AppDataDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbBridge");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string EnsureAskPassHelper()
    {
        var path = Path.Combine(AppDataDir(), "askpass_blank.cmd");
        if (!File.Exists(path))
            File.WriteAllText(path, "@echo.\r\n");
        return path;
    }

    private static string EnsureKnownHostsFile()
    {
        var path = Path.Combine(AppDataDir(), "pinggy_known_hosts");
        if (!File.Exists(path))
            File.WriteAllText(path, string.Empty);
        return path;
    }

    private async Task PumpStreamAsync(StreamReader reader)
    {
        var buf = new char[1024];
        try
        {
            while (true)
            {
                var n = await reader.ReadAsync(buf.AsMemory());
                if (n <= 0) break;
                HandleChunk(new string(buf, 0, n));
            }
        }
        catch
        {
            // Stream closed because the process exited — the monitor loop notices that
            // separately via WaitForExitAsync.
        }
    }

    private void HandleChunk(string chunk)
    {
        if (_addressTcs is { Task.IsCompleted: false })
        {
            _outputBuffer.Append(chunk);
            if (_outputBuffer.Length > MaxBufferChars)
                _outputBuffer.Remove(0, _outputBuffer.Length - MaxBufferChars);

            var match = AddressPattern.Match(_outputBuffer.ToString());
            if (match.Success && TunnelInfo.TryParse(match.Value, out var info) && info is not null)
                _addressTcs.TrySetResult(info);
        }

        var clean = AnsiPattern.Replace(chunk, "").Trim();
        if (clean.Length > 0)
            Log($"[pinggy] {clean}");
    }

    public Task StopAsync()
    {
        _monitorCts?.Cancel();

        if (_process is { HasExited: false })
        {
            try { _process.Kill(true); } catch { /* already gone */ }
        }
        _process = null;
        Current = null;

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        if (_monitorTask is not null)
        {
            try { await _monitorTask; } catch { /* swallow cancellation */ }
        }
        _monitorCts?.Dispose();
    }

    private void Log(string message) => LogMessage?.Invoke(this, message);
}
