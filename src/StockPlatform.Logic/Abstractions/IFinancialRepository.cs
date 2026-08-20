using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>FinancialReport 表的只读侧（写入侧在 Data 层的 SqliteFinancialRepository 上，抓取
/// 专用）。分析程序扫全市场时只需要"每只股票最新一期的几个科目"，所以这里只暴露批量快照，
/// 不做逐只查询——268万行的表逐只查 5000+ 次会把一次扫描拖到分钟级。</summary>
public interface IFinancialRepository
{
    void EnsureSchema();

    /// <summary>每只股票最新报告期的关键科目快照。</summary>
    Dictionary<string, FinancialSnapshot> GetLatestSnapshotByCode();

    /// <summary>每只股票最近 <paramref name="count"/> 个**完整年度**（12-31 报告期）的归母净利润，
    /// 按年份升序。给结果表展示"最近年度净利 / N年累计净利"用——单期利润容易被一次性损益或
    /// 季节性扭曲（连亏三年的公司也可能某个季度微利），多年累计才看得出真实盈利能力。
    ///
    /// **只用于显示，不参与筛选**：回测显示把它做成硬条件反而降低收益（剔掉的那批平均 3.15%~5.63%，
    /// 高于 2.55% 的基准），因为这套方法赚的是超跌反弹的钱，跌得最惨的往往正是基本面最难看的。
    /// 但回测样本已排除 ST/退市股，测不出"踩雷退市"这类尾部风险，所以这个信息仍然要摆在用户眼前
    /// 让人自己判断。详见 doc/analysis-app-design.md 3.2.8。</summary>
    Dictionary<string, List<(int Year, double NetProfitParent)>> GetRecentAnnualNetProfitByCode(int count);
}
