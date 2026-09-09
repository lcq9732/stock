using System.Text.Json;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 龙虎榜概要从新浪换到东财（2026-09-09）。
///
/// 这里的 JSON 全是从 <c>RPT_DAILYBILLBOARD_DETAILSNEW</c> 实抓的原样响应，不是手编的——
/// 换源这件事的全部风险都在**口径**上，手编样本只会把口径写成自己以为的样子。
///
/// 三类各盯一件事：
///   · <b>解析</b>：单位换算（东财给元、库里存万元）和滞后字段的 null。
///   · <b>等价</b>：同一天同一只票，东财解析出来的值跟库里新浪那份历史对得上——
///     这是"敢换"的依据，钉在测试里，将来谁改了解析口径这条会红。
///   · <b>派生</b>：东财不给"对应值"和"成交量"，本地怎么算的。每条规则的命中率都在
///     <see cref="LhbDeviationDeriver"/> 的类注释里，这里锁住算法本身。
/// </summary>
public class LhbEastMoneySourceTests
{
    /// <summary>实抓样本：2026-09-08 的 000560 我爱我家，"日换手率达到20%的前5只证券"那一行。
    /// 选它是因为库里新浪那份同一天同一只票也有（见 <see cref="MatchesSinaOnTheSameRow"/>），
    /// 两边能对。</summary>
    private const string RowJson = """
    {"TRADE_DATE":"2026-09-08 00:00:00","SECURITY_CODE":"000560","SECUCODE":"000560.SZ",
     "SECURITY_NAME_ABBR":"我爱我家","CLOSE_PRICE":3.44,"CHANGE_RATE":9.9042,"TURNOVERRATE":27.6488,
     "EXPLANATION":"日换手率达到20%的前5只证券","EXPLAIN":"普通席位买入，成功率36.00%",
     "ACCUM_AMOUNT":2179212480,"FREE_MARKET_CAP":5169541941.84,
     "BILLBOARD_BUY_AMT":83902522,"BILLBOARD_SELL_AMT":25138378,"BILLBOARD_NET_AMT":58764144,
     "BILLBOARD_DEAL_AMT":109040900,"DEAL_AMOUNT_RATIO":39.103043404124,"DEAL_NET_RATIO":21.073348380637,
     "CHANGE_TYPE":"137001002001002","TRADE_ID":100409471,"TRADE_MARKET":"深交所主板",
     "D1_CLOSE_ADJCHRATE":null,"D2_CLOSE_ADJCHRATE":null,"D5_CLOSE_ADJCHRATE":null,
     "D10_CLOSE_ADJCHRATE":null,"D20_CLOSE_ADJCHRATE":null,"D30_CLOSE_ADJCHRATE":null}
    """;

    /// <summary>实抓样本：2026-07-15 的一行，滞后字段**已经填上了**。跟上面那行凑成一对，
    /// 证明 d1..d30 的空不是解析丢的，是东财当天本来就没算。</summary>
    private const string OldRowJson = """
    {"TRADE_DATE":"2026-07-15 00:00:00","SECURITY_CODE":"920367","SECURITY_NAME_ABBR":"某北交所股",
     "EXPLANATION":"当日换手率达到20%的前5只股票","CLOSE_PRICE":10.0,"CHANGE_RATE":5.0,
     "TURNOVERRATE":21.0,"ACCUM_AMOUNT":10000000,
     "D1_CLOSE_ADJCHRATE":20.61643836,"D2_CLOSE_ADJCHRATE":6.16438356,"D5_CLOSE_ADJCHRATE":5.2739726,
     "D10_CLOSE_ADJCHRATE":-29.79452055,"D20_CLOSE_ADJCHRATE":-17.73972603,"D30_CLOSE_ADJCHRATE":-12.67123288}
    """;

    private static LhbRow Parse(string json) =>
        EastMoneyLhbProvider.Parse(JsonDocument.Parse(json).RootElement)!;

