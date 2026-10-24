using System.Text;
using Microsoft.Data.Sqlite;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 抽取规则升级后重抽旧回购公告（2026-10-23）。
///
/// 10-22 改了抽取规则（以最新「截至X日」那句为准），库里 2069 行进展类公告还是按旧规则抽的，
/// 而正文当初没存，只能按 art_code 重取。这里钉几件事：
///   ① 库里有正文就本地重抽，一个请求都不发；没有才按 art_code 重取；
///   ② 取到新正文才覆盖——取正文抛异常什么都不写、下轮再试；东财说没正文就留旧值、标成已处理；
///   ③ 数值变了的字段进变化清单，没变的不进；
///   ④ 仓储/迁移/清单文件打在真 SQLite 和真文件上。
/// </summary>
public class PlanReextractTests : IDisposable
{
    private const string Title = "关于以集中竞价交易方式回购股份的进展公告";

    /// <summary>永和股份 605020 那份两轮回购的进展公告节选（同 PlanAnnouncementExtractorTests.YongheTwoRounds）。</summary>
    private const string Yonghe = """
        2026 年 7 月 20 日至 2026 年 7 月 31 日期间，公司已累计回购公司股份 186.40 万
        股，累计回购金额 5,999.12 万元（不含印花税、交易佣金等交易费用），前述回购
        方案已实施完成。
        截至 2026 年 8 月 5 日，公司通过上海证券交易所交易系统以集中竞价交易方
        式累计回购公司股份 100.00 万股，占公司当前总股本的比例为 0.20%，回购成交
        的最高价为 34.76 元/股，最低价为 31.29 元/股，累计回购金额为 3,288.79 万元（不
        含印花税、交易佣金等交易费用）。
        """;

    /// <summary>旧规则抽出来的样子：上一轮的 186.4 万股，金额空着，版本 1。</summary>
    private static PlanAnnouncement OldRow(string code = "605020", string? content = null, int day = 6) => new()
    {
        Code = code, Name = "永和股份", AnnounceDate = new DateTime(2026, 8, day), Stage = PlanStage.Progress,
        Title = Title, ArtCode = $"AN202608{day:00}{code}", SourceUrl = "http://static.cninfo.com.cn/x.PDF",
        AsOfDate = new DateTime(2026, 8, 5), CumShares = 1_864_000, PctOfCapital = 0.20,
        PriceHigh = 34.76, PriceLow = 31.29, FetchedAt = new DateTime(2026, 8, 6, 9, 0, 0),
        Content = content, ExtractVersion = 1,
    };

    // ── ① 本地有正文不发请求，没有才按 art_code 取 ─────────────────

    [Fact]
    public async Task 库里有正文_本地重抽_不发请求()
    {
        var repo = new FakeRepo { Stale = [OldRow(content: Yonghe)] };
        var detail = new FakeDetail();
        var log = new List<PlanReextractChange>();

        var r = await new PlanWatchTask(repo, new NoHits(), detail, log.AddRange).RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.Empty(detail.ContentCalls);
        var saved = Assert.Single(repo.Saved);
        Assert.Equal(1_000_000, saved.CumShares);
        Assert.Equal(32_887_900, saved.CumAmount);
        Assert.Equal(PlanAnnouncementExtractor.Version, saved.ExtractVersion);
        Assert.Equal(new DateTime(2026, 8, 6, 9, 0, 0), saved.FetchedAt);   // 本地重抽不算重新取过
        Assert.All(log, c => Assert.Equal("本地", c.Source));
    }

    [Fact]
    public async Task 库里没正文_按art_code重取_正文一并存下()
    {
        var old = OldRow();
        var repo = new FakeRepo { Stale = [old] };
        var detail = new FakeDetail { Content = Yonghe };

        await new PlanWatchTask(repo, new NoHits(), detail).RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal([old.ArtCode], detail.ContentCalls);
        Assert.Equal(0, detail.MatchCalls);                  // 不走"最近 100 条里按标题匹配"那条路
        var saved = Assert.Single(repo.Saved);
        Assert.Equal(1_000_000, saved.CumShares);
        Assert.Equal(Yonghe, saved.Content);
        Assert.Equal(old.ArtCode, saved.ArtCode);
        Assert.Equal(old.SourceUrl, saved.SourceUrl);
    }

    // ── ② 取到新正文才覆盖 ─────────────────────────────────────────

    [Fact]
    public async Task 取正文抛异常_什么都不写_下轮再试()
    {
        var repo = new FakeRepo { Stale = [OldRow()] };
        var detail = new FakeDetail { Throw = true };

        var r = await new PlanWatchTask(repo, new NoHits(), detail).RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Empty(repo.Saved);                            // 旧值、旧版本号原样留在库里
        Assert.Contains("1 条取不到正文", r.Progress);
    }

