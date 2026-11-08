using System.IO.Compression;

namespace AdbBridge.Core.Security;

public static class SafeArchive
{
    public static void Extract(ZipArchiveEntry entry, string destination, long maximumBytes, CancellationToken ct = default)
    {
        if (entry.Length < 0 || entry.Length > maximumBytes) throw new InvalidDataException("Archive entry exceeds the extraction limit.");
        using var input = entry.Open();
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            total += count;
            if (total > maximumBytes || total > entry.Length) throw new InvalidDataException("Archive entry exceeds its declared size.");
            output.Write(buffer, 0, count);
        }
        if (total != entry.Length) throw new InvalidDataException("Archive entry is truncated.");
    }

    public static void RejectLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update and installation paths cannot contain symbolic links or junctions.");
            current = Path.GetDirectoryName(current);
        }
    }
}
