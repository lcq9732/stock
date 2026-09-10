using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【重算回测序列】的待算判据（<see cref="SqliteAdjSeriesAuditor"/>）。
///
/// 为什么要单独测：这套判据判漏了是**静默**的——界面上"待重算 N 只"显示 0，而 day_adj 里躺着
/// 从错数据算出来的价格，回测拿着它跑，没有任何地方会提示。2026-09-10 就踩过一次：
/// 修好了 5312 只票的 day_raw（盘中固化的半天快照），点重算只认出 2 只，因为原有四条判据
/// **全看日期范围和事件时间戳**，而"改已有行的值"一点日期都不变。
/// </summary>
public class AdjSeriesRebuildCriteriaTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteBarRepository _bars;
    private readonly SqliteAdjSeriesAuditor _auditor;

    private static readonly DateTime D1 = new(2026, 9, 1);
    private static readonly DateTime D2 = new(2026, 9, 2);

    public AdjSeriesRebuildCriteriaTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"adjcrit_{Guid.NewGuid():N}.sqlite");
        _bars = new SqliteBarRepository(_dbPath);
        _bars.EnsureSchema();
        _auditor = new SqliteAdjSeriesAuditor(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    private void Put(string code, string gran, DateTime day, DateTime fetchedAt, double close = 10)
    {
        _bars.InsertOrRefreshUnconfirmed([new Bar
        {
            Code = code, Granularity = gran, PeriodStart = day,
            Open = close, Close = close, High = close, Low = close,
            Volume = 100, Amount = 100 * close * 100, Turnover = 1.5,
            FetchedAt = fetchedAt,
        }]);
    }

    /// <summary>直接改一行的值和 fetched_at——模拟"修正已有行"（日期范围完全不变）。</summary>
    private void Overwrite(string code, string gran, DateTime day, double close, DateTime fetchedAt)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Bar SET close = $c, fetched_at = $f " +
                          "WHERE code = $code AND granularity = $g AND period_start = $p;";
        cmd.Parameters.AddWithValue("$c", close);
        cmd.Parameters.AddWithValue("$f", fetchedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$g", gran);
        cmd.Parameters.AddWithValue("$p", day.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    // ─────────────────── 第五条判据（2026-09-10 补） ───────────────────

    /// <summary>这就是踩过的那个坑的回归测试。</summary>
    [Fact]
    public void 不复权的值被改过_日期没变_也要重算()
    {
        // 抓 day_raw、算 day_adj，两边日期范围一样、adj 的时间戳更晚 —— 此刻是齐的
        Put("002650", Granularity.DayRaw, D1, new DateTime(2026, 9, 1, 9, 25, 0));
        Put("002650", Granularity.DayAdj, D1, new DateTime(2026, 9, 10, 2, 39, 0));
        Assert.Empty(_auditor.BuildPlan().Codes);

        // 修正那一行的值（日期范围一点没变，只是值和抓取时刻更新了）
        Overwrite("002650", Granularity.DayRaw, D1, close: 6.01, fetchedAt: new DateTime(2026, 9, 10, 9, 42, 0));

        Assert.Contains("002650", _auditor.BuildPlan().Codes);
        Assert.Contains("002650", _auditor.CodesWithFresherRawBars());
    }

    [Fact]
    public void 重算过之后就不再命中()
    {
        Put("002650", Granularity.DayRaw, D1, new DateTime(2026, 9, 10, 9, 42, 0));
        Put("002650", Granularity.DayAdj, D1, new DateTime(2026, 9, 10, 2, 39, 0));
        Assert.Contains("002650", _auditor.BuildPlan().Codes);

        // 重算：day_adj 的时间戳变成"这一轮的重算时刻"
        Overwrite("002650", Granularity.DayAdj, D1, close: 6.01, fetchedAt: new DateTime(2026, 9, 10, 12, 0, 0));
        Assert.Empty(_auditor.BuildPlan().Codes);
    }

    // ─────────────────── 原有四条不能被破坏 ───────────────────

    [Fact]
    public void 压根没算过_要重算()
    {
        Put("600000", Granularity.DayRaw, D1, D1.AddHours(20));
        Assert.Contains("600000", _auditor.BuildPlan().Codes);
    }

    [Fact]
    public void 不复权长出新日期_要重算()
    {
        Put("600000", Granularity.DayRaw, D1, D1.AddHours(20));
        Put("600000", Granularity.DayAdj, D1, D1.AddHours(22));
        Put("600000", Granularity.DayRaw, D2, D2.AddHours(20));   // 尾巴长出来
        Assert.Contains("600000", _auditor.BuildPlan().Codes);
    }

    [Fact]
    public void 不复权往前补了历史_要重算()
    {
        Put("600000", Granularity.DayRaw, D2, D2.AddHours(20));
        Put("600000", Granularity.DayAdj, D2, D2.AddHours(22));
        Put("600000", Granularity.DayRaw, D1, D2.AddHours(23));   // 前面补上一天
        Assert.Contains("600000", _auditor.BuildPlan().Codes);
    }

    [Fact]
    public void 两边完全对齐_不用重算()
    {
        Put("600000", Granularity.DayRaw, D1, D1.AddHours(20));
        Put("600000", Granularity.DayRaw, D2, D2.AddHours(20));
        Put("600000", Granularity.DayAdj, D1, D2.AddHours(22));
        Put("600000", Granularity.DayAdj, D2, D2.AddHours(22));
        Assert.Empty(_auditor.BuildPlan().Codes);
    }

    [Fact]
    public void 没有不复权的票_压根不进名单()
    {
        // day_adj 是从 day_raw 算的，没有源就无从算起——名单按 day_raw 的票枚举
        Put("sh000001", Granularity.Day, D1, D1.AddHours(20));
        Assert.Empty(_auditor.BuildPlan().Codes);
    }
}