    [Fact]
    public async Task 东财说没有正文_旧值不动_只把版本标成已处理()
    {
        var repo = new FakeRepo { Stale = [OldRow()] };
        var detail = new FakeDetail { Content = null };
        var log = new List<PlanReextractChange>();

        await new PlanWatchTask(repo, new NoHits(), detail, log.AddRange).RunAsync(new TaskRunArgs(), CancellationToken.None);

        var saved = Assert.Single(repo.Saved);
        Assert.Equal(1_864_000, saved.CumShares);            // 没被清成 null
        Assert.Equal(PlanAnnouncementExtractor.Version, saved.ExtractVersion);
        Assert.Empty(log);
    }

    [Fact]
    public async Task 重取连续十条抛异常_收工记跳过()
    {
        var repo = new FakeRepo { Stale = Enumerable.Range(0, 50).Select(i => OldRow($"{600000 + i}")).ToList() };
        var detail = new FakeDetail { Throw = true };

        var r = await new PlanWatchTask(repo, new NoHits(), detail).RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(PlanWatchTask.AbortAfterConsecutiveFailures, detail.ContentCalls.Count);
        Assert.NotNull(r.SkippedReason);
    }

    // ── ③ 变化清单 ────────────────────────────────────────────────

    [Fact]
    public async Task 变化清单只记变了的字段_没变的行不进()
    {
        // 第二行的旧值本来就跟新规则一致
        var same = PlanAnnouncementExtractor.Extract("605021", "永和股份", Title, new DateTime(2026, 8, 7), Yonghe, "AN2", "");
        same.ExtractVersion = 1;
        var repo = new FakeRepo { Stale = [OldRow(content: Yonghe), same] };
        var log = new List<PlanReextractChange>();

        var r = await new PlanWatchTask(repo, new NoHits(), new FakeDetail(), log.AddRange).RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(2, repo.Saved.Count);
        Assert.All(log, c => Assert.Equal("605020", c.Code));
        Assert.Equal(
            [("cum_shares", "1864000", "1000000"), ("cum_amount", "", "32887900")],
            log.Select(c => (c.Field, c.Old, c.New)).ToList());
        Assert.Contains("重抽 2 条（1 条数值有变化", r.Progress);
    }

    [Fact]
    public void 比较字段_空值写成空串_数值去掉浮点尾巴()
    {
        var a = new PlanAnnouncement { CumShares = 100, PriceHigh = 9.5 };
        var b = new PlanAnnouncement { CumShares = 100, PriceHigh = 11.35, CumAmount = 1.5e8, AsOfDate = new DateTime(2026, 9, 30) };

        Assert.Equal(
            [("as_of_date", "", "2026-09-30"), ("cum_amount", "", "150000000"), ("price_high", "9.5", "11.35")],
            PlanAnnouncementExtractor.ChangedValues(a, b));
    }

