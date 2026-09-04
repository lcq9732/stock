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
}
