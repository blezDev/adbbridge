using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using AdbBridge.Core.Adb;
using AdbBridge.Core.Config;
using AdbBridge.Core.Mirroring;
using AdbBridge.Core.Relay;
using AdbBridge.Core.Tunnels;

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}
static async Task Exchange(TcpClient from, TcpClient to, string message, CancellationToken ct)
{
    var bytes = Encoding.ASCII.GetBytes(message);
    await from.GetStream().WriteAsync(bytes, ct);
    var actual = new byte[bytes.Length];
    await to.GetStream().ReadExactlyAsync(actual, ct);
    if (!actual.SequenceEqual(bytes)) throw new Exception("Stream bytes changed.");
}
static async Task Closed(TcpClient client, CancellationToken ct)
{
    try
    {
        if (await client.GetStream().ReadAsync(new byte[1], ct) != 0) throw new Exception("Expected closed stream.");
    }
    catch (IOException) { }
}
static async Task Command(int relayPort, string service, bool transport, CancellationToken ct)
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, relayPort, ct);
    async Task Send(string request)
    {
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"{request.Length:x4}{request}"), ct);
    }
    if (transport)
    {
        await Send("host:transport:phone");
        await client.GetStream().ReadExactlyAsync(new byte[4], ct);
    }
    await Send(service);
    var response = new byte[8];
    await client.GetStream().ReadExactlyAsync(response, ct);
    if (Encoding.ASCII.GetString(response) != "OKAYOKAY") throw new Exception("Unexpected smart-socket response.");
}

foreach (var clearAll in new[] { false, true })
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var ct = timeout.Token;
    await using var gateway = new FakeGateway();
    await using var relay = new TcpRelay(new AdbProcessManager(), 0);
    relay.UpdateTarget(new TunnelInfo("127.0.0.1", gateway.Port));
    await relay.StartAsync(ct);
    var forward = new ForwardPortRelay(relay);
    try
    {
        var port = FreePort();
        await Command(relay.LocalPort, $"host:forward:tcp:{port};localabstract:video", false, ct);
        using var app = new TcpClient();
        await app.ConnectAsync(IPAddress.Loopback, port, ct);
        using var device = await gateway.Forwards.Reader.ReadAsync(ct);
        await Exchange(app, device, "before", ct);
        await Command(relay.LocalPort, clearAll ? "host:killforward-all" : $"host:killforward:tcp:{port}", false, ct);
        await Exchange(device, app, "video-after-remove", ct);
        await Exchange(app, device, "control-after-remove", ct);
        var probe = new TcpListener(IPAddress.Loopback, port);
        probe.Start(); // the mapping listener has gone, even though its streams survived
        probe.Stop();
        await Command(relay.LocalPort, $"host:forward:tcp:{port};localabstract:next", false, ct);
        using var secondApp = new TcpClient();
        await secondApp.ConnectAsync(IPAddress.Loopback, port, ct);
        using var secondDevice = await gateway.Forwards.Reader.ReadAsync(ct);
        await Exchange(secondApp, secondDevice, "new-session", ct);
        await Exchange(device, app, "old-session-still-alive", ct);
        await forward.DisposeAsync();
        await Closed(app, ct);
        await Closed(secondApp, ct);
        Console.WriteLine($"Forward {(clearAll ? "remove-all" : "remove")}: active streams survived, port reused, disconnect closed all sessions.");
    }
    finally { await forward.DisposeAsync(); }
}

