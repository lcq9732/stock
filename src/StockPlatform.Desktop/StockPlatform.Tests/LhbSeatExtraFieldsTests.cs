using System.Reflection;
using System.Text.Json;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 龙虎榜席位的三个"白拿"字段（2026-09-06）。
///
/// 背景：<c>RPT_BILLBOARD_DAILYDETAILSBUY/SELL</c> 是用 <c>columns=ALL</c> 请求的，一次就返回
/// 20 个字段，但当初只解析了 13 个。补 <c>TOTAL_BUYRIO</c>/<c>TOTAL_SELLRIO</c>/<c>CHANGE_TYPE</c>
/// **不需要多发一次请求**，纯粹是解析层的事。
///
/// 这里的 JSON 是 2026-09-06 从接口实抓的原样响应（600127，2026-09-04 那天的榜），
/// 不是手编的——手编样本最容易把口径写成自己以为的样子，而这三个字段的坑正好在口径上：
/// <c>TOTAL_BUYRIO</c> 是**小数**（0.0103 = 1.03%），跟同一条记录里 <c>CHANGE_RATE</c>
/// 用百分数（-2.92 = -2.92%）不一致。<see cref="RatioIsFractionOfAccumAmount"/> 用接口自己
/// 给的 ACCUM_AMOUNT 把这个关系钉死，将来谁改解析改错了单位，这条会红。
/// </summary>
public class LhbSeatExtraFieldsTests : IDisposable
{
    // 实抓样本：买方榜一行
    private const string BuyJson = """
    {"SECURITY_CODE":"600127","SECUCODE":"600127.SH","TRADE_DATE":"2026-09-04 00:00:00",
     "OPERATEDEPT_CODE":"10281327","OPERATEDEPT_NAME":"东方财富证券股份有限公司山南香曲东路证券营业部",
     "EXPLANATION":"有价格涨跌幅限制的日换手率达到20%的前五只证券","CHANGE_RATE":-2.9203,
     "CLOSE_PRICE":12.3,"ACCUM_AMOUNT":3217716926,"ACCUM_VOLUME":261593814,
     "BUY":33075686.85,"SELL":null,"NET":33075686.85,
     "RISE_PROBABILITY_3DAY":34.163701067616,"TOTAL_BUYER_SALESTIMES_3DAY":281,
     "CHANGE_TYPE":"137001004001","OPERATEDEPT_CODE_OLD":"80276317",
     "TOTAL_BUYRIO":0.01027924072,"TOTAL_SELLRIO":null,"TRADE_ID":100407981}
    """;

    // 实抓样本：卖方榜一行（同股同日同一张榜，TRADE_ID 相同）
    private const string SellJson = """
    {"SECURITY_CODE":"600127","SECUCODE":"600127.SH","TRADE_DATE":"2026-09-04 00:00:00",
     "OPERATEDEPT_CODE":"10135341","OPERATEDEPT_NAME":"中信证券股份有限公司上海分公司",
     "EXPLANATION":"有价格涨跌幅限制的日换手率达到20%的前五只证券","CHANGE_RATE":-2.9203,
     "CLOSE_PRICE":12.3,"ACCUM_AMOUNT":3217716926,"ACCUM_VOLUME":261593814,
     "BUY":null,"SELL":35149327.09,"NET":-35149327.09,
     "RISE_PROBABILITY_3DAY":42.690058479532,"TOTAL_BUYER_SALESTIMES_3DAY":855,
     "CHANGE_TYPE":"137001004001","OPERATEDEPT_CODE_OLD":"80139000",
     "TOTAL_BUYRIO":null,"TOTAL_SELLRIO":0.010923685302,"TRADE_ID":100407981}
    """;

    private const double AccumAmount = 3217716926d;   // 接口给的当日总成交额

    private readonly string _dbPath;

