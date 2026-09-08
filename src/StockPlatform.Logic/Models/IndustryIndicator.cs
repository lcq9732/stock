namespace StockPlatform.Logic.Models;

/// <summary>
/// 行业景气指标的**定义**（2026-09-07）。东财 F10 里"同行指标/景气指标"那一块的数据源。
///
/// 这是**传统行业分析**那一路的核心输入，跟风口分析分开看：风口看的是叙事能不能兑现成
/// 别人的报表（题材归属＋资金＋财报），周期股看的是价格和库存本身——猪粮比、螺纹钢库存、
/// 焦煤期货价这些是**日/周频**的，比季报早一个季度告诉你利润要往哪走。
///
/// 实测全貌：116 个指标、覆盖 658 只股票（约 12%，全是周期股）、1190 条股票↔指标映射。
/// 按频率分：日 45、月 55、周 14、旬 1、半年 1。历史只到 2024-04（约 2 年），
/// 够看当下位置和同比，**不够跑长周期回测**。
/// </summary>
public class IndustryIndicator
{
    /// <summary>东财指标库编号，形如 EMI00139010 / EMM00121987。</summary>
    public string IndicatorId { get; set; } = "";

    /// <summary>展示名，如"全国猪粮比价"。</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 东财原始口径名，如"全国大中城市:猪粮比价"。
    /// 留着不是为了显示，是为了**排查**——展示名会重名、会改，原始口径名带着统计范围，
    /// 两个"水泥价格"到底是不是一回事，看这一列才能确定。
    /// </summary>
    public string OrigName { get; set; } = "";

    /// <summary>如"元/公斤"。比值类指标（猪粮比、金银比）没有单位，为空。</summary>
    public string Unit { get; set; } = "";

    /// <summary>日 / 周 / 旬 / 月 / 半年。</summary>
    public string Frequency { get; set; } = "";

    /// <summary>
    /// 001＝个股、002＝行业。
    ///
    /// ⚠ 东财这个标注**不完全准**：涤纶DTY/FDY/POY、维生素A/E/K3/D3 被标成 001，
    /// 但它们明显是行业价格（同一个指标挂在 4 只股票下、值完全一样）。
    /// **我们不纠正**，原样存——哪天东财自己改对了，我们的"修正"反倒成了错的那一方。
    ///
    /// 另外前端代码里还有个 003＝产业链，但实测**一条数据都没有**，别指望它。
    /// </summary>
    public string Granularity { get; set; } = "";

    /// <summary>
    /// 折线图 / 柱状图。这不是显示用的——它决定拉历史序列要走哪个接口
    /// （折线图走 LINECHART，柱状图走 BARCHART，走错了返回 0 条）。
    /// 实测折线图 72 个、柱状图 44 个。
    /// </summary>
    public string ChartType { get; set; } = "";

    /// <summary>数据来源，如"中华人民共和国商务部"、"上市公司公告"。</summary>
    public string Source { get; set; } = "";

    public DateTime FetchedAt { get; set; }
}

/// <summary>
/// 指标的一个时点值。
///
/// <b>不带股票代码</b>，这是刻意的：同一个指标在不同股票下的序列**完全一样**
/// （实测玻璃期货价 4 只股票同日同为 1431；7 个多股共享的指标 569 个日期 0 冲突）。
/// 带上就是把 116 份序列存成 1190 份重复。
///
/// 东财顺带返回的 CLOSE_PRICE（当时股价）也丢掉——我们自己有日K，多留一份只会多一个
/// 会跟本地对不上的口径。
/// </summary>
/// <param name="TradeDate">值所属的日期，不是写入日。</param>
/// <param name="YoyPct">同比%。<b>只有柱状图那 44 个指标给</b>，其余为 null。</param>
public readonly record struct IndicatorPoint(
    string IndicatorId, DateTime TradeDate, double Value, double? YoyPct);

/// <summary>
/// 股票 ↔ 指标 的关联（多对多）。1190 条。
///
/// 反过来用才是它最大的价值：<b>板块 → 它的成分股关联最多的指标</b>，
/// 那是"这个行业现在景气不景气"的入口。
/// </summary>
/// <param name="Order">东财给的展示顺序。第 1 个就是它认为最相关的，可以直接当权重用。</param>
public readonly record struct StockIndicatorLink(string Code, string IndicatorId, int? Order);