    // ─────────────────── 解析 ───────────────────

    [Fact]
    public void ParsesCoreFields()
    {
        var r = Parse(RowJson);

        Assert.Equal("000560", r.StockCode);
        Assert.Equal(new DateTime(2026, 9, 8), r.TradeDate);
        Assert.Equal("我爱我家", r.StockName);
        Assert.Equal(3.44, r.ClosePrice);
        // 上榜原因存的是**交易所原文**，不是新浪那种归并过的粗类——这正是换源的目的
        Assert.Equal("日换手率达到20%的前5只证券", r.Reason);
        Assert.Equal(LhbSources.EastMoney, r.Source);
        Assert.Equal("普通席位买入，成功率36.00%", r.Explain);
        Assert.Equal("100409471", r.TradeId);
    }

    /// <summary>
    /// 成交额：东财 <c>ACCUM_AMOUNT</c> 是**元**，库里这一列历史上存的是**万元**（新浪口径）。
    /// 不换算的话同一列里会并存两个量纲，而且差 10000 倍这种错在图表上一眼看不出来——
    /// 只会让所有按成交额排序/筛选的分析悄悄失真。
    /// </summary>
    [Fact]
    public void ConvertsAmountToWanYuan()
    {
        Assert.Equal(217921.248, Parse(RowJson).Amount, 3);
    }

    /// <summary>东财这张表没有"对应值"和"成交量"两列，provider 一律留 null 不瞎填——
    /// 对应值由 <see cref="LhbDeviationDeriver"/> 用本地日K补，成交量则一直留空
    /// （见 <see cref="DoesNotDeriveVolumeBecauseBarUnitsAreInconsistent"/>）。</summary>
    [Fact]
    public void LeavesDeviationAndVolumeForTheDeriver()
    {
        var r = Parse(RowJson);
        Assert.Null(r.Deviation);
        Assert.Null(r.Volume);
        Assert.Equal("", r.DeviationSource);
    }

    /// <summary>
    /// 上榜后 N 日涨跌幅是**滞后字段**：当天抓一律 null，过了 N 个交易日东财才填上。
    /// 这也是龙虎榜增量要回看 31 个交易日的唯一理由——只靠"抓当天"，这六列永远是空的。
    /// </summary>
    [Fact]
    public void LaggingFieldsAreNullOnTheDayAndFilledLater()
    {
        Assert.Null(Parse(RowJson).D1Chg);
        Assert.Null(Parse(RowJson).D30Chg);

        var old = Parse(OldRowJson);
        Assert.Equal(20.61643836, old.D1Chg!.Value, 6);
        Assert.Equal(-12.67123288, old.D30Chg!.Value, 6);
    }

    /// <summary>主键三列缺任何一个都得丢掉——写进去就是一条永远对不上、也删不掉的脏行。</summary>
    [Theory]
    [InlineData("""{"SECURITY_CODE":"","TRADE_DATE":"2026-09-08 00:00:00","EXPLANATION":"x"}""")]
    [InlineData("""{"SECURITY_CODE":"000560","TRADE_DATE":"","EXPLANATION":"x"}""")]
    [InlineData("""{"SECURITY_CODE":"000560","TRADE_DATE":"2026-09-08 00:00:00","EXPLANATION":""}""")]
    public void DropsRowsMissingAnyPrimaryKeyPart(string json)
    {
        Assert.Null(EastMoneyLhbProvider.Parse(JsonDocument.Parse(json).RootElement));
    }

    // ─────────────────── 两源等价 ───────────────────

