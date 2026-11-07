using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using FolderNest.Infrastructure;
using Forms = System.Windows.Forms;

namespace FolderNest.Desktop;

public partial class App : Application
{
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    private Mutex? instance;
    private EventWaitHandle? showEvent;
    private EventWaitHandle? tickEvent;
    private DispatcherTimer? timer;
    private Forms.NotifyIcon? tray;
    private MainViewModel? vm;
    private MainWindow? window;
    private bool ownsMutex;
    private FileStream? dataLock;
    private bool exiting;
    public bool Exiting=>exiting;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode=ShutdownMode.OnExplicitShutdown;
        SetCurrentProcessExplicitAppUserModelID("FolderNest.Desktop.2");
        try
        {
            var index=Array.IndexOf(e.Args,"--data");
            if(index>=0 && index+1>=e.Args.Length)throw new ArgumentException("--data 缺少路径。");
            var paths=PortablePaths.Resolve(AppContext.BaseDirectory,index>=0 ? e.Args[index+1] : null);
            var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.DataDirectory.ToUpperInvariant())));
            instance=new Mutex(false,@"Local\FolderNest2-"+key);
            try {ownsMutex=instance.WaitOne(0);}catch(AbandonedMutexException){ownsMutex=true;}
            showEvent=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\FolderNest2-show-"+key);
            tickEvent=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\FolderNest2-tick-"+key);
            if(!ownsMutex)
            {
                if(!e.Args.Contains("--smoke-test"))(e.Args.Contains("--background")?tickEvent:showEvent).Set();
                Shutdown(0);return;
            }
            // Physical file locking also prevents alias paths from opening the same data store twice.
            dataLock=new FileStream(Path.Combine(paths.DataDirectory,"foldernest.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            var repository=new SqliteRepository(paths.DatabasePath);
            var log=new RollingAuditLog(paths.LogPath,()=>repository.ReadSettings().LogLimitMiB);
            vm=new(repository,log,paths,manageSchedule:!e.Args.Contains("--smoke-test")&&!e.Args.Contains("--no-schedule"));
            window=new(vm); MainWindow=window;
            log.Write("startup",$"FolderNest 2.2.0；数据：{paths.DataDirectory}");
            if(e.Args.Contains("--smoke-test"))
            {
                window.Show();
                window.Dispatcher.InvokeAsync(()=>{exiting=true;window.Close();Shutdown(0);},DispatcherPriority.ApplicationIdle);
                return;
            }
            var stream=GetResourceStream(new Uri("pack://application:,,,/Assets/FolderNest.ico")).Stream;
            using(stream)using(var icon=new System.Drawing.Icon(stream))
                tray=new Forms.NotifyIcon {Icon=(System.Drawing.Icon)icon.Clone(),Text="FolderNest",Visible=true};
            var menu=new Forms.ContextMenuStrip();
            menu.Items.Add("打开 FolderNest",null,(_,_)=>ShowMain());
            menu.Items.Add("查看到期清单",null,async (_,_)=>{ShowMain();await window.OpenReviewAsync();});
            menu.Items.Add("退出程序",null,(_,_)=>ExitApplication());
            tray.ContextMenuStrip=menu;
            tray.DoubleClick+=(_,_)=>ShowMain();
            tray.BalloonTipClicked+=async (_,_)=>{ShowMain();await window.OpenReviewAsync();};
            vm.Reminder+=(_,count)=>tray.ShowBalloonTip(10000,"FolderNest · 到期提醒",$"{count} 项已到期。点击查看、取消勾选并确认清理。",Forms.ToolTipIcon.Info);
            if(!e.Args.Contains("--background"))window.Show();
            timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(3)};
            timer.Tick+=async (_,_)=>{
                if(showEvent.WaitOne(0))ShowMain();
                tickEvent.WaitOne(0); // The resident tick always rechecks missed daily work.
                await vm.TickAsync();
            };
            timer.Start();
            _=vm.TickAsync();
        }
        catch(Exception ex)
        {
            if(!e.Args.Contains("--smoke-test"))MessageBox.Show(ex.ToString(),"FolderNest 启动失败");
            Shutdown(1);
        }
    }
    public void ShowMain()
    {
        if(window is null)return;
        window.Show();if(window.WindowState==WindowState.Minimized)window.WindowState=WindowState.Normal;
        window.Activate();
    }
    public void ExitApplication()
    {
        if(vm?.IsBusy==true){MessageBox.Show("正在处理文件，请完成后再退出。","FolderNest");return;}
        if(vm?.HasPendingEdits==true && MessageBox.Show(window!,"列表中有未确认的配置修改。退出将丢弃这些修改，继续使用原配置。是否退出？","未保存修改",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        exiting=true;window?.Close();Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        timer?.Stop();tray?.Dispose();vm?.Dispose();
        dataLock?.Dispose();
        showEvent?.Dispose();tickEvent?.Dispose();
        if(ownsMutex)instance?.ReleaseMutex();instance?.Dispose();
        base.OnExit(e);
    }
}
