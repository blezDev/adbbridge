using System.Text.Json;
using System.Text.Json.Serialization;
using AdbBridge.Core.Tunnels;

namespace AdbBridge.Core.Config;

public sealed class HostSettings
{
    public string PlatformToolsPath { get; set; } = string.Empty;
    public TunnelProviderKind Provider { get; set; } = TunnelProviderKind.Ngrok;

    public string NgrokExePath { get; set; } = string.Empty;

    /// <summary>Plaintext, in memory only — never serialized. Populated from
    /// <see cref="NgrokAuthTokenProtected"/> on Load, written back to it on Save.</summary>
    [JsonIgnore]
    public string NgrokAuthToken { get; set; } = string.Empty;
    public string NgrokAuthTokenProtected { get; set; } = string.Empty;

    // The Windows OpenSSH client ships at this path on every Windows 10/11 install —
    // unlike the adb/ngrok paths, this one isn't machine- or user-specific.
    public string SshExePath { get; set; } = @"C:\Windows\System32\OpenSSH\ssh.exe";

    /// <summary>Plaintext, in memory only — never serialized. Populated from
    /// <see cref="PinggyTokenProtected"/> on Load, written back to it on Save.</summary>
    [JsonIgnore]
    public string PinggyToken { get; set; } = string.Empty;
    public string PinggyTokenProtected { get; set; } = string.Empty;

    public bool RunAtStartup { get; set; }

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdbBridge", "host-settings.json");

    public static HostSettings Load()
    {
        var isFirstRun = !File.Exists(FilePath);
        var settings = SettingsFileExtensions.Load<HostSettings>(FilePath);
        settings.NgrokAuthToken = SecretProtector.Unprotect(settings.NgrokAuthTokenProtected);
        settings.PinggyToken = SecretProtector.Unprotect(settings.PinggyTokenProtected);

        if (isFirstRun)
        {
            if (settings.PlatformToolsPath.Length == 0) settings.PlatformToolsPath = PathDetector.FindAdb();
            if (settings.NgrokExePath.Length == 0) settings.NgrokExePath = PathDetector.FindNgrok();
        }

        return settings;
    }

    public void Save()
    {
        NgrokAuthTokenProtected = SecretProtector.Protect(NgrokAuthToken);
        PinggyTokenProtected = SecretProtector.Protect(PinggyToken);
        SettingsFileExtensions.Save(this, FilePath);
    }
}

public sealed class CompanionSettings
{
    public string PlatformToolsPath { get; set; } = string.Empty;
    public string LastTunnelAddress { get; set; } = string.Empty;
    public bool RunAtStartup { get; set; }
    public bool AutoConnectOnStartup { get; set; }

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdbBridge", "companion-settings.json");

    public static CompanionSettings Load()
    {
        var isFirstRun = !File.Exists(FilePath);
        var settings = SettingsFileExtensions.Load<CompanionSettings>(FilePath);

        if (isFirstRun && settings.PlatformToolsPath.Length == 0)
            settings.PlatformToolsPath = PathDetector.FindAdb();

        return settings;
    }

    public void Save() => SettingsFileExtensions.Save(this, FilePath);
}

internal static class SettingsFileExtensions
{
    internal static T Load<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<T>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file — fall back to defaults.
        }
        return new T();
    }

    internal static void Save<T>(T settings, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
