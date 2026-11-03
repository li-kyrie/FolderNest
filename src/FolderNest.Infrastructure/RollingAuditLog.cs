using System.Text;
using FolderNest.Core;

namespace FolderNest.Infrastructure;

public sealed class RollingAuditLog(string path, Func<int>? limitMiB = null) : IAuditLog
{
    private readonly object gate = new();
    public void Write(string operation, string message)
    {
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            message = message.Replace('\r', ' ').Replace('\n', ' ');
            if(message.Length > 8192) message = message[..8192] + "…";
            var line = $"{DateTimeOffset.Now:O}\t{operation}\t{message}{Environment.NewLine}";
            var bytes = Encoding.UTF8.GetByteCount(line);
            var fileLimit = Math.Clamp(limitMiB?.Invoke() ?? 10, 2, 1024) * 1024L * 1024 / 2;
            if(File.Exists(path + ".1") && new FileInfo(path + ".1").Length > fileLimit) File.Delete(path + ".1");
            if (File.Exists(path) && new FileInfo(path).Length + bytes > fileLimit)
                File.Move(path, path + ".1", true);
            if(File.Exists(path + ".1") && new FileInfo(path + ".1").Length > fileLimit) File.Delete(path + ".1");
            File.AppendAllText(path, line, new UTF8Encoding(false));
        }
    }
}
