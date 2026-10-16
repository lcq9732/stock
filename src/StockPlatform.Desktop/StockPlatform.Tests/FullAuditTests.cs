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

    // ─────────── 起点＝ min(第一根, 上市日)，不早于水位表（2026-10-16，见 doc/ipo-issue-design.md §6）───────────

    private void Exec(string sql, params (string, object)[] ps)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    private void ListDate(string code, string date)
    {
        SqliteStockMetaUpsert.Upsert(_dbPath, [(code, code)], SqliteStockMetaUpsert.TypeStock);
        Exec("UPDATE StockMeta SET list_date = $d WHERE code = $c;", ("$d", date), ("$c", code));
    }

    /// <summary>301371 敷尔佳：2023-08-01 上市，不复权从 08-31 才有——上市头几天以前永远查不出来。</summary>
    [Fact]
    public void 上市日早于第一根_头几天算空洞()
    {
        Insert("301371", Granularity.DayRaw, Days[2]);
        ListDate("301371", "2026-09-01");

        Assert.Equal([Days[0], Days[1]], _audit.FindGaps(["301371"], Granularity.DayRaw, Anchor)["301371"]);
    }

    /// <summary>1990 年代老股上市头几天数据源本来就没有，水位表确认过——不报。</summary>
    [Fact]
    public void 水位表确认之前没有的那段不报()
    {
        Insert("600605", Granularity.Day, Days[2]);
        ListDate("600605", "2026-09-01");
        Exec("INSERT INTO BarProbeFloor (code, granularity, no_data_before, probed_at) VALUES ('600605','day','2026-09-02','x');");

        Assert.Equal([Days[1]], _audit.FindGaps(["600605"], Granularity.Day, Anchor)["600605"]);
    }

    /// <summary>600018 上港集团：吸收合并后重新上市，上市日 2006、K线 2000 年就有——起点不动。</summary>
    [Fact]
    public void 上市日晚于第一根_起点不动()
    {
        Insert("600018", Granularity.Day, Days);
        ListDate("600018", "2026-09-03");

        Assert.Empty(_audit.FindGaps(["600018"], Granularity.Day, Anchor));
    }

    /// <summary>名册里没有上市日（退市股、还没跑过【新股发行】）——跟以前一样从第一根算。</summary>
    [Fact]
    public void 没有上市日的照旧从第一根算()
    {
        Insert("600007", Granularity.Day, Days[2]);
        SqliteStockMetaUpsert.Upsert(_dbPath, [("600007", "x")], SqliteStockMetaUpsert.TypeStock);

        Assert.Empty(_audit.FindGaps(["600007"], Granularity.Day, Anchor));
    }

    // ─────────── 终点：个股/ETF 延伸到日历最后一天（2026-10-16，见 doc/ipo-issue-design.md §7）───────────

    private void Meta(string code, string type) => SqliteStockMetaUpsert.Upsert(_dbPath, [(code, code)], type);

    /// <summary>某个口径悄悄停更了：最后一根之后缺的日子以前永远查不出来。</summary>
    [Fact]
    public void 个股最后几天没有_算空洞()
    {
        Meta("600008", SqliteStockMetaUpsert.TypeStock);
        Insert("600008", Granularity.DayRaw, Days[0]);

        Assert.Equal([Days[1], Days[2]], _audit.FindGaps(["600008"], Granularity.DayRaw, Anchor)["600008"]);
    }

    [Fact]
    public void ETF也延伸()
    {
        Meta("sh510300", SqliteStockMetaUpsert.TypeEtf);
        Insert("sh510300", Granularity.Day, Days[0], Days[1]);

        Assert.Equal([Days[2]], _audit.FindGaps(["sh510300"], Granularity.Day, Anchor)["sh510300"]);
    }

    /// <summary>板块指数本地合成、板块撤销后自然不再更新；指数不停牌——都不延伸。名册里没有类型的也不延伸。</summary>
    [Fact]
    public void 板块和指数和没有类型的不延伸()
    {
        Meta("BK0001", SqliteStockMetaUpsert.TypeBoard);
        Insert("BK0001", Granularity.Day, Days[0]);
        Meta("sz399001", SqliteStockMetaUpsert.TypeIndex);
        Insert("sz399001", Granularity.Day, Days[0]);
        Insert("600009", Granularity.Day, Days[0]);       // 名册里没有

        Assert.Empty(_audit.FindGaps(["BK0001", "sz399001", "600009"], Granularity.Day, Anchor));
    }

    /// <summary>day_adj 落后于不复权是【重算回测序列】还没跑，体检另有一行专门报——不延伸，免得报两遍。</summary>
    [Fact]
    public void 回测序列不延伸()
    {
        Meta("600010", SqliteStockMetaUpsert.TypeStock);
        Insert("600010", Granularity.DayAdj, Days[0]);

        Assert.Empty(_audit.FindGaps(["600010"], Granularity.DayAdj, Anchor));
    }

    [Fact]
    public void 老manifest没有口径字段时_默认按前复权()
    {
        var restored = System.Text.Json.JsonSerializer.Deserialize<MissingBarRange>(
            """{"Code":"600006","From":"2026-09-01T00:00:00","To":"2026-09-01T00:00:00","Days":1,"Tries":1}""")!;
        Assert.Equal(Granularity.Day, restored.Granularity);
    }
}
