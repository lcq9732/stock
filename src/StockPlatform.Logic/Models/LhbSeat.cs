namespace StockPlatform.Logic.Models;

/// <summary>
/// 龙虎榜**营业部席位明细**（东财 <c>RPT_BILLBOARD_DAILYDETAILSBUY</c> / <c>...SELL</c>）。
///
/// 现有的 <c>Lhb</c> 表（新浪）只有"某天某股上榜了、原因是涨跌幅偏离、成交额多少"，
/// <b>没有买卖前五营业部名单</b>——而龙虎榜的全部价值恰恰在于看**是谁在买**：
/// 是机构专用席位、还是知名游资、还是深股通。17 万行数据缺了这一块，等于只留了个壳。
///
/// 东财这两个接口给的比预期还多：除了营业部代码/名称和买卖净额，还带该营业部近期的
/// <see cref="RiseProbability3Day"/>（3 日胜率）和上榜频次，有营业部代码就能自建游资库、
/// 自己算某个席位的历史胜率。
/// </summary>
public class LhbSeat
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime TradeDate { get; set; }

    /// <summary>true=买方榜，false=卖方榜。两个接口的字段结构完全一样，靠这个区分。</summary>
    public bool IsBuy { get; set; }

    /// <summary>营业部代码——有它才能跨时间追踪同一个席位。</summary>
    public string SeatCode { get; set; } = "";
    public string SeatName { get; set; } = "";

    /// <summary>该席位当日买入额 / 卖出额 / 净额（元）。</summary>
    public double? Buy { get; set; }
    public double? Sell { get; set; }
    public double? Net { get; set; }

    /// <summary>上榜原因（"当日价格振幅达到30%的前5只股票"等）。同一股同一天可能因多个原因上榜。</summary>
    public string Explanation { get; set; } = "";

    /// <summary>该营业部近期上榜后 3 日上涨概率（%），东财算好的。</summary>
    public double? RiseProbability3Day { get; set; }
    /// <summary>该营业部近 3 日上榜次数。</summary>
    public int? Times3Day { get; set; }

    /// <summary>东财的榜单行 id（同股同日同一张榜共用一个 id，不能单独当主键）。</summary>
    public string TradeId { get; set; } = "";

    /// <summary>
    /// 同一张榜里的位次（按净额降序，0 起）——<b>主键的一部分</b>。
    ///
    /// 为什么非要它：龙虎榜的机构席位是**匿名**的，<see cref="SeatCode"/> 一律是 "0"、
    /// <see cref="SeatName"/> 一律是"机构专用"，但同一只股票同一天可能有 2~3 个不同机构上榜，
    /// 金额各不相同。另外"深股通投资者/机构投资者/自然人/中小投资者"这类投资者类别统计也共用
    /// 同一个营业部代码。只靠 (日期,股票,席位代码,名称,原因) 做主键，这些行会互相覆盖——
    /// 实测一天 415 行里会丢掉 31 行（7.5%），全量 264 万行就是丢十几万。
    ///
    /// 按净额降序定位次而不是按接口返回顺序，是为了**幂等**：重抓同一天得到同样的编号，
    /// UPSERT 覆盖而不是新增。
    /// </summary>
    public int Seq { get; set; }

    /// <summary>当日收盘价与涨跌幅（%），接口顺带给的，存着省得再关联。</summary>
    public double? ClosePrice { get; set; }
    public double? ChangeRate { get; set; }

    /// <summary>
    /// 该席位买入/卖出额占该股当日总成交额的比例（东财 <c>TOTAL_BUYRIO</c>/<c>TOTAL_SELLRIO</c>）。
    ///
    /// ⚠ 是**小数**不是百分数：0.0103 表示 1.03%。跟同样取自接口原值的 <see cref="ChangeRate"/>
    /// （-2.92 表示 -2.92%）口径不一致，混用会差 100 倍。
    ///
    /// 为什么值得单独存：同样是"某席位买了 3300 万"，占当日成交 1% 和占 30% 是两回事——
    /// 前者是噪音，后者才说明这个席位主导了当天的盘面。绝对金额脱离成交规模没法横向比较。
    ///
    /// 接口另外还给 ACCUM_AMOUNT（当日总成交额），**没有存**：它跟本字段是同一个信息，
    /// 当日总成交额 = <see cref="Buy"/> / BuyRatio 就能反推；而 ACCUM_AMOUNT 在同股同日的
    /// 每一行席位上重复出现，177 万行存下来纯属冗余。
    /// </summary>
    public double? BuyRatio { get; set; }
    /// <summary>见 <see cref="BuyRatio"/>。卖方榜才有值。</summary>
    public double? SellRatio { get; set; }

    /// <summary>
    /// 上榜类型代码（东财 <c>CHANGE_TYPE</c>，形如 "137001004001"）。
    ///
    /// 跟 <see cref="Explanation"/> 是同一件事的两种表示，但那个是中文长句
    /// （"有价格涨跌幅限制的日换手率达到20%的前五只证券"），按它做榜单类型统计只能字符串匹配，
    /// 东财改一个字或加一种榜就全错。这个码是稳定的，要分类用它。
    /// </summary>
    public string ChangeType { get; set; } = "";

    public DateTime FetchedAt { get; set; }
}

/// <summary>
/// "抓某一个交易日的全市场龙虎榜席位"这一件事（2026-09-17）。
///
/// ⚠ 一天是**两个接口**（买方榜 + 卖方榜），两侧都收齐才算这一天完整——这张表落库是
/// **整日替换**，只收到一侧就落库等于把另一侧永久删掉，而且事后完全看不出来
/// （行数判据只会觉得"那天本来就少"）。所以 <see cref="IsComplete"/> 要两侧同时成立。
///
/// 单独成接口是为了让 <c>LhbSeatTask</c> 能离线测排期、count 校验、分批收尾这些编排逻辑，
/// 不必为此起一个真的 HTTP 客户端。
/// </summary>
/// <param name="Day">交易日。</param>
/// <param name="Rows">买卖两侧的全部行，<see cref="LhbSeat.Seq"/> 已在整天收齐后自赋。</param>
/// <param name="ReportedBuy">买方榜接口自报的行数。</param>
/// <param name="ReportedSell">卖方榜接口自报的行数。</param>
public sealed record LhbSeatDay(DateTime Day, List<LhbSeat> Rows, int ReportedBuy, int ReportedSell)
{
    /// <summary>实收行数跟数据源自报的对得上吗——买卖**两侧都要对上**，对不上就别落库。</summary>
    public bool IsComplete =>
        Rows.Count(r => r.IsBuy) == ReportedBuy && Rows.Count(r => !r.IsBuy) == ReportedSell;

    /// <summary>接口自报这一天共有多少行（买 + 卖）。</summary>
    public int ReportedCount => ReportedBuy + ReportedSell;
}

/// <summary>见 <see cref="LhbSeatDay"/>。</summary>
public interface ILhbSeatDayFetcher
{
    Task<LhbSeatDay> FetchLhbSeatsOfDayAsync(DateTime day, CancellationToken ct = default);
}
