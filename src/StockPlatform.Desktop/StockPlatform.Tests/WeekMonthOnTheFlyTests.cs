using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 周/月线**不再落库**，读的时候从日线现场聚合（2026-09-10）。
///
/// 为什么去掉：它们 100% 是本地从日线算出来的，一个字节不是抓来的，却占了 Bar 表 20%
/// （week 414 万行 + month 100 万行）。真正的代价不是空间——是同一个数据错误要在**六个口径**
/// 上分别修（当天的成交量单位事故就是这么放大成 283 万行的），外加"日线更新了、周月线还没
/// 重算"这个永远存在的不一致窗口。
///
/// 这里钉三件事：现算的结果跟原来存的一致、区间过滤的顺序、以及抓取路径不再写它们。
/// </summary>
public class WeekMonthOnTheFlyTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteBarRepository _repo;

    public WeekMonthOnTheFlyTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"wkmo_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteBarRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!,
                                             Path.GetFileNameWithoutExtension(_dbPath) + "*"))
            try { File.Delete(f); } catch { /* 临时文件 */ }
    }

    /// <summary>2026-09-07 是周一。造两周共 7 个交易日。</summary>
    private void SeedTwoWeeks()
    {
        var bars = new List<Bar>();
        void Add(DateTime d, double open, double close, double high, double low, double vol) =>
            bars.Add(new Bar
            {
                Code = "600000", Granularity = Granularity.Day, PeriodStart = d,
                Open = open, Close = close, High = high, Low = low,
                Volume = vol, Amount = vol * close * 100, Turnover = 1,
                FetchedAt = d.AddHours(20),
            });

        // 第一周：09-07(一) ~ 09-11(五)
        Add(new DateTime(2026, 9, 7), 10.0, 10.5, 10.8, 9.9, 1000);
        Add(new DateTime(2026, 9, 8), 10.5, 11.0, 11.2, 10.4, 2000);
        Add(new DateTime(2026, 9, 11), 11.0, 10.2, 11.1, 10.0, 3000);
        // 第二周：09-14(一) ~ 09-15(二)
        Add(new DateTime(2026, 9, 14), 10.2, 12.0, 12.5, 10.1, 4000);
        Add(new DateTime(2026, 9, 15), 12.0, 11.5, 12.6, 11.4, 5000);
        _repo.InsertOrRefreshUnconfirmed(bars);
    }

    /// <summary>OHLCV 的聚合口径：开盘取首日、收盘取末日、高低取极值、量额求和。</summary>
    [Fact]
    public void AggregatesWeeklyBarsFromStoredDays()
    {
        SeedTwoWeeks();

        var weeks = _repo.Query("600000", Granularity.Week);

        Assert.Equal(2, weeks.Count);
        var w1 = weeks[0];
        Assert.Equal(new DateTime(2026, 9, 7), w1.PeriodStart);   // 该周第一个交易日
        Assert.Equal(10.0, w1.Open);                              // 周一开盘
        Assert.Equal(10.2, w1.Close);                             // 周五收盘
        Assert.Equal(11.2, w1.High);
        Assert.Equal(9.9, w1.Low);
        Assert.Equal(6000, w1.Volume);                            // 1000+2000+3000
        Assert.Equal(Granularity.Week, w1.Granularity);
    }

    /// <summary>
    /// **区间过滤必须在聚合之后**——这是最容易写反、而且写反了看不出来的地方。
    ///
    /// 若先把日线截断到 09-08 起再聚合，第一周就只剩 09-08 和 09-11 两天：
    /// 开盘价会变成 10.5（本该 10.0）、成交量 5000（本该 6000）、最低 10.0（本该 9.9）。
    /// 每个数字都还"像那么回事"，图上根本看不出来。
    /// </summary>
    [Fact]
    public void FiltersByRangeAfterAggregating_NotBefore()
    {
        SeedTwoWeeks();

        // 起点落在第一周中间
        var weeks = _repo.Query("600000", Granularity.Week, new DateTime(2026, 9, 1), null);

        Assert.Equal(2, weeks.Count);
        Assert.Equal(10.0, weeks[0].Open);      // 仍然是周一的开盘，没被截断影响
        Assert.Equal(6000, weeks[0].Volume);    // 仍然是整周之和
        Assert.Equal(9.9, weeks[0].Low);
    }

    /// <summary>过滤比的是**聚合后那根**的 period_start，不是原始日线的日期。</summary>
    [Fact]
    public void RangeAppliesToAggregatedPeriodStart()
    {
        SeedTwoWeeks();

        var weeks = _repo.Query("600000", Granularity.Week, new DateTime(2026, 9, 14), null);

        Assert.Single(weeks);
        Assert.Equal(new DateTime(2026, 9, 14), weeks[0].PeriodStart);
        Assert.Equal(9000, weeks[0].Volume);    // 4000+5000
    }

    [Fact]
    public void AggregatesMonthlyBars()
    {
        SeedTwoWeeks();

        var months = _repo.Query("600000", Granularity.Month);

        Assert.Single(months);
        Assert.Equal(new DateTime(2026, 9, 7), months[0].PeriodStart);   // 该月第一个交易日
        Assert.Equal(10.0, months[0].Open);
        Assert.Equal(11.5, months[0].Close);
        Assert.Equal(15000, months[0].Volume);
    }

    /// <summary>没有日线就没有周月线——返回空列表，不是抛异常、也不是一根残缺的 bar。</summary>
    [Fact]
    public void ReturnsEmptyWhenNoDayBars()
    {
        Assert.Empty(_repo.Query("600000", Granularity.Week));
        Assert.Empty(_repo.Query("600000", Granularity.Month));
    }

    /// <summary>
    /// 库里**残留的** week 行不该被读出来——现算的结果才是唯一真相。
    /// （删表之前会有一段两者并存的时期，那时读到的必须是现算值。）
    /// </summary>
    [Fact]
    public void IgnoresStaleStoredWeekRows()
    {
        SeedTwoWeeks();
        // 手工塞一根"旧的、错的"周线，模拟还没清理干净的历史
        _repo.InsertOrRefreshUnconfirmed([new Bar
        {
            Code = "600000", Granularity = Granularity.Week, PeriodStart = new DateTime(2026, 9, 7),
            Open = 999, Close = 999, High = 999, Low = 999, Volume = 999999, Amount = 999,
            FetchedAt = new DateTime(2026, 9, 7, 20, 0, 0),
        }]);

        var weeks = _repo.Query("600000", Granularity.Week);

        Assert.Equal(10.0, weeks[0].Open);      // 现算值，不是那根 999
        Assert.Equal(6000, weeks[0].Volume);
    }

    /// <summary>日线一变，周线立刻跟着变——这正是不落库换来的好处：没有"还没重算"的窗口。</summary>
    [Fact]
    public void ReflectsDayBarChangesImmediately()
    {
        SeedTwoWeeks();
        Assert.Equal(6000, _repo.Query("600000", Granularity.Week)[0].Volume);

        // 追加同一周的一天（09-09，周三）
        _repo.InsertOrRefreshUnconfirmed([new Bar
        {
            Code = "600000", Granularity = Granularity.Day, PeriodStart = new DateTime(2026, 9, 9),
            Open = 11.0, Close = 11.3, High = 11.5, Low = 10.9, Volume = 500, Amount = 1,
            FetchedAt = new DateTime(2026, 9, 9, 20, 0, 0),
        }]);

        Assert.Equal(6500, _repo.Query("600000", Granularity.Week)[0].Volume);
    }
}