    public LhbSeatExtraFieldsTests()
        => _dbPath = Path.Combine(Path.GetTempPath(), $"lhbseat_{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    /// <summary>Parse 是私有静态的，走反射——为了测试把它开成 public 不值得。</summary>
    private static LhbSeat Parse(string json, bool isBuy)
    {
        using var doc = JsonDocument.Parse(json);
        var m = typeof(EastMoneyLhbSeatProvider)
            .GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        var seat = m!.Invoke(null, new object[] { doc.RootElement.Clone(), isBuy }) as LhbSeat;
        Assert.NotNull(seat);
        return seat!;
    }

    [Fact]
    public void ParsesThreeAddedFields()
    {
        var buy = Parse(BuyJson, isBuy: true);
        Assert.Equal(0.01027924072, buy.BuyRatio!.Value, 12);
        Assert.Null(buy.SellRatio);                      // 买方榜不给卖出占比
        Assert.Equal("137001004001", buy.ChangeType);

        var sell = Parse(SellJson, isBuy: false);
        Assert.Null(sell.BuyRatio);
        Assert.Equal(0.010923685302, sell.SellRatio!.Value, 12);
        Assert.Equal("137001004001", sell.ChangeType);
    }

    /// <summary>
    /// 口径钉死：ratio = 该席位金额 / 当日总成交额，是**小数**。
    /// 用接口自己给的 ACCUM_AMOUNT 反算，两边必须对上——这同时也证明了
    /// 不单独存 ACCUM_AMOUNT 是安全的（需要时 Buy / BuyRatio 就能反推出来）。
    /// </summary>
    [Fact]
    public void RatioIsFractionOfAccumAmount()
    {
        var buy = Parse(BuyJson, isBuy: true);
        Assert.Equal(buy.Buy!.Value / AccumAmount, buy.BuyRatio!.Value, 10);
        Assert.InRange(buy.BuyRatio.Value, 0d, 1d);      // 小数域，不是 0~100

        var sell = Parse(SellJson, isBuy: false);
        Assert.Equal(sell.Sell!.Value / AccumAmount, sell.SellRatio!.Value, 10);

        // 反推当日总成交额
        Assert.Equal(AccumAmount, buy.Buy.Value / buy.BuyRatio.Value, 0);
    }

    /// <summary>三列要真的落到库里并读得回来——建表和 UPSERT 两条路径都覆盖到。</summary>
    [Fact]
    public void RoundTripsThroughSqlite()
    {
        var repo = new SqliteLhbSeatRepository(_dbPath);
        repo.EnsureSchema();
        var buy = Parse(BuyJson, isBuy: true);
        var sell = Parse(SellJson, isBuy: false);
        Assert.Equal(2, repo.Upsert(new[] { buy, sell }));

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_buy, buy_ratio, sell_ratio, change_type FROM LhbSeat ORDER BY is_buy DESC";
        using var r = cmd.ExecuteReader();

        Assert.True(r.Read());
        Assert.Equal(1L, r.GetInt64(0));
        Assert.Equal(0.01027924072, r.GetDouble(1), 12);
        Assert.True(r.IsDBNull(2));
        Assert.Equal("137001004001", r.GetString(3));

        Assert.True(r.Read());
        Assert.Equal(0L, r.GetInt64(0));
        Assert.True(r.IsDBNull(1));
        Assert.Equal(0.010923685302, r.GetDouble(2), 12);
    }

    /// <summary>
    /// 迁移路径：老库（没有这三列）调 EnsureSchema 之后要能补上。
    /// 生产库里已经有 177 万行，走的正是这条路而不是 CREATE TABLE。
    /// </summary>
    [Fact]
    public void EnsureSchemaAddsColumnsToLegacyTable()
    {
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // 复刻加列之前的老表结构
            cmd.CommandText = """
                CREATE TABLE LhbSeat (
                    trade_date TEXT NOT NULL, code TEXT NOT NULL, name TEXT,
                    is_buy INTEGER NOT NULL, seat_code TEXT NOT NULL, seat_name TEXT,
                    buy REAL, sell REAL, net REAL, explanation TEXT NOT NULL,
                    rise_prob_3day REAL, times_3day INTEGER, trade_id TEXT,
                    seq INTEGER NOT NULL, close_price REAL, change_rate REAL, fetched_at TEXT,
                    PRIMARY KEY (trade_date, code, is_buy, explanation, seq)
                );
                """;
            cmd.ExecuteNonQuery();
        }

        new SqliteLhbSeatRepository(_dbPath).EnsureSchema();

        using var c2 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        c2.Open();
        using var q = c2.CreateCommand();
        q.CommandText = "SELECT name FROM pragma_table_info('LhbSeat')";
        var cols = new List<string>();
        using (var rd = q.ExecuteReader()) while (rd.Read()) cols.Add(rd.GetString(0));
        Assert.Contains("buy_ratio", cols);
        Assert.Contains("sell_ratio", cols);
        Assert.Contains("change_type", cols);
    }
}
