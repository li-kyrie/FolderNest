namespace FolderNest.Core;

public enum DeletionMode { RecycleBin, Permanent }
public enum ScanScope { AllEnabled, Selected, Scheduled }
public enum TaskDeployment { NotInstalled, Installed, Partial, Unknown }

public sealed record MonitorDefinition(Guid Id, string Path, int RetentionDays = 30,
    DeletionMode DeletionMode = DeletionMode.RecycleBin, bool Enabled = true, bool Scheduled = false);

public sealed record FileUnit(string Path, bool IsDirectory, long Size, string Fingerprint);
public sealed record Observation(Guid MonitorId, string Path, bool IsDirectory, long Size,
    string Fingerprint, DateTimeOffset FirstSeenUtc, DateTimeOffset LastActivityUtc);
public sealed record DueItem(MonitorDefinition Monitor, Observation Observation, DateTimeOffset DueAtUtc);
public sealed record ScanResult(int MonitorsScanned, IReadOnlyList<DueItem> DueItems, IReadOnlyList<string> Errors);
public sealed record ScheduleSettings(TimeOnly DailyTime, string TimeZoneId);
public sealed record TaskStatus(TaskDeployment Deployment, string Details);
public sealed record CleanupRequest(IReadOnlyList<DueItem> SelectedItems, DeletionMode Mode);
public sealed record CleanupResult(string Path, bool Success, string? Error);

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

public interface IMonitorRepository
{
    IReadOnlyList<MonitorDefinition> GetMonitors();
    void SaveMonitor(MonitorDefinition monitor);
    void RemoveMonitor(Guid id);
    IReadOnlyList<Observation> GetObservations(Guid monitorId);
    void ReplaceObservations(Guid monitorId, IReadOnlyList<Observation> observations);
}
public interface IFileInventory { IReadOnlyList<FileUnit> Enumerate(string root); }
public interface IAuditLog { void Write(string operation, string message); }
public interface ITaskScheduler
{
    TaskStatus GetStatus();
    TaskStatus Deploy(ScheduleSettings settings, string executablePath);
    TaskStatus Remove();
}
public interface IArrivalTracker : IDisposable
{
    event EventHandler<Guid>? InventoryChanged;
    void Watch(IReadOnlyList<MonitorDefinition> monitors);
}
public interface INotificationService { Task NotifyAsync(int dueCount, CancellationToken cancellationToken); }
public interface ICleanupService
{
    // Only the review window may construct this explicit request; no timer calls cleanup.
    Task<IReadOnlyList<CleanupResult>> ExecuteAsync(CleanupRequest request, CancellationToken cancellationToken);
}
