using System.Runtime.InteropServices;
using System.Windows;
using FolderNest.Core;
using FolderNest.Infrastructure;

namespace FolderNest.Desktop;

public partial class App : Application
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SetCurrentProcessExplicitAppUserModelID("FolderNest.Desktop.Preview");
        try
        {
            var paths = PortablePaths.Resolve(AppContext.BaseDirectory);
            var log = new RollingAuditLog(paths.LogPath);
            var repository = new SqliteRepository(paths.DatabasePath);
            var scan = new ScanService(repository, new LocalFileInventory(), new SystemClock(), log);
            log.Write("startup", $"框架预览启动；数据位置：{paths.DataDirectory}");
            var viewModel = new MainViewModel(repository, scan, log, paths, new WindowsTaskStatusReader());
            var window = new MainWindow(viewModel);
            MainWindow = window;
            if(e.Args.Contains("--smoke-test"))
            {
                window.Show();
                window.Dispatcher.InvokeAsync(() => { window.Close(); Shutdown(0); },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                return;
            }
            window.Show();
        }
        catch(Exception ex)
        {
            if (!e.Args.Contains("--smoke-test")) MessageBox.Show(ex.Message, "FolderNest 启动失败");
            Shutdown(1);
        }
    }
}
