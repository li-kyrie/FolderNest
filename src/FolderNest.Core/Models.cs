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
public sealed record CleanupRequest(IReadOnlyList<DueItem> SelectedItems, DeletionMode? Mode = null);
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
public interface ICleanupService
{
    // Only the review window may construct this explicit request; no timer calls cleanup.
    Task<IReadOnlyList<CleanupResult>> ExecuteAsync(CleanupRequest request, CancellationToken cancellationToken);
}
public interface IFileDeletion { void Delete(string path, bool isDirectory, DeletionMode mode); }
public sealed record AppSettings(string DailyTime = "02:00", int LogLimitMiB = 10,
    DeletionMode DefaultDeletionMode = DeletionMode.RecycleBin)
{
    public ScheduleSettings Schedule => new(TimeOnly.ParseExact(DailyTime, "HH:mm"), TimeZoneInfo.Local.Id);
    public void Validate()
    {
        if (!TimeOnly.TryParseExact(DailyTime, "HH:mm", out _)) throw new ArgumentException("时间必须为 24 小时制 HH:mm，例如 02:00 或 14:00。");
        if (LogLimitMiB is < 2 or > 1024) throw new ArgumentException("日志总限额必须为 2–1024 MiB 的整数。");
        if (!Enum.IsDefined(DefaultDeletionMode)) throw new ArgumentException("删除模式无效。");
    }
}
