using System.Text.Json;
using FolderNest.Core;

namespace FolderNest.Infrastructure;

public static class MonitorConfigImporter
{
    // Import settings only. Never restore stale timing or pending deletion records.
    public static IReadOnlyList<MonitorDefinition> Read(string configPath)
    {
        using var json=JsonDocument.Parse(File.ReadAllText(configPath));
        var root=json.RootElement;
        if(root.ValueKind!=JsonValueKind.Object)throw new ArgumentException("配置顶层必须是 JSON 对象。");
        JsonElement[] entries;
        if(root.TryGetProperty("Monitors",out var list))
        {
            CheckFields(root,["Monitors"]);
            if(list.ValueKind!=JsonValueKind.Array)throw new ArgumentException("Monitors 必须是数组。");
            entries=list.EnumerateArray().ToArray();
        }
        else entries=[root];
        if(entries.Length==0)throw new ArgumentException("Monitors 中没有可导入的目录。");
        var result=new List<MonitorDefinition>();
        for(var i=0;i<entries.Length;i++)
        {
            try
            {
                var item=entries[i];
                CheckFields(item,["Path","Root","Days","DeleteMode","Enabled","Id","Scheduled"]);
                if(item.TryGetProperty("Path",out _)&&item.TryGetProperty("Root",out _))throw new ArgumentException("Path 与 Root 只能填写一个。");
                var p=item.TryGetProperty("Path",out var path)?path:item.GetProperty("Root");
                var normalized=MonitorValidation.NormalizePath(p.GetString()!);
                var days=item.TryGetProperty("Days",out var d)?d.GetInt32():30;
                var mode=DeletionMode.RecycleBin;
                if(item.TryGetProperty("DeleteMode",out var m))mode=m.GetString() switch
                {
                    "RecycleBin"=>DeletionMode.RecycleBin,"Permanent"=>DeletionMode.Permanent,
                    _=>throw new ArgumentException("DeleteMode 只能是 RecycleBin 或 Permanent。")
                };
                var enabled=!item.TryGetProperty("Enabled",out var e)||e.GetBoolean();
                var monitor=new MonitorDefinition(Guid.NewGuid(),normalized,days,mode,enabled);
                MonitorValidation.Validate(monitor,result);result.Add(monitor);
            }
            catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or IOException)
            {throw new ArgumentException($"Monitors 第 {i+1} 项无效：{ex.Message}（Days 必须是 1–36500 的整数，Enabled 必须是布尔值）",ex);}
        }
        return result;
    }
    private static void CheckFields(JsonElement item,string[] allowed)
    {
        if(item.ValueKind!=JsonValueKind.Object)throw new ArgumentException("目录项必须是 JSON 对象。");
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var field in item.EnumerateObject())
        {
            if(!seen.Add(field.Name))throw new ArgumentException($"字段重复：{field.Name}");
            if(!allowed.Contains(field.Name,StringComparer.Ordinal))throw new ArgumentException($"不支持的字段：{field.Name}（字段名区分大小写）");
        }
    }
}