    /// <summary>
    /// 2026-09-08 的 000560，**库里新浪那份历史**记的是：收盘 3.44、成交额 217921.248 万元、
    /// 成交量 64772.4081 万股。东财同一天同一只票解析出来必须对得上——这是当初敢换源的依据。
    ///
    /// 全量比对的结论（2026-09-09，09-08 单日）：两源票集 55 vs 55 双向零差异、55 只收盘价与
    /// 简称全对上、成交额换算比值精确 1.000000。那次比对没法搬进单元测试（要连库连网），
    /// 就把其中一行钉在这儿——口径真被改动时，这条是最先红的。
    /// </summary>
    [Fact]
    public void MatchesSinaOnTheSameRow()
    {
        const double sinaClose = 3.44, sinaAmountWan = 217921.248;
        var r = Parse(RowJson);

        Assert.Equal(sinaClose, r.ClosePrice);
        Assert.Equal(sinaAmountWan, r.Amount, 3);
    }

    /// <summary>
    /// 成交量**故意不派生**，东财源下永远是 null。
    ///
    /// 本来是要从本地日K补的（除以 100 换成万股），2026-09-08 的 000560 还算得严丝合缝。
    /// 但 59 只全跑一遍只对上 39 只——Bar 表的 volume 单位在**科创板是"股"、其余板块是"手"**
    /// （09-08 全市场：主板 3203 只、创业板 1404 只、北交所 342 只都是手，688/689 的 613 只是股），
    /// 而且有些行的量额本身就少了一大截（603999 记 6914 万，实际 2.13 亿）。
    /// 在这里按板块判单位等于把 Bar 的毛病抹平在一个角落，别处照样踩，所以宁可留空。
    ///
    /// 这条测试是那个决定的守卫：哪天有人"顺手"把派生加回来，它会红。
    /// </summary>
    [Fact]
    public void DoesNotDeriveVolumeBecauseBarUnitsAreInconsistent()
    {
        var r = Parse(RowJson);
        new LhbDeviationDeriver((code, gran) => gran == Granularity.DayRaw
            ? [Bar(new DateTime(2026, 9, 5), 3.13), Bar(new DateTime(2026, 9, 8), 3.44, volume: 6477241)]
            : []).Apply([r]);

        Assert.Null(r.Volume);
    }

    // ─────────────────── 上榜原因分类 ───────────────────

    /// <summary>
    /// 分类的判断顺序不能改，这些原文是交易所真用过的（2008/2015/2021/2026 四段抽样共 53 种）。
    /// 最容易错的是前两条：它们同时含"涨跌幅"，先判到那个泛规则就全归错。
    /// </summary>
    [Theory]
    [InlineData("有价格涨跌幅限制的日换手率达到20%的前五只证券", LhbDeviationDeriver.DeviationKind.Turnover)]
    [InlineData("有价格涨跌幅限制的日价格振幅达到15%的前三只证券", LhbDeviationDeriver.DeviationKind.Amplitude)]
    [InlineData("日换手率达到20%的前5只证券", LhbDeviationDeriver.DeviationKind.Turnover)]
    [InlineData("日振幅值达到15%的前五只证券", LhbDeviationDeriver.DeviationKind.Amplitude)]
    [InlineData("日涨幅偏离值达到7%的前5只证券", LhbDeviationDeriver.DeviationKind.DailyDeviation)]
    [InlineData("有价格涨跌幅限制的日收盘价格跌幅偏离值达到7%的前五只证券", LhbDeviationDeriver.DeviationKind.DailyDeviation)]
    [InlineData("连续三个交易日内，涨幅偏离值累计达到20%的证券", LhbDeviationDeriver.DeviationKind.CumulativeDeviation)]
    [InlineData("非S证券连续三个交易日内收盘价格涨幅偏离值累计达到20%的证券", LhbDeviationDeriver.DeviationKind.CumulativeDeviation)]
    [InlineData("日涨幅达到15%的前5只证券", LhbDeviationDeriver.DeviationKind.ChangeRate)]
    [InlineData("当日收盘价涨幅达到20%的前5只股票", LhbDeviationDeriver.DeviationKind.ChangeRate)]
    // 对应值是**倍数**不是换手率，必须在"换手率"那一条之前被拦下
    [InlineData("连续三个交易日内，日均换手率与前五个交易日的日均换手率的比值达到30倍，且换手率累计达20%的证券",
                LhbDeviationDeriver.DeviationKind.None)]
    [InlineData("严重异常期间日收盘价格涨幅偏离值累计达到100%的证券", LhbDeviationDeriver.DeviationKind.None)]
    [InlineData("退市整理的证券", LhbDeviationDeriver.DeviationKind.None)]
    [InlineData("无价格涨跌幅限制的证券", LhbDeviationDeriver.DeviationKind.None)]
    [InlineData("单只标的证券的当日融资买入数量达到当日该证券总交易量的50%以上", LhbDeviationDeriver.DeviationKind.None)]
    public void ClassifiesRealExchangeWordings(string reason, LhbDeviationDeriver.DeviationKind expected)
    {
        Assert.Equal(expected, LhbDeviationDeriver.Classify(reason));
    }

