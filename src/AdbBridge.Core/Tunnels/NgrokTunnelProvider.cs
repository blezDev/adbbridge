using System.Diagnostics;
using System.Text.Json;

namespace AdbBridge.Core.Tunnels;

/// <summary>
/// Runs `ngrok.exe tcp &lt;port&gt;` as a child process and reports the public tcp://host:port
/// it gets assigned. Free-plan addresses change on every restart, so this watches the
/// process and re-publishes a new address (via AddressChanged) whenever it comes back up.
/// </summary>
public sealed class NgrokTunnelProvider : ITunnelProvider
{
    private readonly string _ngrokExePath;
    private readonly string _authToken;
    private readonly int _localPort;
    private readonly int _apiPort;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private Process? _process;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;

    public TunnelInfo? Current { get; private set; }
    public bool IsRunning => _process is { HasExited: false };

    public event EventHandler<TunnelInfo>? AddressChanged;
    public event EventHandler<string>? LogMessage;

    public NgrokTunnelProvider(string ngrokExePath, string authToken, int localPort = 5037, int apiPort = 4040)
    {
        _ngrokExePath = ngrokExePath;
        _authToken = authToken;
        _localPort = localPort;
        _apiPort = apiPort;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_ngrokExePath))
            throw new FileNotFoundException($"ngrok.exe not found at '{_ngrokExePath}'.", _ngrokExePath);

        // Writes the token to ngrok's own config file once (~/.ngrok2/ngrok.yml or the
        // v3 equivalent) instead of passing --authtoken on every launch. A command-line
        // argument is visible to any other local process/account via tools as basic as
        // `tasklist -v` or `wmic process get CommandLine` for as long as ngrok is
        // running; the config file is only readable by this Windows account.
        await RunAddAuthTokenAsync(ct);

        _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _monitorTask = MonitorLoopAsync(_monitorCts.Token);
    }

    private async Task RunAddAuthTokenAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ngrokExePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("config");
        psi.ArgumentList.Add("add-authtoken");
        psi.ArgumentList.Add(_authToken);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ngrok.exe to save the authtoken.");
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"ngrok rejected the authtoken: {stderr.Trim()}");
        }
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Log("Starting ngrok...");
                LaunchProcess();

                var info = await WaitForAddressAsync(ct);
                if (info is not null)
                {
                    Current = info;
                    AddressChanged?.Invoke(this, info);
                    Log($"ngrok tunnel live: {info}");
                    backoff = TimeSpan.FromSeconds(2);
                }
                else
                {
                    Log("Timed out waiting for ngrok to report a public address.");
                }

                if (_process is not null)
                    await _process.WaitForExitAsync(ct);

                Log("ngrok process exited, restarting...");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"ngrok monitor error: {ex.Message}");
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

    private void LaunchProcess()
    {
        KillStrayNgrok();

        var psi = new ProcessStartInfo
        {
            FileName = _ngrokExePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // The authtoken was already saved to ngrok's own config via `config
        // add-authtoken` in StartAsync, so it doesn't need to appear here.
        psi.ArgumentList.Add("tcp");
        psi.ArgumentList.Add(_localPort.ToString());
        psi.ArgumentList.Add("--log=stdout");

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log($"[ngrok] {e.Data}"); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log($"[ngrok] {e.Data}"); };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private static void KillStrayNgrok()
    {
        foreach (var p in Process.GetProcessesByName("ngrok"))
        {
            try { p.Kill(true); } catch { /* already gone */ }
        }
    }

    private async Task<TunnelInfo?> WaitForAddressAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var json = await _http.GetStringAsync($"http://127.0.0.1:{_apiPort}/api/tunnels", ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("tunnels", out var tunnels))
                {
                    foreach (var t in tunnels.EnumerateArray())
                    {
                        var proto = t.TryGetProperty("proto", out var p) ? p.GetString() : null;
                        if (proto == "tcp" && t.TryGetProperty("public_url", out var url) &&
                            TunnelInfo.TryParse(url.GetString(), out var info))
                        {
                            return info;
                        }
                    }
                }
            }
            catch
            {
                // ngrok's local API isn't up yet — keep polling until the deadline.
            }

            await Task.Delay(500, ct);
        }

        return null;
    }

    public async Task StopAsync()
    {
        _monitorCts?.Cancel();

        if (_process is { HasExited: false })
        {
            try { _process.Kill(true); } catch { /* already gone */ }
        }

        KillStrayNgrok();
        Current = null;

        if (_monitorTask is not null)
        {
            try { await _monitorTask; } catch { /* swallow cancellation */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
        _monitorCts?.Dispose();
    }

    private void Log(string message) => LogMessage?.Invoke(this, message);
}
