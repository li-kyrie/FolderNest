namespace FolderNest.Core;

public sealed class CleanupService(IMonitorRepository repository, IFileInventory inventory,
    IFileDeletion deletion, IClock clock, IAuditLog log) : ICleanupService
{
    public Task<IReadOnlyList<CleanupResult>> ExecuteAsync(CleanupRequest request, CancellationToken token) =>
        Task.Run<IReadOnlyList<CleanupResult>>(() =>
        {
            var results = new List<CleanupResult>();
            foreach (var item in request.SelectedItems)
            {
                token.ThrowIfCancellationRequested();
                var path = item.Observation.Path;
                try
                {
                    var monitor = repository.GetMonitors().SingleOrDefault(m => m.Id == item.Monitor.Id);
                    if (monitor is null || !monitor.Enabled || !string.Equals(monitor.Path, item.Monitor.Path, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("目录配置已改变或已停用，请重新扫描。");
                    if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(monitor.Path)), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("清理目标不属于该监控目录的直接子项。");
                    var stored = repository.GetObservations(monitor.Id).SingleOrDefault(o => string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase));
                    if (stored is null || stored != item.Observation || ExpiryPolicy.DueAt(stored, monitor.RetentionDays) > clock.UtcNow)
                        throw new IOException("计时记录已变化或尚未到期，请重新扫描。");
                    var current = inventory.Enumerate(monitor.Path).SingleOrDefault(u => string.Equals(u.Path, path, StringComparison.OrdinalIgnoreCase));
                    if (current is null || current.IsDirectory != stored.IsDirectory || current.Fingerprint != stored.Fingerprint)
                        throw new IOException("目标已修改、消失或替换为链接，已跳过。");
                    var mode = request.Mode ?? monitor.DeletionMode;
                    if (request.Mode is null && monitor.DeletionMode != item.Monitor.DeletionMode)
                        throw new IOException("删除模式已改变，请重新打开清单。");
                    deletion.Delete(path, stored.IsDirectory, mode);
                    // Success is recorded only after the OS reports completion.
                    repository.ReplaceObservations(monitor.Id, repository.GetObservations(monitor.Id)
                        .Where(o => !string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray());
                    log.Write("cleanup-success", $"{mode}；{path}");
                    results.Add(new(path, true, null));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    log.Write("cleanup-skipped", $"{path}；{ex.Message}"); results.Add(new(path, false, ex.Message));
                }
            }
            return results;
        }, token);
}
