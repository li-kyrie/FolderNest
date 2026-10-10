using System.Collections.ObjectModel;
using System.ComponentModel;
using FolderNest.Core;
using FolderNest.Infrastructure;

namespace FolderNest.Desktop;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IMonitorRepository repository;
    private readonly ScanService scanner;
    private readonly IAuditLog log;
    private readonly WindowsTaskStatusReader tasks;
    private string status="就绪";
    private bool isBusy;
    public MainViewModel(IMonitorRepository repository, ScanService scanner, IAuditLog log,
        PortablePaths paths, WindowsTaskStatusReader tasks)
    {
        this.repository=repository; this.scanner=scanner; this.log=log; this.tasks=tasks; Paths=paths;
        foreach(var monitor in repository.GetMonitors()) Monitors.Add(CreateRow(monitor));
    }
    public PortablePaths Paths { get; }
    public ObservableCollection<MonitorRow> Monitors { get; } = [];
    public string Status { get => status; set { status=value; PropertyChanged?.Invoke(this,new(nameof(Status))); } }
    public bool IsBusy { get => isBusy; private set { isBusy=value; PropertyChanged?.Invoke(this,new(nameof(IsBusy))); PropertyChanged?.Invoke(this,new(nameof(IsIdle))); } }
    public bool IsIdle => !IsBusy;
    public string TaskText { get; private set; } = "新框架定时任务：未部署";
    public string LegacyTaskText { get; private set; } = "";
    public string DataText => $"数据位置：{Paths.DataDirectory}" + (Paths.IsFallback ? "（程序目录不可写，使用备用位置）" : "");
    public void RefreshTaskStatus()
    {
        LegacyTaskText=tasks.GetLegacyStatus().Details;
        PropertyChanged?.Invoke(this,new(nameof(LegacyTaskText)));
    }
    private MonitorRow CreateRow(MonitorDefinition monitor) => new(monitor, Save);
    private void Save(MonitorDefinition monitor)
    {
        MonitorValidation.Validate(monitor, repository.GetMonitors());
        var app=AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        if(MonitorValidation.IsWithin(Paths.DataDirectory, monitor.Path) ||
            MonitorValidation.IsWithin(monitor.Path, Paths.DataDirectory) ||
            MonitorValidation.IsWithin(app, monitor.Path) || MonitorValidation.IsWithin(monitor.Path, app))
            throw new ArgumentException("不能监控程序或数据所在文件夹、其子目录或上级目录。");
        repository.SaveMonitor(monitor);
        log.Write("monitor-save", $"{monitor.Path}；{monitor.RetentionDays}天；启用={monitor.Enabled}；自动扫描选择={monitor.Scheduled}");
        Status="配置已保存";
    }
    public MonitorRow Add(string path)
    {
        var m=new MonitorDefinition(Guid.NewGuid(), MonitorValidation.NormalizePath(path));
        Save(m); var row=CreateRow(m); Monitors.Add(row); return row;
    }
    public void Toggle(IReadOnlyList<MonitorRow> rows)
    {
        if(rows.Count==0) throw new ArgumentException("请先选择监控目录。");
        var target=rows.Any(r=>!r.Enabled);
        foreach(var row in rows) row.Enabled=target;
        Status=$"已{(target ? "启用" : "停用")} {rows.Count} 个目录";
    }
    public void Remove(IReadOnlyList<MonitorRow> rows)
    {
        foreach(var row in rows) { repository.RemoveMonitor(row.Id); Monitors.Remove(row); log.Write("monitor-remove",row.Path); }
        Status=$"已删除 {rows.Count} 个监控设置";
    }
    public async Task<ScanResult> ScanAsync(IReadOnlyList<MonitorRow>? rows)
    {
        if(IsBusy) throw new InvalidOperationException("正在扫描，请稍后。");
        IsBusy=true;
        try
        {
            var ids=rows?.Select(r=>r.Id).ToHashSet();
            var result=await Task.Run(()=>scanner.Scan(rows is null ? ScanScope.AllEnabled : ScanScope.Selected,ids));
            Status=$"已扫描 {result.MonitorsScanned} 个目录；到期 {result.DueItems.Count} 项；异常 {result.Errors.Count} 项";
            return result;
        }
        finally { IsBusy=false; }
    }
    public IReadOnlyList<DueItem> GetDue() => scanner.GetDue();
    public event PropertyChangedEventHandler? PropertyChanged;
}
