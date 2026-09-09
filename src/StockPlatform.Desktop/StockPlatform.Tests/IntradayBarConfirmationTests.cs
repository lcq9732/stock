using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// "盘中抓的K线不是最终数据"这条判据的两半（2026-09-09 新增）。
///
/// 为什么值得一整套测试：这个 bug 静默固化了真实数据，而且藏了很久没人看见。2026-09-01 早上
/// 09:25~10:10 跑【不复权首次整段回补】，抓到的当天K线是**开盘半小时的快照**——002650 那天在库里
/// 是"四价合一 6.04、成交量 23 手"，真实收盘是 6.01、20953 手；day_adj 原样继承了 1555 行。
/// 2026-07-16 11:25 抓的 1330 个指数/ETF 同一个坑，其中 sh000001 是全库交易日锚兼 MA60 择时输入。
///
/// 两半都要成立才修得掉：
///   ① <see cref="IncrementalWindowCalculator.IncrementalStart"/> 要肯回头（原来只在"最新那根正好
///      是今天"时才判确认，跨过午夜就从今天起抓、永不回头）；
///   ② <see cref="SqliteBarRepository.InsertOrRefreshUnconfirmed"/> 要肯覆盖（原来是 INSERT OR
///      IGNORE，就算回头重抓了也会把新数据默默丢掉）。
/// </summary>
public class IntradayBarConfirmationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteBarRepository _repo;

    private static readonly DateTime Today = new(2026, 9, 9);
    private const string Code = "000001";

    public IntradayBarConfirmationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"intraday_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteBarRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    private static Bar Row(DateTime day, double close, DateTime fetchedAt, double volume = 100) => new()
    {
        Code = Code, Granularity = Granularity.DayRaw, PeriodStart = day,
        Open = close, Close = close, High = close, Low = close,
        Volume = volume, Amount = close * volume, Turnover = 1, FetchedAt = fetchedAt,
    };

    private Bar? Stored(DateTime day) =>
        _repo.Query(Code, Granularity.DayRaw, day, day).FirstOrDefault();

    // ─────────────────── ① 增量起点 ───────────────────

    [Fact]
    public void 没抓过_按回看年数补()
    {
        Assert.Equal(Today.AddYears(-3), IncrementalWindowCalculator.IncrementalStart(null, Today, 3));
    }

    [Fact]
    public void 最新那根是往天且收盘后抓的_从次日起()
    {
        var latest = (new DateTime(2026, 9, 8), new DateTime(2026, 9, 8, 22, 10, 0));
        Assert.Equal(new DateTime(2026, 9, 9), IncrementalWindowCalculator.IncrementalStart(latest, Today, 3));
    }

    /// <summary>这次修的就是这一条：往天的盘中行必须被重抓，不能因为"它比今天早"就跳过。</summary>
    [Fact]
    public void 最新那根是往天但盘中抓的_回退到它自己重抓()
    {
        var latest = (new DateTime(2026, 9, 8), new DateTime(2026, 9, 8, 9, 33, 49));
        Assert.Equal(new DateTime(2026, 9, 8), IncrementalWindowCalculator.IncrementalStart(latest, Today, 3));
    }

    [Fact]
    public void 最新那根是今天且已确认_返回明天由调用方跳过()
    {
        var latest = (Today, Today.AddHours(20));
        Assert.Equal(Today.AddDays(1), IncrementalWindowCalculator.IncrementalStart(latest, Today, 3));
    }

    [Fact]
    public void 最新那根是今天但盘中抓的_重抓今天()
    {
        var latest = (Today, Today.AddHours(10));
        Assert.Equal(Today, IncrementalWindowCalculator.IncrementalStart(latest, Today, 3));
    }

    [Theory]
    [InlineData(15, 59, false)]   // 收盘了但没到判定时刻——宁可多等，不可提前认定
    [InlineData(16, 0, true)]
    [InlineData(9, 25, false)]
    public void 确认判据卡在16点(int hour, int minute, bool expected)
    {
        var day = new DateTime(2026, 9, 1);
        Assert.Equal(expected, IncrementalWindowCalculator.IsConfirmedFinal(
            day.AddHours(hour).AddMinutes(minute), day));
    }

    // ─────────────────── ② 写入覆盖 ───────────────────

    [Fact]
    public void 盘中抓的行_被后来的收盘数据覆盖()
    {
        var day = new DateTime(2026, 9, 1);
        _repo.InsertOrRefreshUnconfirmed([Row(day, 6.04, day.AddHours(9).AddMinutes(25), volume: 23)]);
        _repo.InsertOrRefreshUnconfirmed([Row(day, 6.01, day.AddDays(1).AddHours(1), volume: 20953)]);

        var got = Stored(day);
        Assert.NotNull(got);
        Assert.Equal(6.01, got!.Close);
        Assert.Equal(20953, got.Volume);
    }

    [Fact]
    public void 已确认的行_永不被覆盖()
    {
        var day = new DateTime(2026, 9, 1);
        _repo.InsertOrRefreshUnconfirmed([Row(day, 6.01, day.AddHours(22))]);
        // 复权基准可能已经变了，重抓同一天的值不能覆盖历史行——这是原来 INSERT OR IGNORE 的本意，必须保住
        _repo.InsertOrRefreshUnconfirmed([Row(day, 5.55, day.AddDays(5))]);

        Assert.Equal(6.01, Stored(day)!.Close);
    }

    [Fact]
    public void 更早的抓取结果_不覆盖已有的盘中行()
    {
        var day = new DateTime(2026, 9, 1);
        _repo.InsertOrRefreshUnconfirmed([Row(day, 6.04, day.AddHours(11))]);
        _repo.InsertOrRefreshUnconfirmed([Row(day, 5.90, day.AddHours(9))]);   // 更早，不该赢

        Assert.Equal(6.04, Stored(day)!.Close);
    }

    [Fact]
    public void 库里没有的行_照常插入()
    {
        var day = new DateTime(2026, 9, 2);
        _repo.InsertOrRefreshUnconfirmed([Row(day, 7.77, day.AddHours(20))]);
        Assert.Equal(7.77, Stored(day)!.Close);
    }
}
