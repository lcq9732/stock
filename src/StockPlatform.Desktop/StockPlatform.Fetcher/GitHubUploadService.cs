using System.IO;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
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
        // 程序里各个仓库启动时都开过 current.sqlite，Microsoft.Data.Sqlite 的连接池会把文件句柄
        // 留着不放；直接去压缩这个文件会报"文件被另一程序占用"。压缩前先清空连接池，释放句柄
        // （跟 SqliteMerger/DailyIncrementExporter 里"释放文件句柄"用的是同一招）。上传是 IsBusy
        // 互斥的，不会跟抓取同时跑，所以此刻没有正在使用中的活连接，清池后文件即可被读取。
        // 先让 current.sqlite 跑一遍 EnsureSchema（把已废弃的 pct_chg 列删掉），这样打包出去的基线
        // 就是新表结构，用户端下载后跟每日增量列结构一致、能正常合并（见 2026-07-14 变更记录）。
        using (var mig = new SqliteConnection($"Data Source={_paths.CurrentDb}")) { mig.Open(); StockPlatform.Data.Sqlite.SqliteSchema.EnsureSchema(mig); }
        SqliteConnection.ClearAllPools();
        log.Report("正在压缩全量库 current.sqlite（800MB 左右，可能要几分钟）…");
        await Task.Run(() => ZipSingleFile(_paths.CurrentDb, "current.sqlite", zip), ct);

        var client = new GitHubReleaseClient(token);
        log.Report("确认/创建 GitHub 数据 release…");
        var releaseId = await client.EnsureReleaseIdAsync(ct);
        log.Report($"上传 baseline-{date:yyyyMMdd}.zip（{new FileInfo(zip).Length / 1e6:F0}MB）…");
        await client.UploadAsync(releaseId, zip, $"baseline-{date:yyyyMMdd}.zip", null, ct);
        File.Delete(zip);

        // 新基线上传后，删掉被它取代的旧资产：其它 baseline-*，以及日期 ≤ 基线日期的 daily-*
        // （这些天基线已经包含了）。这样 release 上不堆积重复/过期包，也避免老 schema 的旧增量残留。
        var newName = $"baseline-{date:yyyyMMdd}.zip";
        foreach (var a in await client.ListAssetsAsync(ct))
        {
            bool otherBaseline = a.Name.StartsWith("baseline-") && a.Name != newName;
            bool coveredDaily = a.Name.StartsWith("daily-") && TryAssetDate(a.Name, out var dd) && dd <= date.Date;
            if (otherBaseline || coveredDaily)
            {
                log.Report($"清理被基线取代的旧资产 {a.Name}…");
                await client.DeleteAssetAsync(a.Id, ct);
            }
        }
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

    private static bool TryAssetDate(string name, out DateTime date)
    {
        date = default;
        var m = System.Text.RegularExpressions.Regex.Match(name, @"-(\d{8})\.zip$");
        return m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd", null,
            System.Globalization.DateTimeStyles.None, out date);
    }

    private static void ZipSingleFile(string filePath, string entryName, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(filePath, entryName, CompressionLevel.Optimal);
    }
}