    [Theory]
    [InlineData("连续三个交易日内，涨幅偏离值累计达到20%的证券", 3)]
    [InlineData("有价格涨跌幅限制的连续3个交易日内收盘价格涨幅偏离值累计达到30%的证券", 3)]
    [InlineData("当日有涨跌幅限制的A股，连续2个交易日触及涨幅限制", 2)]
    [InlineData("日涨幅偏离值达到7%的前5只证券", null)]
    public void ParsesConsecutiveDays(string reason, int? expected)
    {
        Assert.Equal(expected, LhbDeviationDeriver.ParseConsecutiveDays(reason));
    }

    // ─────────────────── 派生 ───────────────────

    private static Bar Bar(DateTime d, double close, double high = 0, double low = 0, double volume = 0) =>
        new()
        {
            PeriodStart = d, Close = close, Volume = volume,
            High = high == 0 ? close : high,
            Low = low == 0 ? close : low,
        };

    private static LhbRow Row(string code, string reason, double? changeRate = null, double? turnover = null) =>
        new()
        {
            StockCode = code, TradeDate = new DateTime(2026, 9, 8), Reason = reason,
            ChangeRate = changeRate, TurnoverRate = turnover, Source = LhbSources.EastMoney,
        };

    /// <summary>换手率和"涨跌幅达X%"两类，东财自己就给了值，不该本地再算一遍。</summary>
    [Fact]
    public void TakesTurnoverAndChangeRateStraightFromTheSource()
    {
        var turnoverRow = Row("600000", "日换手率达到20%的前5只证券", turnover: 27.6488);
        var changeRow = Row("600000", "日涨幅达到15%的前5只证券", changeRate: 15.3);
        new LhbDeviationDeriver((_, _) => []).Apply([turnoverRow, changeRow]);

        Assert.Equal(27.6488, turnoverRow.Deviation!.Value, 4);
        Assert.Equal(LhbDeviationSources.FromSource, turnoverRow.DeviationSource);
        Assert.Equal(15.3, changeRow.Deviation!.Value, 4);
        Assert.Equal(LhbDeviationSources.FromSource, changeRow.DeviationSource);
    }

    /// <summary>
    /// 振幅 ＝ (最高 − 最低) / **最低**。反直觉但实测如此：拿 312 行真值比过，
    /// 除以最低命中 93.6%，除以前收 0.0%，除以收盘 9.9%。
    /// </summary>
    [Fact]
    public void DerivesAmplitudeAgainstTheLow()
    {
        var row = Row("600000", "日振幅值达到15%的前五只证券");
        new LhbDeviationDeriver((code, gran) => gran == Granularity.DayRaw
            ? [Bar(new DateTime(2026, 9, 5), 10), Bar(new DateTime(2026, 9, 8), 11, high: 12, low: 10)]
            : []).Apply([row]);

        Assert.Equal(20.0, row.Deviation!.Value, 6);   // (12-10)/10
        Assert.Equal(LhbDeviationSources.Derived, row.DeviationSource);
    }

