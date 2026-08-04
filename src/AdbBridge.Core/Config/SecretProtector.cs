using System.Security.Cryptography;
using System.Text;

namespace AdbBridge.Core.Config;

/// <summary>
/// Encrypts secrets (tunnel provider tokens) at rest using Windows DPAPI, scoped to the
/// current Windows user account. Only that same Windows account on this same machine can
/// decrypt the result — a stolen settings file is useless anywhere else.
/// </summary>
internal static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AdbBridge.SettingsSecret.v1");

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return string.Empty;
        try
        {
            var protectedBytes = Convert.FromBase64String(ciphertext);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Not valid ciphertext for this user/machine (e.g. a pre-encryption plaintext
            // settings file, or the file was copied from another machine) — fail closed
            // rather than throwing, so a corrupt/foreign value just means re-entering it.
            return string.Empty;
        }
    }
}
