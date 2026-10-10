using System.Text;
using FolderNest.Core;

namespace FolderNest.Infrastructure;

public sealed class RollingAuditLog(string path) : IAuditLog
{
    private const long FileLimit = 5 * 1024 * 1024; // current + one archive <= 10 MiB
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
            if (File.Exists(path) && new FileInfo(path).Length + bytes > FileLimit)
                File.Move(path, path + ".1", true);
            File.AppendAllText(path, line, new UTF8Encoding(false));
        }
    }
}