foreach (var clearAll in new[] { false, true })
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var ct = timeout.Token;
    await using var gateway = new FakeGateway();
    await using var relay = new TcpRelay(new AdbProcessManager(), 0);
    relay.UpdateTarget(new TunnelInfo("127.0.0.1", gateway.Port));
    await relay.StartAsync(ct);
    var reverse = new ReversePoolRelay(relay);
    var appListener = new TcpListener(IPAddress.Loopback, 0);
    appListener.Start();
    try
    {
        var port = ((IPEndPoint)appListener.LocalEndpoint).Port;
        await Command(relay.LocalPort, $"reverse:forward:localabstract:video;tcp:{port}", true, ct);
        using var device = await gateway.Reverses.Reader.ReadAsync(ct);
        await device.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GO\n"), ct);
        using var app = await appListener.AcceptTcpClientAsync(ct);
        using var idle = await gateway.Reverses.Reader.ReadAsync(ct);
        await Exchange(device, app, "before", ct);
        await Command(relay.LocalPort, clearAll ? "reverse:killforward-all" : "reverse:killforward:localabstract:video", true, ct);
        await Exchange(device, app, "video-after-remove", ct);
        await Exchange(app, device, "control-after-remove", ct);
        await Closed(idle, ct);
        await reverse.DisposeAsync();
        await Closed(app, ct);
        Console.WriteLine($"Reverse {(clearAll ? "remove-all" : "remove")}: active streams survived, idle slots closed, disconnect closed the session.");
    }
    finally { appListener.Stop(); await reverse.DisposeAsync(); }
}
// Emulate WAN reordering: if two idle slots are available, deliver the second
// device connection's GO first. The application must still receive video before control.
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var ct = timeout.Token;
    await using var gateway = new FakeGateway();
    await using var relay = new TcpRelay(new AdbProcessManager(), 0);
    relay.UpdateTarget(new TunnelInfo("127.0.0.1", gateway.Port));
    await relay.StartAsync(ct);
    await using var reverse = new ReversePoolRelay(relay);
    var appListener = new TcpListener(IPAddress.Loopback, 0);
    appListener.Start();
    try
    {
        var port = ((IPEndPoint)appListener.LocalEndpoint).Port;
        await Command(relay.LocalPort, $"reverse:forward:localabstract:ordered;tcp:{port}", true, ct);
        using var video = await gateway.Reverses.Reader.ReadAsync(ct);
        TcpClient? control = null;
        using (var reorder = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            reorder.CancelAfter(100);
            try { control = await gateway.Reverses.Reader.ReadAsync(reorder.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        if (control is not null)
            await control.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GO\n"), ct);
        await video.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GO\n"), ct);
        using var firstApp = await appListener.AcceptTcpClientAsync(ct);
        if (control is null)
        {
            control = await gateway.Reverses.Reader.ReadAsync(ct);
            await control.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GO\n"), ct);
        }
        using (control)
        using (var secondApp = await appListener.AcceptTcpClientAsync(ct))
        {
            await video.GetStream().WriteAsync(new byte[] { (byte)'V' }, ct);
            await control.GetStream().WriteAsync(new byte[] { (byte)'C' }, ct);
            var first = new byte[1];
            var second = new byte[1];
            await firstApp.GetStream().ReadExactlyAsync(first, ct);
            await secondApp.GetStream().ReadExactlyAsync(second, ct);
            if (first[0] != 'V' || second[0] != 'C') throw new Exception("Video and control reverse connections arrived out of order.");
        }
        Console.WriteLine("Reverse video/control order survived simulated WAN reordering.");
    }
    finally { appListener.Stop(); }
}
Console.WriteLine("All relay lifecycle regression checks passed.");

// Real-device validation uses an isolated local ADB port and the existing Host
// tunnel. It does not stop, replace, or change the running Companion installation.
if (args.Contains("--device"))
{
    var settings = CompanionSettings.Load();
    if (!TunnelInfo.TryParse(settings.LastTunnelAddress, out var target) || target is null) throw new Exception("No Host tunnel configured.");
    var executable = new ScrcpyInstaller().FindExisting(settings.ScrcpyExePath) ?? throw new Exception("scrcpy is not installed.");
    await using var relay = new TcpRelay(new AdbProcessManager(), 0);
    relay.UpdateTarget(target);
    await relay.StartAsync();
    await using var forward = new ForwardPortRelay(relay);
    await using var reverse = new ReversePoolRelay(relay);
    forward.LogMessage += (_, message) => Console.WriteLine(message);
    reverse.LogMessage += (_, message) => Console.WriteLine(message);
    foreach (var forceForward in new[] { true, false })
    {
        if (forceForward && args.Contains("--reverse-only")) continue;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["ADB"] = settings.PlatformToolsPath;
        start.Environment["ADB_SERVER_SOCKET"] = $"tcp:127.0.0.1:{relay.LocalPort}";
        foreach (var argument in new[] { "--video-codec=h264", "--max-size=1280", "--max-fps=30", "--no-audio", "--time-limit=20", "--print-fps", "--port=" + FreePort() }) start.ArgumentList.Add(argument);
        if (forceForward) start.ArgumentList.Add("--force-adb-forward");
        Console.WriteLine("Starting live " + (forceForward ? "forward" : "reverse") + " mirroring test...");
        using var process = Process.Start(start) ?? throw new Exception("Could not launch scrcpy.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Console.WriteLine(await stdout + await stderr);
            throw;
        }
        var output = await stdout + await stderr;
        Console.WriteLine(output);
        if (process.ExitCode != 0 || !output.Contains("Texture") || !output.Contains("Time limit reached"))
            throw new Exception($"Mirroring did not complete successfully (exit {process.ExitCode}).");
        Console.WriteLine("LIVE MIRRORING PASSED: " + (forceForward ? "forward" : "reverse"));
    }
}

sealed class FakeGateway : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<Task> _tasks = new();
    private readonly Task _accept;
    public readonly Channel<TcpClient> Forwards = Channel.CreateUnbounded<TcpClient>();
    public readonly Channel<TcpClient> Reverses = Channel.CreateUnbounded<TcpClient>();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public FakeGateway() { _listener.Start(); _accept = AcceptAsync(); }
    private async Task AcceptAsync()
    {
        try { while (!_cts.IsCancellationRequested) _tasks.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_cts.Token))); }
        catch (OperationCanceledException) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var first = new byte[1];
                await stream.ReadExactlyAsync(first, _cts.Token);
                if (first[0] is (byte)'F' or (byte)'R')
                {
                    var kind = first[0];
                    while (first[0] != '\n') await stream.ReadExactlyAsync(first, _cts.Token);
                    await (kind == 'F' ? Forwards : Reverses).Writer.WriteAsync(client, _cts.Token);
                    await Task.Delay(Timeout.Infinite, _cts.Token);
                    return;
                }
                while (true)
                {
                    var length = new byte[4];
                    length[0] = first[0];
                    await stream.ReadExactlyAsync(length.AsMemory(1), _cts.Token);
                    var body = new byte[Convert.ToInt32(Encoding.ASCII.GetString(length), 16)];
                    await stream.ReadExactlyAsync(body, _cts.Token);
                    var service = Encoding.ASCII.GetString(body);
                    if (service.StartsWith("host:transport:"))
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("OKAY"), _cts.Token);
                        await stream.ReadExactlyAsync(first, _cts.Token);
                    }
                    else { await stream.WriteAsync(Encoding.ASCII.GetBytes("OKAYOKAY"), _cts.Token); return; }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_tasks);
        _cts.Dispose();
    }
}
