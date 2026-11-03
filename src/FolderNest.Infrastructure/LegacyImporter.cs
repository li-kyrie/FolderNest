using System.Text.Json;
using FolderNest.Core;

namespace FolderNest.Infrastructure;

public static class LegacyImporter
{
    // Explicit GUI import only. Source JSON is never edited or removed.
    public static IReadOnlyList<MonitorDefinition> Read(string configPath)
    {
        using var json=JsonDocument.Parse(File.ReadAllText(configPath));
        var root=json.RootElement;
        var result=new List<MonitorDefinition>();
        var monitors=root.TryGetProperty("Monitors",out var list) ? list.EnumerateArray().ToArray() : [root];
        foreach(var monitor in monitors)
        {
            var path=monitor.TryGetProperty("Path",out var p) ? p.GetString() : monitor.GetProperty("Root").GetString();
            var days=monitor.TryGetProperty("Days",out var d) ? d.GetInt32() : 30;
            var mode=monitor.TryGetProperty("DeleteMode",out var m) && m.GetString()=="Permanent" ? DeletionMode.Permanent : DeletionMode.RecycleBin;
            // Import configuration only: a stale old Pending/FirstSeen cannot authorize early cleanup.
            result.Add(new(Guid.NewGuid(),MonitorValidation.NormalizePath(path!),days,mode,
                !monitor.TryGetProperty("Enabled",out var e) || e.GetBoolean(),false));
        }
        return result;
    }
}
