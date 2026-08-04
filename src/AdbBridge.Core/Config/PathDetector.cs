namespace AdbBridge.Core.Config;

/// <summary>
/// Best-effort first-run auto-detection for adb.exe/ngrok.exe, so the app doesn't need
/// any machine-specific path baked in as a default. Everything here is derived from the
/// current user's environment at runtime (PATH, %LocalAppData%) rather than hardcoded,
/// so it works for whoever runs the app without disclosing anything about the machine
/// that built it.
/// </summary>
internal static class PathDetector
{
    public static string FindAdb() => FindOnPathOrWellKnown("adb.exe", WellKnownAdbCandidates());

    public static string FindNgrok() => FindOnPath("ngrok.exe");

    private static IEnumerable<string> WellKnownAdbCandidates()
    {
        // The standard Android Studio default SDK location — always under the current
        // user's own LocalAppData, so this is the same for any Windows account.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe");
    }

    private static string FindOnPathOrWellKnown(string exeName, IEnumerable<string> wellKnownCandidates)
    {
        var onPath = FindOnPath(exeName);
        if (onPath.Length > 0) return onPath;

        foreach (var candidate in wellKnownCandidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return string.Empty;
    }

    private static string FindOnPath(string exeName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(dir.Trim('"'), exeName);
            }
            catch (ArgumentException)
            {
                continue; // malformed PATH entry — skip it
            }

            if (File.Exists(candidate)) return candidate;
        }

        return string.Empty;
    }
}
