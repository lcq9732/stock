using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer;

/// <summary>
/// 分析程序端的"从 GitHub 更新数据"逻辑(见 doc/data-platform-design.md 2026-07-14 变更记录)：
/// 本地没有库→下载最新全量基线(baseline-*.zip)解压成 total.sqlite；之后→只下载比本地新的每日增量
/// (daily-*.zip)按日期合并进 total.sqlite。本地"数据到哪天"直接用库里最新日线日期推断，不另存状态。
/// 发布了更新的全量基线(baseline 日期 > 本地)时会重新下全量(等于重基线)，其余时候只下增量、省流量。
/// </summary>
public class DataSyncService
{
    private static readonly Regex BaselineRe = new(@"^baseline-(\d{8})\.zip$", RegexOptions.IgnoreCase);
    private static readonly Regex DailyRe = new(@"^daily-(\d{8})\.zip$", RegexOptions.IgnoreCase);

    private readonly AnalyzerPaths _paths;
    public DataSyncService(AnalyzerPaths paths) => _paths = paths;

    public async Task UpdateAsync(IProgress<string> status, CancellationToken ct = default)
    {
        var client = new GitHubReleaseClient();
        status.Report("连接 GitHub，读取可用数据…");
        var assets = await client.ListAssetsAsync(ct);
        if (assets.Count == 0) { status.Report("GitHub 上还没有任何数据（需要发布端先用 Fetcher 上传全量基线）。"); return; }

        (DateTime Date, GitHubReleaseClient.Asset Asset)? Parse(GitHubReleaseClient.Asset a, Regex re)
        {
            var m = re.Match(a.Name);
            return m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd", null,
                System.Globalization.DateTimeStyles.None, out var dt) ? (dt, a) : null;
        }
        var baselines = assets.Select(a => Parse(a, BaselineRe)).Where(x => x != null).Select(x => x!.Value).ToList();
        var dailies = assets.Select(a => Parse(a, DailyRe)).Where(x => x != null).Select(x => x!.Value).OrderBy(x => x.Date).ToList();

        DateTime? localThrough = GetLocalThroughDate();
        var latestBaseline = baselines.Count > 0 ? baselines.OrderByDescending(x => x.Date).First() : ((DateTime, GitHubReleaseClient.Asset)?)null;

        int fullDownloads = 0, merged = 0;
        bool needBaseline = localThrough == null || (latestBaseline != null && latestBaseline.Value.Item1 > localThrough.Value);
        if (needBaseline)
        {
            if (latestBaseline == null) { status.Report("本地没有数据、GitHub 上也没有全量基线，无法初始化。"); return; }
            var (bdate, basset) = latestBaseline.Value;
            await DownloadAndInstallBaselineAsync(client, basset, bdate, status, ct);
            localThrough = bdate;
            fullDownloads = 1;
        }

        var toApply = dailies.Where(x => x.Date > localThrough!.Value).ToList();
        for (int i = 0; i < toApply.Count; i++)
        {
            var (ddate, dasset) = toApply[i];
            await DownloadAndMergeDailyAsync(client, dasset, ddate, i + 1, toApply.Count, status, ct);
            localThrough = ddate;
            merged++;
        }

        status.Report($"数据已更新到 {localThrough:yyyy-MM-dd}" +
            (fullDownloads > 0 ? "（下载了全量基线" + (merged > 0 ? $" + {merged} 天增量）" : "）") : merged > 0 ? $"（合并了 {merged} 天增量）" : "（本地已是最新，无需下载）"));
    }

    private async Task DownloadAndInstallBaselineAsync(GitHubReleaseClient client, GitHubReleaseClient.Asset asset, DateTime date, IProgress<string> status, CancellationToken ct)
    {
        var zipPath = Path.Combine(_paths.LocalDir, "_baseline_download.zip");
        var pct = new Progress<double>(p => status.Report($"下载全量基线 {date:yyyy-MM-dd}（{asset.Size / 1e6:F0}MB）… {p * 100:F0}%"));
        await client.DownloadAsync(asset, zipPath, pct, ct);

        status.Report("解压全量基线…");
        SqliteConnection.ClearAllPools(); // 释放对 total.sqlite 的池化句柄，才能覆盖它
        ExtractFirstSqlite(zipPath, _paths.TotalDb);
        File.Delete(zipPath);
    }

    private async Task DownloadAndMergeDailyAsync(GitHubReleaseClient client, GitHubReleaseClient.Asset asset, DateTime date, int idx, int total, IProgress<string> status, CancellationToken ct)
    {
        var zipPath = Path.Combine(_paths.LocalDir, "_daily_download.zip");
        var tmpDb = Path.Combine(_paths.LocalDir, "_daily_download.sqlite");
        var pct = new Progress<double>(p => status.Report($"下载增量 {date:yyyy-MM-dd}（{idx}/{total}）… {p * 100:F0}%"));
        await client.DownloadAsync(asset, zipPath, pct, ct);

        status.Report($"合并增量 {date:yyyy-MM-dd}（{idx}/{total}）…");
        ExtractFirstSqlite(zipPath, tmpDb);
        SqliteConnection.ClearAllPools();
        SqliteMerger.MergeInto(_paths.TotalDb, tmpDb);
        File.Delete(zipPath);
        File.Delete(tmpDb);
    }

    private DateTime? GetLocalThroughDate()
    {
        if (!File.Exists(_paths.TotalDb)) return null;
        try { return new SqliteBarRepository(_paths.TotalDb).GetOverallLatestPeriodStart(Granularity.Day); }
        catch { return null; }
    }

    private static void ExtractFirstSqlite(string zipPath, string destSqlite)
    {
        using var z = ZipFile.OpenRead(zipPath);
        var entry = z.Entries.FirstOrDefault(e => e.Name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"下载的压缩包 {Path.GetFileName(zipPath)} 里没有 .sqlite 文件");
        if (File.Exists(destSqlite)) File.Delete(destSqlite);
        entry.ExtractToFile(destSqlite);
    }
}
