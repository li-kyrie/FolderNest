namespace FolderNest.Infrastructure;

public sealed record PortablePaths(string DataDirectory, bool IsFallback)
{
    public string DatabasePath => Path.Combine(DataDirectory, "foldernest.db");
    public string LogPath => Path.Combine(DataDirectory, "logs", "foldernest.log");
    public static PortablePaths Resolve(string applicationDirectory, string? overrideDirectory = null)
    {
        if(overrideDirectory is not null) { var specified=Path.GetFullPath(overrideDirectory); Probe(specified); return new(specified,false); }
        // Reuse an existing preview database when upgrading a portable installation.
        var preview=Path.Combine(applicationDirectory,"data-v2");
        var preferred = File.Exists(Path.Combine(preview,"foldernest.db")) ? preview : Path.Combine(applicationDirectory, "data");
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
