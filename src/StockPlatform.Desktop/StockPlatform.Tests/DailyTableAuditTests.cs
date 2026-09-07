using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 日频表体检的判据（2026-09-04，随全库体检的第三块一起加）。
///
/// 这套判据比K线那套更容易悄悄失灵，因为它跨了两种日期格式：交易日历在 Bar 表里是
/// "yyyy-MM-dd HH:mm:ss"，日频表存的是 "yyyy-MM-dd"。少截那 10 位，join 一条都对不上，
/// 结果是"每一天都缺"或者"一天都不缺"——两种都错得很安静。所以这里全用真 schema、真日期跑。
/// </summary>
public class DailyTableAuditTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDailyTableAuditor _auditor;

    private const string Anchor = MarketIndexCatalog.ShanghaiCompositeSymbol;

    /// <summary>被测表用融资余额（每交易日全市场几千行，最典型的日频表）。</summary>
    private static readonly SqliteDailyTableAuditor.Spec Margin =
        SqliteDailyTableAuditor.DailyTables.First(t => t.Table == "MarginDetail");

    private static readonly DateTime[] Cal =
    [
        new(2026, 8, 24), new(2026, 8, 25), new(2026, 8, 26), new(2026, 8, 27), new(2026, 8, 28),
    ];

    /// <summary>体检的截止线（"这天之后不算缺"）——测试里固定住，免得跟着今天飘。</summary>
    private static readonly DateTime Cutoff = new(2026, 8, 28);

    public DailyTableAuditTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"daily_{Guid.NewGuid():N}.sqlite");
        var bars = new SqliteBarRepository(_dbPath);
        bars.EnsureSchema();
        bars.InsertOrIgnore(Cal.Select(d => new Bar
        {
            Code = Anchor, Granularity = Granularity.Day, PeriodStart = d,
            Open = 1, Close = 1, High = 1, Low = 1, Volume = 1, Amount = 1,
        }));
        _auditor = new SqliteDailyTableAuditor(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    /// <summary>往 MarginDetail 塞某一天的若干行（代码随便编，体检只数行数）。</summary>
    private void InsertMargin(DateTime day, int rows)
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
            cmd.Parameters.AddWithValue("$c", $"60{i:D4}");
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    [Fact]
    public void 每天都有数据时_报齐()
    {
        foreach (var d in Cal) InsertMargin(d, 100);

        var r = _auditor.Check(Margin, Anchor, Cutoff)!;

        Assert.Empty(r.EmptyDays);
        Assert.Empty(r.ThinDays);
        Assert.Equal(Cal.Length, r.TradingDays);
        Assert.Equal(100, r.MedianRows);
    }

    [Fact]
    public void 中间某个交易日一行都没有_报出来()
    {
        foreach (var d in Cal) if (d != Cal[2]) InsertMargin(d, 100);

        var r = _auditor.Check(Margin, Anchor, Cutoff)!;

        Assert.Equal([Cal[2]], r.EmptyDays);
    }

    [Fact]
    public void 只在表自己的区间内查_开头之前不算缺()
    {
        // 这张表本地只从第 3 天开始有——前两天不是"缺"，是还没抓那么早
        InsertMargin(Cal[2], 100);
        InsertMargin(Cal[3], 100);
        InsertMargin(Cal[4], 100);

        var r = _auditor.Check(Margin, Anchor, Cutoff)!;

        Assert.Empty(r.EmptyDays);
        Assert.Equal(Cal[2], r.From);
        Assert.Equal(3, r.TradingDays);
    }

    [Fact]
    public void 截止线之后的日子不算缺()
    {
        foreach (var d in Cal) InsertMargin(d, 100);
        // 假装最后两天还没到"数据源该有了"的时点
        var r = _auditor.Check(Margin, Anchor, new DateTime(2026, 8, 26))!;

        Assert.Equal(3, r.TradingDays);
        Assert.Equal(new DateTime(2026, 8, 26), r.To);
    }

    [Fact]
    public void 行数只有平时零头的那天_报偏少()
    {
        InsertMargin(Cal[0], 100);
        InsertMargin(Cal[1], 100);
        InsertMargin(Cal[2], 5);      // 半拉子轮次：抓了但只抓到一点点
        InsertMargin(Cal[3], 100);
        InsertMargin(Cal[4], 100);

        var r = _auditor.Check(Margin, Anchor, Cutoff)!;

        Assert.Empty(r.EmptyDays);
        Assert.Equal([(Cal[2], 5)], r.ThinDays);
    }

    [Fact]
    public void 只是略少的那天_不报()
    {
        InsertMargin(Cal[0], 100);
        InsertMargin(Cal[1], 100);
        InsertMargin(Cal[2], 80);     // 正常波动，别报
        InsertMargin(Cal[3], 100);
        InsertMargin(Cal[4], 100);

        Assert.Empty(_auditor.Check(Margin, Anchor, Cutoff)!.ThinDays);
    }

    [Fact]
    public void 表里一行都没有时_返回null而不是满屏缺失()
    {
        // "这项功能还没用过"不是"数据缺了"
        Assert.Null(_auditor.Check(Margin, Anchor, Cutoff));
    }

    [Fact]
    public void 所有登记在册的日频表都能跑_不会拼错表名列名()
    {
        // 表名/列名是拼进 SQL 的，拼错了要在这里炸，而不是等用户点体检
        foreach (var spec in SqliteDailyTableAuditor.DailyTables)
            Assert.Null(Record.Exception(() => _auditor.Check(spec, Anchor, Cutoff)));
    }

    // ───────────────── 尾部滞后（2026-09-06 加）─────────────────
    // 起因：龙虎榜停在 09-01、落后 3 个交易日，体检一个字都没报——因为区间上界取的是
    // "表里最新那天"，尾巴上的缺口天生落在体检视野之外。

    /// <summary>融资余额的 LagDays 是 1（交易所 T+1 发布），所以要落后 2 天以上才该报。</summary>
    [Fact]
    public void 尾巴停在几天前_报出来()
    {
        InsertMargin(Cal[0], 100);
        InsertMargin(Cal[1], 100);
        InsertMargin(Cal[2], 100);

        var r = _auditor.Check(Margin, Anchor, Cutoff)!;

        Assert.Equal([Cal[3], Cal[4]], r.TailMissingDays);
        Assert.Empty(r.EmptyDays);        // 尾巴不该同时算成"空日"，那会重复报
    }

    [Fact]
    public void 落后在允许范围内_不报()
    {
        foreach (var d in Cal[..4]) InsertMargin(d, 100);   // 只差最后一天＝T+1 的正常滞后

        Assert.Empty(_auditor.Check(Margin, Anchor, Cutoff)!.TailMissingDays);
    }

    // ───────────────── 清淡日豁免（2026-09-06 加）─────────────────
    // 起因：大宗交易那 6 个"偏少日"全是熔断日和长假前后——市场本身就没怎么交易，
    // 任何行数判据都会报，而它们根本没什么可补的。

    /// <summary>把交易日历上某一天的成交额改掉（日历默认每天 amount=1）。</summary>
    private void SetCalendarAmount(DateTime day, double amount)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE Bar SET amount = $a WHERE code = $c AND granularity = 'day' AND period_start = $d;";
        cmd.Parameters.AddWithValue("$a", amount);
        cmd.Parameters.AddWithValue("$c", Anchor);
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void 全市场那天本来就没怎么交易_行数少也不报()
    {
        foreach (var d in Cal) SetCalendarAmount(d, 1000);
        SetCalendarAmount(Cal[2], 100);        // 熔断/半天休市：成交额只有平时一成

        InsertMargin(Cal[0], 100);
        InsertMargin(Cal[1], 100);
        InsertMargin(Cal[2], 5);
        InsertMargin(Cal[3], 100);
        InsertMargin(Cal[4], 100);

        Assert.Empty(_auditor.Check(Margin, Anchor, Cutoff)!.ThinDays);
    }

    [Fact]
    public void 市场正常成交却只抓到零头_照报()
    {
        // 半拉子轮次跟清淡日的区别就在这里：那天市场是正常交易的
        foreach (var d in Cal) SetCalendarAmount(d, 1000);

        InsertMargin(Cal[0], 100);
        InsertMargin(Cal[1], 100);
        InsertMargin(Cal[2], 5);
        InsertMargin(Cal[3], 100);
        InsertMargin(Cal[4], 100);

        Assert.Equal([(Cal[2], 5)], _auditor.Check(Margin, Anchor, Cutoff)!.ThinDays);
    }

    // ───────────────── 滚动窗口（2026-09-06 加）─────────────────

    /// <summary>
    /// 东财资金流明细只给最近 120 天，而本地表里还留着更早那些"逐只回补时先抓的几只票"的
    /// 零星行（实测 2025-12-26 那天只有 1 只票）。不按窗口裁的话，这些尾巴会被当成
    /// 47 天"只抓了一半"——2026-09-06 那次体检就是这么误报的。
    /// </summary>
    [Fact]
    public void 数据源窗口之外的零星行_不算偏少()
    {
        var windowed = Margin with { WindowDays = 3 };   // 日历共 5 天 → 只体检最后 3 天

        InsertMargin(Cal[0], 1);        // 窗口外的回补尾巴
        InsertMargin(Cal[2], 100);
        InsertMargin(Cal[3], 100);
        InsertMargin(Cal[4], 100);

        var r = _auditor.Check(windowed, Anchor, Cutoff)!;

        Assert.Equal(Cal[2], r.From);
        Assert.Empty(r.ThinDays);
        Assert.Empty(r.EmptyDays);      // Cal[1] 也在窗口外，不该算缺
    }
}
