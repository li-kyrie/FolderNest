using FolderNest.Core;

namespace FolderNest.Infrastructure;

public sealed record ActivityEvent(Guid MonitorId, string Path, DateTimeOffset ObservedAt, bool Resync = false);
public sealed class ArrivalTracker : IDisposable
{
    private readonly List<FileSystemWatcher> watchers=[];
    public event EventHandler<ActivityEvent>? Changed;
    public void Watch(IReadOnlyList<MonitorDefinition> monitors, IAuditLog log)
    {
        Dispose();
        foreach(var monitor in monitors.Where(m=>m.Enabled && Directory.Exists(m.Path)))
        {
            try
            {
                var watcher=new FileSystemWatcher(monitor.Path) { IncludeSubdirectories=true,
                    NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName|NotifyFilters.LastWrite|NotifyFilters.Size,
                    InternalBufferSize=32768 };
                watcher.Created+=(_,e)=>Emit(monitor.Id,e.FullPath);
                watcher.Changed+=(_,e)=>Emit(monitor.Id,e.FullPath);
                watcher.Deleted+=(_,e)=>Emit(monitor.Id,e.FullPath);
                watcher.Renamed+=(_,e)=>{Emit(monitor.Id,e.OldFullPath);Emit(monitor.Id,e.FullPath);};
                watcher.Error+=(_,e)=>{log.Write("watch-overflow",$"{monitor.Path}；{e.GetException().Message}");Changed?.Invoke(this,new(monitor.Id,monitor.Path,DateTimeOffset.UtcNow,true));};
                watcher.EnableRaisingEvents=true;watchers.Add(watcher);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){log.Write("watch-failed",$"{monitor.Path}；{ex.Message}");}
        }
    }
    private void Emit(Guid id,string path)=>Changed?.Invoke(this,new(id,path,DateTimeOffset.UtcNow));
    public void Dispose(){foreach(var watcher in watchers)watcher.Dispose();watchers.Clear();}
}
