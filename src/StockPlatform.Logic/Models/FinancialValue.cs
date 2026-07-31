namespace StockPlatform.Logic.Models;

/// <summary>财务报表的规范化科目键——只抽取因子需要的少数科目，不存整张报表。</summary>
public static class FinancialKeys
{
    /// <summary>营业(总)收入（银行=一、营业收入）。</summary>
    public const string Revenue = "revenue";
    /// <summary>营业成本（银行/券商没有，毛利率因子对它们为空）。</summary>
    public const string OperCost = "oper_cost";
    /// <summary>净利润（利润表"五、净利润"；现金流量表里同名行是补充资料，不取）。</summary>
    public const string NetProfit = "net_profit";
    /// <summary>归属于母公司所有者的净利润（银行叫"归属于母公司的净利润"）。</summary>
    public const string NetProfitParent = "np_parent";
    public const string TotalAssets = "assets";
    public const string TotalLiabilities = "liab";
    /// <summary>归属于母公司股东权益（一般/银行叫法不同，取不到时退回所有者权益合计）。</summary>
    public const string EquityParent = "equity_parent";
    /// <summary>经营活动产生的现金流量净额。</summary>
    public const string Ocf = "ocf";
}

/// <summary>一只股票某报告期某科目的值（单位：元）。报表值是**年内累计**口径（A股定期报告惯例），
/// TTM/单季由消费端换算。</summary>
public class FinancialValue
{
    public string Code { get; set; } = "";
    /// <summary>报告期（季度末：0331/0630/0930/1231）。注意这不是公告日——数据源没有公告日，
    /// 消费端按法定披露截止日估计可用时点（见 FactorLab 的说明）。</summary>
    public DateTime ReportDate { get; set; }
    public string Key { get; set; } = "";
    public double Value { get; set; }
}