    /// <summary>
    /// 单日偏离值 ＝ 个股涨跌幅 − **对应指数**涨跌幅。沪市对上证综指实测中位误差 0.0039、
    /// 命中 91.4%。这里同时锁住"深市主板用深证综指 399106 而不是深证成指 399001"——
    /// 配错指数的话命中率会从 70.6% 掉到 2.3%，而结果看上去仍然像个正常的数。
    /// </summary>
    [Fact]
    public void DerivesDailyDeviationAgainstTheRightBenchmark()
    {
        var asked = new List<string>();
        var row = Row("000560", "日涨幅偏离值达到7%的前5只证券");
        new LhbDeviationDeriver((code, gran) =>
        {
            asked.Add(code);
            return code switch
            {
                // 个股：3.13 → 3.44，涨 9.9042%
                "000560" when gran == Granularity.DayRaw =>
                    [Bar(new DateTime(2026, 9, 5), 3.13), Bar(new DateTime(2026, 9, 8), 3.44)],
                // 深证综指：跌 0.2358%
                "sz399106" => [Bar(new DateTime(2026, 9, 5), 1000), Bar(new DateTime(2026, 9, 8), 997.642)],
                _ => [],
            };
        }).Apply([row]);

        Assert.Contains("sz399106", asked);
        Assert.DoesNotContain("sz399001", asked);
        Assert.Equal(10.14, row.Deviation!.Value, 2);
        Assert.Equal(LhbDeviationSources.Derived, row.DeviationSource);
    }

    /// <summary>创业板/科创板/北交所的派生规则没有可信真值能验（唯一的历史对照——新浪那份——
    /// 对这三个板块本身就是错配的），所以算出来的值必须标"未核验"，别让下游当成验证过的数用。</summary>
    [Fact]
    public void MarksNonMainBoardDerivationsAsUnverified()
    {
        var row = Row("300750", "日涨幅偏离值达到7%的前5只证券");
        new LhbDeviationDeriver((code, gran) => code switch
        {
            "300750" when gran == Granularity.DayRaw =>
                [Bar(new DateTime(2026, 9, 5), 100), Bar(new DateTime(2026, 9, 8), 110)],
            "sz399102" => [Bar(new DateTime(2026, 9, 5), 1000), Bar(new DateTime(2026, 9, 8), 1010)],
            _ => [],
        }).Apply([row]);

        Assert.Equal(9.0, row.Deviation!.Value, 6);    // +10% − (+1%)
        Assert.Equal(LhbDeviationSources.DerivedUnverified, row.DeviationSource);
    }

    /// <summary>
    /// 连续N日累计偏离值**故意不算**：起算日由交易所判定（哪三天算"连续三个交易日"是它认定
    /// 异动的窗口），本地复现不出来。试过的所有算法最好也只有 54.9%——
    /// 留空是"没有"，写一个一半对一半错的值是"有毒"。
    /// </summary>
    [Fact]
    public void RefusesToGuessCumulativeDeviation()
    {
        var row = Row("600000", "连续三个交易日内，涨幅偏离值累计达到20%的证券");
        new LhbDeviationDeriver((code, gran) => gran == Granularity.DayRaw
            ? [Bar(new DateTime(2026, 9, 3), 10), Bar(new DateTime(2026, 9, 4), 11),
               Bar(new DateTime(2026, 9, 5), 12), Bar(new DateTime(2026, 9, 8), 13)]
            : []).Apply([row]);

        Assert.Null(row.Deviation);
        Assert.Equal("", row.DeviationSource);
    }

    /// <summary>停牌/退市这类本地没有日K的，留空——不折成 0。0 在数值列里是个有意义的值，
    /// 拿它冒充"不知道"会让下游统计悄悄失真。</summary>
    [Fact]
    public void LeavesNullWhenLocalBarsAreMissing()
    {
        var row = Row("600000", "日涨幅偏离值达到7%的前5只证券");
        new LhbDeviationDeriver((_, _) => []).Apply([row]);

        Assert.Null(row.Deviation);
        Assert.Equal("", row.DeviationSource);
    }
}
