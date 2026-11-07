using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using FolderNest.Core;
using FolderNest.Infrastructure;

namespace FolderNest.Desktop;

// Configuration belongs to the dispatcher; scans/reset/cleanup use one gate.
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SqliteRepository repository;
    private readonly ScanService scanner;
    private readonly CleanupService cleanup;
    private readonly TimingResetService reset;
    private readonly IAuditLog log;
    private readonly ITaskScheduler tasks;
    private readonly bool manageSchedule;
    private readonly ArrivalTracker tracker=new();
    private readonly ConcurrentQueue<ActivityEvent> activity=new();
    private readonly SemaphoreSlim gate=new(1);
    private string status="就绪";
    private bool busy;
    private DateTimeOffset lastScan=DateTimeOffset.MinValue;
    private DateOnly? sweepDay;
    private DateTimeOffset nextTaskCheck;
    private int lostEvents;
    public MainViewModel(SqliteRepository repository,IAuditLog log,PortablePaths paths,
        ITaskScheduler? scheduler=null,bool manageSchedule=true)
    {
        this.repository=repository;this.log=log;Paths=paths;this.manageSchedule=manageSchedule;
        Settings=repository.ReadSettings();Settings.Validate();
        scanner=new(repository,new LocalFileInventory(),new SystemClock(),log);
        cleanup=new(repository,new LocalFileInventory(),new WindowsFileDeletion(),new SystemClock(),log);
        reset=new(repository,new LocalFileInventory(),new SystemClock(),log);
        tasks=scheduler??new WindowsTaskScheduler(paths.DataDirectory,log,expectedDailyTime:()=>Settings.Schedule.DailyTime);
        foreach(var m in repository.GetMonitors())Monitors.Add(CreateRow(m));
        tracker.Changed+=(_,e)=>
        {
            if(activity.Count>=10000)Interlocked.Exchange(ref lostEvents,1);
            else activity.Enqueue(e);
        };
    }
    public PortablePaths Paths {get;}
    public AppSettings Settings {get;private set;}
    public ObservableCollection<MonitorRow> Monitors {get;}=[];
    public string Status {get=>status;set {status=value;Notify(nameof(Status));}}
    public bool IsBusy=>busy;
    public bool IsIdle=>!busy;
    public bool HasMonitors=>Monitors.Count>0;
    public bool HasPendingEdits=>Monitors.Any(r=>r.HasPendingChanges||r.HasErrors);
    public string EditNotice=>HasPendingEdits?"有未确认修改：请点击“确认修改”或“取消修改”。扫描和后台仍使用已保存配置。":"直接编辑后，请点击“确认修改”；弹出“配置已保存生效”才表示保存成功。";
    public string BackgroundText=>$"后台扫描：每天{Settings.DailyTime.Replace(':','：')}＋登录补扫";
    public string BackgroundError {get;private set;}="";
    public string DataText=>$"数据：{Paths.DataDirectory}"+(Paths.IsFallback?"（备用位置）":"");
    public string ConfigurationText=>$"配置（SQLite）：{Paths.DatabasePath}";
    public event EventHandler<int>? Reminder;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name)=>PropertyChanged?.Invoke(this,new(name));
    private void SetBusy(bool value){busy=value;Notify(nameof(IsIdle));}
    private void EnsureIdle(){if(busy)throw new InvalidOperationException("正在处理文件，请稍后再修改配置。");}
    // No separate deployment workflow: directory configuration owns its Windows startup plans.
    public void SynchronizeSchedule()
    {
        if(!manageSchedule)return;
        try
        {
            var current=tasks.GetStatus();
            if(repository.GetMonitors().Count==0)
            {
                current=tasks.Remove(); // Also retire same-data 2.0 names when no new-name plans exist.
                if(current.Deployment!=TaskDeployment.NotInstalled)throw new IOException(current.Details);
            }
            else if(current.Deployment!=TaskDeployment.Installed)
                current=tasks.Deploy(Settings.Schedule,Environment.ProcessPath!);
            if(repository.GetMonitors().Count>0 && current.Deployment!=TaskDeployment.Installed)throw new IOException(current.Details);
            BackgroundError="";
            log.Write("schedule-sync",$"{current.Details}；目录："+string.Join("；",repository.GetMonitors().Select(m=>$"{m.Path} [{(m.Enabled?"启用":"停用")}]")));
        }
        catch(Exception ex)
        {
            BackgroundError="后台扫描设置失败（配置和计时仍保留，可手动扫描）："+ex.Message;
            log.Write("schedule-error",ex.ToString());
        }
        Notify(nameof(BackgroundError));Notify(nameof(HasMonitors));
        nextTaskCheck=DateTimeOffset.UtcNow.AddMinutes(5);Rewatch();
    }
    public void Validate(MonitorDefinition m)
    {
        var previous=repository.GetMonitors().SingleOrDefault(item=>item.Id==m.Id);
        if(previous is null || !string.Equals(previous.Path,m.Path,StringComparison.OrdinalIgnoreCase))
            MonitorValidation.Validate(m,repository.GetMonitors());
        else if(m.RetentionDays is <1 or >36500 || !Enum.IsDefined(m.DeletionMode))
            throw new ArgumentException("天数或删除模式无效。");
        var app=Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if(MonitorValidation.IsWithin(m.Path,Paths.DataDirectory)||MonitorValidation.IsWithin(Paths.DataDirectory,m.Path)||
           MonitorValidation.IsWithin(m.Path,app)||MonitorValidation.IsWithin(app,m.Path))
            throw new ArgumentException("不能监控程序或数据文件夹、其子目录或上级目录。");
    }
    private MonitorRow CreateRow(MonitorDefinition m)
    {
        var row=new MonitorRow(m,Validate);
        row.PropertyChanged+=(_,_)=>{Notify(nameof(HasPendingEdits));Notify(nameof(EditNotice));};
        return row;
    }
    public IReadOnlyList<MonitorRow> PrepareEdits()
    {
        EnsureIdle();
        if(Monitors.Any(r=>r.HasErrors))throw new ArgumentException("修改中有无效值，请先修正红框提示。");
        var rows=Monitors.Where(r=>r.HasPendingChanges).ToArray();
        if(rows.Length==0)throw new ArgumentException("没有待确认的配置修改。");
        foreach(var row in rows)Validate(row.Candidate);
        var final=Monitors.Select(r=>r.Candidate).ToArray();
        foreach(var m in final)foreach(var other in final.Where(x=>x.Id!=m.Id))
            if(MonitorValidation.IsWithin(m.Path,other.Path)||MonitorValidation.IsWithin(other.Path,m.Path))
                throw new ArgumentException("修改后的目录存在重复或相互包含，请调整路径。");
        return rows;
    }
    public void ApplyEdits()
    {
        var rows=PrepareEdits();
        Status="正在保存配置…";
        try{repository.SaveMonitors(rows.Select(r=>r.Candidate).ToArray());}
        catch(Exception ex){Status="配置保存失败，修改未生效："+ex.Message;throw;}
        // SQLite commit must succeed before the UI can call these edits effective.
        foreach(var row in rows)row.Accept(row.Candidate);
        string? warning=null;
        foreach(var row in rows)
            try{log.Write("monitor-save",$"{row.Definition.Path}；{row.Definition.RetentionDays}天；{row.Definition.DeletionMode}；启用={row.Definition.Enabled}；用户确认保存");}
            catch(Exception ex){warning="日志写入失败："+ex.Message;}
        try{Rewatch();}catch(Exception ex){warning="文件监听刷新失败，仍可扫描："+ex.Message;}
        lastScan=DateTimeOffset.MinValue;
        Status="配置已保存生效"+(warning is null?"":"；"+warning);
    }
    public void CancelEdits()
    {
        EnsureIdle();foreach(var row in Monitors)row.CancelEdit();Status="已取消修改，继续使用原配置";
    }
    private void Rewatch()=>tracker.Watch(repository.GetMonitors(),log);
    public async Task<MonitorRow> AddAsync(MonitorDefinition monitor)
    {
        EnsureIdle();Validate(monitor);
        if(repository.GetMonitors().Any(m=>m.Id==monitor.Id))throw new ArgumentException("监控 ID 已存在。");
        if(!await gate.WaitAsync(0))throw new InvalidOperationException("正在处理文件，请稍后。");
        SetBusy(true);
        try
        {
            var joined=DateTimeOffset.UtcNow;
            var observations=await Task.Run(()=>new LocalFileInventory().Enumerate(monitor.Path)
                .Select(u=>ExpiryPolicy.Observe(monitor.Id,u,null,joined)).ToArray());
            repository.SaveMonitor(monitor);
            try{repository.ReplaceObservations(monitor.Id,observations);}
            catch {repository.RemoveMonitor(monitor.Id);throw;}
            var row=CreateRow(monitor);Monitors.Add(row);
            repository.SaveOrder(Monitors.Select(r=>r.Id).ToArray());
            log.Write("monitor-add",$"{monitor.Path}；加入={joined:O}；初始 {observations.Length} 项；启用={monitor.Enabled}");
            SynchronizeSchedule();lastScan=DateTimeOffset.MinValue;sweepDay=null;
            Status="监控已添加，现有文件从加入时开始计时";return row;
        }
        finally{SetBusy(false);gate.Release();}
    }
    public void Toggle(IReadOnlyList<MonitorRow> rows)
    {
        EnsureIdle();if(rows.Count==0)throw new ArgumentException("请先选择监控目录。");
        if(HasPendingEdits)throw new ArgumentException("请先确认或取消列表内的配置修改。");
        bool enabled=rows.Any(r=>!r.Enabled);
        foreach(var row in rows)
        {
            var next=row.Definition with{Enabled=enabled};Validate(next);
            repository.SaveMonitor(next);row.Accept(next);log.Write("monitor-toggle",$"{next.Path}；启用={enabled}；保留计时");
        }
        SynchronizeSchedule();lastScan=DateTimeOffset.MinValue;
        Status=$"已{(enabled?"启用":"停用")}选中的 {rows.Count} 个目录；原计时保留";
    }
    public void Remove(IReadOnlyList<MonitorRow> rows)
    {
        EnsureIdle();if(rows.Count==0)throw new ArgumentException("请勾选要删除的监控目录。");
        foreach(var row in rows.Distinct())
        {
            repository.RemoveMonitor(row.Id);Monitors.Remove(row);log.Write("monitor-remove",row.Definition.Path+"；仅删除配置和计时");
        }
        repository.SaveOrder(Monitors.Select(r=>r.Id).ToArray());SynchronizeSchedule();
        Status=$"已删除 {rows.Count} 个监控；真实文件未删除，重新添加会重新计时";
    }
    public void Reorder(IReadOnlyList<MonitorRow> order)
    {
        EnsureIdle();repository.SaveOrder(order.Select(r=>r.Id).ToArray());
        for(int i=0;i<order.Count;i++){int old=Monitors.IndexOf(order[i]);if(old!=i)Monitors.Move(old,i);}
        log.Write("monitor-order",string.Join("；",order.Select(r=>r.Definition.Path)));Status="目录顺序已保存（不改变计时）";
    }
    public void MoveSelection(IReadOnlyList<MonitorRow> rows,int delta)
    {
        if(rows.Count==0)throw new ArgumentException("请先选择要移动的目录。");
        if(delta is not (-1 or 1))throw new ArgumentOutOfRangeException(nameof(delta));
        var order=Monitors.ToList();var set=rows.ToHashSet();
        if(delta<0){for(int i=1;i<order.Count;i++)if(set.Contains(order[i])&&!set.Contains(order[i-1]))(order[i-1],order[i])=(order[i],order[i-1]);}
        else{for(int i=order.Count-2;i>=0;i--)if(set.Contains(order[i])&&!set.Contains(order[i+1]))(order[i+1],order[i])=(order[i],order[i+1]);}
        Reorder(order);
    }
    public IReadOnlyList<Observation> Observations(Guid id)=>repository.GetObservations(id);
    public async Task<IReadOnlyList<ResetCandidateGroup>> GetResetCandidatesAsync()
    {
        if(!await gate.WaitAsync(0))throw new InvalidOperationException("正在处理文件，请稍后。");
        SetBusy(true);
        try
        {
            var monitors=repository.GetMonitors();
            return await Task.Run(()=>monitors.Select(m=>
            {
                try{return new ResetCandidateGroup(m,new LocalFileInventory().Enumerate(m.Path),null);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException)
                {return new ResetCandidateGroup(m,[],ex.Message);}
            }).ToArray());
        }
        finally{SetBusy(false);gate.Release();}
    }
    public async Task<IReadOnlyList<TimingResetResult>> ResetAsync(IReadOnlyList<TimingResetSelection> selections)
    {
        if(selections.Count==0 || selections.All(s=>s.Paths is {Count:0}))throw new ArgumentException("请勾选目录或文件项目。");
        if(!await gate.WaitAsync(0))throw new InvalidOperationException("正在处理文件，请稍后。");
        SetBusy(true);
        try
        {
            var results=await Task.Run(()=>{ApplyActivity();return reset.Reset(selections);});
            Status=$"已重置 {results.Sum(r=>r.ResetCount)} 项计时；异常 {results.Count(r=>r.Error is not null)} 个目录";
            return results;
        }
        finally{SetBusy(false);gate.Release();}
    }
    public void SaveSettings(AppSettings settings)
    {
        EnsureIdle();settings.Validate();
        repository.WriteSettings(settings);Settings=settings;Notify(nameof(BackgroundText));sweepDay=null;
        SynchronizeSchedule();
        log.Write("settings",$"时间={settings.DailyTime}；日志={settings.LogLimitMiB} MiB；默认={settings.DefaultDeletionMode}");
        Status="设置已保存";
    }
    private void ApplyActivity()
    {
        if(Interlocked.Exchange(ref lostEvents,0)!=0)
        {
            foreach(var m in repository.GetMonitors().Where(m=>m.Enabled))
                repository.ReplaceObservations(m.Id,repository.GetObservations(m.Id).Select(o=>o with{LastActivityUtc=DateTimeOffset.UtcNow}).ToArray());
            log.Write("watch-resync","队列达到限额，保守刷新已知项目计时并全量校正");
        }
        var events=new List<ActivityEvent>();while(activity.TryDequeue(out var e))events.Add(e);
        foreach(var group in events.GroupBy(e=>e.MonitorId))
        {
            var m=repository.GetMonitors().SingleOrDefault(m=>m.Id==group.Key && m.Enabled);if(m is null)continue;
            var observations=repository.GetObservations(m.Id).ToArray();bool changed=false;
            if(group.Any(e=>e.Resync)){observations=observations.Select(o=>o with{LastActivityUtc=DateTimeOffset.UtcNow}).ToArray();changed=true;}
            foreach(var e in group.Where(e=>!e.Resync))
            {
                if(!MonitorValidation.IsWithin(e.Path,m.Path)||IsLinkedEvent(e.Path,m.Path))continue;
                var top=Path.Combine(m.Path,Path.GetRelativePath(m.Path,e.Path).Split(Path.DirectorySeparatorChar)[0]);
                for(int i=0;i<observations.Length;i++)if(string.Equals(observations[i].Path,top,StringComparison.OrdinalIgnoreCase)&&e.ObservedAt>observations[i].LastActivityUtc)
                {observations[i]=observations[i] with{LastActivityUtc=e.ObservedAt};changed=true;}
            }
            if(changed)repository.ReplaceObservations(m.Id,observations);
        }
    }
    private static bool IsLinkedEvent(string path,string root)
    {
        for(string? p=path;p is not null&&!string.Equals(p,root,StringComparison.OrdinalIgnoreCase);p=Path.GetDirectoryName(p))
            if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)return true;
        return false;
    }
    public async Task<ScanResult> ScanAsync(IReadOnlyList<MonitorRow>? rows=null)
    {
        if(!await gate.WaitAsync(0))throw new InvalidOperationException("正在处理文件，请稍后。");
        SetBusy(true);
        try
        {
            var ids=rows?.Select(r=>r.Id).ToHashSet();
            var result=await Task.Run(()=>{ApplyActivity();return scanner.Scan(rows is null?ScanScope.AllEnabled:ScanScope.Selected,ids);});
            Status=$"已扫描 {result.MonitorsScanned} 个目录；到期 {result.DueItems.Count} 项；异常 {result.Errors.Count} 项";return result;
        }
        finally{SetBusy(false);gate.Release();}
    }
    public async Task<IReadOnlyList<CleanupResult>> CleanupAsync(CleanupRequest request)
    {
        if(!await gate.WaitAsync(0))throw new InvalidOperationException("正在处理文件，请稍后。");
        SetBusy(true);
        try{await Task.Run(ApplyActivity);return await cleanup.ExecuteAsync(request,CancellationToken.None);}
        finally{SetBusy(false);gate.Release();}
    }
    public async Task TickAsync()
    {
        if(busy || !manageSchedule)return;
        try
        {
            if(DateTimeOffset.UtcNow>=nextTaskCheck)SynchronizeSchedule();
            if(!repository.GetMonitors().Any(m=>m.Enabled))return;
            var today=DateOnly.FromDateTime(DateTime.Now);
            bool daily=TimeOnly.FromDateTime(DateTime.Now)>=Settings.Schedule.DailyTime&&sweepDay!=today;
            bool periodic=DateTimeOffset.UtcNow-lastScan>=TimeSpan.FromMinutes(15);
            bool events=activity.TryPeek(out var e)&&DateTimeOffset.UtcNow-e.ObservedAt>=TimeSpan.FromSeconds(2);
            if(!daily&&!periodic&&!events)return;
            var result=await ScanAsync();lastScan=DateTimeOffset.UtcNow;
            if(periodic)Rewatch();if(daily)sweepDay=today;
            DateOnly? previous=DateOnly.TryParse(repository.ReadValue("last-reminder"),out var date)?date:null;
            if(ReminderPolicy.ShouldRemind(DateTimeOffset.UtcNow,Settings.Schedule,result.DueItems.Count,previous))
            {
                Reminder?.Invoke(this,result.DueItems.Count);
                repository.WriteValue("last-reminder",today.ToString("yyyy-MM-dd"));
                log.Write("reminder",$"到期 {result.DueItems.Count} 项；日期={today:yyyy-MM-dd}");
            }
        }
        catch(Exception ex){log.Write("background-error",ex.ToString());Status="后台检查异常："+ex.Message;lastScan=DateTimeOffset.UtcNow;}
    }
    public void Dispose()=>tracker.Dispose();
}
