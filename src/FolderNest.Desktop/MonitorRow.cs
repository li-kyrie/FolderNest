using System.Collections;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FolderNest.Core;

namespace FolderNest.Desktop;

public sealed class MonitorRow : INotifyPropertyChanged, INotifyDataErrorInfo, IEditableObject
{
    private MonitorDefinition model;
    private MonitorDefinition draft;
    private readonly Action<MonitorDefinition> validate;
    private readonly Dictionary<string,string> errors=[];
    private string? pathDraft;
    public MonitorRow(MonitorDefinition model,Action<MonitorDefinition> validate){this.model=model;draft=model;this.validate=validate;}
    public Guid Id=>model.Id;
    public MonitorDefinition Definition=>model;
    public MonitorDefinition Candidate=>draft;
    public bool HasPendingChanges=>draft!=model||pathDraft is not null||HasErrors;
    public string EditState=>HasErrors?"待修正":HasPendingChanges?"待确认":"已生效";
    public string Path { get=>pathDraft??draft.Path;set {pathDraft=value;Edit(()=>draft with{Path=MonitorValidation.NormalizePath(value)});} }
    public int RetentionDays {get=>draft.RetentionDays;set=>Edit(()=>draft with{RetentionDays=value});}
    public bool Enabled {get=>draft.Enabled;set=>Edit(()=>draft with{Enabled=value});}
    public DeletionMode DeletionMode {get=>draft.DeletionMode;set=>Edit(()=>draft with{DeletionMode=value});}
    private void Edit(Func<MonitorDefinition> candidate,[CallerMemberName]string property="")
    {
        try
        {
            var next=candidate();validate(next);draft=next;
            if(property==nameof(Path))pathDraft=null;
            errors.Remove(property);
        }
        catch(Exception ex) when(ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {errors[property]=ex.Message;}
        ErrorsChanged?.Invoke(this,new(property));PropertyChanged?.Invoke(this,new(property));
        PropertyChanged?.Invoke(this,new(nameof(HasPendingChanges)));PropertyChanged?.Invoke(this,new(nameof(EditState)));
    }
    public bool HasErrors=>errors.Count>0;
    public void Accept(MonitorDefinition committed)
    {
        model=committed;draft=committed;pathDraft=null;
        foreach(var name in errors.Keys.ToArray()){errors.Remove(name);ErrorsChanged?.Invoke(this,new(name));}
        PropertyChanged?.Invoke(this,new(""));
    }
    public void BeginEdit() { }
    public void EndEdit() { }
    public void CancelEdit()
    {
        draft=model;pathDraft=null;
        foreach(var name in errors.Keys.ToArray()){errors.Remove(name);ErrorsChanged?.Invoke(this,new(name));}
        PropertyChanged?.Invoke(this,new(""));
    }
    public IEnumerable GetErrors(string? propertyName)=>propertyName is not null && errors.TryGetValue(propertyName,out var e)?new[]{e}:Array.Empty<string>();
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
}
