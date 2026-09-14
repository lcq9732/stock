namespace StockPlatform.Logic.Models;

/// <summary>
/// 全市场 PE(TTM) 的分位（2026-09-14）。给财务分析的「PE (TTM)」那一行当参考值用——
/// 光一个 "10.3 倍" 没有位置感，得知道它在全市场排哪儿。
///
/// ⚠ **口径必须跟 FinancialAnalyzer 算 PE 的方式一致**：
/// <c>收盘价 × 报表股本(share_capital) / TTM归母净利</c>，用**总股本**不是流通市值。
/// 那一行的注释写着"股本用报表的实收资本，不是流通市值倒推"，两边口径不同的话
/// 界面上的数和参考分位就不是一把尺子，比了等于没比。
/// （实测差别不小：流通市值口径中位 32.0 倍，总股本口径 38.6 倍。）
///
/// ⚠ **只统计盈利股**。亏损股 PE 为负，混进来会把分位算歪；而且那些票的 PE 行本来就不显示
/// （<c>ttm is > 0</c> 才渲染），给它们参考值没有意义。
///
/// 跟 <see cref="BankPeerStats"/> 同样的模式：Logic 层零 IO，值由调用方填或用
/// <see cref="Builtin"/> 内置快照。
/// </summary>
public class MarketPeStats
{
    /// <summary>这份分位对应的统计时点。</summary>
    public DateTime AsOf { get; init; }

    /// <summary>true=从本地库当期实算；false=内置快照。</summary>
    public bool IsLive { get; init; }

    /// <summary>参与统计的盈利股家数。</summary>
    public int SampleSize { get; init; }

    public double P10 { get; init; }
    public double P25 { get; init; }
    public double Median { get; init; }
    public double P75 { get; init; }
    public double P90 { get; init; }

    /// <summary>
    /// 内置快照——**兜底用**，本地数据够时应该走实算（见 <c>MarketPeStatsBuilder</c>）。
    ///
    /// ⚠ 2026-09-14 重算过。此前那份（P10 13.4 / 中位 38.6 / P90 193.8）是用财报
    /// <c>share_capital</c> 当股数算的，而那是「实收资本」、是金额——面值不是 1 元的票全错
    /// （见 <c>MetricKeys.TotalShares</c>）。换成真实总股本后 277 只票的 PE 变了，分位小幅上移。
    /// 分布本身移动不大（中位 38.6 → 39.1），**错的是个股落点不是分布**：
    /// 中芯国际原先算出 PE 3.9、落在「最低的 10%」，真值 139.5、其实高于电子行业中位。
    ///
    /// 顺带记一个有意思的数：基准 12 倍（8.3% 盈利收益率）**只落在第 7 百分位**——
    /// 也就是说全市场只有 7% 的股票，已赚到的利润足以按合理估值撑起全部市值。
    /// </summary>
    public static MarketPeStats Builtin { get; } = new()
    {
        AsOf = new DateTime(2026, 9, 14),
        IsLive = false,
        SampleSize = 3923,
        P10 = 13.8,
        P25 = 21.2,
        Median = 39.1,
        P75 = 87.6,
        P90 = 194.4,
    };

    /// <summary>
    /// 这个 PE 在全市场的位置，一句话。
    ///
    /// ⚠ **按档说，不插值**。只有五个分位点，硬算出"比 83.7% 的票低"是假装精确——
    /// 分位点之间的分布形状根本不知道（PE 的右尾极长：P75 是 87.6 倍、P90 已经 194.4 倍）。
    ///
    /// ⚠ **只说高低，不说便宜贵**（2026-09-14 用户要求，措辞见 <see cref="PositionWords"/>）。
    /// 原先写的是"处在最便宜的 10%"，跟这个功能自己的纪律矛盾：PE 行刻意钉死
    /// <c>Verdict.Neutral</c> 不上色，正是因为 FactorLab 十分组实测 D1（PE 最高那组）年化 +10.6%
    /// 是十组里最高的、曲线呈 U 型不单调——"贵"并不预示跌。为了不暗示结论而不上色，
    /// 却在文字里写"便宜"，等于把刚守住的纪律又破了。
    /// "低/高"是对 PE 数值的客观陈述，划不划算留给用户判断。
    /// </summary>
    public string DescribePosition(double pe) => PositionWords(pe, P10, P25, Median, P75, P90);

    /// <summary>
    /// 分档措辞的**唯一出处**——全市场分位和行业分位（<see cref="IndustryPeStats"/>）共用，
    /// 两处写法不一致反而更让人困惑。<paramref name="scope"/> 是"全市场"/"行业"这类限定词。
    /// </summary>
    public static string PositionWords(double pe, double p10, double p25, double median,
                                       double p75, double p90, string scope = "")
        => pe <= p10 ? $"{scope}PE 最低的 10%"
         : pe <= p25 ? $"{scope}PE 最低的 25%"
         : pe <= median ? $"低于{scope}中位"
         : pe <= p75 ? $"高于{scope}中位"
         : pe <= p90 ? $"{scope}PE 最高的 25%"
         : $"{scope}PE 最高的 10%";
}
