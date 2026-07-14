using System.IO;
using System.IO.Compression;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;

namespace StockPlatform.Fetcher;

/// <summary>
/// 发布端(Fetcher)把数据上传到 GitHub Releases(见 doc/data-platform-design.md 2026-07-14 变更记录)：
/// 全量基线 `baseline-YYYYMMDD.zip`(偶尔重传一次) + 每日增量 `daily-YYYYMMDD.zip`(每天抓完传一次)。
/// token 从 <see cref="FetchPaths.GitHubTokenPath"/> 读；用户端下载不需要 token。
/// </summary>
public class GitHubUploadService
{
    private readonly FetchPaths _paths;
    public GitHubUploadService(FetchPaths paths) => _paths = paths;

    public string? ReadToken()
    {
        try
        {
            if (!File.Exists(_paths.GitHubTokenPath)) return null;
            var t = File.ReadAllText(_paths.GitHubTokenPath).Trim();
            return string.IsNullOrWhiteSpace(t) ? null : t;
        }
        catch { return null; }
    }

    /// <summary>压缩整个 current.sqlite 上传为 baseline-{date}.zip（替换同名旧资产）。</summary>
    public async Task UploadBaselineAsync(string token, DateTime date, IProgress<string> log, CancellationToken ct = default)
    {
        var zip = Path.Combine(_paths.BaseDir, "local", $"baseline-{date:yyyyMMdd}.zip");
        log.Report("正在压缩全量库 current.sqlite（856MB 左右，可能要几分钟）…");
        await Task.Run(() => ZipSingleFile(_paths.CurrentDb, "current.sqlite", zip), ct);

        var client = new GitHubReleaseClient(token);
        log.Report("确认/创建 GitHub 数据 release…");
        var releaseId = await client.EnsureReleaseIdAsync(ct);
        log.Report($"上传 baseline-{date:yyyyMMdd}.zip（{new FileInfo(zip).Length / 1e6:F0}MB）…");
        await client.UploadAsync(releaseId, zip, $"baseline-{date:yyyyMMdd}.zip", null, ct);
        File.Delete(zip);
        log.Report($"全量基线 baseline-{date:yyyyMMdd}.zip 上传完成。");
    }

    /// <summary>导出并上传某天的增量 daily-{date}.zip（替换同名旧资产）。</summary>
    public async Task UploadDailyAsync(string token, DateTime date, IProgress<string> log, CancellationToken ct = default)
    {
        var tmpDb = Path.Combine(_paths.BaseDir, "local", "_daily_export.sqlite");
        var zip = Path.Combine(_paths.BaseDir, "local", $"daily-{date:yyyyMMdd}.zip");
        log.Report($"正在生成 {date:yyyy-MM-dd} 当天增量…");
        await Task.Run(() =>
        {
            DailyIncrementExporter.Export(_paths.CurrentDb, date, tmpDb);
            ZipSingleFile(tmpDb, "daily.sqlite", zip);
        }, ct);

        var client = new GitHubReleaseClient(token);
        var releaseId = await client.EnsureReleaseIdAsync(ct);
        log.Report($"上传 daily-{date:yyyyMMdd}.zip（{new FileInfo(zip).Length / 1e3:F0}KB）…");
        await client.UploadAsync(releaseId, zip, $"daily-{date:yyyyMMdd}.zip", null, ct);
        File.Delete(tmpDb);
        File.Delete(zip);
        log.Report($"当天增量 daily-{date:yyyyMMdd}.zip 上传完成。");
    }

    private static void ZipSingleFile(string filePath, string entryName, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(filePath, entryName, CompressionLevel.Optimal);
    }
}
