using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AdbBridge.Core.Config;
using AdbBridge.Core.Relay;
using AdbBridge.Core.Security;
using AdbBridge.Core.Tunnels;
using AdbBridge.Core.Update;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static byte[] Frame(string service) => Encoding.ASCII.GetBytes($"{service.Length:x4}{service}");
static async Task Reject(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception ex) when (ex is AuthenticationException or IOException or SocketException or InvalidDataException) { return; }
    throw new Exception(message);
}
static async Task Exchange(Stream stream, string payload, CancellationToken ct)
{
    var bytes = Encoding.ASCII.GetBytes(payload);
    await stream.WriteAsync(bytes, ct);
    var response = new byte[bytes.Length];
    await stream.ReadExactlyAsync(response, ct);
    Check(response.SequenceEqual(bytes), "TLS forwarding changed the payload.");
}

using var identity = BridgeIdentity.Create();
using var stranger = BridgeIdentity.Create();
await using var adb = new TestAdb();
await using var broker = new ForwardBroker(0, adb.Port, identity);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
var ct = deadline.Token;
await broker.StartAsync(ct);
var target = broker.SecureAddress(new TunnelInfo("127.0.0.1", broker.LocalPort));
Check(TunnelInfo.TryParse(target.ConnectionCode, out var parsed) && parsed?.Credentials is not null, "Secure connection code did not round-trip.");
Check(!target.ToString().Contains(target.Credentials!.ClientPfx), "Diagnostic endpoint exposed credentials.");
Check(!target.Credentials.ToString().Contains(target.Credentials.ClientPfx), "Credential diagnostics exposed a private key.");
Check(!JsonSerializer.Serialize(new CompanionSettings { LastTunnelAddress = target.ConnectionCode }).Contains(target.ConnectionCode), "Connection code serialized as plaintext.");

var tokenFixture = "SecurityTestToken_123";
await using (var ngrok = new NgrokTunnelProvider("ngrok.exe", tokenFixture))
{
    var start = (ProcessStartInfo)typeof(NgrokTunnelProvider).GetMethod("BuildStartInfo", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ngrok, null)!;
    Check(!start.ArgumentList.Any(argument => argument.Contains(tokenFixture)), "ngrok token exposed in arguments.");
    Check(start.Environment["NGROK_AUTHTOKEN"] == tokenFixture, "ngrok token not supplied through child environment.");
}
await using (var pinggy = new PinggyTunnelProvider("ssh.exe", tokenFixture))
{
    var start = (ProcessStartInfo)typeof(PinggyTunnelProvider).GetMethod("BuildStartInfo", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pinggy, null)!;
    Check(!start.ArgumentList.Any(argument => argument.Contains(tokenFixture)), "Pinggy token exposed in arguments.");
    var file = start.ArgumentList[start.ArgumentList.IndexOf("-F") + 1];
    Check(File.ReadAllText(file).Contains("User " + tokenFixture + "+tcp"), "Private SSH configuration not supplied.");
}
try { _ = new PinggyTunnelProvider("ssh.exe", "token\nProxyCommand bad"); throw new Exception("SSH configuration injection accepted."); }
catch (ArgumentException) { }

using (var exported = BridgeIdentity.FromExport(identity.Export()))
    Check(BridgeIdentity.Pin(exported.ServerCertificate) == BridgeIdentity.Pin(identity.ServerCertificate), "Stored Host identity changed.");

using (var raw = new TcpClient())
{
    await raw.ConnectAsync(IPAddress.Loopback, broker.LocalPort, ct);
    await raw.GetStream().WriteAsync(Frame("host:version"), ct);
    try { Check(await raw.GetStream().ReadAsync(new byte[1], ct) == 0, "Raw ADB request accepted."); }
    catch (IOException) { }
}
Check(adb.Requests == 0, "Unauthenticated traffic reached ADB.");

async Task Request(TunnelInfo peer)
{
    using var client = new TcpClient();
    await using var secure = await SecureTunnel.ConnectAsync(client, peer, ct);
    await secure.WriteAsync(Frame("host:version"), ct);
    var response = new byte[8];
    await secure.ReadExactlyAsync(response, ct);
}
await Reject(() => Request(target with { Credentials = new(target.Credentials.ServerPin, stranger.Credentials.ClientPfx) }), "Unpaired client was accepted.");
Check(adb.Requests == 0, "Unpaired client reached ADB.");
await Reject(() => Request(target with { Credentials = new(stranger.Credentials.ServerPin, target.Credentials.ClientPfx) }), "Incorrect Host certificate was accepted.");
Check(adb.Requests == 0, "Incorrectly pinned Host reached ADB.");
await Reject(() => Request(new TunnelInfo("127.0.0.1", broker.LocalPort)), "Plaintext downgrade accepted.");

async Task AdbCommand(string command)
{
    using var client = new TcpClient();
    await using var secure = await SecureTunnel.ConnectAsync(client, parsed!, ct);
    await secure.WriteAsync(Frame(command), ct);
    var response = new byte[8];
    await secure.ReadExactlyAsync(response, ct);
    Check(Encoding.ASCII.GetString(response) == "OKAYOKAY", "Paired ADB request failed.");
}

