using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FolderNest.Desktop;

internal static class ExplorerReveal
{
    // The optional shell action keeps UI regression tests from opening Explorer windows.
    public static void Show(string path, Action<string>? showItem = null)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("项目路径无效，请重新扫描到期清单。");
        var fullPath = Path.GetFullPath(path);
        try { _ = File.GetAttributes(fullPath); }
        catch (FileNotFoundException) { throw new IOException("项目已不存在或已移动，请重新扫描到期清单。"); }
        catch (DirectoryNotFoundException) { throw new IOException("项目或所在目录已不存在，请重新扫描到期清单。"); }
        (showItem ?? ShowNative)(fullPath);
    }

    private static void ShowNative(string path)
    {
        // Called on a worker thread: parsing an offline/network path can take time.
        var initialized = CoInitializeEx(IntPtr.Zero, 0);
        Marshal.ThrowExceptionForHR(initialized);
        IntPtr pidl = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _));
            // cidl=0 selects this exact item in its parent, including folders and .lnk files.
            // No command-line parsing or execution of the target is involved.
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0));
        }
        finally
        {
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            CoUninitialize();
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributes, out uint actualAttributes);
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidl, uint count, IntPtr children, uint flags);
}
