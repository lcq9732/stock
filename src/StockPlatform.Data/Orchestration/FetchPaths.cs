namespace StockPlatform.Data.Orchestration;

/// <summary>Local folder/file layout for the data-fetcher program, all rooted next to the executable.</summary>
public class FetchPaths
{
    public string BaseDir { get; }
    public string CurrentDb => Path.Combine(BaseDir, "local", "current.sqlite");
    public string ManifestPath => Path.Combine(BaseDir, "local", "manifest.json");
    public string OutputDir => Path.Combine(BaseDir, "output");
    public string OutboxDir => Path.Combine(BaseDir, "netdisk_outbox");
    public string InboxDir => Path.Combine(BaseDir, "netdisk_inbox");

    public FetchPaths(string? baseDir = null)
    {
        BaseDir = baseDir ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(Path.Combine(BaseDir, "local"));
        Directory.CreateDirectory(OutputDir);
    }
}
