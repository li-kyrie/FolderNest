namespace FolderNest.Core;

public static class ReminderPolicy
{
    // Daily run or the first interactive login after the day's configured reminder time.
    // A dismissed reminder creates no persistent frozen queue; future runs recalculate due items.
    public static bool ShouldRemind(DateTimeOffset now, ScheduleSettings settings, int dueCount,
        DateOnly? lastReminderDate)
    {
        if (dueCount == 0) return false;
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId));
        var today = DateOnly.FromDateTime(local.DateTime);
        return TimeOnly.FromDateTime(local.DateTime) >= settings.DailyTime && lastReminderDate != today;
    }
}
