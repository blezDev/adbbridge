using System.Text.Json;
using System.Text.Json.Serialization;
using AdbBridge.Core.Tunnels;

namespace AdbBridge.Core.Config;

public sealed class HostSettings
{
    public string BridgeIdentityProtected { get; set; } = string.Empty;

    public AdbBridge.Core.Security.BridgeIdentity OpenIdentity()
    {
        if (!string.IsNullOrEmpty(BridgeIdentityProtected))
        {
            var data = SecretProtector.Unprotect(BridgeIdentityProtected);
            if (string.IsNullOrEmpty(data)) throw new InvalidDataException("Cannot unlock this Host's pairing identity. Restore its settings or clear its pairing identity to pair again.");
            return AdbBridge.Core.Security.BridgeIdentity.FromExport(data);
        }
        var identity = AdbBridge.Core.Security.BridgeIdentity.Create();
        BridgeIdentityProtected = SecretProtector.Protect(identity.Export());
        Save();
        return identity;
    }
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
    public string ScrcpyExePath { get; set; } = string.Empty;
    public string PlatformToolsPath { get; set; } = string.Empty;
    [JsonIgnore]
    public string LastTunnelAddress { get; set; } = string.Empty;
    public string LastTunnelAddressProtected { get; set; } = string.Empty;
    public bool RunAtStartup { get; set; }
    public bool AutoConnectOnStartup { get; set; }

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdbBridge", "companion-settings.json");

    public static CompanionSettings Load()
    {
        var isFirstRun = !File.Exists(FilePath);
        var settings = SettingsFileExtensions.Load<CompanionSettings>(FilePath);
        settings.LastTunnelAddress = SecretProtector.Unprotect(settings.LastTunnelAddressProtected);

        if (isFirstRun && settings.PlatformToolsPath.Length == 0)
            settings.PlatformToolsPath = PathDetector.FindAdb();

        return settings;
    }

    public void Save()
    {
        LastTunnelAddressProtected = SecretProtector.Protect(LastTunnelAddress);
        SettingsFileExtensions.Save(this, FilePath);
    }
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
            AdbBridge.Core.Security.PrivateFiles.EnsureDirectory(dir);

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        AdbBridge.Core.Security.SafeArchive.RejectLinks(path);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
