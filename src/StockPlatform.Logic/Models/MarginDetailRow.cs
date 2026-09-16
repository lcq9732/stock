namespace StockPlatform.Logic.Models;

/// <summary>
/// 某只标的某交易日的融资融券明细（交易所官方数据）——核心是 <see cref="MarginBalance"/> 融资余额。
/// 按交易日累积（INSERT OR IGNORE），跟 K线/龙虎榜一样是每日数据。
///
/// ⚠ **两所给的字段不一样，别指望字段齐全**（2026-09-16 实测，见 doc/margin-fields-design.md）：
///
/// | 字段 | 上交所 JSON | 深交所 xlsx |
/// |---|---|---|
/// | 融资余额 / 买入额 | rzye / rzmre | 列3 / 列2 |
/// | 融资偿还额 | rzche | **没有** |
/// | 融券余量 / 卖出量 | rqyl / rqmcl | 列5 / 列4 |
/// | 融券偿还量 | rqchl | **没有** |
/// | 融券余额 | **恒为 null** | 列6 |
///
/// 所以可空字段（<c>double?</c>）表达的是"**这个市场不提供**"，不是"当天是 0"。
/// 这个区分是拿教训换来的：原先 <see cref="ShortBalance"/> 是非空 double，沪市的 null 被
/// 读成 0，结果 1675 只沪市票的融券余额**历史上从来没有过非 0 值**，而维度4「融券余额变化」
/// 对所有沪市票静默失效。凡是源头可能不给的列，一律可空。
/// </summary>
public class MarginDetailRow
{
    public string Code { get; set; } = "";        // 6 位标的代码
    public DateTime TradeDate { get; set; }        // 交易日
    public string Name { get; set; } = "";
    public double MarginBalance { get; set; }      // 融资余额（元）——两所都给
    public double MarginBuy { get; set; }          // 融资买入额（元）——两所都给
    public double ShortVolume { get; set; }        // 融券余量（股/份）——两所都给

    /// <summary>
    /// 融券余额（元）。**沪市数据源不提供**（rqylje 恒为 null），深市 xlsx 第 6 列直接给。
    ///
    /// 沪市的值由本地按交易所官方公式补算：<c>融券余量 × 当日收盘价</c>——这条公式是深交所
    /// 报表页脚自己写明的（"本日融券余额(元)=本日融券余量×本日收盘价"），并且在沪深两市都实测
    /// 精确成立（深市 1603 只、沪市 456 只，误差中位 0.0000%）。见 MarginShortBalanceFiller。
    ///
    /// null = 还没算（没有当日收盘价，或这一行还没过补算步骤）。**0 才表示"确实没有融券"**。
    /// </summary>
    public double? ShortBalance { get; set; }

    /// <summary>融资偿还额（元）。**只有沪市给**（rzche），深市那张表没这一列 → NULL。
    /// 有了它就能按官方口径算「融资净买入 = 买入 − 偿还」；深市只能用余额差分
    /// （交易所页脚定义：本日融资余额 = 前日融资余额 + 本日买入 − 本日偿还，故差分恒等于净买入）。</summary>
    public double? MarginRepay { get; set; }

    /// <summary>融券卖出量（股）。沪市 rqmcl、深市 xlsx 第 4 列——**两所都有**，
    /// 但深市那一列 2026-09-16 之前一直被解析跳过（读的是列 0,1,2,3,5,6）。</summary>
    public double? ShortSellVolume { get; set; }

    /// <summary>融券偿还量（股）。**只有沪市给**（rqchl），深市无 → NULL。</summary>
    public double? ShortRepayVolume { get; set; }

    public DateTime FetchedAt { get; set; }
}
