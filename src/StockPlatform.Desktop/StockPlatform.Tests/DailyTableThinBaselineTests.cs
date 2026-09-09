using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// "偏少日"的**基准**是邻近水平、不是全区间中位数（2026-09-06 改）。
///
/// 起因：各表的日均行数在十年里本身就在变——大宗交易 2016 年日均 71 行、2017 年 195 行、
/// 2024 年 83 行。拿全区间中位数（196）当基准，2016 和 2024 的正常日会被系统性判成"只抓了一半"：
/// 2026-09-06 那次全库体检报出来的 6 个可疑日，有 5 个就是这么来的（包括熔断日 2016-01-04，
/// 它的成交额其实比邻近还高 7%，靠"清淡日豁免"根本挡不住，靠邻近行数一比就正常了）。
///
/// 这一套要 30 天以上的日历才测得出来（滚动窗口是前后各 10 个交易日），所以单开一个类，
/// 不去动 <see cref="DailyTableAuditTests"/> 那份 5 天的日历。
/// </summary>
public class DailyTableThinBaselineTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDailyTableAuditor _auditor;

    private const string Anchor = MarketIndexCatalog.ShanghaiCompositeSymbol;

    private static readonly SqliteDailyTableAuditor.Spec Margin =
        SqliteDailyTableAuditor.DailyTables.First(t => t.Table == "MarginDetail");

    /// <summary>40 个连续工作日当交易日历（够放下 ±10 的滚动窗口，还能分出前后两段）。</summary>
    private static readonly List<DateTime> Cal = BuildCalendar(40);

    private static readonly DateTime Cutoff = Cal[^1];

    private static List<DateTime> BuildCalendar(int count)
    {
        var days = new List<DateTime>();
        var d = new DateTime(2026, 1, 5);       // 周一
        while (days.Count < count)
        {
            if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday) days.Add(d);
            d = d.AddDays(1);
        }
        return days;
    }

    public DailyTableThinBaselineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"thin_{Guid.NewGuid():N}.sqlite");
        var bars = new SqliteBarRepository(_dbPath);
        bars.EnsureSchema();
        // 成交额给个恒定值：这一套测的是"行数基准"，别让清淡日豁免插进来搅局
        bars.InsertOrRefreshUnconfirmed(Cal.Select(day => new Bar
        {
            Code = Anchor, Granularity = Granularity.Day, PeriodStart = day,
            Open = 1, Close = 1, High = 1, Low = 1, Volume = 1, Amount = 1000,
        }));
        _auditor = new SqliteDailyTableAuditor(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

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

    /// <summary>前 20 天日常 20 行、后 20 天日常 200 行——早年的正常日一天都不该报。</summary>
    [Fact]
    public void 量级随年份变化_早年的正常日不报()
    {
        for (int i = 0; i < Cal.Count; i++) InsertMargin(Cal[i], i < 20 ? 20 : 200);

        Assert.Empty(_auditor.Check(Margin, Anchor, Cutoff)!.ThinDays);
    }

    /// <summary>同样的量级变化下，早年那段里真的只抓到零头的那天，照样要报出来。</summary>
    [Fact]
    public void 量级随年份变化_早年真出问题那天照报()
    {
        // 注意 InsertMargin 是 INSERT OR IGNORE、代码从 600000 顺排，同一天不能插两次——
        // 第二次的行会被前一次的主键挡掉，那天的行数根本不会变（这个坑第一版就踩到了）
        for (int i = 0; i < Cal.Count; i++)
            InsertMargin(Cal[i], i == 5 ? 2 : i < 20 ? 20 : 200);   // 第 6 天只抓到 2 行

        var thin = _auditor.Check(Margin, Anchor, Cutoff)!.ThinDays;

        Assert.Equal([(Cal[5], 2)], thin);
    }
}