var localService = new TcpListener(IPAddress.Loopback, 0);
localService.Start();
var localPort = ((IPEndPoint)localService.LocalEndpoint).Port;
async Task RejectMarker(string marker)
{
    using var client = new TcpClient();
    await using var secure = await SecureTunnel.ConnectAsync(client, target, ct);
    await secure.WriteAsync(Encoding.ASCII.GetBytes(marker), ct);
    try { Check(await secure.ReadAsync(new byte[1], ct) == 0, "Unregistered marker was accepted."); }
    catch (IOException) { }
}
try
{
    await RejectMarker($"FWD:{localPort}\n");
    Check(!localService.Pending(), "Broker exposed an arbitrary localhost service.");
    var reverseProbe = new TcpListener(IPAddress.Loopback, 0);
    reverseProbe.Start();
    var reversePort = ((IPEndPoint)reverseProbe.LocalEndpoint).Port;
    reverseProbe.Stop();
    await RejectMarker($"REV:{reversePort}\n");
    reverseProbe.Start(); // an unregistered reverse request did not reserve this port
    reverseProbe.Stop();
    await AdbCommand($"host:forward:tcp:{localPort};localabstract:video");
    using var forward = new TcpClient();
    await using var stream = await SecureTunnel.ConnectAsync(forward, target, ct);
    await stream.WriteAsync(Encoding.ASCII.GetBytes($"FWD:{localPort}\n"), ct);
    using var app = await localService.AcceptTcpClientAsync(ct);
    var echo = Task.Run(async () =>
    {
        var buffer = new byte[128];
        int count;
        while ((count = await app.GetStream().ReadAsync(buffer, ct)) > 0)
            await app.GetStream().WriteAsync(buffer.AsMemory(0, count), ct);
    });
    await Exchange(stream, "paired-forward", ct);
    await AdbCommand($"host:killforward:tcp:{localPort}");
    await Exchange(stream, "survives-removal", ct);
    await RejectMarker($"FWD:{localPort}\n");
    forward.Dispose();
    await echo;
}
finally { localService.Stop(); }

// Exercise the actual Host reverse broker, including pairing and mapping removal.
var reverseListener = new TcpListener(IPAddress.Loopback, 0);
reverseListener.Start();
var registeredReversePort = ((IPEndPoint)reverseListener.LocalEndpoint).Port;
reverseListener.Stop();
await AdbCommand($"reverse:forward:localabstract:security-test;tcp:{registeredReversePort}");
using (var reverse = new TcpClient())
await using (var reverseStream = await SecureTunnel.ConnectAsync(reverse, target, ct))
{
    await reverseStream.WriteAsync(Encoding.ASCII.GetBytes($"REV:{registeredReversePort}\n"), ct);
    using var device = new TcpClient();
    // Registration is asynchronous relative to the client marker write.
    while (!device.Connected)
    {
        try { await device.ConnectAsync(IPAddress.Loopback, registeredReversePort, ct); }
        catch (SocketException) { await Task.Delay(25, ct); }
    }
    var go = new byte[3];
    await reverseStream.ReadExactlyAsync(go, ct);
    Check(Encoding.ASCII.GetString(go) == "GO\n", "Registered reverse tunnel was not paired.");
    var echo = Task.Run(async () =>
    {
        var buffer = new byte[128];
        int count;
        while ((count = await device.GetStream().ReadAsync(buffer, ct)) > 0)
            await device.GetStream().WriteAsync(buffer.AsMemory(0, count), ct);
    });
    await Exchange(reverseStream, "paired-reverse", ct);
    await AdbCommand("reverse:killforward:localabstract:security-test");
    await Exchange(reverseStream, "reverse-survives-removal", ct);
    await RejectMarker($"REV:{registeredReversePort}\n");
    reverseStream.Dispose();
    await echo;
}

foreach (var command in new[] { "shell:curl -H 'Authorization: secret-value'", "shell,v2:password=secret-value", "exec:token=secret-value", "abb_exec:secret-value" })
    Check(!SafeDiagnostics.ServiceName(command).Contains("secret-value"), "Command payload leaked to logs.");

await Reject(async () =>
    await UpdatePackage.DownloadAsync(new("v1.0.5", "", "http://example.com/update.zip", "https://example.com/checksum"), Path.GetTempPath()),
    "Updater accepted an untrusted download URL.");

var scratch = Path.Combine(Path.GetTempPath(), "adbbridge-security-" + Guid.NewGuid().ToString("N"));
try
{
    PrivateFiles.EnsureDirectory(scratch);
    var permissions = System.IO.FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(scratch));
    Check(permissions.AreAccessRulesProtected, "Private files inherit broad access rules.");
    PrivateFiles.WriteText(Path.Combine(scratch, "fixture.conf"), "test credential");
    Check(File.ReadAllText(Path.Combine(scratch, "fixture.conf")) == "test credential", "Private configuration not written.");
}
finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }

Console.WriteLine("Security checks passed: mutual TLS, paired access, Host pinning, no plaintext downgrade, broker port restrictions, stream survival, credential serialization, log redaction, trusted update URLs, and private ACLs.");

sealed class TestAdb : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;
    private readonly List<Task> _tasks = new();
    public int Requests;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public TestAdb() { _listener.Start(); _accept = Accept(); }
    private async Task Accept()
    {
        try
        {
            while (!_cts.IsCancellationRequested) _tasks.Add(Serve(await _listener.AcceptTcpClientAsync(_cts.Token)));
        }
        catch (OperationCanceledException) { }
    }
    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var header = new byte[4];
                await client.GetStream().ReadExactlyAsync(header, _cts.Token);
                var body = new byte[Convert.ToInt32(Encoding.ASCII.GetString(header), 16)];
                await client.GetStream().ReadExactlyAsync(body, _cts.Token);
                Interlocked.Increment(ref Requests);
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("OKAYOKAY"), _cts.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
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
