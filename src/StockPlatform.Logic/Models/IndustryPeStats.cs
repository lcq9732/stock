namespace StockPlatform.Logic.Models;

/// <summary>
/// 一只票所属行业的 PE 分位（2026-09-14 新增），给「PE (TTM)」那行当参考值用。
///
/// ════ 为什么要分行业 ════
/// 全市场中位 39.1 倍这把尺子对谁都不准——一级行业的中位从**银行 6.3** 到**通信 97.9**，
/// 差 15 倍。北京银行 PE 5.6 在全市场是"最低的 10%"，在银行业里只是"低于行业中位"；
/// 结论没反转，但"低"的程度完全不是一回事。
///
/// ════ 用哪一级：二级优先，样本不足逐级回退 ════
/// 东财三级分类，按 3923 只盈利股统计（2026-09-14）：
///   · 一级 31 个，样本中位 84 只，**没有一个少于 15 只**
///   · 二级 127 个，样本中位 20 只，36 个少于 10 只
///   · 三级 325 个，样本中位 8 只 —— 不可用
/// 所以：二级 ≥<see cref="MinSampleForBands"/> 用二级带分位档；≥<see cref="MinSample"/>
/// 只说中位；再不够回退一级。实测落点：69.3% 走二级带档、25.1% 二级只说中位、
/// 5.6% 回退一级、**没有一只需要退到全市场**。
///
/// ════ 两档门槛的理由 ════
/// 中国平安是活教材：二级「保险Ⅱ」只有 5 只样本，PE 6.2 被判成 PE 最高的 10%；
/// 一级「非银金融」77 只，判的是最低的 10%。5 只样本的 P90 就是第 5 名，纯噪音。
/// 10~30 只之间中位数还算稳、P10/P90 已经是"第 2 名和第 19 名"，所以给中位不给档。
/// </summary>
public class IndustryPeStats
{
    /// <summary>低于这个样本数就别用这一级（分位和中位都不可信），回退上一级。</summary>
    public const int MinSample = 10;

    /// <summary>低于这个样本数只报中位、不报分位档——P10/P90 在小样本里就是第 2 名和第 19 名。</summary>
    public const int MinSampleForBands = 30;

    /// <summary>行业名（东财口径，如"银行"/"电池"）。</summary>
    public string Name { get; init; } = "";

    /// <summary>取自哪一级（1=一级 31 个 / 2=二级 127 个）。界面要写出来，
    /// 否则看到"行业中位"不知道说的是电力设备还是电池。</summary>
    public int Level { get; init; }

    /// <summary>参与统计的盈利股家数（同行业、同口径算得出 PE 的）。</summary>
    public int SampleSize { get; init; }

    public double P10 { get; init; }
    public double P25 { get; init; }
    public double Median { get; init; }
    public double P75 { get; init; }
    public double P90 { get; init; }

    /// <summary>样本够不够给分位档。不够就只报中位。</summary>
    public bool HasBands => SampleSize >= MinSampleForBands;

    /// <summary>
    /// 这个 PE 在本行业的位置，一句话。措辞跟全市场那边共用
    /// <see cref="MarketPeStats.PositionWords"/>——**只说高低、不说便宜贵**，理由见那里。
    /// 样本不够给档时返回空串，调用方只显示中位。
    /// </summary>
    public string DescribePosition(double pe) =>
        HasBands ? MarketPeStats.PositionWords(pe, P10, P25, Median, P75, P90, "行业") : "";

    /// <summary>
    /// 参考值那一列的完整文本：<c>银行 中位 6.3 倍（42 只）低于行业中位</c>。
    /// 样本 10~30 只时省掉后半句，只留 <c>中位 … 倍（n 只）</c>。
    /// </summary>
    public string Describe(double pe)
    {
        var band = DescribePosition(pe);
        var head = $"{Name} 中位 {Median:F1} 倍（{SampleSize} 只）";
        return band.Length > 0 ? $"{head}{band}" : $"{head}样本偏少，只给中位";
    }
}
