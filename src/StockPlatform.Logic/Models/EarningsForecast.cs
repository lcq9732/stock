namespace StockPlatform.Logic.Models;

/// <summary>
/// 业绩预告（东财 <c>RPT_PUBLIC_OP_NEWPREDICT</c>）。本地此前完全没有这份数据。
///
/// 为什么值得单独存一张表：
/// 1. <b>比正式财报早一个月以上</b>——Q3 预告 10 月中出、财报 10 月底才出；年报预告 1 月底，
///    年报要等到 4 月。做景气度判断时这一个月的提前量是实打实的。
/// 2. <b>强制披露规则正好对准剧变</b>——净利变动超 50%、扭亏、首亏都必须预告，也就是说
///    "业绩发生剧变的公司"全都在这张表里，而这正是判断产业景气最需要盯的那批。
/// 3. <see cref="ChangeReason"/> 是<b>公司自述的变动原因</b>，能从里面提"产品涨价""供不应求"
///    "产能满负荷"这类词。这是法定披露文件里公司自己写的，比研报的转述硬。
/// </summary>
public class EarningsForecast
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>报告期（yyyy-MM-dd），如 2026-06-30。</summary>
    public DateTime ReportDate { get; set; }
    /// <summary>公告日 —— 增量抓取按它切片。</summary>
    public DateTime NoticeDate { get; set; }

    /// <summary>预测指标代码，004=归属于上市公司股东的净利润（最常用的一个）。</summary>
    public string PredictFinanceCode { get; set; } = "";
    /// <summary>预测指标名称。</summary>
    public string PredictFinance { get; set; } = "";

    /// <summary>预测区间下限/上限（元）。公司通常给一个区间而不是一个数。</summary>
    public double? AmountLower { get; set; }
    public double? AmountUpper { get; set; }
    /// <summary>同比增幅区间（%）。</summary>
    public double? AmplitudeLower { get; set; }
    public double? AmplitudeUpper { get; set; }

    /// <summary>预告类型：预增/预减/扭亏/首亏/续亏/略增…</summary>
    public string PredictType { get; set; } = "";
    /// <summary>预告正文，如"预计2026年1-6月归属于上市公司股东的净利润盈利:265万元至315万元"。</summary>
    public string Content { get; set; } = "";
    /// <summary>业绩变动原因（公司自述）。</summary>
    public string ChangeReason { get; set; } = "";
    /// <summary>去年同期值（元），用来算增速。</summary>
    public double? PreYearSamePeriod { get; set; }
    /// <summary>是否是这个报告期的最新一次预告——同一期公司可能修正多次。</summary>
    public bool IsLatest { get; set; }

    public DateTime FetchedAt { get; set; }
}

/// <summary>
/// 业绩快报（东财 <c>RPT_FCI_PERFORMANCEE</c>）。介于预告和正式财报之间：比预告详细（给的是
/// 确切数字而非区间，还带营收/ROE/每股净资产），比正式财报早。非强制披露，所以覆盖面不如预告。
/// </summary>
public class EarningsExpress
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime ReportDate { get; set; }
    public DateTime? NoticeDate { get; set; }
    /// <summary>数据更新日 —— 增量抓取按它切片（快报会修正，NOTICE_DATE 有时为空）。</summary>
    public DateTime? UpdateDate { get; set; }

    public double? Eps { get; set; }
    public double? Revenue { get; set; }
    /// <summary>营收同比（%）。</summary>
    public double? RevenueYoy { get; set; }
    public double? NetProfitParent { get; set; }
    /// <summary>归母净利同比（%）。</summary>
    public double? NetProfitYoy { get; set; }
    /// <summary>每股净资产。</summary>
    public double? Bvps { get; set; }
    /// <summary>加权平均 ROE（%）。</summary>
    public double? Roe { get; set; }
    /// <summary>单季营收环比 / 单季净利环比（%）。</summary>
    public double? RevenueQoq { get; set; }
    public double? NetProfitQoq { get; set; }

    public DateTime FetchedAt { get; set; }
}
