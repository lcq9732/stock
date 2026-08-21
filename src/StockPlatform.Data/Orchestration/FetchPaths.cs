namespace StockPlatform.Data.Orchestration;

/// <summary>Local folder/file layout for the data-fetcher program, all rooted next to the executable.</summary>
public class FetchPaths
{
    public string BaseDir { get; }
    public string CurrentDb => Path.Combine(BaseDir, "local", "current.sqlite");
    public string ManifestPath => Path.Combine(BaseDir, "local", "manifest.json");

    /// <summary>运行日志落盘位置（2026-07-08新增）——每次程序启动时清空重写，逐行写入并立即
    /// flush，这样即使程序异常退出（崩溃/被强制结束），也能打开这个文件看到崩溃前最后发生了
    /// 什么，不需要依赖还开着的界面窗口。</summary>
    public string LogFilePath => Path.Combine(BaseDir, "local", "fetch.log");

    public FetchPaths(string? baseDir = null)
    {
        BaseDir = baseDir ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(Path.Combine(BaseDir, "local"));
    }
}
