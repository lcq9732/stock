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
    /// 内置快照（2026-09-14 用 2026 年中报 + 当日收盘算的，盈利股 3,948 只）。
    ///
    /// **暂时只有这一条路，没做实算。** 银行体检表能实算是因为只有 40 多家；
    /// 这里要读全市场 5,678 只的财报和收盘价才能算一次分位，而这个分布季度才动一次——
    /// 开销和收益不成比例。<see cref="IsLive"/> 留着，以后要实算不用改结构。
    ///
    /// 顺带记一个有意思的数：基准 12 倍（8.3% 盈利收益率）**只落在第 7 百分位**——
    /// 也就是说全市场只有 7% 的股票，已赚到的利润足以按合理估值撑起全部市值。
    /// </summary>
    public static MarketPeStats Builtin { get; } = new()
    {
        AsOf = new DateTime(2026, 9, 11),
        IsLive = false,
        SampleSize = 3948,
        P10 = 13.4,
        P25 = 20.7,
        Median = 38.6,
        P75 = 87.0,
        P90 = 193.8,
    };

    /// <summary>
    /// 这个 PE 在全市场的位置，一句话。
    ///
    /// ⚠ **按档说，不插值**。只有五个分位点，硬算出"比 83.7% 的票便宜"是假装精确——
    /// 分位点之间的分布形状根本不知道（PE 的右尾极长：P75 是 87 倍、P90 已经 194 倍）。
    /// </summary>
    public string DescribePosition(double pe) =>
        pe <= P10 ? "处在最便宜的 10%"
        : pe <= P25 ? "处在最便宜的 25%"
        : pe <= Median ? "低于中位"
        : pe <= P75 ? "高于中位"
        : pe <= P90 ? "处在最贵的 25%"
        : "处在最贵的 10%";
}
