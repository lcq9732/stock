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

    /// <summary>上传数据到 GitHub Releases 用的 PAT token 文件（一行纯文本，放本地、不进 git）——
    /// 只有发布端(自己)需要；用户端分析程序下载公开 release 不需要 token（见 GitHubReleaseClient）。</summary>
    public string GitHubTokenPath => Path.Combine(BaseDir, "local", "github_token.txt");

    public FetchPaths(string? baseDir = null)
    {
        BaseDir = baseDir ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(Path.Combine(BaseDir, "local"));
    }
}
