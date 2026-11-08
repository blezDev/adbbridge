using System.Text;
using AdbBridge.Core.Relay;

static byte[] Frame(string service) => Encoding.ASCII.GetBytes($"{service.Length:x4}{service}");
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

// TCP may split either status, the length, or the port at any byte boundary.
foreach (var fragmented in new[] { false, true })
{
    foreach (var service in new[] { "host:forward:tcp:0;localabstract:inspection",
                 "host-serial:phone:forward:norebind:tcp:0;jdwp:123" })
    {
        var watcher = new AdbForwardWatcher();
        var ports = new List<int>();
        watcher.ForwardEstablished += (_, port) => ports.Add(port);
        watcher.OnClientToServer(Frame(service));
        var response = Encoding.ASCII.GetBytes("OKAYOKAY000543210");
        if (fragmented)
            foreach (var value in response) watcher.OnServerToClient(new[] { value });
        else watcher.OnServerToClient(response);
        Check(ports.SequenceEqual(new[] { 43210 }), "Allocated inspection port was lost.");
    }
}

foreach (var reverse in new[] { false, true })
{
    var watcher = new AdbForwardWatcher();
    var established = 0;
    watcher.ForwardEstablished += (_, _) => established++;
    watcher.ReverseForwardEstablished += (_, _) => established++;
    if (reverse)
    {
        watcher.OnClientToServer(Frame("host:tport:serial:phone"));
        watcher.OnServerToClient(Encoding.ASCII.GetBytes("OKAY12345678"));
    }
    watcher.OnClientToServer(Frame(reverse
        ? "reverse:forward:localabstract:scrcpy;tcp:27183"
        : "host:forward:tcp:27183;localabstract:scrcpy"));
    watcher.OnServerToClient(Encoding.ASCII.GetBytes("OKAY"));
    Check(established == 0, "Mapping announced before operation status.");
    watcher.OnServerToClient(Encoding.ASCII.GetBytes("FAIL0006denied"));
    Check(established == 0, "Failed mapping announced as successful.");
}

var combined = new AdbForwardWatcher();
var reversePort = 0;
combined.ReverseForwardEstablished += (_, mapping) => reversePort = mapping.LocalPort;
combined.OnClientToServer(Frame("host:transport:phone")
    .Concat(Frame("reverse:forward:localabstract:scrcpy;tcp:27183")).ToArray());
combined.OnServerToClient(Encoding.ASCII.GetBytes("OKAYOKAY"));
Check(reversePort == 27183, "Coalesced transport and reverse request lost.");
Console.WriteLine("All ADB forwarding protocol checks passed.");
