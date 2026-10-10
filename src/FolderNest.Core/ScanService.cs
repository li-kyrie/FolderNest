namespace FolderNest.Core;

public sealed class ScanService(IMonitorRepository repository, IFileInventory inventory, IClock clock, IAuditLog log)
{
    public ScanResult Scan(ScanScope scope, IReadOnlySet<Guid>? selected = null)
    {
        if (scope == ScanScope.Selected && (selected is null || selected.Count == 0))
            throw new ArgumentException("请先选择要扫描的目录。", nameof(selected));
        var errors = new List<string>();
        var count = 0;
        foreach (var monitor in repository.GetMonitors().Where(m => m.Enabled && (scope switch
        {
            ScanScope.Scheduled => m.Scheduled,
            ScanScope.Selected => selected!.Contains(m.Id),
            _ => true
        })))
        {
            try
            {
                var now = clock.UtcNow;
                var previous = repository.GetObservations(monitor.Id)
                    .ToDictionary(o => o.Path, StringComparer.OrdinalIgnoreCase);
                // Enumerate fully before committing; unavailable folders retain their previous history.
                var observations = inventory.Enumerate(monitor.Path).Select(u =>
                    ExpiryPolicy.Observe(monitor.Id, u, previous.GetValueOrDefault(u.Path), now)).ToArray();
                repository.ReplaceObservations(monitor.Id, observations);
                count++;
                log.Write("scan", $"{monitor.Path}：{observations.Length} 个清理单元");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{monitor.Path}：{error.Message}");
                log.Write("scan-failed", errors[^1]);
            }
        }
        var due = GetDue().Where(i => scope switch
        {
            ScanScope.Scheduled => i.Monitor.Scheduled,
            ScanScope.Selected => selected!.Contains(i.Monitor.Id),
            _ => true
        }).ToArray();
        return new(count, due, errors);
    }

    public IReadOnlyList<DueItem> GetDue() => repository.GetMonitors()
        .SelectMany(m => ExpiryPolicy.GetDue(m, repository.GetObservations(m.Id), clock.UtcNow))
        .OrderBy(i => i.DueAtUtc).ToArray();
}
