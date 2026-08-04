namespace StockPlatform.Logic.Models;

/// <summary>一只股票"最新一期"财报的关键科目快照——按 code 批量取回给全市场扫描用（逐只查
/// FinancialReport 表在 5000+ 只的扫描里太慢）。<see cref="Values"/> 的键见 <see cref="FinancialKeys"/>，
/// 缺失的科目直接不在字典里（数据源没给），消费端要按"取不到就跳过这条判断"处理。</summary>
public class FinancialSnapshot
{
    /// <summary>报告期（季度末）。累计口径的科目要按它所在季度年化，见 <see cref="AnnualizeCumulative"/>。</summary>
    public DateTime ReportDate { get; set; }

    public Dictionary<string, double> Values { get; set; } = new();

    public double? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;

    /// <summary>把年内累计值折算成年化——A股定期报告是累计口径（一季报=Q1、半年报=Q1+Q2…），
    /// 直接拿一季报的净利润除以净资产会把 ROE 低估到四分之一。</summary>
    public double AnnualizeCumulative(double cumulative) => ReportDate.Month switch
    {
        3 => cumulative * 4.0,
        6 => cumulative * 2.0,
        9 => cumulative * 4.0 / 3.0,
        _ => cumulative,
    };
}
