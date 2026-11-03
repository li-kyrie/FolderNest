using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using FolderNest.Core;
using FolderNest.Infrastructure;

namespace FolderNest.Desktop;

// Dispatcher-owned configuration; scans, watcher reconciliation and cleanup share one gate.
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SqliteRepository repository;
    private readonly ScanService scanner;
    private readonly CleanupService cleanup;
    private readonly IAuditLog log;
    private readonly WindowsTaskScheduler tasks;
    private readonly ArrivalTracker tracker = new();
    private readonly ConcurrentQueue<ActivityEvent> activity = new();
    private readonly SemaphoreSlim gate = new(1);
    private string status = "就绪";
    private bool busy;
    private DateTimeOffset lastScan = DateTimeOffset.MinValue;
    private DateOnly? sweepDay;
    private DateTimeOffset nextTaskCheck;
    private TaskDeployment deployment;
    private int lostEvents;
    public MainViewModel(SqliteRepository repository, IAuditLog log, PortablePaths paths)
    {
        this.repository=repository; this.log=log; Paths=paths;
        Settings=repository.ReadSettings(); Settings.Validate();
        scanner=new(repository,new LocalFileInventory(),new SystemClock(),log);
        cleanup=new(repository,new LocalFileInventory(),new WindowsFileDeletion(),new SystemClock(),log);
        tasks=new(paths.DataDirectory,log,expectedDailyTime:()=>Settings.Schedule.DailyTime);
        foreach(var monitor in repository.GetMonitors()) Monitors.Add(CreateRow(monitor));
        tracker.Changed+=(_,e)=>
        {
            if(activity.Count>=10000)Interlocked.Exchange(ref lostEvents,1);
            else activity.Enqueue(e);
        };
        RefreshTaskStatus(); Rewatch();
    }
    public PortablePaths Paths { get; }
    public AppSettings Settings { get; private set; }
    public ObservableCollection<MonitorRow> Monitors { get; } = [];
    public string Status { get=>status; set { status=value; Notify(nameof(Status)); } }
    public bool IsBusy => busy;
    public bool IsIdle=>!busy;
    public bool HasTasks => deployment is TaskDeployment.Installed or TaskDeployment.Partial;
    public string TaskText { get; private set; } = "";
    public string DataText => $"数据：{Paths.DataDirectory}"+(Paths.IsFallback ? "（备用位置）" : "");
    public event EventHandler<int>? Reminder;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name)=>PropertyChanged?.Invoke(this,new(name));
    private void SetBusy(bool value){busy=value;Notify(nameof(IsIdle));}
    public void RefreshTaskStatus()
    {
        var result=tasks.GetStatus(); var previous=deployment;deployment=result.Deployment;
        TaskText=$"自动任务：{result.Deployment switch { TaskDeployment.Installed=>"已部署",TaskDeployment.NotInstalled=>"未部署",TaskDeployment.Partial=>"部分部署",_=>"无法查询" }} · 每天 {Settings.DailyTime}（本地时间） · {result.Details}";
        Notify(nameof(TaskText)); foreach(var row in Monitors) row.SetDeployment(deployment);
        nextTaskCheck=DateTimeOffset.UtcNow.AddMinutes(5);
        if(previous!=deployment){Rewatch();lastScan=DateTimeOffset.MinValue;}
    }
    private MonitorRow CreateRow(MonitorDefinition m) { var row=new MonitorRow(m,Save);row.SetDeployment(deployment);return row; }
    private void EnsureIdle() { if(busy)throw new InvalidOperationException("正在处理文件，请稍后再修改配置。"); }
    public void Validate(MonitorDefinition m)
    {
        var all=repository.GetMonitors();
        var previous=all.SingleOrDefault(item=>item.Id==m.Id);
        // Suspension, schedule selection and safe removal must still work for offline drives.
        if(previous is null || !string.Equals(previous.Path,m.Path,StringComparison.OrdinalIgnoreCase))
            MonitorValidation.Validate(m,all);
        else if(m.RetentionDays is <1 or >36500 || !Enum.IsDefined(m.DeletionMode))
            throw new ArgumentException("天数或删除模式无效。");
        var app=System.IO.Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if(MonitorValidation.IsWithin(m.Path,Paths.DataDirectory)||MonitorValidation.IsWithin(Paths.DataDirectory,m.Path)||
           MonitorValidation.IsWithin(m.Path,app)||MonitorValidation.IsWithin(app,m.Path))
            throw new ArgumentException("不能监控程序或数据文件夹、其子目录或上级目录。");
    }
    private void Save(MonitorDefinition m)
    {
        EnsureIdle(); Validate(m); repository.SaveMonitor(m);
        log.Write("monitor-save",$"{m.Path}；{m.RetentionDays}天；{m.DeletionMode}；启用={m.Enabled}；自动扫描={m.Scheduled}");
        Rewatch(); lastScan=DateTimeOffset.MinValue; Status="配置已保存";
    }
    private void Rewatch()=>tracker.Watch(repository.GetMonitors().Where(m=>m.Scheduled && deployment==TaskDeployment.Installed).ToArray(),log);
    public MonitorRow Add(MonitorDefinition monitor) { Save(monitor);var row=CreateRow(monitor);Monitors.Add(row);return row; }
    public void Toggle(IReadOnlyList<MonitorRow> rows)
    {
        EnsureIdle(); if(rows.Count==0)throw new ArgumentException("请先选择监控目录。");
        var enabled=rows.Any(r=>!r.Enabled);
        foreach(var row in rows) row.Enabled=enabled;
        Status=$"已{(enabled ? "启用" : "停用")} {rows.Count} 个目录";
    }
    public void Remove(IReadOnlyList<MonitorRow> rows)
    {
        EnsureIdle();foreach(var row in rows){repository.RemoveMonitor(row.Id);Monitors.Remove(row);log.Write("monitor-remove",row.Definition.Path);}
        Rewatch();Status=$"已移除 {rows.Count} 个监控（未删除文件）";
    }
    public void Deploy(IReadOnlySet<Guid> selected)
    {
        EnsureIdle(); var monitors=repository.GetMonitors();
        if(!monitors.Any(m=>m.Enabled && selected.Contains(m.Id)))
        {log.Write("task-deploy-skipped","没有启用且勾选的监控目录");throw new ArgumentException("没有目录待安装：请添加、启用并勾选至少一个目录。");}
        var result=tasks.Deploy(Settings.Schedule,Environment.ProcessPath!);
        if(result.Deployment!=TaskDeployment.Installed) {RefreshTaskStatus();throw new IOException("任务未完整部署："+result.Details);}
        repository.SetScheduled(selected);
        foreach(var row in Monitors) row.Accept(repository.GetMonitors().Single(m=>m.Id==row.Id));
        log.Write("task-directories",string.Join("；",Monitors.Where(r=>r.Scheduled).Select(r=>r.Definition.Path)));
        RefreshTaskStatus();Rewatch();lastScan=DateTimeOffset.MinValue;sweepDay=null; Status="每日与登录任务已部署，参与目录已保存";
    }
    public void Uninstall()
    {
        EnsureIdle();tasks.Remove();RefreshTaskStatus();
        if(deployment!=TaskDeployment.NotInstalled)throw new IOException("任务未完全卸载，请查看任务状态。");
        Status="自动任务已卸载；目录配置和计时记录保留";
    }
    public void SaveSettings(AppSettings settings)
    {
        EnsureIdle();settings.Validate();
        if(HasTasks && settings.DailyTime!=Settings.DailyTime)
        {
            var result=tasks.Deploy(settings.Schedule,Environment.ProcessPath!);
            if(result.Deployment!=TaskDeployment.Installed)throw new IOException("无法更新计划时间："+result.Details);
        }
        repository.WriteSettings(settings);Settings=settings;RefreshTaskStatus();sweepDay=null;
        log.Write("settings",$"时间={settings.DailyTime}；日志={settings.LogLimitMiB} MiB；默认={settings.DefaultDeletionMode}");
        Status="设置已保存";
    }
    private void ApplyActivity()
    {
        if(Interlocked.Exchange(ref lostEvents,0)!=0)
        {
            foreach(var monitor in repository.GetMonitors().Where(m=>m.Enabled && m.Scheduled))
                repository.ReplaceObservations(monitor.Id,repository.GetObservations(monitor.Id).Select(o=>o with{LastActivityUtc=DateTimeOffset.UtcNow}).ToArray());
            log.Write("watch-resync","事件队列达到限额，保守刷新已知项目计时并全量校正");
        }
        var events=new List<ActivityEvent>();while(activity.TryDequeue(out var e))events.Add(e);
        foreach(var group in events.GroupBy(e=>e.MonitorId))
        {
            var m=repository.GetMonitors().SingleOrDefault(m=>m.Id==group.Key && m.Enabled && m.Scheduled);
            if(m is null)continue;
            var observations=repository.GetObservations(m.Id).ToArray();bool changed=false;
            if(group.Any(e=>e.Resync))
            {
                observations=observations.Select(o=>o with{LastActivityUtc=DateTimeOffset.UtcNow}).ToArray();changed=true;
            }
            foreach(var e in group.Where(e=>!e.Resync))
            {
                if(!MonitorValidation.IsWithin(e.Path,m.Path)||IsLinkedEvent(e.Path,m.Path))continue;
                var relative=System.IO.Path.GetRelativePath(m.Path,e.Path);
                var top=System.IO.Path.Combine(m.Path,relative.Split(System.IO.Path.DirectorySeparatorChar)[0]);
                for(int i=0;i<observations.Length;i++) if(string.Equals(observations[i].Path,top,StringComparison.OrdinalIgnoreCase) && e.ObservedAt>observations[i].LastActivityUtc)
                {observations[i]=observations[i] with {LastActivityUtc=e.ObservedAt};changed=true;}
            }
            if(changed)repository.ReplaceObservations(m.Id,observations);
        }
    }
    private static bool IsLinkedEvent(string path,string root)
    {
        for(string? p=path;p is not null && !string.Equals(p,root,StringComparison.OrdinalIgnoreCase);p=System.IO.Path.GetDirectoryName(p))
            if((File.Exists(p)||Directory.Exists(p)) && (File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)return true;
        return false;
    }
    public async Task<ScanResult> ScanAsync(IReadOnlyList<MonitorRow>? rows=null,ScanScope? scope=null)
    {
        if(!await gate.WaitAsync(0))throw new InvalidOperationException("正在处理文件，请稍后。");
        SetBusy(true);
        try
        {
            var ids=rows?.Select(r=>r.Id).ToHashSet();
            var result=await Task.Run(()=>{ApplyActivity();return scanner.Scan(scope??(rows is null ? ScanScope.AllEnabled : ScanScope.Selected),ids);});
            Status=$"已扫描 {result.MonitorsScanned} 个目录；到期 {result.DueItems.Count} 项；异常 {result.Errors.Count} 项";
            return result;
        }
        finally {SetBusy(false);gate.Release();}
    }
    public async Task<IReadOnlyList<CleanupResult>> CleanupAsync(CleanupRequest request)
    {
        if(!await gate.WaitAsync(0))throw new InvalidOperationException("正在处理文件，请稍后。");
        SetBusy(true);
        try {await Task.Run(ApplyActivity);return await cleanup.ExecuteAsync(request,CancellationToken.None);}
        finally {SetBusy(false);gate.Release();}
    }
    public async Task TickAsync()
    {
        if(busy)return;
        try
        {
            if(DateTimeOffset.UtcNow>=nextTaskCheck)RefreshTaskStatus();
            if(deployment!=TaskDeployment.Installed || !Monitors.Any(m=>m.Enabled && m.Scheduled))return;
            var today=DateOnly.FromDateTime(DateTime.Now);
            bool scheduled=deployment==TaskDeployment.Installed && TimeOnly.FromDateTime(DateTime.Now)>=Settings.Schedule.DailyTime && sweepDay!=today;
            bool periodic=DateTimeOffset.UtcNow-lastScan>=TimeSpan.FromMinutes(15);
            bool events=activity.TryPeek(out var last) && DateTimeOffset.UtcNow-last.ObservedAt>=TimeSpan.FromSeconds(2);
            if(!scheduled && !periodic && !events)return;
            var result=await ScanAsync(scope:ScanScope.Scheduled);lastScan=DateTimeOffset.UtcNow;
            if(periodic)Rewatch();
            if(scheduled)sweepDay=today;
            var due=result.DueItems.Count(i=>i.Monitor.Scheduled);
            DateOnly? previous=DateOnly.TryParse(repository.ReadValue("last-reminder"),out var d)?d:null;
            if(deployment==TaskDeployment.Installed && ReminderPolicy.ShouldRemind(DateTimeOffset.UtcNow,Settings.Schedule,due,previous))
            {
                Reminder?.Invoke(this,due);
                repository.WriteValue("last-reminder",today.ToString("yyyy-MM-dd"));
                log.Write("reminder",$"到期 {due} 项；日期={today:yyyy-MM-dd}");
            }
        }
        catch(Exception ex){log.Write("background-error",ex.ToString());Status="后台检查异常："+ex.Message;lastScan=DateTimeOffset.UtcNow;}
    }
    public void Dispose()=>tracker.Dispose();
}
