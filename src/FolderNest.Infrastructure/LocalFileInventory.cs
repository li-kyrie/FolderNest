using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FolderNest.Core;

namespace FolderNest.Infrastructure;

public sealed class LocalFileInventory : IFileInventory
{
    public IReadOnlyList<FileUnit> Enumerate(string root)
    {
        var directory = new DirectoryInfo(root);
        if (!directory.Exists) throw new DirectoryNotFoundException("监控目录暂时不可访问。");
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("监控根目录变成了链接，请重新选择真实目录。");
        var units = new List<FileUnit>();
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (IsLink(entry)) continue;
            if (entry is FileInfo file)
                units.Add(new(file.FullName, false, file.Length, Hash([FileToken(file, file.Name)])));
            else if (entry is DirectoryInfo folder)
            {
                var tokens = new List<string>(); long size = 0;
                Collect(folder, folder.FullName, tokens, ref size);
                units.Add(new(folder.FullName, true, size, Hash(tokens)));
            }
        }
        return units;
    }

    private static bool IsLink(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;
    private static string FileToken(FileInfo file, string relative) =>
        $"F\0{relative}\0{file.Length.ToString(CultureInfo.InvariantCulture)}\0{file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}";
    private static void Collect(DirectoryInfo folder, string root, List<string> tokens, ref long size)
    {
        tokens.Add("D\0" + Path.GetRelativePath(root, folder.FullName));
        foreach (var entry in folder.EnumerateFileSystemInfos())
        {
            if (IsLink(entry)) continue;
            if (entry is FileInfo file) { tokens.Add(FileToken(file, Path.GetRelativePath(root, file.FullName))); size += file.Length; }
            else if (entry is DirectoryInfo child) Collect(child, root, tokens, ref size);
        }
    }
    private static string Hash(IEnumerable<string> tokens) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('\n', tokens.Order(StringComparer.Ordinal)))));
}
