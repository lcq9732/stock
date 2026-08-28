namespace StockPlatform.Logic.Models;

/// <summary>一行指标的判定。界面据此上色，异常项也据此汇总。</summary>
public enum Verdict
{
    /// <summary>正常/健康。</summary>
    Good,
    /// <summary>值得留意，还不算问题。</summary>
    Warn,
    /// <summary>明确的负面信号。</summary>
    Bad,
    /// <summary>只是陈述事实，不做好坏判断（比如"营收 140 亿"）。</summary>
    Neutral,
    /// <summary>缺数据算不出来。</summary>
    Missing,
}

/// <summary>
/// 财务分析里的一行：**数值 + 同比 + 判定 + 这意味着什么**。
///
/// 最后那个 <see cref="Note"/> 是这整个功能的重点。只显示"经营现金流/净利 = 0.35"没有用——
/// 得同时说"利润没变成现金，窟窿在经营性应收占用13.72亿和存货9.25亿"，才叫看得明白。
/// </summary>
public class AnalysisLine
{
    public string Label { get; init; } = "";
    /// <summary>已格式化好的数值文本（含单位）。</summary>
    public string Value { get; init; } = "";
    /// <summary>同比/环比等对照文本；没有就留空。</summary>
    public string Change { get; init; } = "";
    public Verdict Verdict { get; init; } = Verdict.Neutral;
    /// <summary>这个数意味着什么。空字符串表示不需要解释。</summary>
    public string Note { get; init; } = "";
}

/// <summary>分析报告的一节（规模与效率 / 净利率归因 / 现金流质量 / 资金来源 / 回报与估值）。</summary>
public class AnalysisSection
{
    public string Title { get; init; } = "";
    public List<AnalysisLine> Lines { get; init; } = new();
    /// <summary>本节的结论——那句"→ …"。整节看下来该得出什么，不让用户自己拼。</summary>
    public string Conclusion { get; init; } = "";
}

/// <summary>一个指标的多期趋势（给折线/柱状图用）。</summary>
public class TrendSeries
{
    public string Name { get; init; } = "";
    /// <summary>按报告期升序。**所有 TrendSeries 共用同一条报告期轴**——某期算不出来时值为
    /// <see cref="double.NaN"/> 占位，而不是把点删掉。这样几张图并排时 x 轴严格对齐，
    /// 同一列一定是同一期（见 FinancialAnalyzer.BuildTrends 的说明）。画图时跳过 NaN。</summary>
    public List<(DateTime Period, double Value)> Points { get; init; } = new();
    /// <summary>数值单位后缀（"亿" / "%"），画图时标在轴上。</summary>
    public string Unit { get; init; } = "";
}

/// <summary>
/// 一只股票某个报告期的财务分析结果（2026-08-27 新增）。
///
/// 定位：**把 52 个财务科目翻译成人能一眼看懂的判断**，不是再列一张比率表。三件事：
///   ① 数字变判断——每行都带"这意味着什么"（<see cref="AnalysisLine.Note"/>）
///   ② 自动归因——"净利率掉5.9pct"没用，"其中1.7pct是毛利率(结构性)、4.2pct是费用和浮亏(部分可逆)"才有用
///   ③ 抓出容易误读的地方——比如资产负债率降了看着是好事，但同期有息负债靠转债转股才降、
///      短期借款反而翻倍，单看那个比率会得出相反结论
///
/// 所有内容都是从 <see cref="FinancialKeys"/> 的科目算出来的纯计算，不联网。
/// </summary>
public class FinancialAnalysisReport
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>本期报告期。</summary>
    public DateTime ReportDate { get; init; }
    /// <summary>去年同期（用于同比）；没有就是 null。</summary>
    public DateTime? PriorYearDate { get; init; }

    /// <summary>报告期的中文说法（"2026年中报"）。</summary>
    public string PeriodName { get; init; } = "";

    /// <summary>最上面那句总结。</summary>
    public string Headline { get; init; } = "";

    public List<AnalysisSection> Sections { get; init; } = new();

    /// <summary>异常项汇总——判定为 <see cref="Verdict.Bad"/> 的那些，集中列在最后。</summary>
    public List<string> Alerts { get; init; } = new();

    public List<TrendSeries> Trends { get; init; } = new();

    /// <summary>
    /// 是不是金融机构（银行/券商/保险）。它们没有"营业成本"，毛利率算不出来；"存货""收现比"
    /// 对它们也没有意义。判定为真时只出能算的那几项，而不是显示一堆 n/a。
    /// </summary>
    public bool IsFinancialInstitution { get; init; }

    /// <summary>数据不足时的说明（比如库里这只票还没抓到财务数据）；正常时为空。</summary>
    public string? Error { get; init; }
}
