namespace StockPlatform.Logic.Models;

/// <summary>
/// 一只标的在本地库里"除K线之外"的全部数据，按来源表分节——供"行情详情"窗口的"其他数据"按钮
/// 一次性展示（读取见 SqliteStockDossierReader，界面见 StockDossierWindow）。
///
/// 表格值一律在读取时就格式化成字符串：各节的列数和量纲完全不同（户数是整数、余额是亿元、权重是
/// 百分比、公告是长文本），让界面按统一的"表头 + 字符串行"渲染，比让它认识十几种行类型简单得多。
/// 代价是列排序会退化成字典序，所以各节在读取时就按"最新在前"排好、界面关掉列排序（见
/// <see cref="DossierSection.Rows"/>）。
///
/// 多行的时间序列另外带一份**数值**序列（<see cref="DossierSection.Chart"/>）给上面的趋势图用——
/// 表格是"查具体某天的值"，图是"看走势"，两者都要，但格式化后的字符串画不了图，所以数值单独留一份。
/// </summary>
public class StockDossier
{
    public string Code { get; init; } = "";
    public string? Name { get; init; }

    /// <summary>按展示顺序排列的各节。**没有数据的节也保留**（<see cref="DossierSection.Rows"/> 为空），
    /// 界面显示"（无数据）"——这样用户能看出"这类数据本来就没抓"和"抓了但这只票没有"的区别，
    /// 而不是面对一个悄悄少了几个页签的窗口。</summary>
    public List<DossierSection> Sections { get; } = new();
}

/// <summary>一节 = 一张来源表（或一张表的一个切片，如十大股东 vs 十大流通股东）。</summary>
public class DossierSection
{
    /// <summary>节标题。由 SqliteStockDossierReader.Read 统一赋值（各读取方法只管数据）。</summary>
    public string Title { get; set; } = "";

    /// <summary>数据源/口径的一句话说明，显示在表格上方——避免把"年内累计"当单季、把"每10股派息"
    /// 当每股、把只覆盖两融标的的空值当成"融资余额为0"。同样由 Read 统一赋值。</summary>
    public string Note { get; set; } = "";

    /// <summary>该节读取失败时的原因（表不存在、字段缺失等），成功为 null。单节失败不影响其它节。</summary>
    public string? Error { get; set; }

    public List<DossierColumn> Columns { get; init; } = new();

    /// <summary>已格式化好的行，顺序即展示顺序（时间序列一律最新在前）。每行长度等于
    /// <see cref="Columns"/> 的长度。</summary>
    public List<string[]> Rows { get; init; } = new();

    /// <summary>该节的趋势图数据（只有时间序列的节有，快照/名单类为 null）。**时间正序**，跟
    /// <see cref="Rows"/> 的倒序相反——图从左到右读是时间往后走。</summary>
    public DossierChart? Chart { get; init; }
}

/// <param name="Header">列标题。</param>
/// <param name="Width">DataGrid 列宽（像素）；0 = 占满剩余宽度（一节里最多给一列用）。</param>
/// <param name="RightAlign">数值列右对齐，文本列左对齐。</param>
public record DossierColumn(string Header, double Width = 0, bool RightAlign = false);

/// <summary>
/// 一节的趋势图：共用一条 x 轴（<see cref="XLabels"/> 的下标）的若干条序列，量纲差太多的放右轴。
/// 只有数值，没有颜色/线型——那些是展示细节，由 Analyzer 的 DossierChartBuilder 按序列顺序分配。
/// </summary>
public class DossierChart
{
    /// <summary>x 轴刻度标签（日期/报告期，**时间正序**），下标与各序列 Values 一一对应。</summary>
    public List<string> XLabels { get; init; } = new();

    public List<DossierSeries> Series { get; init; } = new();

    /// <summary>左/右轴的量纲标题（如"亿元"/"%"）。右轴为 null 表示这张图只有左轴。</summary>
    public string LeftAxisTitle { get; init; } = "";
    public string? RightAxisTitle { get; init; }

    /// <summary>图特有的口径提醒（表格那份 Note 说的是整节）——比如财报图画的是单季而表格是累计，
    /// 不写清楚会被当成同一个数看。为空则不显示。</summary>
    public string Note { get; init; } = "";
}

/// <param name="Label">图例文字。</param>
/// <param name="Values">与 <see cref="DossierChart.XLabels"/> 等长；缺失点为 double.NaN（不连线）。</param>
/// <param name="OnRightAxis">true = 画在右轴（百分比这类跟左轴量纲差几个数量级的）。</param>
/// <param name="AsBars">true = 画成柱（有正有负、逐期独立的量，如单日资金净流入、每期派息）；
/// false = 折线（余额、户数这类连续存量）。柱按正负上红下绿，跟行情图一个约定。</param>
public record DossierSeries(string Label, double[] Values, bool OnRightAxis = false, bool AsBars = false);
