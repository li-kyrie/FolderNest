namespace FolderNest.Core;

public static class MonitorValidation
{
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path))
            throw new ArgumentException("请输入完整的文件夹路径。");
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path.Trim()));
        if (!Directory.Exists(full)) throw new ArgumentException("路径无效：文件夹不存在或当前不可访问。");
        if (string.Equals(full, System.IO.Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择具体文件夹，不能监控整个磁盘。");
        RejectLinkedAncestors(full);
        return full;
    }

    public static void Validate(MonitorDefinition monitor, IReadOnlyList<MonitorDefinition> others)
    {
        var path = NormalizePath(monitor.Path);
        if (monitor.RetentionDays is < 1 or > 36500) throw new ArgumentException("天数必须是 1–36500 的整数。");
        if (!Enum.IsDefined(monitor.DeletionMode)) throw new ArgumentException("请选择有效的删除模式。");
        foreach (var other in others.Where(m => m.Id != monitor.Id))
            if (IsWithin(path, other.Path) || IsWithin(other.Path, path))
                throw new ArgumentException("该目录与已有监控目录相同或相互包含，请避免重复监控。");
    }

    public static bool IsWithin(string path, string parent) =>
        string.Equals(path, parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(parent) + System.IO.Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static void RejectLinkedAncestors(string path)
    {
        for(var directory=new DirectoryInfo(path);directory is not null;directory=directory.Parent)
            if((directory.Attributes & FileAttributes.ReparsePoint)!=0)
                throw new IOException("路径包含符号链接或目录联接，请直接选择真实目录。");
    }
}
