namespace FolderNest.Core;

public sealed class TimingResetService(IMonitorRepository repository,IFileInventory inventory,IClock clock,IAuditLog log)
{
    public IReadOnlyList<TimingResetResult> Reset(IReadOnlyList<TimingResetSelection> selections)
    {
        var results=new List<TimingResetResult>();
        foreach(var group in selections.GroupBy(s=>s.MonitorId))
        {
            try
            {
                var monitor=repository.GetMonitors().SingleOrDefault(m=>m.Id==group.Key)
                    ??throw new IOException("监控已删除，请重新打开重置窗口。");
                bool all=group.Any(s=>s.Paths is null);
                var paths=group.SelectMany(s=>s.Paths??new HashSet<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if(!all && paths.Count==0)continue;
                // Enumerate before committing: failed/offline roots must never lose their old history.
                var units=inventory.Enumerate(monitor.Path);
                var previous=repository.GetObservations(monitor.Id).ToDictionary(o=>o.Path,StringComparer.OrdinalIgnoreCase);
                var now=clock.UtcNow;var resetPaths=new List<string>();
                foreach(var unit in units.Where(u=>all || paths.Contains(u.Path)))
                {
                    if(!string.Equals(Path.GetDirectoryName(unit.Path),Path.TrimEndingDirectorySeparator(monitor.Path),StringComparison.OrdinalIgnoreCase))
                        throw new IOException("重置目标不属于监控目录。");
                    previous[unit.Path]=ExpiryPolicy.Observe(monitor.Id,unit,null,now);resetPaths.Add(unit.Path);
                }
                if(!all && resetPaths.Count!=paths.Count)throw new IOException("部分勾选项目已消失或变为链接，未重置该目录，请刷新后重选。");
                var currentPaths=units.Select(u=>u.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                repository.ReplaceObservations(monitor.Id,all ? previous.Values.Where(o=>currentPaths.Contains(o.Path)).ToArray() : previous.Values.ToArray());
                foreach(var path in resetPaths)log.Write("timer-reset-item",path);
                log.Write("timer-reset",$"{monitor.Path}；{resetPaths.Count} 项；开始={now:O}");results.Add(new(monitor.Id,resetPaths.Count,null));
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException)
            {log.Write("timer-reset-failed",$"{group.Key}；{ex.Message}");results.Add(new(group.Key,0,ex.Message));}
        }
        return results;
    }
}
