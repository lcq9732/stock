using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 财报"这一轮抓哪些票"的判据（2026-09-19）。
///
/// 这一组钉的是一个真空转了四天的洞：判据只问"本地报告期够不够新"，而法定披露截止日每季往前
/// 走一格，停牌/退市那批票就被判"落后"一次——抓回来的最新期却永远追不上，于是每 20 分钟重抓
/// 一轮、每轮重写 29263 行。实测 13 只票，2026-09-15 起每天 17~24 轮。
///
/// 三层判据依次收口，每层都得有测试守着：
///   ① <b>退市日封顶</b>——退市公司不会披露退市日之后的报告期，"应该有哪一期"按它自己的退市日算；
///   ② <b>已退市且没有K线</b>——回测池根本用不到，问也是白问（原先这类票两道闸门全穿）；
///   ③ <b>问过了就冷却</b>——名单永远有赶不上现实的时候，这是最后一层。
/// 判错的两个方向后果不对等：多抓只是多花请求，**漏抓是静默的**，所以每层的"该抓还是要抓"
/// 也都要有反向用例。
/// </summary>
public class FinancialFetchPlannerTests : IDisposable
{
    private readonly string _dir;
    private readonly FetchPaths _paths;
    private readonly SqliteFinancialRepository _fin;

    /// <summary>一只正常在市的票。</summary>
    private const string Live = "000001";

