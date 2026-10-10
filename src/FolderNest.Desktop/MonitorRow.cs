using System.ComponentModel;
using System.Runtime.CompilerServices;
using FolderNest.Core;

namespace FolderNest.Desktop;

public sealed class MonitorRow : INotifyPropertyChanged
{
    private MonitorDefinition model;
    private readonly Action<MonitorDefinition> save;
    public MonitorRow(MonitorDefinition model, Action<MonitorDefinition> save) { this.model=model; this.save=save; }
    public Guid Id => model.Id;
    public MonitorDefinition Definition => model;
    public string Path { get => model.Path; set => Commit(model with { Path=MonitorValidation.NormalizePath(value) }); }
    public int RetentionDays { get => model.RetentionDays; set => Commit(model with { RetentionDays=value }); }
    public bool Enabled { get => model.Enabled; set => Commit(model with { Enabled=value }); }
    public DeletionMode DeletionMode { get => model.DeletionMode; set => Commit(model with { DeletionMode=value }); }
    public bool Scheduled { get => model.Scheduled; set => Commit(model with { Scheduled=value }); }
    public string ScheduleText => Scheduled ? (Enabled ? "等待部署" : "已选择／停用") : "未选择";
    private void Commit(MonitorDefinition next, [CallerMemberName]string? property=null)
    {
        if(next == model) return;
        save(next); // persist valid candidate before replacing displayed configuration
        model=next;
        PropertyChanged?.Invoke(this,new(property));
        PropertyChanged?.Invoke(this,new(nameof(ScheduleText)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
