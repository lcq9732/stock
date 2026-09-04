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

    public DateTime FetchedAt { get; set; }
}
