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

    /// <summary>自选股列表——是分析程序自己的状态（用户手动挑选的、跨方法通用），不是从
    /// Fetcher 那边拷贝来的共享只读数据，所以特意放在 LocalDir 外面、跟 TotalDb 分开，避免
    /// 以后被误当成"可以直接删了重新拷贝"的那类文件（见 JsonWatchlistStore）。</summary>
    public string WatchlistPath => Path.Combine(BaseDir, "watchlist.json");

    public AnalyzerPaths(string? baseDir = null)
    {
        BaseDir = baseDir ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(LocalDir);
    }
}
