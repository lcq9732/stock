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

    /// <summary>历次运行的日志归档目录（2026-08-21新增，见 Fetcher 的 MainViewModel.ArchivePreviousLog）——
    /// 上一轮的 fetch.log 在下次启动时挪到这里，不再被直接冲掉。</summary>
    public string LogArchiveDir => Path.Combine(BaseDir, "local", "logs");

    /// <summary>抓取程序自己的界面设置（2026-08-27新增）——目前只有"空闲时自动补财务数据"这个
    /// 开关。跟 manifest.json 分开：那个是数据状态（抓到哪天了），这个是用户偏好。</summary>
    public string SettingsPath => Path.Combine(BaseDir, "fetcher-settings.json");

    public FetchPaths(string? baseDir = null)
    {
        BaseDir = baseDir ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(Path.Combine(BaseDir, "local"));
    }
}
