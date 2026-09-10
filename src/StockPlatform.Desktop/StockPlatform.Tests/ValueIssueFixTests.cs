using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 值问题**补过一轮之后**的两件事（2026-09-09）：判定还在不在（<see cref="ValueIssueRecheck"/>），
/// 以及"只覆盖量额换手三列"那条落库路径（<see cref="SqliteBarRepository.UpdateVolumeAmountTurnover"/>）。
///
/// 后者必须测：它是**直接改生产数据**的路径，而"绝不动 OHLC"这条约束一旦破了，
/// 历史行的复权基准会被数据源当前基准平移，同一只票的序列里就混着两套基准——
/// 那种错在图上看不出来，只有回测收益率会莫名其妙。
/// </summary>
public class ValueIssueFixTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteBarRepository _bars;

    private static readonly DateTime Day = new(2026, 9, 1);

    public ValueIssueFixTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"valuefix_{Guid.NewGuid():N}.sqlite");
        _bars = new SqliteBarRepository(_dbPath);
        _bars.EnsureSchema();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    private static MissingBarRange Range(string reason, int tries = 0, DateTime? from = null, DateTime? to = null) =>
        new()
        {
            Code = "600000", Granularity = Granularity.DayRaw, Reason = reason,
            From = from ?? Day, To = to ?? Day.AddDays(2), Days = 3, Tries = tries,
        };

    // ─────────────────── 复查判定 ───────────────────

    [Fact]
    public void 判据不再命中_算修好了()
    {
        Assert.Null(ValueIssueRecheck.Survives(Range(AuditFindingKind.Intraday), null));
        Assert.Null(ValueIssueRecheck.Survives(Range(AuditFindingKind.Intraday), []));
    }

    [Fact]
    public void 还命中_段收窄到仍然命中的那几天且Tries加一()
    {
        var r = ValueIssueRecheck.Survives(
            Range(AuditFindingKind.Intraday, tries: 1), [Day.AddDays(1)]);

        Assert.NotNull(r);
        Assert.Equal(Day.AddDays(1), r!.From);
        Assert.Equal(Day.AddDays(1), r.To);
        Assert.Equal(1, r.Days);
        Assert.Equal(2, r.Tries);
        Assert.Equal(AuditFindingKind.Intraday, r.EffectiveReason);
    }

    [Fact]
    public void 命中的日子落在段外_不算还在()
    {
        // 复查是按 code 查的，会带回这只票在别的日子上的同类问题——不属于这一段
        var r = ValueIssueRecheck.Survives(Range(AuditFindingKind.Ohlc), [Day.AddDays(30)]);
        Assert.Null(r);
    }

    [Fact]
    public void Tries到顶也照样留着_不转白名单()
    {
        // 缺行那条路补满两轮就写进「确认没有」白名单；值错**不能**那样收敛——
        // 那等于给错值发永久豁免。这里断言的是"到顶了仍然返回段"，白名单由调用方不写来保证。
        var r = ValueIssueRecheck.Survives(Range(AuditFindingKind.Inconsistent, tries: 5), [Day]);
        Assert.NotNull(r);
        Assert.Equal(6, r!.Tries);
    }

    [Fact]
    public void 重复日期去重()
    {
        var r = ValueIssueRecheck.Survives(Range(AuditFindingKind.NullValue), [Day, Day, Day.AddDays(1)]);
        Assert.Equal(2, r!.Days);
    }

    // ─────────────────── 只覆盖三列 ───────────────────

    private void Seed(string gran, double volume, double amount, double turnover,
                      double open = 6.04, double close = 6.01, double high = 6.08, double low = 5.98)
    {
        _bars.InsertOrRefreshUnconfirmed([new Bar
        {
            Code = "600000", Granularity = gran, PeriodStart = Day,
            Open = open, Close = close, High = high, Low = low,
            Volume = volume, Amount = amount, Turnover = turnover,
            FetchedAt = Day.AddHours(20),
        }]);
    }

    private Bar Stored(string gran) =>
        _bars.Query("600000", gran, Day, Day).Single();

    [Fact]
    public void 覆盖三列时OHLC一动不动()
    {
        Seed(Granularity.DayRaw, volume: 23, amount: 13_800, turnover: 0.26);   // 盘中固化留下的量额

        int n = _bars.UpdateVolumeAmountTurnover([new Bar
        {
            Code = "600000", Granularity = Granularity.DayRaw, PeriodStart = Day,
            // 数据源当前的价格跟库里的不一样（复权基准平移过）——**绝不能**被写进去
            Open = 99, Close = 99, High = 99, Low = 99,
            Volume = 20_953, Amount = 12_600_000, Turnover = 1.41,
        }], Granularity.DayRaw);

        Assert.Equal(1, n);
        var got = Stored(Granularity.DayRaw);
        Assert.Equal(20_953, got.Volume);
        Assert.Equal(12_600_000, got.Amount);
        Assert.Equal(1.41, got.Turnover);
        // 价格四列必须还是原来的
        Assert.Equal(6.04, got.Open);
        Assert.Equal(6.01, got.Close);
        Assert.Equal(6.08, got.High);
        Assert.Equal(5.98, got.Low);
    }

    [Fact]
    public void 按口径隔离_只动指定的那一套()
    {
        Seed(Granularity.Day, volume: 20_953, amount: 12_600_000, turnover: 1.41);
        Seed(Granularity.DayRaw, volume: 23, amount: 13_800, turnover: 0.26);

        _bars.UpdateVolumeAmountTurnover([new Bar
        {
            Code = "600000", Granularity = Granularity.DayRaw, PeriodStart = Day,
            Volume = 20_953, Amount = 12_600_000, Turnover = 1.41,
        }], Granularity.DayRaw);

        Assert.Equal(20_953, Stored(Granularity.DayRaw).Volume);
        Assert.Equal(20_953, Stored(Granularity.Day).Volume);      // 本来就对，没被动
    }

    [Fact]
    public void 回填模式只填空的_不覆盖已有值()
    {
        Seed(Granularity.Day, volume: 100, amount: 12_600_000, turnover: 1.41);   // amount 已有值

        int n = _bars.UpdateVolumeAmountTurnover([new Bar
        {
            Code = "600000", Granularity = Granularity.Day, PeriodStart = Day,
            Volume = 999, Amount = 999, Turnover = 9.99,
        }], Granularity.Day, onlyWhenAmountZero: true);

        Assert.Equal(0, n);
        Assert.Equal(12_600_000, Stored(Granularity.Day).Amount);
    }

    [Fact]
    public void 回填模式_amount为0的行会被填上()
    {
        Seed(Granularity.Day, volume: 100, amount: 0, turnover: 0);

        int n = _bars.UpdateVolumeAmountTurnover([new Bar
        {
            Code = "600000", Granularity = Granularity.Day, PeriodStart = Day,
            Volume = 100, Amount = 12_600_000, Turnover = 1.41,
        }], Granularity.Day, onlyWhenAmountZero: true);

        Assert.Equal(1, n);
        Assert.Equal(12_600_000, Stored(Granularity.Day).Amount);
    }

    [Fact]
    public void 回填模式_数据源没给成交额时跳过()
    {
        Seed(Granularity.Day, volume: 100, amount: 0, turnover: 0);

        // 新浪那种不给成交额的源：抓回来 amount 还是 0，写进去没意义
        int n = _bars.UpdateVolumeAmountTurnover([new Bar
        {
            Code = "600000", Granularity = Granularity.Day, PeriodStart = Day,
            Volume = 100, Amount = 0, Turnover = 0,
        }], Granularity.Day, onlyWhenAmountZero: true);

        Assert.Equal(0, n);
    }
}
