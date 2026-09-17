using System.Reflection;
using System.Text.Json;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 市场事件三表补的字段（2026-09-06）。跟 <see cref="LhbSeatExtraFieldsTests"/> 同一个来路：
/// datacenter 一直用 <c>columns=ALL</c>，这些字段本来就跟着回来了，当初没解析而已，
/// 补它们不产生任何新请求。
///
/// JSON 全是 2026-09-06 从接口实抓的原样响应，不是手编——这批字段的坑正好都在口径上，
/// 手编样本只会把口径写成自己以为的样子：
/// <list type="bullet">
/// <item>同一条大宗交易记录里 PREMIUM_RATIO 是小数、DISCOUNT_RATIO 是百分数，基准还不一样；</item>
/// <item>ShareLift 的比例是小数，但同一条里的涨跌幅是百分数；</item>
/// <item>HolderChange 的 CHANGE_RATE 根本不是变动比例而是股价涨跌幅（已修）。</item>
/// </list>
/// </summary>
public class MarketEventExtraFieldsTests : IDisposable
{
    /// <summary>实抓：000338 潍柴动力 2020-06-10 —— 老数据，滞后字段**有值**。</summary>
    private const string BlockTradeOldJson =
        "{\"SECUCODE\":\"000338.SZ\",\"SECURITY_CODE\":\"000338\",\"SECURITY_NAME_ABBR\":\"潍柴动力\"," +
        "\"TRADE_DATE\":\"2020-06-10 00:00:00\",\"DEAL_PRICE\":15.05,\"PREMIUM_RATIO\":0.075," +
        "\"DEAL_VOLUME\":18000000,\"DEAL_AMT\":270900000,\"DAILY_RANK\":1," +
        "\"BUYER_NAME\":\"中信证券股份有限公司宁波甬江大道证券营业部\"," +
        "\"SELLER_NAME\":\"国泰君安证券股份有限公司顺德大良证券营业部\"," +
        "\"BUYER_CODE\":\"10138484\",\"SELLER_CODE\":\"10064718\"," +
        "\"CLOSE_PRICE\":14,\"PRE_CLOSE_PRICE\":14.2,\"TURNOVER_RATE\":0.455830214199,\"CHANGE_RATE\":-1.4084," +
        "\"CHANGE_RATE_1DAYS\":-2.07142857,\"CHANGE_RATE_5DAYS\":-4.42857143," +
        "\"CHANGE_RATE_10DAYS\":-3.28571429,\"CHANGE_RATE_20DAYS\":9.5," +
        "\"FREE_SHARES_RATIO\":0.290883628308,\"TOTAL_SHARES_RATIO\":0.226875297468," +
        "\"TOTAL_SHARES\":7933873895,\"UNLIMITED_A_SHARES\":4245001625," +
        "\"DISCOUNT_RATIO\":5.985915492958,\"PREMIUM_TURNOVER\":270900000,\"DISCOUNT_TURNOVER\":0}";

    /// <summary>实抓：000007 全新好 2026-09-04 —— 最新交易日，滞后字段**全为 null**。</summary>
    private const string BlockTradeFreshJson =
        "{\"SECURITY_CODE\":\"000007\",\"SECURITY_NAME_ABBR\":\"全新好\",\"TRADE_DATE\":\"2026-09-04 00:00:00\"," +
        "\"DEAL_PRICE\":12.41,\"CLOSE_PRICE\":12.42,\"PRE_CLOSE_PRICE\":12.5," +
        "\"PREMIUM_RATIO\":-0.000805152979,\"DISCOUNT_RATIO\":-0.72," +
        "\"CHANGE_RATE_1DAYS\":null,\"CHANGE_RATE_5DAYS\":null," +
        "\"CHANGE_RATE_10DAYS\":null,\"CHANGE_RATE_20DAYS\":null," +
        "\"FREE_SHARES_RATIO\":0.230914855447,\"TOTAL_SHARES_RATIO\":0.230914855447," +
        "\"BUYER_CODE\":\"10427018\",\"SELLER_CODE\":\"10427018\",\"DAILY_RANK\":12," +
        "\"DEAL_VOLUME\":800000,\"DEAL_AMT\":9928000," +
        "\"BUYER_NAME\":\"华鑫证券有限责任公司重庆江北嘴证券营业部\"," +
        "\"SELLER_NAME\":\"华鑫证券有限责任公司重庆江北嘴证券营业部\"," +
        "\"TURNOVER_RATE\":0.230728933663,\"CHANGE_RATE\":-0.64}";

