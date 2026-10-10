using System.Diagnostics;
using System.Text.RegularExpressions;
using FolderNest.Core;
using TaskStatus = FolderNest.Core.TaskStatus;

namespace FolderNest.Infrastructure;

// Legacy tasks are observed only. The new scheduler adapter will use dedicated V2 task names.
public sealed class WindowsTaskStatusReader
{
    public TaskStatus GetLegacyStatus()
    {
        try
        {
            var names = ReadTaskNames();
            var daily = names.Contains(@"\FolderNest Daily"); var logon = names.Contains(@"\FolderNest Logon");
            return new(daily && logon ? TaskDeployment.Installed : daily || logon ? TaskDeployment.Partial :
                TaskDeployment.NotInstalled, $"旧版每日任务：{(daily ? "存在" : "未部署")}；旧版登录任务：{(logon ? "存在" : "未部署")}");
        }
        catch (Exception ex) { return new(TaskDeployment.Unknown, ex.Message); }
    }
    private static HashSet<string> ReadTaskNames()
    {
        var start = new ProcessStartInfo("schtasks.exe") { CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach(var arg in new[]{"/Query", "/FO", "CSV", "/NH"}) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(5000)) { process.Kill(); throw new TimeoutException("查询定时任务超时。"); }
        Task.WaitAll(stdout, stderr);
        if(process.ExitCode != 0 || !string.IsNullOrWhiteSpace(stderr.Result))
            throw new IOException("无法完整查询定时任务：" + stderr.Result);
        // CSV's first field is the fully-qualified task name regardless of Windows UI language.
        return stdout.Result.Split('\n').Select(line => Regex.Match(line.Trim(), "^\"((?:[^\"]|\"\")*)\","))
            .Where(match => match.Success).Select(match => match.Groups[1].Value.Replace("\"\"", "\""))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
