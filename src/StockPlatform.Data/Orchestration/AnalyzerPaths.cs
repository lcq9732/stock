namespace StockPlatform.Data.Orchestration;

/// <summary>
/// Local folder/file layout for the analysis program, all rooted next to the executable. No
/// netdisk sync — automating that turned out not to be workable (see
/// doc/data-platform-design.md section 6.5.1), so the Analyzer just reads whatever database sits
/// at <see cref="TotalDb"/>; the user is responsible for manually copying the Fetcher's output
/// there with this exact name.
/// </summary>
public class AnalyzerPaths
{
    public string BaseDir { get; }
    public string LocalDir => Path.Combine(BaseDir, "local");
    public string TotalDb => Path.Combine(LocalDir, "total.sqlite");

    public AnalyzerPaths(string? baseDir = null)
    {
        BaseDir = baseDir ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(LocalDir);
    }
}