    // ── ④ 真 SQLite / 真文件 ──────────────────────────────────────

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"planReextract_{Guid.NewGuid():N}");
    private string Db => Path.Combine(_dir, "t.sqlite");

    public void Dispose()
    {
        // 不调 SqliteConnection.ClearAllPools()——那是进程级的，会崩掉别的测试类
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录 */ }
    }

    private SqlitePlanAnnouncementRepository NewRepo()
    {
        Directory.CreateDirectory(_dir);
        var repo = new SqlitePlanAnnouncementRepository(Db);
        repo.EnsureSchema();
        return repo;
    }

    private void Exec(string sql)
    {
        using var c = new SqliteConnection($"Data Source={Db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void 仓储_正文和版本号存得进读得出_空壳写入不冲掉已存正文()
    {
        var repo = NewRepo();
        var row = OldRow(content: Yonghe);
        row.ExtractVersion = 2;
        repo.Upsert([row]);

        // 同一键再写一次但没带正文（取正文失败留的那种），正文要留着
        var again = OldRow();
        again.ExtractVersion = 2;
        repo.Upsert([again]);

        var got = Assert.Single(repo.GetByCode("605020", PlanKind.Buyback));
        Assert.Equal(Yonghe, got.Content);
        Assert.Equal(2, got.ExtractVersion);
    }

    [Fact]
    public void 仓储_待重抽_有正文的全给_没正文的按新到旧限量_空壳和已是新版的不给()
    {
        var repo = NewRepo();
        repo.Upsert(
        [
            OldRow("600001", content: Yonghe, day: 1),                       // 有正文：不占限额
            OldRow("600002", day: 2), OldRow("600003", day: 3), OldRow("600004", day: 4),
            new PlanAnnouncement { Code = "600005", AnnounceDate = new DateTime(2026, 8, 5), Stage = PlanStage.Progress, Title = Title, ArtCode = "" },  // 空壳
        ]);
        var v2 = OldRow("600006", day: 6);
        v2.ExtractVersion = 2;
        repo.Upsert([v2]);
        // 老库的行：extract_ver 是 NULL，按 1 算
        Exec("INSERT INTO PlanAnnouncement (code, kind, announce_date, stage, title, art_code) VALUES ('600007','回购','2026-08-07','进展','t','AN7');");

        var stale = repo.GetStaleExtraction(PlanKind.Buyback, 2, fetchLimit: 2);

        Assert.Equal(["600001", "600007", "600004"], stale.Select(x => x.Code).ToList());
        Assert.Equal(5, repo.CountStaleExtraction(PlanKind.Buyback, 2));     // 600001..004 + 600007
    }

    [Fact]
    public void 迁移_只把方案行和空壳记成第二版_其余留着重抽()
    {
        var repo = NewRepo();
        Exec($"DELETE FROM DataMigration WHERE id = '{SqliteDataMigrations.PlanExtractV2Baseline}';");
        Exec("""
            INSERT INTO PlanAnnouncement (code, kind, announce_date, stage, title, art_code) VALUES
                ('600001','回购','2026-08-01','方案','方案公告','AN1'),
                ('600002','回购','2026-08-01','进展','进展公告','AN2'),
                ('600003','回购','2026-08-01','进展','进展公告',''),
                ('600004','回购','2026-08-01','首次回购','首次回购公告','AN4');
            """);

        repo.EnsureSchema();   // 启动时跑迁移

        Assert.Equal(["600004", "600002"],
            repo.GetStaleExtraction(PlanKind.Buyback, 2, 10).Select(x => x.Code).OrderDescending().ToList());
    }

    [Fact]
    public void 变化清单_跨轮追加_表头和BOM只有一份()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "sub", "回购公告重抽变化.csv");
        var c = new PlanReextractChange("605020", "永和,股份", new DateTime(2026, 8, 6), "进展", "cum_shares", "1864000", "1000000", "重取");

        PlanReextractChangeLog.Append(path, [c]);
        PlanReextractChangeLog.Append(path, [c, c]);
        PlanReextractChangeLog.Append(path, []);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        // 数字节，不用 Assert.DoesNotContain：那个按区域规则比，零宽的 BOM 字符在任何位置都"找得到"
        Assert.Equal(1, Enumerable.Range(0, bytes.Length - 2).Count(i => bytes[i] == 0xEF && bytes[i + 1] == 0xBB && bytes[i + 2] == 0xBF));
        var text = Encoding.UTF8.GetString(bytes[3..]);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);                       // 表头 + 3 行
        Assert.StartsWith("记录时间,", lines[0]);
        Assert.Contains(",605020,\"永和,股份\",2026-08-06,进展,cum_shares,1864000,1000000,重取", lines[1]);
    }

    // ─────────────────── 脚手架 ───────────────────

    private sealed class FakeRepo : IPlanAnnouncementRepository
    {
        public List<PlanAnnouncement> Stale = [];
        public readonly List<PlanAnnouncement> Saved = [];

        public void EnsureSchema() { }
        public int Upsert(IEnumerable<PlanAnnouncement> items) { var l = items.ToList(); Saved.AddRange(l); return l.Count; }
        public List<PlanAnnouncement> GetByCode(string code, string kind) => [];
        public List<PlanAnnouncement> GetMissingDetail(string kind, DateTime since, int limit) => [];
        public Dictionary<string, PlanAnnouncement> GetOpenPlans(string kind) => [];
        public DateTime? GetLatestAnnounceDate(string kind) => DateTime.Today;
        public HashSet<(string Code, DateTime AnnounceDate, string Title)> GetFetchedKeys(string kind, DateTime since) => [];
        public (int Rows, int Stocks, int OpenPlans) GetCounts(string kind) => (Saved.Count, 1, 0);
        public List<PlanAnnouncement> GetStaleExtraction(string kind, int version, int fetchLimit) => Stale;
        public int CountStaleExtraction(string kind, int version) => 0;
    }

    private sealed class NoHits : IAnnouncementSearchProvider
    {
        public Task<List<AnnouncementSearchHit>> SearchAsync(
            string keyword, DateOnly start, DateOnly end, IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new List<AnnouncementSearchHit>());
    }

    private sealed class FakeDetail : IAnnouncementDetailFetcher
    {
        public event Action<string>? OnStatus { add { } remove { } }
        public string? Content = Yonghe;
        public bool Throw;
        public int MatchCalls;
        public readonly List<string> ContentCalls = [];

        public Task<(string ArtCode, string Content)?> FetchDetailAsync(
            string code, string title, DateOnly approxDate, CancellationToken ct = default)
        {
            MatchCalls++;
            return Task.FromResult<(string, string)?>(null);
        }

        public Task<string?> FetchContentAsync(string artCode, CancellationToken ct = default)
        {
            ContentCalls.Add(artCode);
            if (Throw) throw new HttpRequestException("假的：东财限流");
            return Task.FromResult(Content);
        }
    }
}
