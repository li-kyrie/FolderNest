using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using FolderNest.Core;
using TaskStatus = FolderNest.Core.TaskStatus;

namespace FolderNest.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class WindowsTaskScheduler(string dataDirectory, IAuditLog log, string? namePrefix = null, Func<TimeOnly>? expectedDailyTime = null) : ITaskScheduler
{
    private readonly string sid = WindowsIdentity.GetCurrent().User!.Value;
    public string Prefix => namePrefix ?? "FolderNest";
    private string[] Names => [Prefix + " Daily", Prefix + " Logon"];
    private dynamic Connect()
    {
        dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();return service;
    }
    public TaskStatus GetStatus()=>Query(expectedDailyTime?.Invoke());
    private TaskStatus Query(TimeOnly? expectedTime)
    {
        try
        {
            dynamic service=Connect();dynamic root=service.GetFolder("\\");
            var found=new List<string>();var stale=new List<string>();
            foreach(var name in Names)
            {
                dynamic? task=Find(root,name);
                if(task is null)continue;
                if(!(bool)task.Enabled) {stale.Add(name+"（已禁用）");continue;}
                dynamic action=task.Definition.Actions.Item(1);
                if(expectedTime is not null && name.EndsWith("Daily",StringComparison.Ordinal) &&
                    (!DateTime.TryParse((string)task.Definition.Triggers.Item(1).StartBoundary,CultureInfo.InvariantCulture,DateTimeStyles.None,out var boundary) ||
                     TimeOnly.FromDateTime(boundary)!=expectedTime || (int)task.Definition.Triggers.Item(1).DaysInterval!=1))
                {stale.Add(name+"（任务时间与设置不同，请重新部署）");continue;}
                var actual=Path.GetFullPath((string)action.Path);
                if(!string.Equals(actual,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals((string)action.Arguments,Arguments,StringComparison.Ordinal) ||
                    (int)task.Definition.Actions.Count!=1 || (int)task.Definition.Triggers.Count!=1 ||
                    !(bool)task.Definition.Triggers.Item(1).Enabled ||
                    (int)task.Definition.Triggers.Item(1).Type!=(name.EndsWith("Daily",StringComparison.Ordinal)?2:9) ||
                    !SameUser((string)task.Definition.Principal.UserId))
                    stale.Add(name+"（程序或数据路径不同，请重新部署）");
                else found.Add(name);
            }
            return new(found.Count==2 ? TaskDeployment.Installed : found.Count+stale.Count==0 ? TaskDeployment.NotInstalled : TaskDeployment.Partial,
                found.Count==2 ? "每日与登录任务均已部署" : string.Join("；",found.Concat(stale)) is {Length:>0} details ? details : "尚未部署自动任务");
        }
        catch(Exception ex){return new(TaskDeployment.Unknown,"无法查询任务："+ex.Message);}
    }
    private static dynamic? Find(dynamic root,string name)
    {
        try{return root.GetTask(name);}
        catch(Exception ex) when(ex.HResult==unchecked((int)0x80070002) || ex.HResult==unchecked((int)0x8004130F)){return null;}
    }
    private bool SameUser(string value)
    {
        if(string.Equals(value,sid,StringComparison.OrdinalIgnoreCase))return true;
        try{return string.Equals(((SecurityIdentifier)new NTAccount(value).Translate(typeof(SecurityIdentifier))).Value,sid,StringComparison.OrdinalIgnoreCase);}
        catch(IdentityNotMappedException){return false;}
    }
    private void CheckOwnership(dynamic task)
    {
        string description=(string)task.Definition.RegistrationInfo.Description;
        if(!SameUser((string)task.Definition.Principal.UserId) ||
            !description.StartsWith("FolderNest",StringComparison.Ordinal) ||
            (int)task.Definition.Actions.Count!=1 ||
            !string.Equals((string)task.Definition.Actions.Item(1).Arguments,Arguments,StringComparison.Ordinal))
            throw new IOException("同名任务不属于当前用户和数据目录，未覆盖或删除。请使用原数据位置打开 FolderNest，或检查 Windows 任务计划程序中的同名任务。");
    }
    private string Arguments => $"--background --data \"{Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory))}\"";
    public TaskStatus Deploy(ScheduleSettings settings,string executablePath)
    {
        if(!File.Exists(executablePath))throw new ArgumentException("程序路径无效。");
        if(!string.Equals(settings.TimeZoneId,TimeZoneInfo.Local.Id,StringComparison.Ordinal))
            throw new ArgumentException("任务使用 Windows 本地时区，请重新保存设置。");
        dynamic service=Connect();dynamic root=service.GetFolder("\\");
        var backups=new Dictionary<string,string?>();
        foreach(var name in Names)
        {
            dynamic? task=Find(root,name);if(task is not null)CheckOwnership(task);
            backups[name]=task is null ? null : (string)task.Xml;
        }
        try
        {
            foreach(var name in Names)
            {
                dynamic definition=service.NewTask(0);
                definition.RegistrationInfo.Description="FolderNest：每日及登录后检查启用的监控目录；仅提醒，不自动删除。";
                definition.Principal.UserId=sid;definition.Principal.LogonType=3;definition.Principal.RunLevel=0;
                definition.Settings.Enabled=true;definition.Settings.StartWhenAvailable=true;
                definition.Settings.DisallowStartIfOnBatteries=false;definition.Settings.StopIfGoingOnBatteries=false;
                definition.Settings.ExecutionTimeLimit="PT0S";definition.Settings.MultipleInstances=2;
                if(name.EndsWith("Daily",StringComparison.Ordinal))
                {
                    dynamic trigger=definition.Triggers.Create(2);trigger.DaysInterval=1;
                    trigger.StartBoundary=DateTime.Today.Add(settings.DailyTime.ToTimeSpan()).ToString("yyyy-MM-dd'T'HH:mm:ss",CultureInfo.InvariantCulture);
                }
                else {dynamic trigger=definition.Triggers.Create(9);trigger.UserId=sid;}
                dynamic action=definition.Actions.Create(0);action.Path=executablePath;action.Arguments=Arguments;action.WorkingDirectory=Path.GetDirectoryName(executablePath);
                root.RegisterTaskDefinition(name,definition,6,sid,null,3,null);
                log.Write("task-deploy",$"{name}；{executablePath} {Arguments}；时间={settings.DailyTime}");
            }
            var verification=Query(settings.DailyTime);
            if(verification.Deployment!=TaskDeployment.Installed)throw new IOException("任务写入后核验失败："+verification.Details);
            RemoveLegacyTasks(root);
            return verification;
        }
        catch(Exception ex)
        {
            foreach(var backup in backups)
                try{if(backup.Value is null){if(Find(root,backup.Key) is not null)root.DeleteTask(backup.Key,0);}else root.RegisterTask(backup.Key,backup.Value,6,sid,null,3,null);}
                catch(Exception rollback){log.Write("task-rollback-failed",rollback.Message);}
            log.Write("task-deploy-failed",ex.Message);throw new IOException("任务部署失败，已尝试恢复原任务："+ex.Message,ex);
        }
    }
    public TaskStatus Remove()
    {
        dynamic service=Connect();dynamic root=service.GetFolder("\\");
        foreach(var name in Names){dynamic? task=Find(root,name);if(task is not null)CheckOwnership(task);}
        foreach(var name in Names)if(Find(root,name) is not null){root.DeleteTask(name,0);log.Write("task-remove",name);}
        RemoveLegacyTasks(root);
        return GetStatus();
    }
    private void RemoveLegacyTasks(dynamic root)
    {
        if(namePrefix is not null)return; // Isolated adapter tests never touch production tasks.
        foreach(var name in new[]{"FolderNest 2 "+sid+" Daily","FolderNest 2 "+sid+" Logon"})
        {
            dynamic? task=Find(root,name);if(task is null)continue;
            try
            {
                CheckOwnership(task);
                var backup=Path.Combine(dataDirectory,"legacy-backup","tasks-v2");Directory.CreateDirectory(backup);
                var target=Path.Combine(backup,name+".xml");
                if(!File.Exists(target))File.WriteAllText(target,(string)task.Xml);
                root.DeleteTask(name,0);log.Write("task-migrate",name+" → "+Prefix+"；XML="+target);
            }
            catch(Exception ex){log.Write("task-migrate-skipped",name+"；"+ex.Message);}
        }
    }
}
