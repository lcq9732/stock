using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【当日完整性体检】的判据（2026-09-16，第 13 步从"只查个股K线"扩写成"当天该有的都查"）。
///
/// ════ 这一组守的是什么 ════
/// 这一步是全库**唯一**会发现"今天静默漏抓"的地方，而它漏掉的那一类，界面上一切正常：
/// 失败名单是空的、状态列是绿的、日志里一句异常都没有。所以每加一类被查的对象，
/// 都得有一条测试钉住它——否则某天那类数据整批没抓到，仍然没人会知道。
///
/// 三段各自的回归点：
///   ① K线：**ETF 和指数曾经完全不在体检范围内**（判据是"六位纯数字代码"，而它们带 sh/sz 前缀）；
///   ② 日更表：只查一天的判据必须跟全库体检那套**给出同样的结论**，不然两处迟早漂移；
///   ③ 覆盖式快照：这类表只留最新一份，能判的只有"停在哪天"。
/// </summary>
public class DayCompletenessTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteBarRepository _bars;
    private readonly SqliteDailyTableAuditor _auditor;

    private const string Anchor = MarketIndexCatalog.ShanghaiCompositeSymbol;

    /// <summary>
    /// 12 个交易日。为什么不能像别的测试那样只造 5 天：**市场判据的基线是从数据自己算的**——
    /// 出现频率 ≥90% 的交易所才算"这张表每天都该有"（北交所是后来才有的，写死"沪深"会让
    /// 2021 年以前全部误报）。5 天里缺 1 天就是 80%，深市直接被判成"本来就不是核心市场"，
    /// 于是那条判据整个失效。12 天里缺 1 天是 92%，才测得出来。
    /// </summary>
    private static readonly DateTime[] Cal =
    [
        new(2026, 8, 10), new(2026, 8, 11), new(2026, 8, 12), new(2026, 8, 13),
        new(2026, 8, 14), new(2026, 8, 17), new(2026, 8, 18), new(2026, 8, 19),
        new(2026, 8, 20), new(2026, 8, 21), new(2026, 8, 24), new(2026, 8, 25),
    ];

    private static DateTime Latest => Cal[^1];
    private static DateTime Previous => Cal[^2];

    private static readonly SqliteDailyTableAuditor.Spec Margin =
        SqliteDailyTableAuditor.DailyTables.First(t => t.Table == "MarginDetail");

    public DayCompletenessTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"daycomp_{Guid.NewGuid():N}.sqlite");
        _bars = new SqliteBarRepository(_dbPath);
        _bars.EnsureSchema();
        // 交易日锚：上证指数本身也是一条 index 类型的K线，所以它同时是"日历"和"被查对象"。
        foreach (var d in Cal) InsertBar(Anchor, Granularity.Day, d);
        SqliteStockMetaUpsert.Upsert(_dbPath, [(Anchor, "上证指数")], SqliteStockMetaUpsert.TypeIndex);
        _auditor = new SqliteDailyTableAuditor(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    private void InsertBar(string code, string gran, DateTime day) =>
        _bars.InsertOrRefreshUnconfirmed([new Bar
        {
            Code = code, Granularity = gran, PeriodStart = day,
            Open = 1, Close = 1, High = 1, Low = 1, Volume = 1, Amount = 1,
        }]);

    /// <summary>登记一个标的，并给它插上"上一个交易日有、最新交易日按 <paramref name="hasLatest"/>"的日K。</summary>
    private void Seed(string code, string type, bool hasLatest)
    {
        SqliteStockMetaUpsert.Upsert(_dbPath, [(code, code)], type);
        InsertBar(code, Granularity.Day, Previous);
        if (hasLatest) InsertBar(code, Granularity.Day, Latest);
    }

    private void InsertMargin(DateTime day, int rows, string prefix = "60")
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var tx = conn.BeginTransaction();
        for (int i = 0; i < rows; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT OR IGNORE INTO MarginDetail (trade_date, code, margin_balance) VALUES ($d, $c, 1);";
            cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$c", $"{prefix}{i:D4}");
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ═══════════════ ① K线：按标的类型对齐 ═══════════════

    [Fact]
    public void ETF当天没抓到_要能查出来()
    {
        // 回归测试：ETF 在本地是**带前缀存**的（sh510300），而判据原来是"六位纯数字代码"，
        // 于是 1,665 只 ETF 整批没抓到，体检照样打印"当天的个股日线是齐的"。
        Seed("sh510300", SqliteStockMetaUpsert.TypeEtf, hasLatest: false);
        Seed("sh510500", SqliteStockMetaUpsert.TypeEtf, hasLatest: true);

        var missing = _bars.GetCodesMissingDay(
            Granularity.Day, Latest, Previous, SqliteStockMetaUpsert.TypeEtf);

        Assert.Equal(["sh510300"], missing);
    }

    [Fact]
    public void 指数当天没抓到_要能查出来()
    {
        Seed("sz399001", SqliteStockMetaUpsert.TypeIndex, hasLatest: false);

        var missing = _bars.GetCodesMissingDay(
            Granularity.Day, Latest, Previous, SqliteStockMetaUpsert.TypeIndex);

        Assert.Contains("sz399001", missing);
    }

    [Fact]
    public void 三类标的各查各的_不串名单()
    {
        // 名单串了比查不出更糟：ETF 混进个股名单，【重新拉取失败】会拿它去跑后复权/不复权，
        // 白发一轮请求还可能记一笔失败。
        Seed("600000", SqliteStockMetaUpsert.TypeStock, hasLatest: false);
        Seed("sh510300", SqliteStockMetaUpsert.TypeEtf, hasLatest: false);
        Seed("sz399001", SqliteStockMetaUpsert.TypeIndex, hasLatest: false);

        var stock = _bars.GetCodesMissingDay(Granularity.Day, Latest, Previous);
        var etf = _bars.GetCodesMissingDay(Granularity.Day, Latest, Previous, SqliteStockMetaUpsert.TypeEtf);
        var index = _bars.GetCodesMissingDay(Granularity.Day, Latest, Previous, SqliteStockMetaUpsert.TypeIndex);

        Assert.Equal(["600000"], stock);
        Assert.Equal(["sh510300"], etf);
        Assert.Contains("sz399001", index);
        Assert.DoesNotContain("sh510300", stock);
    }

    [Fact]
    public void ETF不复权是独立的一条线_单独记单独补()
    {
        // ETF 的 day_raw 是 2026-09-17 才有的第二条线（回测序列的输入）。它跟前复权是两个任务、
        // 两条水位线：这里造的这只**前复权齐、不复权缺**，要是两条线合在一起记，
        // 它就会被前复权那份"齐"盖过去，day_raw 缺一天回测就错一天，而没人会知道。
        SqliteStockMetaUpsert.Upsert(_dbPath, [("sh510300", "ETF")], SqliteStockMetaUpsert.TypeEtf);
        InsertBar("sh510300", Granularity.Day, Previous);
        InsertBar("sh510300", Granularity.Day, Latest);          // 前复权齐
        InsertBar("sh510300", Granularity.DayRaw, Previous);     // 不复权只有上一天

        var qfq = _bars.GetCodesMissingDay(
            Granularity.Day, Latest, Previous, SqliteStockMetaUpsert.TypeEtf);
        var raw = _bars.GetCodesMissingDay(
            Granularity.DayRaw, Latest, Previous, SqliteStockMetaUpsert.TypeEtf);

        Assert.Empty(qfq);
        Assert.Equal(["sh510300"], raw);
    }

    [Fact]
    public void 退市股不算个股漏抓()
    {
        // 退市股代码也是六位数字，原判据会把它们混进个股名单——数据源根本不再更新，
        // 补多少轮都是无用功。它们的尾巴归【退市股收尾】。
        Seed("600001", SqliteStockMetaUpsert.TypeDelisted, hasLatest: false);

        var stock = _bars.GetCodesMissingDay(Granularity.Day, Latest, Previous);

        Assert.Empty(stock);
    }

    // ═══════════════ ② 日更表：只查一天 ═══════════════

    [Fact]
    public void 日更表当天齐_不报()
    {
        foreach (var d in Cal) InsertMargin(d, 100);

        var check = _auditor.CheckOneDay(Margin, Anchor, Latest)!;

        Assert.False(check.IsBad);
        Assert.Equal(100, check.Rows);
    }

    [Fact]
    public void 日更表当天一行都没有_报空()
    {
        foreach (var d in Cal[..^1]) InsertMargin(d, 100);

        var check = _auditor.CheckOneDay(Margin, Anchor, Latest)!;

        Assert.True(check.IsBad);
        Assert.True(check.IsEmpty);
        Assert.Contains("一行都没有", check.Text);
    }

    [Fact]
    public void 日更表当天只抓到一半_报偏少()
    {
        foreach (var d in Cal[..^1]) InsertMargin(d, 100);
        InsertMargin(Latest, 30);   // 邻近 100 行，30 行远低于快照型 0.7 的线

        var check = _auditor.CheckOneDay(Margin, Anchor, Latest)!;

        Assert.True(check.Reason.HasFlag(SqliteDailyTableAuditor.PartialReason.ThinRows));
        Assert.Contains("邻近水平是 100 行", check.Text);
    }

    [Fact]
    public void 日更表当天缺一个交易所_报缺市场()
    {
        // 2026-08-21 融资余额那次事故的形状：沪市 1,998 行照常、深市整天 0 行，
        // 总量只掉了一半——旧的 0.2 阈值检不出来，靠市场判据才抓得住。
        foreach (var d in Cal)
        {
            InsertMargin(d, 100, "60");
            if (d != Latest) InsertMargin(d, 100, "00");
        }

        var check = _auditor.CheckOneDay(Margin, Anchor, Latest)!;

        Assert.True(check.Reason.HasFlag(SqliteDailyTableAuditor.PartialReason.MissingMarket));
        Assert.Contains("深市", check.Text);
    }

    [Fact]
    public void 只查一天跟全库体检必须给同样的结论()
    {
        // 这条是防漂移的：CheckOneDay 为了省几十秒的全表扫描带了日期下界，
        // 要是下界把判据要用的样本截掉了，它就会比全库体检宽松——
        // 那种"体检说齐了、其实缺着"的静默错误，正是这一整套东西要消灭的。
        foreach (var d in Cal[..^1]) InsertMargin(d, 100);
        InsertMargin(Latest, 30);

        var one = _auditor.CheckOneDay(Margin, Anchor, Latest)!;
        var full = _auditor.Check(Margin, Anchor, Latest)!;

        var samDay = Assert.Single(full.PartialDays.Where(p => p.Day == Latest));
        Assert.Equal(samDay.Reason, one.Reason);
        Assert.Equal(samDay.Rows, one.Rows);
        Assert.Equal(samDay.Nearby, one.Nearby);
    }

    [Fact]
    public void 日历里没有那天_判不了就别报()
    {
        // 指数日K还没抓到当天时会走到这里。判不了不是告警——报出来只会让人去查一个不存在的问题。
        foreach (var d in Cal) InsertMargin(d, 100);

        var check = _auditor.CheckOneDay(Margin, Anchor, Latest.AddDays(1));

        Assert.Null(check);
    }

    [Fact]
    public void 本地还没抓过这类数据_判不了就别报()
    {
        // MarginDetail 建了表但一行都没有＝这一项从没跑过，不是"今天缺了"。
        var check = _auditor.CheckOneDay(Margin, Anchor, Latest);

        Assert.Null(check);
    }

    [Fact]
    public void 空日补不上时_复查不能判成补齐()
    {
        // 当日体检会把"今天整天没有"记成残缺日待办（龙虎榜、融资余额都可能这样）。
        // 补完之后的复查走 CheckDays，它以前对 0 行的天是"不算残缺"——于是补没补上都过关，
        // 那天被从名单里静默划掉、再也没人管。这条钉住的就是这个。
        foreach (var d in Cal[..^1]) InsertMargin(d, 100);

        var still = _auditor.CheckDays(Margin, Anchor, [Latest]);

        var p = Assert.Single(still);
        Assert.Equal(Latest, p.Day);
        Assert.Equal(0, p.Rows);
    }

    [Fact]
    public void 排在体检之后的日更表_必须标RunsAfterDayCheck()
    {
        // 当日体检对"排在自己后面才跑"的项要往前挪一天查，否则每轮必报一条假告警
        // （体检跑的时候它今天那批还没抓）。这个事实写在 Spec 上，而判据是日更顺序——
        // 两边一分叉就静默出错，所以这里拿 DailyOrder 直接核对。
        //
        // ⚠ 这条是给**改日更顺序的人**准备的：【拉取市场事件】拆成四项、或者谁把席位挪到前面，
        //    忘了改 Spec 的话这里会红，而不是等到某天看见一条莫名其妙的"龙虎榜席位不齐"。
        var order = FetchTaskCatalog.DailyOrder.ToList();
        int coverageAt = order.IndexOf(FetchActionId.StepDayCoverage);
        Assert.True(coverageAt >= 0, "日更组里没有【当日完整性体检】");

        foreach (var spec in SqliteDailyTableAuditor.DailyTables)
        {
            if (spec.OwnerTaskId.Length == 0) continue;
            Assert.True(Enum.TryParse<FetchActionId>(spec.OwnerTaskId, out var action),
                $"{spec.Table} 的 OwnerTaskId「{spec.OwnerTaskId}」不是合法的 FetchActionId");
            int at = order.IndexOf(action);
            if (at < 0) continue;   // 不在日更组里（手动项），谈不上先后

            Assert.True(spec.RunsAfterDayCheck == at > coverageAt,
                $"{spec.Table}（{FetchTaskCatalog.Info(action).Name}）在日更里排"
                + (at > coverageAt ? "体检之后，Spec 却没标 RunsAfterDayCheck"
                                   : "体检之前，Spec 却标了 RunsAfterDayCheck"));
        }
    }

    // ═══════════════ ③ 覆盖式快照：停在哪天 ═══════════════

    [Fact]
    public void 覆盖式快照表_读得出停在哪天()
    {
        // Board.as_of 存的是带时分秒的时间戳，判"停在哪天"要截前 10 位——
        // 少截这一下，日期比对一条都对不上，而且错得很安静。
        var board = SqliteDailyTableAuditor.SnapshotTables.First(s => s.Table == "Board");
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO Board (board_code, board_type, name, as_of) "
                            + "VALUES ('BK0001', 1, 'T', $t);";
            cmd.Parameters.AddWithValue("$t", "2026-08-24 16:13:15");
            cmd.ExecuteNonQuery();
        }

        Assert.Equal(new DateTime(2026, 8, 24), _auditor.LatestDayOf(board));
    }

    [Fact]
    public void 覆盖式快照表_没抓过就返回null()
    {
        var fm = SqliteDailyTableAuditor.SnapshotTables.First(s => s.Table == "FundamentalMetric");

        Assert.Null(_auditor.LatestDayOf(fm));
    }
}
