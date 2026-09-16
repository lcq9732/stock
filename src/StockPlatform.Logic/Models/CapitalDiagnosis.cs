namespace StockPlatform.Logic.Models;

/// <summary>
/// 资金面诊断（2026-09-16，见 doc/capital-diagnosis-design.md）——把 Bar / NetInflowDetail /
/// MarginDetail / BlockTrade / Lhb **横着串起来**得出的结论，嵌在【分析详情】窗口第一列。
///
/// 跟现有两层的分工：条件详情回答"这个方法为什么选中它"、个股档案回答"某天的值是多少"，
/// 这里回答的是第三层——"这只票的量能和资金处于什么状态"。
///
/// ⚠ **没有结论句就没有第三层**：早期原型只给区间对照表，六个维度铺开后跟档案窗口的原始表
/// 没有区别（2026-09-16 用户反馈"样例没看出来是6个维度"）。所以 <see cref="DiagnosisDimension.Conclusions"/>
/// 是必填项，表格反倒是可选的。
///
/// 结论句一律**规则拼装**（按符号、比值、阈值套模板），不是每次现编——同一组数据永远得出同一句话，
/// 可以写回归测试。措辞只陈述事实与对比，不下买卖结论（跟 PE 行业分位一个原则）。
///
/// **不做评分/总分**：六个维度压成一个分数会掩盖矛盾信号，而"主力在跑、融资在买"这种矛盾
/// 恰恰是最该被看见的（宁德时代 2026-09 实测就是这个状态，见 <see cref="GlobalNote"/>）。
/// </summary>
public class CapitalDiagnosis
{
    public string Code { get; init; } = "";
    public string? Name { get; init; }

    /// <summary>K线数据截止日——各表的截止日**各不相同**，分别记在各维度的告警里。</summary>
    public DateTime AsOf { get; init; }

    public DiagnosisWindows Windows { get; init; } = new();

    public double AnchorClose { get; init; }
    public double LatestClose { get; init; }

    /// <summary>锚点以来的累计涨跌幅（%）。</summary>
    public double AnchorChangePct { get; init; }

    public List<DiagnosisDimension> Dimensions { get; } = new();

    /// <summary>底部的全局提示。矛盾信号在这里点名——不合成总分的理由就是它。</summary>
    public string GlobalNote { get; set; } = "";

    /// <summary>整份诊断都做不了时的原因（本地没有这只票的K线）。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// 三个区间。**主锚点 + 两个固定对照**，缺一不可：
/// 宁德时代实测本波 -21.65%（跑输创业板指 13.5pct），而近60日 -19.70% 反而**跑赢** 3.9pct，
/// 换个窗口结论符号就翻转——只给一个区间会误导，所以固定对照列不是装饰。
/// </summary>
public class DiagnosisWindows
{
    /// <summary>主锚点：近 <see cref="Lookback"/> 个交易日内的最高收盘日。自动适配"这波走了多久"，
    /// 涨势中锚点就落在最近几天、区间自然变短——这是"不管涨跌都给同样诊断"的实现方式。</summary>
    public DateTime AnchorDate { get; init; }

    /// <summary>锚点在 Bars 里的下标。</summary>
    public int AnchorIndex { get; init; }

    /// <summary>回看窗口长度（交易日），默认 60——既定的短线口径。</summary>
    public int Lookback { get; init; } = 60;

    /// <summary>按展示顺序：本波 / 近20日 / 近60日。</summary>
    public List<DiagnosisRange> Ranges { get; init; } = new();
}

/// <param name="Label">区间名（"本波" / "近20日" / "近60日"）。</param>
/// <param name="StartIndex">起点在 Bars 里的下标（闭区间）。</param>
/// <param name="Start">起点交易日。</param>
/// <param name="End">终点交易日（= 最新一根K线）。</param>
/// <param name="TradingDays">区间包含的交易日数。</param>
public record DiagnosisRange(string Label, int StartIndex, DateTime Start, DateTime End, int TradingDays);

/// <summary>
/// 一个维度 = 界面上一个区块，四段式：**标题(带 Tooltip) → 本次异常 → 结论 → 证据**。
///
/// 口径拆两类是 2026-09-16 定的（用户："口径这个说明只是早期有用，后面熟悉了就不用看了"）：
/// 静态口径进 <see cref="Tooltip"/>，本次异常留在 <see cref="Warnings"/> 常驻。
/// 全塞进 Tooltip 会出现**"结论看着正常、其实数据是降级的"**——这类信息一旦藏起来结论就不可信。
/// </summary>
public class DiagnosisDimension
{
    /// <summary>1..6，界面显示成"维度 N/6"。</summary>
    public int Index { get; init; }

    public string Title { get; init; } = "";

    /// <summary>静态口径说明 → 标题的 Tooltip。指标定义、分档标准、算法选择理由、已知陷阱。
    /// 这些看两次就不用再看，所以不占行。</summary>
    public string Tooltip { get; init; } = "";

    /// <summary>本次异常，**仅异常时非空**，正常时界面零占位。
    /// 四类：降级 / 不适用 / 区间内缺日 / 真落后（超出该表既有的滞后规律）。</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>1–2 句中性判断。**必填**——没有它这个维度就退化成原始数据。</summary>
    public List<string> Conclusions { get; } = new();

    /// <summary>证据：区间对照表 + Top3 明细，按展示顺序。</summary>
    public List<DiagnosisTable> Tables { get; } = new();

    /// <summary>整个维度不可用时的原因（如"非两融标的"），置位后界面只显示这一句。</summary>
    public string? Unavailable { get; set; }
}

/// <param name="Caption">表上方的小标题（如"本波主力净流出 Top3"）；为空则不显示。</param>
public class DiagnosisTable
{
    public string Caption { get; init; } = "";
    public List<DiagnosisColumn> Columns { get; init; } = new();
    public List<DiagnosisCell[]> Rows { get; init; } = new();
}

/// <param name="Header">列标题；为空表示这一列不要表头（Top3 那种明细表）。</param>
/// <param name="Width">列宽（像素）；0 = 自适应内容。</param>
/// <param name="RightAlign">数值列右对齐。</param>
public record DiagnosisColumn(string Header, double Width = 0, bool RightAlign = false);

/// <summary>
/// 一个单元格。**只带语义不带颜色**——颜色是展示细节，由 Analyzer 那边按主题资源分配
/// （跟 <see cref="DossierSeries"/> 不放颜色是同一个理由，Logic 层不认识画刷）。
/// </summary>
/// <param name="Text">已格式化好的显示文本。</param>
/// <param name="Tone">语义色调。</param>
public record DiagnosisCell(string Text, CellTone Tone = CellTone.Neutral);

/// <summary>
/// 单元格语义。注意 <see cref="Positive"/>/<see cref="Negative"/> 指的是**这个数本身的正负**
/// （净流入为正、跌幅为负），不是"好消息/坏消息"——资金流出对空头是好消息，本功能不替用户判断立场。
/// </summary>
public enum CellTone
{
    Neutral,
    Positive,
    Negative,
    /// <summary>次要信息（覆盖比、日期这类），显示成淡色。</summary>
    Muted,
    /// <summary>需要注意的值（背离、放量下跌），显示成强调色。</summary>
    Alert,
}
