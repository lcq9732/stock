using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// TradingDay / DailyFetchNoData 两张表的 SQL 本身（2026-09-08）。
///
/// 判据逻辑在 DailyBackfillGateTests、TradingCalendarTaskTests 那边（都用假仓储）；
/// 这里盯的是**真的写进去、读得出来**——上面那两套测试再全，SQL 写错一样全盘失效。
/// 三条最容易写错的：官方值要能覆盖归纳值、按 dataset 隔离、清空只清一个数据集。
/// </summary>
public class TradingDayAndNoDataRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteTradingDayRepository _days;
    private readonly SqliteDailyFetchNoDataRepository _noData;

    public TradingDayAndNoDataRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tradingday_{Guid.NewGuid():N}.sqlite");
        _days = new SqliteTradingDayRepository(_dbPath);
        _days.EnsureSchema();
        _noData = new SqliteDailyFetchNoDataRepository(_dbPath);
        _noData.EnsureSchema();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    private static DateOnly D(string s) => DateOnly.Parse(s);

    [Fact]
    public void 交易日历_写入读出与区间查询()
    {
        _days.Upsert([(D("2005-01-04"), ITradingDayRepository.SzseSource),
                      (D("2005-01-05"), ITradingDayRepository.SzseSource),
                      (D("1990-12-19"), ITradingDayRepository.LocalSource)]);

        Assert.Equal(3, _days.Count());
        Assert.Equal(2, _days.Count(ITradingDayRepository.SzseSource));
        Assert.Equal(1, _days.Count(ITradingDayRepository.LocalSource));

        var (min, max) = _days.GetRange();
        Assert.Equal(new DateTime(1990, 12, 19), min);
        Assert.Equal(new DateTime(2005, 1, 5), max);

        Assert.Equal([D("2005-01-04"), D("2005-01-05")],
                     _days.GetBetween(D("2005-01-01"), D("2005-01-31")).OrderBy(d => d));
    }

    /// <summary>
    /// 同一天再写一次要**按新来源覆盖**（UPSERT，不是 INSERT OR IGNORE）。
    /// 归纳出来的日子后来被官方确认时，来源标记得跟着变成 szse——否则日后没法回答
    /// "这一天到底是官方说的还是我们猜的"。
    /// </summary>
    [Fact]
    public void 交易日历_官方值覆盖归纳值()
    {
        _days.Upsert([(D("2005-01-04"), ITradingDayRepository.LocalSource)]);
        _days.Upsert([(D("2005-01-04"), ITradingDayRepository.SzseSource)]);

        Assert.Equal(1, _days.Count());
        Assert.Equal(1, _days.Count(ITradingDayRepository.SzseSource));
        Assert.Equal(0, _days.Count(ITradingDayRepository.LocalSource));
    }

    [Fact]
    public void 交易日历_空表时范围是空()
    {
        var (min, max) = _days.GetRange();
        Assert.Null(min);
        Assert.Null(max);
        Assert.Empty(_days.GetAll());
    }

    /// <summary>两个数据集互不干扰——龙虎榜确认没有的日子，不能连累融资余额那天也被跳过。</summary>
    [Fact]
    public void 空日名单_按数据集隔离()
    {
        _noData.Confirm(IDailyFetchNoDataRepository.LhbDataset, D("2003-01-06"));
        _noData.Confirm(IDailyFetchNoDataRepository.MarginDataset, D("2011-05-03"));

        Assert.Equal([D("2003-01-06")], _noData.GetConfirmed(IDailyFetchNoDataRepository.LhbDataset));
        Assert.Equal([D("2011-05-03")], _noData.GetConfirmed(IDailyFetchNoDataRepository.MarginDataset));
    }

    /// <summary>重复确认同一天不能炸（主键冲突走 DO NOTHING）——回补重跑时会真撞上。</summary>
    [Fact]
    public void 空日名单_重复确认幂等()
    {
        _noData.Confirm(IDailyFetchNoDataRepository.LhbDataset, D("2003-01-06"));
        _noData.Confirm(IDailyFetchNoDataRepository.LhbDataset, D("2003-01-06"));

        Assert.Equal(1, _noData.Count(IDailyFetchNoDataRepository.LhbDataset));
    }

    /// <summary>数据源后来补上了 → 撤销结论（Case 6 自愈）。</summary>
    [Fact]
    public void 空日名单_可以撤销一天()
    {
        _noData.Confirm(IDailyFetchNoDataRepository.LhbDataset, D("2003-01-06"));
        _noData.Remove(IDailyFetchNoDataRepository.LhbDataset, D("2003-01-06"));

        Assert.Empty(_noData.GetConfirmed(IDailyFetchNoDataRepository.LhbDataset));
    }

    /// <summary>「彻底体检」按数据集清空：清龙虎榜不能把融资余额的结论也带走。</summary>
    [Fact]
    public void 空日名单_清空只清一个数据集()
    {
        _noData.Confirm(IDailyFetchNoDataRepository.LhbDataset, D("2003-01-06"));
        _noData.Confirm(IDailyFetchNoDataRepository.LhbDataset, D("2003-01-07"));
        _noData.Confirm(IDailyFetchNoDataRepository.MarginDataset, D("2011-05-03"));

        int cleared = _noData.Clear(IDailyFetchNoDataRepository.LhbDataset);

        Assert.Equal(2, cleared);
        Assert.Empty(_noData.GetConfirmed(IDailyFetchNoDataRepository.LhbDataset));
        Assert.Equal(1, _noData.Count(IDailyFetchNoDataRepository.MarginDataset));
    }
}
