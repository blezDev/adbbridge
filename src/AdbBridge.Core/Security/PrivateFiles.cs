using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace AdbBridge.Core.Security;

public static class PrivateFiles
{
    public static string DirectoryPath
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbBridge", "private");
            EnsureDirectory(path);
            return path;
        }
    }

    public static void EnsureDirectory(string path)
    {
        SafeArchive.RejectLinks(path);
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("A private directory cannot be a link.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("Cannot identify the current Windows user.");
        security.SetOwner(user);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public static void WriteText(string path, string text)
    {
        EnsureDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("A private file cannot be a link.");
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }
}
