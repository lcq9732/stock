using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// <see cref="SqliteBarRepository.QueryForAppend"/> 打在**真 SQLite** 上（2026-09-21）。
///
/// ⚠ 为什么必须有这一组：这个方法是给【板块指数合成】只追加那条路取基准用的，
/// 它的等价性测试（<see cref="BoardIndexAppendEquivalenceTests"/>）用的是假仓储——
/// 假仓储照着接口约定用 LINQ 实现，**永远验不到真 SQL**。
/// 2026-09-21 真机实测就栽在这儿：第一版 SQL 把 ORDER BY/LIMIT 写在了 UNION ALL 的分支里
/// （SQLite 不允许），950 个板块的追加全数抛异常，而 2003 个单元测试全绿。
///
/// 规矩：**仓储方法的行为约定，必须有一条打在真库上的测试**；假仓储只用来测调用方的逻辑。
/// </summary>
public class SqliteBarRepositoryAppendTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteBarRepository _bars;

    public SqliteBarRepositoryAppendTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"appendq_{Guid.NewGuid():N}.sqlite");
        _bars = new SqliteBarRepository(_dbPath);
        _bars.EnsureSchema();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    private void Put(string code, string gran, DateTime day, double close) =>
        _bars.InsertOrRefreshUnconfirmed([new Bar
        {
            Code = code, Granularity = gran, PeriodStart = day,
            Open = close, Close = close, High = close, Low = close,
            Volume = 100, Amount = 100 * close, Turnover = 1.5,
            FetchedAt = day.AddHours(20),
        }]);

    private static DateTime D(int m, int d) => new(2026, m, d);

    [Fact]
    public void 带回from之前的最后一根当基准()
    {
        foreach (var (d, c) in new[] { (D(9, 14), 10.0), (D(9, 15), 11.0), (D(9, 16), 12.0), (D(9, 17), 13.0) })
            Put("600000", Granularity.DayAdj, d, c);

        var got = _bars.QueryForAppend("600000", Granularity.DayAdj, D(9, 16));

        Assert.Equal([D(9, 15), D(9, 16), D(9, 17)], got.Select(b => b.PeriodStart));
        Assert.Equal(11.0, got[0].Close);   // 基准那根的值也要读对，不能只对上日期
    }

    /// <summary>⭐ 长期停牌：基准可能远在几百天前——这正是"不能按固定天数往前切"的那条约束。</summary>
    [Fact]
    public void 长期停牌时基准仍是它自己的上一根()
    {
        Put("600001", Granularity.DayAdj, new DateTime(2024, 3, 5), 7.0);   // 两年半前
        Put("600001", Granularity.DayAdj, D(9, 17), 9.0);

        var got = _bars.QueryForAppend("600001", Granularity.DayAdj, D(9, 16));

        Assert.Equal(2, got.Count);
        Assert.Equal(new DateTime(2024, 3, 5), got[0].PeriodStart);
        Assert.Equal(7.0, got[0].Close);
    }

    /// <summary>from 之前一根都没有（新股）→ 只给 from 及以后，调用方按"不足两根"跳过。</summary>
    [Fact]
    public void from之前没有数据时只给后半段()
    {
        Put("600002", Granularity.DayAdj, D(9, 17), 5.0);
        Put("600002", Granularity.DayAdj, D(9, 18), 6.0);

        var got = _bars.QueryForAppend("600002", Granularity.DayAdj, D(9, 17));

        Assert.Equal([D(9, 17), D(9, 18)], got.Select(b => b.PeriodStart));
    }

    /// <summary>from 及以后一根都没有 → 只剩基准那一根（合成器会因为"没有新的一天"返回空）。</summary>
    [Fact]
    public void 没有新数据时只剩基准那一根()
    {
        Put("600003", Granularity.DayAdj, D(9, 15), 5.0);

        var got = _bars.QueryForAppend("600003", Granularity.DayAdj, D(9, 16));

        Assert.Single(got);
        Assert.Equal(D(9, 15), got[0].PeriodStart);
    }

    [Fact]
    public void 整张表都没有这只票时返回空()
        => Assert.Empty(_bars.QueryForAppend("999999", Granularity.DayAdj, D(9, 16)));

    /// <summary>结果必须**按日期升序**——合成是累乘的，顺序错了值就错，而且不会报。</summary>
    [Fact]
    public void 结果按日期升序()
    {
        foreach (var d in new[] { D(9, 18), D(9, 14), D(9, 16), D(9, 15), D(9, 17) })
            Put("600004", Granularity.DayAdj, d, 10.0 + d.Day);

        var got = _bars.QueryForAppend("600004", Granularity.DayAdj, D(9, 16));

        Assert.Equal(got.Select(b => b.PeriodStart).OrderBy(x => x), got.Select(b => b.PeriodStart));
    }

    /// <summary>别的口径/别的票的行不能混进来（两段 WHERE 各写一遍，漏一处就串味）。</summary>
    [Fact]
    public void 不串别的口径和别的票()
    {
        Put("600005", Granularity.DayAdj, D(9, 15), 1.0);
        Put("600005", Granularity.DayAdj, D(9, 17), 2.0);
        Put("600005", Granularity.DayRaw, D(9, 15), 100.0);   // 同票别的口径
        Put("600005", Granularity.DayRaw, D(9, 17), 200.0);
        Put("600006", Granularity.DayAdj, D(9, 15), 300.0);   // 别的票
        Put("600006", Granularity.DayAdj, D(9, 17), 400.0);

        var got = _bars.QueryForAppend("600005", Granularity.DayAdj, D(9, 16));

        Assert.All(got, b => Assert.Equal("600005", b.Code));
        Assert.All(got, b => Assert.Equal(Granularity.DayAdj, b.Granularity));
        Assert.Equal([1.0, 2.0], got.Select(b => b.Close));
    }

    /// <summary>周/月线不落库、由日线现算，传进来要明确拒绝而不是返回空。</summary>
    [Theory]
    [InlineData(Granularity.Week)]
    [InlineData(Granularity.Month)]
    public void 周月线明确拒绝(string gran)
        => Assert.Throws<ArgumentException>(() => _bars.QueryForAppend("600000", gran, D(9, 16)));
}
