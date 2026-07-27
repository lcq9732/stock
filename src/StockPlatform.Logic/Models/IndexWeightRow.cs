namespace StockPlatform.Logic.Models;

/// <summary>指数成分股的权重（中证指数官网 closeweight.xls）——只有中证系指数(000/399 部分)有；
/// <see cref="Weight"/> 单位为百分比（例如 3.45 表示占 3.45%）。<see cref="AsOfDate"/> 是中证披露的
/// 权重基准日（xls 里的"日期"列，通常是上个月末）。</summary>
public class IndexWeightRow
{
    public string IndexCode { get; set; } = "";   // 6 位指数代码
    public string StockCode { get; set; } = "";   // 6 位成分股代码
    public double Weight { get; set; }            // 占指数权重（%）
    public DateTime AsOfDate { get; set; }        // 权重基准日
    public DateTime FetchedAt { get; set; }
}
