namespace FolderNest.Infrastructure;

public sealed record PortablePaths(string DataDirectory, bool IsFallback)
{
    public string DatabasePath => Path.Combine(DataDirectory, "foldernest.db");
    public string LogPath => Path.Combine(DataDirectory, "logs", "foldernest.log");
    public static PortablePaths Resolve(string applicationDirectory)
    {
        var preferred = Path.Combine(applicationDirectory, "data-v2");
        try { Probe(preferred); return new(preferred, false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FolderNest", "v2");
            Probe(fallback); return new(fallback, true);
        }
    }
    private static void Probe(string directory)
    {
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, Guid.NewGuid().ToString() + ".probe");
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1, FileOptions.DeleteOnClose);
    }
}
