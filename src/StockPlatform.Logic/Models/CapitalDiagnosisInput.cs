namespace StockPlatform.Logic.Models;

/// <summary>
/// 资金面诊断的输入——**全部已从库里读好**，<see cref="Services.CapitalDiagnosisAnalyzer"/> 只算不查，
/// 保持 Logic 层零 IO（见 project_logic_layer_boundary）。读取见 SqliteCapitalDiagnosisReader。
///
/// 为什么同业收益是"已算好的"而不是给一堆K线：同业按 major_name 可能有 380 只，把它们的日K
/// 全搬进内存只为算三个中位数不划算；而区间是**先由本股K线定锚点**才知道的，所以读取方要
/// 先调 <see cref="Services.CapitalDiagnosisAnalyzer.ResolveWindows"/> 拿到区间，再去查同业。
/// </summary>
public class CapitalDiagnosisInput
{
    public string Code { get; init; } = "";
    public string? Name { get; init; }

    /// <summary>本股日K（不复权），**时间正序**。空则整份诊断不可用。</summary>
    public IReadOnlyList<Bar> Bars { get; init; } = Array.Empty<Bar>();

    // ── 维度1：相对强弱 ───────────────────────────────────────────────

    /// <summary>对照指数名（"创业板指"）。</summary>
    public string IndexName { get; init; } = "";

    /// <summary>指数在各区间的涨跌幅（%），键是 <see cref="DiagnosisRange.Label"/>。
    /// 取不到的区间不放键（指数K线可能比个股晚抓，实测 9/15 当晚指数一条都没有）。</summary>
    public Dictionary<string, double> IndexReturns { get; init; } = new();

    /// <summary>同业中位数涨跌幅（%），键同上。</summary>
    public Dictionary<string, double> PeerMedianReturns { get; init; } = new();

    /// <summary>同业口径名（"电气机械和器材制造业"）；为空表示没有行业归属。</summary>
    public string PeerScopeName { get; init; } = "";

    /// <summary>同业总只数（含本股）。</summary>
    public int PeerTotal { get; init; }

    /// <summary>同业中实际算出收益的只数（区间内有首尾K线的）。</summary>
    public int PeerValid { get; init; }

    /// <summary>true = 样本不足已从 major_name 退回 class_name 门类口径。</summary>
    public bool PeerDowngraded { get; init; }

    // ── 维度3：资金流 ─────────────────────────────────────────────────

    /// <summary>分档资金流，**时间正序**，只含本股。</summary>
    public IReadOnlyList<NetInflowDetail> Flows { get; init; } = Array.Empty<NetInflowDetail>();

    /// <summary>本股分档数据的首日；null = 这只票一条都没有。</summary>
    public DateTime? FlowStart { get; init; }

    /// <summary>全市场分档数据的首日（覆盖率达 80% 的第一天）。
    /// ⚠ 不是全表 MIN——实测全表 MIN 是 2025-11-14 但那天只有 1 只票，会虚报 4 个月可用历史。</summary>
    public DateTime? FlowMarketStart { get; init; }

    // ── 维度4：杠杆 ───────────────────────────────────────────────────

    /// <summary>两融明细，**时间正序**。</summary>
    public IReadOnlyList<MarginDetailRow> Margins { get; init; } = Array.Empty<MarginDetailRow>();

    /// <summary>这只票是不是两融标的。false 时维度4整个不可用（与"余额为0"必须区分）。</summary>
    public bool IsMarginTarget { get; init; }

    /// <summary>两融数据的全市场最新交易日。</summary>
    public DateTime? MarginLatest { get; init; }

    /// <summary>两融相对K线的**惯常滞后天数**（交易日），由历史统计得出的众数。
    /// 判"是否真落后"要拿它当基线：两融交易所本就 T+1 披露，直接用"日期 &lt; K线日期"判会天天误报。</summary>
    public int MarginNormalLagDays { get; init; } = 1;

    /// <summary>区间内两融整天缺数据的交易日（不是本票没有，是那天全市场都没抓到那一半）。
    /// 实测 2026-08-21 / 2026-09-02 深市整个缺失。</summary>
    public List<DateTime> MarginMissingDays { get; init; } = new();

    // ── 维度5/6 ───────────────────────────────────────────────────────

    /// <summary>大宗交易，时间正序。</summary>
    public IReadOnlyList<BlockTrade> BlockTrades { get; init; } = Array.Empty<BlockTrade>();

    /// <summary>区间内的龙虎榜上榜日（去重）。</summary>
    public List<DateTime> LhbDates { get; init; } = new();

    /// <summary>最近一次上榜日（可能远早于区间）；null = 从没上过。</summary>
    public DateTime? LhbLastEver { get; set; }
}
