namespace AdbBridge.Core.Security;

public static class SafeDiagnostics
{
    public static string ServiceName(string service)
    {
        // Shell/exec/ABB payloads can contain passwords, auth headers, or app data.
        // Never write command bodies, device serials, or arbitrary service payloads.
        foreach (var family in new[] { "shell", "exec", "abb", "sync", "jdwp", "track-jdwp", "localabstract" })
            if (service.StartsWith(family + ":", StringComparison.Ordinal) || service.StartsWith(family + ",", StringComparison.Ordinal))
                return family + " (payload omitted)";
        foreach (var name in new[] { "killforward-all", "killforward", "forward", "transport-id", "transport", "tport", "version", "devices-l", "devices", "features", "track-devices-proto-binary", "track-devices-l", "track-devices" })
            if (service.StartsWith("reverse:" + name, StringComparison.Ordinal)) return "reverse:" + name;
            else if (service.StartsWith("host:" + name, StringComparison.Ordinal) || service.Contains(":" + name + ":", StringComparison.Ordinal)) return "host:" + name;
        return "ADB service (payload omitted)";
    }
}
