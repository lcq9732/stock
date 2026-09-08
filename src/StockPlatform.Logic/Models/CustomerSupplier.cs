namespace StockPlatform.Logic.Models;

/// <summary>
/// 公司披露的**前五大客户/供应商**（2026-09-07，东财 F10「客户及供应商」）。
///
/// 为什么要它：找"产业链上/中/下游"标签一路找空之后（东财网页、终端本地文件、终端菜单、
/// F10 栏目全查过，前端代码里有 003=产业链 的分支但线上一条数据都没有），这是能拿到的
/// **最硬的产业链数据**——不是别人的分类判断，是年报里的交易金额：供应商就是上游，客户就是下游。
///
/// 实测全貌：76.5 万行、2002 年至今、2025 年覆盖 5284 只股票（92%）。
/// 交易对手名 <b>53% 是真名</b>（"福建时代星云科技有限公司"），其余是"第一名""客户1"这类
/// 匿名披露；<b>71% 的股票至少有一个真名对手</b>。
///
/// 两种用法，价值和难度差很远：
///   · <b>集中度</b>（100% 可用）：前五大占比多少、"其余"占比多少 —— 大客户依赖风险、议价能力
///   · <b>供应链网络</b>（要实体消歧，第二期）：把上市公司之间的买卖关系连成图
/// </summary>
public class CustomerSupplier
{
    public string Code { get; set; } = "";

    /// <summary>报告期（值所属日期，不是写入日）。含年报/中报/一季报。</summary>
    public DateTime ReportDate { get; set; }

    /// <summary>false=客户（下游），true=供应商（上游）。东财的 TYPE_CODE 1/2。</summary>
    public bool IsSupplier { get; set; }

    /// <summary>1-5 是前五名，<b>6 是「其余客户/其余供应商」</b>。实测不会超过 6。</summary>
    public int Rank { get; set; }

    /// <summary>
    /// 交易对手名。可能是真名，也可能是"第一名""客户1""其余客户"这类匿名披露——
    /// 是否匿名由公司自己决定，大公司（宁德时代）往往匿名。
    /// </summary>
    public string PartnerName { get; set; } = "";

    public double Amount { get; set; }

    /// <summary>
    /// 占该类合计的百分比 = <see cref="Amount"/> / <see cref="TotalAmount"/>。
    ///
    /// ⚠ 东财那边这一列叫 <c>TOI_RATIO</c>（Total Operating Income），**名字是骗人的**：
    ///   客户那组的分母确实近似营收，但供应商那组的分母是**采购总额**——宁德时代 2025 年
    ///   供应商合计 5774 亿，比它营收还大。照抄字段名会让以后每个用它的人都以为是"占营收比"。
    /// </summary>
    public double Pct { get; set; }

    /// <summary>该类合计：客户组≈营业收入口径，供应商组＝采购总额口径。两者不同源，别混着比。</summary>
    public double TotalAmount { get; set; }

    /// <summary>"2025年报" / "2026中报"。</summary>
    public string ReportName { get; set; } = "";

    public DateTime FetchedAt { get; set; }
}