    /// <summary>实抓：000065 北方国际 2026-08-03 解禁。</summary>
    private const string ShareLiftJson =
        "{\"SECUCODE\":\"000065.SZ\",\"SECURITY_CODE\":\"000065\",\"SECURITY_NAME_ABBR\":\"北方国际\"," +
        "\"FREE_DATE\":\"2026-08-03 00:00:00\",\"BATCH_HOLDER_NUM\":14," +
        "\"CURRENT_FREE_SHARES\":9005.6285,\"LIFT_MARKET_CAP\":83031.89477," +
        "\"FREE_SHARES_TYPE\":\"定向增发机构配售股份\",\"B20_ADJCHRATE\":-4.81064483,\"A20_ADJCHRATE\":10," +
        "\"FREE_SHARES\":106501.8573,\"NON_FREE_SHARES\":9642.3586,\"FREE_RATIO\":0.084558417368," +
        "\"NEW\":9.22,\"TOTAL_RATIO\":0.077538329655,\"ABLE_FREE_SHARES\":9005.6285," +
        "\"ALIFT_MARKET_CAP\":83031.89477,\"TOTALSHARES_RATIO\":0.077538329655}";

    /// <summary>
    /// 实抓：002203 海亮股份 2026-09-05 增持。挑它是因为它同时证明两件事——
    /// CHANGE_FREE_RATIO 有值，且 CHANGE_RATE 为负却是"增持"。
    /// </summary>
    private const string HolderChangeJson =
        "{\"SECURITY_CODE\":\"002203\",\"SECURITY_NAME_ABBR\":\"海亮股份\",\"NOTICE_DATE\":\"2026-09-05 00:00:00\"," +
        "\"HOLDER_NAME\":\"海亮集团有限公司\",\"DIRECTION\":\"增持\"," +
        "\"CHANGE_NUM\":1519.76,\"CHANGE_NUM_SYMBOL\":1519.76," +
        "\"CHANGE_RATE\":-3.1362,\"AFTER_CHANGE_RATE\":0.663142359589," +
        "\"CHANGE_FREE_RATIO\":0.68,\"HOLD_RATIO\":28.04,\"FREE_SHARES_RATIO\":0.68," +
        "\"END_DATE\":\"2026-09-04 00:00:00\",\"START_DATE\":null," +
        "\"CLOSE_PRICE\":12.5,\"REAL_PRICE\":12.61,\"TRADE_AVERAGE_PRICE\":12.61," +
        "\"AFTER_HOLDER_NUM\":42000.0,\"CHANGE_RATE_QUOTES\":null}";

