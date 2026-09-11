namespace StockPlatform.Logic.Models;

/// <summary>
/// 年报里披露的一家子公司（2026-09-11）。来源是「合并财务报表范围 / 在子公司中的权益」那张表。
///
/// ════ 要它干什么 ════
/// 给交易对手做**实体消歧**。年报里的客户/供应商写的是"中国建筑第六工程局有限公司"，
/// 本地股票池里只有母公司"中国建筑 601668"，直接对不上——实测 10.9 万个未还原的对手名里，
/// 相当一部分是上市公司的子公司。有了这份名单就能把它们归到母公司代码上。
///
/// ⚠ 归并是**假设**，不是事实：子公司跟你做生意不等于母公司跟你做生意。所以落到
/// StockCustomerSupplier.match_type 上要单独标 "subsidiary"，置信度低于 exact/normalized，
/// 用的时候能区分开。
/// </summary>
public class CompanySubsidiary
{
    /// <summary>母公司的 A 股代码（6 位）。</summary>
    public string Code { get; init; } = "";

    /// <summary>这份年报的报告期。同一家子公司在不同年报里都会出现，按报告期各存一行。</summary>
    public DateTime ReportDate { get; init; }

    /// <summary>子公司全称，年报里怎么写就怎么存（已去掉排版造成的换行和空格）。</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 持股比例（%）。**很多时候取不到**——各家表格的列名和列序都不一样，
    /// 有的分"直接/间接"两列、有的合成一列、有的干脆不在这张表里。
    ///
    /// ⚠ 取不到就是 null，**不拿它做过滤**。能进合并报表范围本身就意味着控制，
    /// 再加一道经常取不准的过滤只会误杀正确的名单。存着是因为将来可能有用。
    /// </summary>
    public double? HoldPct { get; init; }

    /// <summary>在年报 PDF 的第几页找到的。出了问题要能翻回原文核对。</summary>
    public int SourcePage { get; init; }
}
