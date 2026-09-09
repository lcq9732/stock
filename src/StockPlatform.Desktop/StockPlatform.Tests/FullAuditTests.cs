using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 全库数据体检的空洞判据（2026-09-04，随体检从"只查个股前复权"扩到全口径一起加）。
///
/// 盯的是两件最容易悄悄失效的事：
/// ① **交易日历必须固定读前复权**。后复权/不复权只对个股和退市股抓，指数根本没有那两个口径的
///    K线——日历要是跟着被查口径走，查 day_hfq/day_raw 时日历是空的，一个空洞都报不出来，
///    而界面上看起来是"体检通过、数据很干净"，这种假阴性比报错难发现得多。
/// ② **白名单按口径隔离**。前复权确认"数据源确实没有"的那些日子（多半是停牌），不该连带把
///    不复权的同一天也从体检里抹掉——两套数据是分别抓的，缺不缺互相说明不了。
/// </summary>
public class FullAuditTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteBarRepository _bars;
    private readonly SqliteMissingBarRepository _audit;

    /// <summary>交易日历锚：上证指数。体检里写死用它的前复权日线。</summary>
    private const string Anchor = MarketIndexCatalog.ShanghaiCompositeSymbol;

    private static readonly DateTime[] Days =
    [
        new(2026, 9, 1), new(2026, 9, 2), new(2026, 9, 3),
    ];

    public FullAuditTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"audit_{Guid.NewGuid():N}.sqlite");
        _bars = new SqliteBarRepository(_dbPath);
        _bars.EnsureSchema();
        _audit = new SqliteMissingBarRepository(_dbPath);

        // 日历只有前复权一套（真实库就是这样：指数不抓后复权/不复权）
        Insert(Anchor, Granularity.Day, Days);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    private void Insert(string code, string gran, params DateTime[] days) =>
        _bars.InsertOrRefreshUnconfirmed(days.Select(d => new Bar
        {
            Code = code, Granularity = gran, PeriodStart = d,
            Open = 1, Close = 1, High = 1, Low = 1, Volume = 1, Amount = 1,
        }));

    [Fact]
    public void 不复权缺一天_日历用前复权照样查得出来()
    {
        // 这只票前复权是齐的，不复权缺 9-2——扩体检之前这种缺口没有任何人会发现
        Insert("600000", Granularity.Day, Days);
        Insert("600000", Granularity.DayRaw, Days[0], Days[2]);

        var qfq = _audit.FindGaps(["600000"], Granularity.Day, Anchor);
        var raw = _audit.FindGaps(["600000"], Granularity.DayRaw, Anchor);

        Assert.Empty(qfq);
        Assert.Equal([Days[1]], raw["600000"]);
    }

    [Fact]
    public void 后复权同理()
    {
        Insert("600001", Granularity.DayHfq, Days[0], Days[2]);
        var hfq = _audit.FindGaps(["600001"], Granularity.DayHfq, Anchor);
        Assert.Equal([Days[1]], hfq["600001"]);
    }

    [Fact]
    public void 只查存续区间内_两头之外不算缺()
    {
        // 9-3 才上市：9-1、9-2 不是它的空洞
        Insert("600002", Granularity.Day, Days[2]);
        Assert.Empty(_audit.FindGaps(["600002"], Granularity.Day, Anchor));
    }

    [Fact]
    public void 白名单按口径隔离_前复权确认了不影响不复权()
    {
        Insert("600003", Granularity.Day, Days[0], Days[2]);
        Insert("600003", Granularity.DayRaw, Days[0], Days[2]);

        // 前复权那天补两轮拿不到，认了（停牌）
        _audit.Confirm([("600003", Days[1])], Granularity.Day, tries: 2);

        Assert.Empty(_audit.FindGaps(["600003"], Granularity.Day, Anchor));
        Assert.Equal([Days[1]], _audit.FindGaps(["600003"], Granularity.DayRaw, Anchor)["600003"]);
    }

    [Fact]
    public void 彻底体检_把白名单里的也重新报出来()
    {
        Insert("600004", Granularity.Day, Days[0], Days[2]);
        _audit.Confirm([("600004", Days[1])], Granularity.Day, tries: 2);

        Assert.Empty(_audit.FindGaps(["600004"], Granularity.Day, Anchor));
        Assert.Equal([Days[1]],
            _audit.FindGaps(["600004"], Granularity.Day, Anchor, ignoreConfirmed: true)["600004"]);
    }

    [Fact]
    public void 一根都没有的口径_查不出空洞()
    {
        // 这是体检查不到的那一类：整只票压根没有 day_raw，进不了区间表。
        // 所以 RunStepFullAuditAsync 另外用 GetLatestPeriodStartByCode 数了一遍"一根都没有的"，
        // 单独报出来提醒去跑【拉取区间数据】——这条测试就是钉住"FindGaps 确实管不了这种"。
        Insert("600005", Granularity.Day, Days);
        Assert.Empty(_audit.FindGaps(["600005"], Granularity.DayRaw, Anchor));
        Assert.DoesNotContain("600005", _bars.GetLatestPeriodStartByCode(Granularity.DayRaw).Keys);
    }

    [Fact]
    public void 老manifest没有口径字段时_默认按前复权()
    {
        var restored = System.Text.Json.JsonSerializer.Deserialize<MissingBarRange>(
            """{"Code":"600006","From":"2026-09-01T00:00:00","To":"2026-09-01T00:00:00","Days":1,"Tries":1}""")!;
        Assert.Equal(Granularity.Day, restored.Granularity);
    }
}
