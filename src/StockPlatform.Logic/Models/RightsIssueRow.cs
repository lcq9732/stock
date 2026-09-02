namespace StockPlatform.Logic.Models;

/// <summary>
/// 一次**配股**（2026-09-01 新增）——A股第四类除权事件，前面三类（现金分红/送股/转增）在
/// <see cref="DividendRow"/> 里。
///
/// ════ 为什么必须单独处理 ════
/// 配股是股东**掏钱**按低于市价的价格认购新股，所以除权参考价的分子分母都要动：
///     除权参考价 = (前收 − 每股现金分红 + 配股价 × 配股比例) ÷ (1 + 每股送转 + 配股比例)
/// 漏掉它的后果不是"少算一点"，而是**凭空多一根大阴线**——实测中信证券 2022-01 那次
/// 10配1.5@14.43，理论跳空 −5.72%、实际 −6.03%；10配3 那个量级的能到 −15%。
/// 配股集中在**银行和券商**（本地样本：中信证券/招商证券/兴业证券/浙商银行/宁波银行都配过），
/// 恰好是底仓最关心的板块。
///
/// ════ 数据来源：不需要额外请求 ════
/// 跟分红同在新浪 vISSUE_ShareBonus 那一页，分红是 <c>sharebonus_1</c> 表、配股是
/// <c>sharebonus_2</c> 表，一次 HTTP 拿两张表（见 SinaDividendProvider）。
/// </summary>
public class RightsIssueRow
{
    /// <summary>6位股票代码。</summary>
    public string Code { get; set; } = "";

    /// <summary>公告日期——同 <see cref="DividendRow.AnnounceDate"/>，作为方案标识进主键。</summary>
    public DateTime AnnounceDate { get; set; }

    /// <summary>配股方案：每10股配X股（10配3 = 3.0）。每股配股比例 = 本值 ÷ 10。</summary>
    public double SharesPer10 { get; set; }

    /// <summary>配股价格（元/股）。除权参考价的分子里加的就是它 × 配股比例。</summary>
    public double Price { get; set; }

    /// <summary>
    /// 除权日（可空：方案未实施时源给 "--"）。
    ///
    /// ⚠ **不能用"股权登记日 + 1 个交易日"推**：配股缴款期通常停牌，除权效应体现在**复牌那天**。
    /// 中信证券那次登记日 2022-01-18、缴款 01-19~01-25，中间整段停牌，价格上的除权跳空落在
    /// 复牌日 01-27——新浪这个字段填的正是 01-27，直接用它是对的。
    /// </summary>
    public DateTime? ExDate { get; set; }

    /// <summary>股权登记日（可空，同上）。只用于核对，复权计算不看它。</summary>
    public DateTime? RecordDate { get; set; }

    public DateTime FetchedAt { get; set; }
}
