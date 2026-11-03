using System.Collections;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FolderNest.Core;

namespace FolderNest.Desktop;

public sealed class MonitorRow : INotifyPropertyChanged, INotifyDataErrorInfo, IEditableObject
{
    private MonitorDefinition model;
    private readonly Action<MonitorDefinition> save;
    private readonly Dictionary<string,string> errors=[];
    private string? pathDraft;
    private TaskDeployment deployment;
    public MonitorRow(MonitorDefinition model,Action<MonitorDefinition> save){this.model=model;this.save=save;}
    public Guid Id=>model.Id;
    public MonitorDefinition Definition=>model;
    public string Path { get=>pathDraft??model.Path;set {pathDraft=value;Commit(()=>model with{Path=MonitorValidation.NormalizePath(value)});} }
    public int RetentionDays {get=>model.RetentionDays;set=>Commit(()=>model with{RetentionDays=value});}
    public bool Enabled {get=>model.Enabled;set=>Commit(()=>model with{Enabled=value});}
    public DeletionMode DeletionMode {get=>model.DeletionMode;set=>Commit(()=>model with{DeletionMode=value});}
    public bool Scheduled {get=>model.Scheduled;set=>Commit(()=>model with{Scheduled=value});}
    public string ScheduleText=>!Scheduled ? "未参与" : !Enabled ? "已选择／停用" : deployment switch
    {TaskDeployment.Installed=>"已部署",TaskDeployment.NotInstalled=>"未部署",TaskDeployment.Partial=>"部分部署",_=>"无法查询"};
    public void SetDeployment(TaskDeployment state){deployment=state;PropertyChanged?.Invoke(this,new(nameof(ScheduleText)));}
    private void Commit(Func<MonitorDefinition> candidate,[CallerMemberName]string property="")
    {
        try
        {
            var next=candidate();if(next!=model)save(next);model=next;
            if(property==nameof(Path))pathDraft=null;
            errors.Remove(property);
        }
        catch(Exception ex) when(ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {errors[property]=ex.Message;}
        ErrorsChanged?.Invoke(this,new(property));PropertyChanged?.Invoke(this,new(property));
        PropertyChanged?.Invoke(this,new(nameof(ScheduleText)));
    }
    public bool HasErrors=>errors.Count>0;
    public void Accept(MonitorDefinition committed)
    {
        model=committed;pathDraft=null;errors.Clear();PropertyChanged?.Invoke(this,new(""));
    }
    public void BeginEdit() { }
    public void EndEdit() { }
    public void CancelEdit()
    {
        pathDraft=null;
        foreach(var name in errors.Keys.ToArray()){errors.Remove(name);ErrorsChanged?.Invoke(this,new(name));}
        PropertyChanged?.Invoke(this,new(""));
    }
    public IEnumerable GetErrors(string? propertyName)=>propertyName is not null && errors.TryGetValue(propertyName,out var e)?new[]{e}:Array.Empty<string>();
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
}
