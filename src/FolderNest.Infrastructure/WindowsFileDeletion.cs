using FolderNest.Core;
using Microsoft.VisualBasic.FileIO;

namespace FolderNest.Infrastructure;

public sealed class WindowsFileDeletion : IFileDeletion
{
    public void Delete(string path, bool isDirectory, DeletionMode mode)
    {
        if(!Enum.IsDefined(mode))throw new IOException("删除模式无效，已跳过。");
        // Refuse reparse ancestors AND descendants, so recursive cleanup cannot cross link targets.
        for(var parent = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!); parent is not null; parent=parent.Parent)
            if((parent.Attributes & FileAttributes.ReparsePoint)!=0) throw new IOException("路径上级包含链接，已跳过。");
        Check(new FileInfo(path), isDirectory);
        var recycle = mode == DeletionMode.RecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
        try
        {
            if (isDirectory) FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, recycle, UICancelOption.ThrowException);
            else FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, recycle, UICancelOption.ThrowException);
        }
        catch(OperationCanceledException ex) { throw new IOException("系统取消了清理操作。", ex); }
        if(File.Exists(path) || Directory.Exists(path)) throw new IOException("系统未移除目标，请检查权限或占用。");
    }
    private static void Check(FileSystemInfo entry, bool directory)
    {
        if((entry.Attributes & FileAttributes.ReparsePoint)!=0) throw new IOException("清理目标包含符号链接或目录联接，已跳过。");
        if(directory)
            foreach(var child in new DirectoryInfo(entry.FullName).EnumerateFileSystemInfos())
                Check(child, child is DirectoryInfo);
    }
}