    public FinancialFetchPlannerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"finPlan_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        _fin = new SqliteFinancialRepository(_paths.CurrentDb);
        _fin.EnsureSchema();
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, [(Live, Live)], "stock");
        // 市场锚：本地判交易日一贯拿上证指数当基准，没有它"一年没成交"会拿 Today 当基准。
        Bar("sh000001", DateTime.Today);
    }

    public void Dispose()
    {
        // 不调 SqliteConnection.ClearAllPools()——那是进程级的，会崩掉别的测试类（踩过）。
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录 */ }
    }

    // ── 造数据 ────────────────────────────────────────────────────

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

    /// <summary>登记一只在市个股（目标集＝在市名册 ∪ 退市名单，不登记的票根本不会被遍历）。</summary>
    private void LiveStock(string code)
        => SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, [(code, code)], "stock");

    /// <summary>登记一只退市股。<paramref name="delistDate"/> 传 null 模拟"名单只给了代码没给日期"。</summary>
    private void Delisted(string code, DateTime? delistDate)
    {
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, [(code, code)], SqliteStockMetaUpsert.TypeDelisted);
        var repo = new SqliteDelistedRepository(_paths.CurrentDb);
        repo.EnsureSchema();
        repo.Upsert([new DelistedStockRow { Code = code, Name = code, Exchange = "szse", DelistDate = delistDate }]);
    }

    /// <summary>这只票本地抓到了 <paramref name="period"/> 这一期（<paramref name="target"/> 是当时冲着哪期去的）。</summary>
    private void Have(string code, DateTime period, DateTime? target = null)
        => _fin.ReplaceByCode(code,
            [new FinancialValue { Code = code, ReportDate = period, Key = FinancialKeys.NetProfitParent, Value = 1 }],
            target);

    private FinancialFetchPlan Plan() => new FinancialFetchPlanner(_paths).Plan();

    /// <summary>今天这个口径下"全市场应该已经能拿到"的最新一期。</summary>
    private static DateTime Expected => FinancialFetchPlanner.LatestExpectedReportPeriod(DateTime.Today);

    // ── ① 退市日封顶 ──────────────────────────────────────────────

    [Fact]
    public void 退市股抓到退市前最后一期就够了()
    {
        // 2026-06-03 退市 ⇒ 最后能有的是一季报（4-30 截止）；半年报它已经不在了，不会披露。
        // 原判据拿全市场的法定截止日比，这只票会被判"落后"，而且抓回来永远追不上 ⇒ 每轮重抓。
        var delist = new DateTime(2026, 6, 3);
        Delisted("000638", delist);
        Bar("000638", delist.AddDays(-1));
        Have("000638", FinancialFetchPlanner.LatestExpectedReportPeriod(delist));

        var plan = Plan();
        Assert.DoesNotContain("000638", plan.AllPending);
    }

    [Fact]
    public void 退市股缺退市前的报告期_照样要抓()
    {
        // 封顶不是"退市股一律不抓"：退市前的报告期缺了就得补，历史财报对回测有用。
        var delist = new DateTime(2026, 6, 3);
        Delisted("000638", delist);
        Bar("000638", delist.AddDays(-1));
        Have("000638", new DateTime(2023, 12, 31));

        Assert.Contains("000638", Plan().AllPending);
    }

    [Fact]
    public void 在市股不受封顶影响_落后就得抓()
    {
        Bar(Live, DateTime.Today);
        Have(Live, Expected.AddYears(-1));

        Assert.Contains(Live, Plan().AllPending);
    }

    // ── ② 已退市且没有K线 ────────────────────────────────────────

    [Fact]
    public void 已退市又没有K线的不抓_退市日为空也拦得住()
    {
        // 生产上这类有 5 只（603388 *ST元成、688086 退市紫晶…）：退市名单只给了代码没给日期，
        // 于是躲过封顶；又一根K线都没有，于是"一年没成交"那条豁免的 TryGetValue 取不到值、
        // 同样不生效。两道闸门全穿，每轮都在名单里。
        Delisted("688086", null);
        Have("688086", new DateTime(2022, 12, 31));

        var plan = Plan();
        Assert.DoesNotContain("688086", plan.AllPending);
        Assert.Equal(1, plan.Unusable);
    }

    [Fact]
    public void 在市股没有K线_照样要抓()
    {
        // 判据只排除"已退市的"。在市新股上市前没有K线，不能因此漏掉它——
        // 而且它哪天有了K线也该自己回到名单里，不需要谁去手工恢复。
        Have(Live, Expected.AddYears(-1));

        Assert.Contains(Live, Plan().AllPending);
    }

    // ── ③ 问过了就冷却 ────────────────────────────────────────────

    [Fact]
    public void 上一轮冲着同一期问过_数据源没有就先别再问()
    {
        // 002731 *ST萃华是活样本：还在交易，把披露日从 04-29 一路改到 08-22，至今没出。
        // 任何名单都证明不了"它这期就是没有"，只能靠"问过了、没有、隔阵子再问"。
        LiveStock("002731");
        Bar("002731", DateTime.Today);
        Have("002731", Expected.AddYears(-1), target: Expected);   // 冲着最新一期问的，只拿回一年前那期

        var plan = Plan();
        Assert.DoesNotContain("002731", plan.AllPending);
        Assert.Equal(1, plan.AskedRecently);
    }

    [Fact]
    public void 目标期一往前走_立刻重新抓()
    {
        // 冷却必须是**自愈**的：公司一旦真披露，EarningsSchedule 让 target 前进，
        // 上一轮记下的 target_date 就比不上了，这只票当轮回到名单。
        LiveStock("002731");
        Bar("002731", DateTime.Today);
        Have("002731", Expected.AddYears(-1), target: Expected.AddYears(-1));

        Assert.Contains("002731", Plan().AllPending);
    }

    [Fact]
    public void 抓到了目标期的票_本来就不该在名单里()
    {
        Bar(Live, DateTime.Today);
        Have(Live, Expected, target: Expected);

        var plan = Plan();
        Assert.Empty(plan.AllPending);
        Assert.Equal(0, plan.AskedRecently);      // 是"已经最新"，不是"冷却中"
    }

    // ── 落库侧：目标期要真的写进去 ────────────────────────────────

    [Fact]
    public void 目标期落库_内容没变时也要更新()
    {
        // 短路（内容没变就不重写 2000+ 行）不能把状态列一起省掉——
        // "什么时候问的、冲着哪期问的"正是下一轮冷却判据的依据。
        Have(Live, Expected.AddYears(-1), target: Expected.AddYears(-1));
        Have(Live, Expected.AddYears(-1), target: Expected);       // 同样的数据，新的目标

        var st = _fin.GetFetchStateByCode()[Live];
        Assert.Equal(Expected, st.TargetDate);
        Assert.Equal(Expected.AddYears(-1), st.ReportDate);
    }

    [Fact]
    public void 数据源一行都没给_也要记下问过了()
    {
        Have(Live, Expected.AddYears(-1), target: Expected.AddYears(-1));
        _fin.MarkAsked(Live, Expected);

        var st = _fin.GetFetchStateByCode()[Live];
        Assert.Equal(Expected, st.TargetDate);
        Assert.Equal(Expected.AddYears(-1), st.ReportDate);        // 数据一个字没动
    }
}
