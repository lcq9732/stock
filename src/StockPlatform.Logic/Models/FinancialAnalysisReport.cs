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

    /// <summary>
    /// 参考值/正常范围（2026-08-29 新增，银行体检表在用；其它节留空、界面不显示这一列）。
    /// 体检报告式的"一列值、一列正常范围"，来源分两类：
    ///   · 结构性阈值——不随周期漂移的，写死（成本收入比 30/35/45）
    ///   · 相对性阈值——跟着行业走的，用当期全行业分位现算（"行业中位 9.07 / 75分位 10.73"）
    /// 后者是刻意的：ROE&gt;10% 这类固定阈值会随时代失效（净息差从 2.2% 降到 1.37% 之后，
    /// 42 家银行只剩 14 家 ROE 过 10%，六大行全军覆没），钉死一个数就是在筛时代而不是筛公司。
    /// </summary>
    public string Reference { get; init; } = "";

    /// <summary>体检表里对应"十二条"的第几条（1~12）；非体检表的行为 0。</summary>
    public int ClauseNo { get; init; }
}

/// <summary>分析报告的一节（规模与效率 / 净利率归因 / 现金流质量 / 资金来源 / 回报与估值）。</summary>
public class AnalysisSection
{
    public string Title { get; init; } = "";
    public List<AnalysisLine> Lines { get; init; } = new();
    /// <summary>本节的结论——那句"→ …"。整节看下来该得出什么，不让用户自己拼。</summary>
    public string Conclusion { get; init; } = "";

    /// <summary>体检表节（银行专用）——界面据此多渲染一列"参考值"，并按条目编号分组。</summary>
    public bool IsHealthCheck { get; init; }
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

    /// <summary>
    /// 算 PE/PB/股息率用的那个收盘价，以及它是**哪一天**的（2026-09-14 用户要求显示在标题上）。
    ///
    /// 为什么日期必须一起给：这个价取的是本地库里最新一根日K，而本地未必抓到了今天——
    /// 估值三行算出来的是"那一天的估值"，不标日期的话看到的人会默认它是现价。
    /// 尤其是 PE：股本走的是日更的 <c>total_shares</c>（今天的），价格却可能是几天前的，
    /// 两个输入日期不同，标出来才对得上账。
    /// </summary>
    public double? Price { get; init; }
    public DateTime? PriceDate { get; init; }

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
    public bool IsFinancialInstitution => Kind != FinancialInstitutionKind.NonFinancial;

    /// <summary>机构类型（2026-08-29 细分）——见 <see cref="FinancialInstitutionKind"/>。</summary>
    public FinancialInstitutionKind Kind { get; init; }

    /// <summary>数据不足时的说明（比如库里这只票还没抓到财务数据）；正常时为空。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// 机构类型（2026-08-29 新增）——原来只有一个 <c>isFin</c> 布尔，把银行/券商/保险混成一类。
///
/// 为什么必须拆：银行有一套自己的监管指标体系（不良率、拨备覆盖、资本充足率、净息差、成本
/// 收入比），券商看的是净资本/风险覆盖率/自营与经纪结构，保险看内含价值/偿付能力充足率——
/// 三者互不通用。不拆开就把银行那套指标套到券商保险上，ROE/ROA 照样能算出数来，但拿银行的
/// 行业分位去评判券商纯属误判（券商 ROE 跟着市场行情大起大落）。
///
/// 判定只用**科目特征**、不查行业表：行业表覆盖率不满，漏判会让整份报告显示一堆 n/a
/// （沿用原 isFin 的思路，见 FinancialAnalyzer 类注释）。
/// </summary>
public enum FinancialInstitutionKind
{
    /// <summary>工商企业——有"营业成本"，走完整分析（含毛利率归因、存货、收现比）。</summary>
    NonFinancial = 0,

    /// <summary>银行——没有"营业成本"，且利息净收入占营业收入 40% 以上。走银行体检表。</summary>
    Bank = 1,

    /// <summary>
    /// 金融机构但认不出具体类型，走通用简版。
    /// **老数据会落在这里**：库里没有 v4 的特征科目（利息净收入/已赚保费/代理买卖证券业务净收入）
    /// 就判不出是哪一类。这是有意的安全降级——宁可少出一张体检表，也不能把券商当银行体检。
    /// 重抓一次财务报表后会自动归位。
    /// </summary>
    OtherFinancial = 2,

    /// <summary>券商——有"代理买卖证券业务净收入"等特征科目。看净资本/风险覆盖率/收入结构，
    /// 跟银行完全是两套指标体系。</summary>
    Broker = 3,

    /// <summary>保险——有"已赚保费"。看偿付能力充足率/综合成本率，又是另一套。</summary>
    Insurer = 4,
}
