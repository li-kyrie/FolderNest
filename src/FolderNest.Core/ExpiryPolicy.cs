namespace FolderNest.Core;

public static class ExpiryPolicy
{
    public static Observation Observe(Guid monitorId, FileUnit unit, Observation? previous, DateTimeOffset now)
    {
        // Imported LastWriteTime never controls the initial retention period.
        if (previous is null)
            return new(monitorId, unit.Path, unit.IsDirectory, unit.Size, unit.Fingerprint, now, now);
        var changed = previous.Fingerprint != unit.Fingerprint || previous.IsDirectory != unit.IsDirectory;
        return previous with { IsDirectory = unit.IsDirectory, Size = unit.Size,
            Fingerprint = unit.Fingerprint, LastActivityUtc = changed ? now : previous.LastActivityUtc };
    }

    public static DateTimeOffset DueAt(Observation observation, int days)
    {
        if (days is < 1 or > 36500) throw new ArgumentOutOfRangeException(nameof(days));
        var anchor = observation.LastActivityUtc > observation.FirstSeenUtc
            ? observation.LastActivityUtc : observation.FirstSeenUtc;
        return anchor.AddDays(days);
    }

    public static IReadOnlyList<DueItem> GetDue(MonitorDefinition monitor,
        IReadOnlyList<Observation> observations, DateTimeOffset now) => !monitor.Enabled ? [] :
        observations.Select(o => new DueItem(monitor, o, DueAt(o, monitor.RetentionDays)))
            .Where(i => i.DueAtUtc <= now).ToArray();
}