    private readonly string _dbPath;
    public MarketEventExtraFieldsTests()
        => _dbPath = Path.Combine(Path.GetTempPath(), $"mktevt_{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    /// <summary>解析方法是私有静态的，走反射——为测试开成 public 不值得。</summary>
    private static T Parse<T>(string method, string json) where T : class
    {
        using var doc = JsonDocument.Parse(json);
        var m = typeof(EastMoneyMarketEventProvider)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        var v = m!.Invoke(null, new object[] { doc.RootElement.Clone() }) as T;
        Assert.NotNull(v);
        return v!;
    }

    // ── 大宗交易 ───────────────────────────────────────────────────

    [Fact]
    public void BlockTradeParsesAddedFields()
    {
        var x = Parse<BlockTrade>("ParseBlockTrade", BlockTradeOldJson);
        Assert.Equal("10138484", x.BuyerCode);
        Assert.Equal("10064718", x.SellerCode);
        Assert.Equal(5.985915492958, x.DiscountRatio!.Value, 10);
        Assert.Equal(0.290883628308, x.FreeSharesRatio!.Value, 10);
        Assert.Equal(0.226875297468, x.TotalSharesRatio!.Value, 10);
        Assert.Equal(-2.07142857, x.ChangeRate1D!.Value, 8);
        Assert.Equal(-4.42857143, x.ChangeRate5D!.Value, 8);
        Assert.Equal(-3.28571429, x.ChangeRate10D!.Value, 8);
        Assert.Equal(9.5, x.ChangeRate20D!.Value, 8);
    }

    /// <summary>
    /// 口径钉死：PremiumRatio 是**小数**、基准是当日收盘价；DiscountRatio 是**百分数**、
    /// 基准是**前收盘价**。两个样本都要成立，谁把单位或基准改错了这条会红。
    /// </summary>
    [Theory]
    [InlineData(true, 15.05, 14.0, 14.2)]
    [InlineData(false, 12.41, 12.42, 12.5)]
    public void PremiumAndDiscountUseDifferentUnitsAndBases(
        bool useOldSample, double dealPrice, double close, double preClose)
    {
        var x = Parse<BlockTrade>("ParseBlockTrade",
            useOldSample ? BlockTradeOldJson : BlockTradeFreshJson);
        Assert.Equal(dealPrice, x.DealPrice!.Value, 6);

        // PremiumRatio：小数，基准 = 当日收盘价
        Assert.Equal(dealPrice / close - 1, x.PremiumRatio!.Value, 6);
        // DiscountRatio：百分数，基准 = 前收盘价
        Assert.Equal((dealPrice / preClose - 1) * 100, x.DiscountRatio!.Value, 4);
    }

    /// <summary>
    /// TOTAL_SHARES_RATIO 是百分数：成交量 ÷ 总股本 × 100。用接口同一行给的 TOTAL_SHARES 反算。
    /// </summary>
    [Fact]
    public void SharesRatioIsPercentNotFraction()
    {
        var x = Parse<BlockTrade>("ParseBlockTrade", BlockTradeOldJson);
        const double totalShares = 7933873895d;
        Assert.Equal(x.DealVolume!.Value / totalShares * 100, x.TotalSharesRatio!.Value, 6);
        Assert.InRange(x.TotalSharesRatio.Value, 0d, 100d);
    }

    /// <summary>
    /// 滞后字段的核心事实：最新交易日那批，change_rate_1d/5d/10d/20d 一律是 null。
    /// 这就是为什么增量必须往前回看 30 天——只抓"水位线→今天"这四列永远填不上。
    /// </summary>
    [Fact]
    public void LaggingFieldsAreNullOnFreshData()
    {
        var fresh = Parse<BlockTrade>("ParseBlockTrade", BlockTradeFreshJson);
        Assert.Null(fresh.ChangeRate1D);
        Assert.Null(fresh.ChangeRate5D);
        Assert.Null(fresh.ChangeRate10D);
        Assert.Null(fresh.ChangeRate20D);
        // 非滞后字段同一行里是有值的——证明 null 不是解析错，是接口就没给
        Assert.NotNull(fresh.FreeSharesRatio);
        Assert.NotEmpty(fresh.BuyerCode);

        var old = Parse<BlockTrade>("ParseBlockTrade", BlockTradeOldJson);
        Assert.NotNull(old.ChangeRate20D);
    }

    // ── 限售解禁 ───────────────────────────────────────────────────

    [Fact]
    public void ShareLiftParsesAddedFields()
    {
        var x = BuildShareLift();
        Assert.Equal(106501.8573, x.PreFreeShares!.Value, 6);
        Assert.Equal(9642.3586, x.NonFreeShares!.Value, 6);
        Assert.Equal(-4.81064483, x.Before20Change!.Value, 8);
        Assert.Equal(10, x.After20Change!.Value, 8);
    }

    /// <summary>
    /// 两个口径一起钉死：
    /// ① LiftShares 取的是 CURRENT_FREE_SHARES（本次解禁），不是 FREE_SHARES（解禁前已流通）；
    /// ② FreeRatio 是**小数**，而同一条里的 Before20Change 是**百分数**。
    /// </summary>
    [Fact]
    public void ShareLiftRatioIsFractionWhileChangeIsPercent()
    {
        var x = BuildShareLift();
        Assert.Equal(9005.6285, x.LiftShares!.Value, 6);          // 本次解禁
        Assert.Equal(106501.8573, x.PreFreeShares!.Value, 6);     // 解禁前已流通（分母）
        Assert.Equal(x.LiftShares.Value / x.PreFreeShares.Value, x.FreeRatio!.Value, 8);
        Assert.InRange(x.FreeRatio.Value, 0d, 1d);                // 小数域
        Assert.Equal(-4.81064483, x.Before20Change!.Value, 8);    // 百分数域，同一条记录
    }

    /// <summary>
    /// 限售解禁的解析还留在 provider 的 lambda 里（那个方法不走 RunSlicedAsync），
    /// 所以这里按同样的字段映射构造对象，覆盖口径断言与仓储往返。
    /// 映射一旦改动，<see cref="ShareLiftRatioIsFractionWhileChangeIsPercent"/> 会先红。
    /// </summary>
    private static ShareLift BuildShareLift()
    {
        using var doc = JsonDocument.Parse(ShareLiftJson);
        var el = doc.RootElement;
        double? N(string k) => el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;
        return new ShareLift
        {
            Code = el.GetProperty("SECURITY_CODE").GetString()!,
            Name = el.GetProperty("SECURITY_NAME_ABBR").GetString()!,
            FreeDate = DateTime.Parse(el.GetProperty("FREE_DATE").GetString()!),
            ShareType = el.GetProperty("FREE_SHARES_TYPE").GetString()!,
            LiftShares = N("CURRENT_FREE_SHARES"),
            LiftMarketCap = N("LIFT_MARKET_CAP"),
            FreeRatio = N("FREE_RATIO"),
            TotalRatio = N("TOTALSHARES_RATIO"),
            HolderCount = (int?)N("BATCH_HOLDER_NUM"),
            PreFreeShares = N("FREE_SHARES"),
            NonFreeShares = N("NON_FREE_SHARES"),
            Before20Change = N("B20_ADJCHRATE"),
            After20Change = N("A20_ADJCHRATE"),
            FetchedAt = DateTime.Now,
        };
    }

    // ── 股东增减持 ─────────────────────────────────────────────────

    [Fact]
    public void HolderChangeParsesAddedFields()
    {
        var x = Parse<HolderChange>("ParseHolderChange", HolderChangeJson);
        Assert.Equal(0.68, x.ChangeFreeRatio!.Value, 6);
        Assert.Equal(12.5, x.ClosePrice!.Value, 6);
        Assert.Equal(12.61, x.RealPrice!.Value, 6);
        Assert.Null(x.ChangeRateQuotes);      // 覆盖不全，容忍空
    }

    /// <summary>
    /// 回归测试：ChangeRatio 必须取 AFTER_CHANGE_RATE，**不能**取 CHANGE_RATE。
    ///
    /// 这个样本是"增持"但 CHANGE_RATE = -3.1362（那是公告日股价涨跌幅）。取错字段的话，
    /// 一条增持记录的"变动比例"会是负数。原先就是这么错的，11 万行全中。
    /// </summary>
    [Fact]
    public void ChangeRatioIsNotStockPriceChange()
    {
        var x = Parse<HolderChange>("ParseHolderChange", HolderChangeJson);
        Assert.Equal("增持", x.Direction);
        Assert.Equal(0.663142359589, x.ChangeRatio!.Value, 10);
        Assert.True(x.ChangeRatio.Value > 0, "增持的变动比例必须为正——取成 CHANGE_RATE 就会是 -3.1362");
    }

    // ── 落库往返与老库迁移 ─────────────────────────────────────────

    [Fact]
    public void RoundTripsThroughSqlite()
    {
        var repo = new SqliteMarketEventRepository(_dbPath);
        repo.EnsureSchema();

        var bt = Parse<BlockTrade>("ParseBlockTrade", BlockTradeOldJson);
        Assert.Equal(1, repo.ReplaceBlockTradesForDay(bt.TradeDate, [bt]));
        Assert.Equal(1, repo.UpsertShareLifts(new[] { BuildShareLift() }));
        Assert.Equal(1, repo.UpsertHolderChanges(new[] { Parse<HolderChange>("ParseHolderChange", HolderChangeJson) }));

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        double? One(string sql)
        {
            using var c = conn.CreateCommand();
            c.CommandText = sql;
            var v = c.ExecuteScalar();
            return v == null || v is DBNull ? null : Convert.ToDouble(v);
        }
        string? OneStr(string sql)
        {
            using var c = conn.CreateCommand();
            c.CommandText = sql;
            return c.ExecuteScalar() as string;
        }

        Assert.Equal("10138484", OneStr("SELECT buyer_code FROM BlockTrade"));
        Assert.Equal(9.5, One("SELECT change_rate_20d FROM BlockTrade")!.Value, 6);
        Assert.Equal(5.985915492958, One("SELECT discount_ratio FROM BlockTrade")!.Value, 10);
        Assert.Equal(0.226875297468, One("SELECT total_shares_ratio FROM BlockTrade")!.Value, 10);

        Assert.Equal(106501.8573, One("SELECT pre_free_shares FROM ShareLift")!.Value, 6);
        Assert.Equal(-4.81064483, One("SELECT before20_change FROM ShareLift")!.Value, 8);

        Assert.Equal(0.68, One("SELECT change_free_ratio FROM HolderChange")!.Value, 6);
        Assert.Equal(12.61, One("SELECT real_price FROM HolderChange")!.Value, 6);
        Assert.Null(One("SELECT change_rate_quotes FROM HolderChange"));
    }

    /// <summary>
    /// 迁移路径：生产库里这三张表已有 57.9 万 / 3.1 万 / 11.1 万行，走的是 ALTER TABLE
    /// 而不是 CREATE TABLE。老表结构复刻加列之前的样子，EnsureSchema 之后要能补齐。
    /// </summary>
    [Fact]
    public void EnsureSchemaAddsColumnsToLegacyTables()
    {
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE BlockTrade (
                    trade_date TEXT NOT NULL, code TEXT NOT NULL, daily_rank INTEGER NOT NULL,
                    name TEXT, deal_price REAL, deal_volume REAL, deal_amount REAL,
                    premium_ratio REAL, close_price REAL, change_rate REAL, turnover_rate REAL,
                    buyer_name TEXT, seller_name TEXT, fetched_at TEXT,
                    PRIMARY KEY (trade_date, code, daily_rank)
                );
                CREATE TABLE ShareLift (
                    code TEXT NOT NULL, free_date TEXT NOT NULL, share_type TEXT NOT NULL,
                    name TEXT, lift_shares REAL, lift_market_cap REAL,
                    free_ratio REAL, total_ratio REAL, holder_count INTEGER, fetched_at TEXT,
                    PRIMARY KEY (code, free_date, share_type)
                );
                CREATE TABLE HolderChange (
                    code TEXT NOT NULL, notice_date TEXT NOT NULL, holder_name TEXT NOT NULL,
                    end_date TEXT NOT NULL, name TEXT, direction TEXT, change_shares REAL,
                    change_ratio REAL, after_shares REAL, after_ratio REAL,
                    start_date TEXT, average_price REAL, fetched_at TEXT,
                    PRIMARY KEY (code, notice_date, holder_name, end_date)
                );";
            cmd.ExecuteNonQuery();
        }

        new SqliteMarketEventRepository(_dbPath).EnsureSchema();

        using var c2 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        c2.Open();
        List<string> Cols(string table)
        {
            using var q = c2.CreateCommand();
            q.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
            var cols = new List<string>();
            using var rd = q.ExecuteReader();
            while (rd.Read()) cols.Add(rd.GetString(0));
            return cols;
        }

        var bt = Cols("BlockTrade");
        foreach (var c in new[] { "buyer_code", "seller_code", "discount_ratio", "free_shares_ratio",
                                  "total_shares_ratio", "change_rate_1d", "change_rate_5d",
                                  "change_rate_10d", "change_rate_20d" })
            Assert.Contains(c, bt);

        var sl = Cols("ShareLift");
        foreach (var c in new[] { "pre_free_shares", "non_free_shares", "before20_change", "after20_change" })
            Assert.Contains(c, sl);

        var hc = Cols("HolderChange");
        foreach (var c in new[] { "change_free_ratio", "close_price", "real_price", "change_rate_quotes" })
            Assert.Contains(c, hc);
    }
}
