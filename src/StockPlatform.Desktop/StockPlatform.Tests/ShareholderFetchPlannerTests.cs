using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 股东数据"这一轮抓哪些票"的判据（2026-09-18，见 doc/shareholder-task-design.md §2）。
///
/// 这一套判据是这次迁移的核心价值：它让【拉取股东数据】从"每轮全量 11130 个请求"变成
/// "半年报抓齐之后、三季报披露之前**一个请求都不发**"。判错的两个方向后果完全不对等——
/// 多抓只是多花请求，**漏抓是静默的**，所以每条分支都要有测试钉住。
/// </summary>
public class ShareholderFetchPlannerTests : IDisposable
{
    private readonly string _dir;
    private readonly FetchPaths _paths;
    private readonly SqliteShareholderRepository _sh;

    /// <summary>三只在市个股。</summary>
    private static readonly string[] Codes = ["000001", "600000", "600519"];

    /// <summary>一只退市股。</summary>
    private const string Delisted = "600001";

    public ShareholderFetchPlannerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"shPlan_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        _sh = new SqliteShareholderRepository(_paths.CurrentDb);
        _sh.EnsureSchema();
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, Codes.Select(c => (c, c)).ToList(), "stock");
        // 一只退市股（2026-09-18 纳入名单）。它没有K线，所以"停牌豁免"那条对它天然成立。
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, [(Delisted, Delisted)],
                                     SqliteStockMetaUpsert.TypeDelisted);
        // 市场锚：上证指数的最新交易日。没有它，停牌判据会拿 DateTime.Today 当基准。
        Bar("sh000001", DateTime.Today);
    }

    public void Dispose()
    {
        // 不调 SqliteConnection.ClearAllPools()——那是进程级的，会崩掉别的测试类（踩过）。
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录 */ }
    }

    // ── 造数据 ────────────────────────────────────────────────────

    /// <summary>塞一条股东户数：这只票"抓到了 period 这一期，抓取时刻是 fetchedAt"。</summary>
    private void Holder(string code, DateTime period, DateTime fetchedAt)
        => _sh.ReplaceByCode(code, new ShareholderData
        {
            Counts =
            [
                new ShareholderCountRow
                {
                    Code = code, ReportDate = period, HolderNum = 10000, AvgShares = 1000,
                    FetchedAt = fetchedAt,
                },
            ],
        });

    /// <summary>塞一条"这只票 period 那期已于 actual 实际披露"。</summary>
    private void Disclosed(string code, DateTime period, DateTime actual)
    {
        var repo = new SqliteEarningsScheduleRepository(_paths.CurrentDb);
        repo.EnsureSchema();
        repo.Upsert([new EarningsScheduleRow(code, period, null, null, null, null, actual)]);
    }

    private void Bar(string code, DateTime day)
    {
        var repo = new SqliteBarRepository(_paths.CurrentDb);
        repo.EnsureSchema();
        repo.InsertOrRefreshUnconfirmed([new Bar
        {
            Code = code, Granularity = Granularity.Day, PeriodStart = day,
            Open = 10, Close = 10, High = 10, Low = 10, Volume = 1, Amount = 10,
            FetchedAt = DateTime.Now,
        }]);
    }

    private ShareholderFetchPlan Plan(bool all = false) => new ShareholderFetchPlanner(_paths).Plan(all);

    // ── 判据 ──────────────────────────────────────────────────────

    [Fact]
    public void 从没抓过的要抓_退市股也在名单里()
    {
        var plan = Plan();
        Assert.Equal(4, plan.Total);                                  // 3 只在市 + 1 只退市
        Assert.Equal(["000001", "600000", "600001", "600519"],
                     plan.Pending.OrderBy(c => c, StringComparer.Ordinal));
        Assert.Equal(4, plan.NewPeriod);
        // **退市股"从没抓过"必须立刻抓**：它没有K线，要是让停牌豁免把它挡掉，
        // 纳入退市股这件事第一轮就不会发生（库里 337 只当时只有 5 只有数据）。
        Assert.Equal(1, plan.DelistedPending);
    }

    [Fact]
    public void 已经是最新报告期的不抓()
    {
        // 三只都抓到了"法定截止口径的最新一期"，且都是刚抓的 ⇒ 一个请求都不该发。
        var expected = Plan().ExpectedPeriod;
        foreach (var c in Codes) Holder(c, expected, DateTime.Now);
        Holder(Delisted, expected, DateTime.Now);

        Assert.Empty(Plan().Pending);
    }

    [Fact]
    public void 报告期落后的要抓()
    {
        var expected = Plan().ExpectedPeriod;
        foreach (var c in Codes) Holder(c, expected, DateTime.Now);
        Holder(Delisted, expected, DateTime.Now);
        Holder("600000", expected.AddMonths(-3), DateTime.Now);    // 落后一期

        var plan = Plan();
        Assert.Equal(["600000"], plan.Pending);
        Assert.Equal(1, plan.NewPeriod);
    }

    [Fact]
    public void 按这只票的实际披露日判_没披露的那期不算落后()
    {
        // 这是跟"法定截止日一刀切"的分界：某票只披露到上一期，那它就不算落后——
        // 去抓也拿不到新东西。66% 的公司挤在截止日前五天披露，一刀切会让几千只同时涌进队列。
        var expected = Plan().ExpectedPeriod;
        var prev = expected.AddMonths(-3);
        foreach (var c in Codes) Holder(c, expected, DateTime.Now);
        Holder(Delisted, expected, DateTime.Now);
        Holder("600519", prev, DateTime.Now);
        Disclosed("600519", prev, DateTime.Today.AddDays(-10));     // 它只披露到上一期

        Assert.Empty(Plan().Pending);
    }

    [Fact]
    public void 太久没抓的整只重刷_哪怕报告期已经最新()
    {
        // 时间兜底：那 4% 不定期的股东名单变动公告只能靠它捞回来。
        var expected = Plan().ExpectedPeriod;
        foreach (var c in Codes) Holder(c, expected, DateTime.Now);
        Holder(Delisted, expected, DateTime.Now);
        Holder("000001", expected, DateTime.Now.AddDays(-ShareholderFetchPlanner.StaleDays - 1));

        var plan = Plan();
        Assert.Equal(["000001"], plan.Pending);
        Assert.Equal(1, plan.TimeStale);
        Assert.Equal(0, plan.NewPeriod);
    }

    [Fact]
    public void 一年多没成交的跳过_但会被报出来()
    {
        var expected = Plan().ExpectedPeriod;
        foreach (var c in Codes) Holder(c, expected, DateTime.Now);
        Holder(Delisted, expected, DateTime.Now);
        Holder("600000", expected.AddMonths(-3), DateTime.Now);     // 报告期落后
        Bar("600000", DateTime.Today.AddDays(-400));                // 但一年多没成交

        var plan = Plan();
        Assert.Empty(plan.Pending);
        Assert.Equal(1, plan.Dormant);
    }

    [Fact]
    public void 停牌豁免不管时间兜底那条()
    {
        // 豁免只针对"报告期落后"。太久没抓是"整只重刷一遍"，跟还交不交易无关。
        var expected = Plan().ExpectedPeriod;
        foreach (var c in Codes) Holder(c, expected, DateTime.Now);
        Holder(Delisted, expected, DateTime.Now);
        Holder("600000", expected, DateTime.Now.AddDays(-ShareholderFetchPlanner.StaleDays - 1));
        Bar("600000", DateTime.Today.AddDays(-400));

        Assert.Equal(["600000"], Plan().Pending);
    }

    [Fact]
    public void 整段回补无视判据全抓()
    {
        var expected = Plan().ExpectedPeriod;
        foreach (var c in Codes) Holder(c, expected, DateTime.Now);
        Holder(Delisted, expected, DateTime.Now);
        Bar("600000", DateTime.Today.AddDays(-400));                // 连长停的也要

        var plan = Plan(all: true);
        Assert.Equal(4, plan.Pending.Count);                        // 含退市股
    }

    [Fact]
    public void 自选的票排最前()
    {
        File.WriteAllText(Path.Combine(_paths.BaseDir, "watchlist.json"),
                          """[{"Code":"600519"}]""");

        Assert.Equal("600519", Plan().Pending[0]);
    }

    [Fact]
    public void 退市股走更长的时间兜底_一百天不重刷()
    {
        // 退市股的股东数据基本是静态历史，没必要跟在市股同频重刷（337 只×2 请求）。
        var expected = Plan().ExpectedPeriod;
        foreach (var c in Codes) Holder(c, expected, DateTime.Now);
        // 在市股和退市股都是"120 天前抓的、报告期已最新"
        Holder("000001", expected, DateTime.Now.AddDays(-120));
        Holder(Delisted, expected, DateTime.Now.AddDays(-120));

        // 在市股过了 100 天要重刷，退市股要等 365 天
        Assert.Equal(["000001"], Plan().Pending);
    }

    [Fact]
    public void 退市股过了一年也要重刷()
    {
        var expected = Plan().ExpectedPeriod;
        foreach (var c in Codes) Holder(c, expected, DateTime.Now);
        Holder(Delisted, expected, DateTime.Now.AddDays(-ShareholderFetchPlanner.DelistedStaleDays - 1));

        var plan = Plan();
        Assert.Equal([Delisted], plan.Pending);
        Assert.Equal(1, plan.TimeStale);
        Assert.Equal(1, plan.DelistedPending);
    }

    [Fact]
    public void 退市股抓过之后不再因为报告期落后而重抓()
    {
        // 退市公司的法定报告期早就停了，"报告期落后"对它们永远成立。**不能只靠停牌豁免**：
        // 实测 337 只里 18 只一根日K都没有（豁免压根不触发）、12 只今年刚退市（K线还在一年内），
        // 那 30 只会每轮白抓 60 个请求、永远抓不完。所以退市股只走 365 天那一档。
        var expected = Plan().ExpectedPeriod;
        foreach (var c in Codes) Holder(c, expected, DateTime.Now);
        Holder(Delisted, expected.AddYears(-3), DateTime.Now);      // 报告期停在三年前、刚抓过

        Assert.Empty(Plan().Pending);
    }

    [Fact]
    public void 退市股没有日K也不会每轮重抓()
    {
        // 这一条钉的是上面那 18 只：它们在 Bar 表里一行都没有，
        // 所以 lastBarByCode.TryGetValue 返回 false、停牌豁免不触发。
        var expected = Plan().ExpectedPeriod;
        foreach (var c in Codes) Holder(c, expected, DateTime.Now);
        Holder(Delisted, expected.AddYears(-5), DateTime.Now);

        Assert.DoesNotContain(Delisted, Plan().Pending);
    }

    [Fact]
    public void 报告期取户数表不取十大股东表()
    {
        // 两表最新期不一致的有 7.9%，方向是十大股东更"新"（不定期的股东名单变动公告）。
        // 拿十大股东的 max 去比会把这只票判成"已经很新"⇒ 漏抓。
        var expected = Plan().ExpectedPeriod;
        foreach (var c in Codes) Holder(c, expected, DateTime.Now);
        Holder(Delisted, expected, DateTime.Now);
        _sh.ReplaceByCode("600000", new ShareholderData
        {
            // 户数停在上一期
            Counts =
            [
                new ShareholderCountRow
                {
                    Code = "600000", ReportDate = expected.AddMonths(-3),
                    HolderNum = 1, AvgShares = 1, FetchedAt = DateTime.Now,
                },
            ],
            // 十大股东却有一条比 expected 还新的不定期期次
            TopHolders =
            [
                new TopShareholderRow
                {
                    Code = "600000", ReportDate = expected.AddDays(20),
                    Kind = TopShareholderRow.KindTotal, Rank = 1,
                    HolderName = "某某", Shares = 100, Ratio = 1, FetchedAt = DateTime.Now,
                },
            ],
        });

        Assert.Equal(["600000"], Plan().Pending);
    }
}
