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

    /// <summary>
    /// 每只股票的财务抓取状态（最新报告期 + 抓取时的科目集版本），供抓取端做增量判断。
    ///
    /// 为什么不能只看报告期：科目集扩充后，老数据的 report_date 仍然是"最新"的，但里面只有旧版
    /// 那几个科目。2026-08-27 实测 5780 只里 5552 只因此被跳过、44 个新科目一条没进库。
    /// 版本落后就必须重抓，见 <see cref="Models.FinancialKeys.Version"/>。
    ///
    /// 没有状态记录的票（旧库里抓过但那时还没这张表）返回 version=0，一律视为需要重抓。
    ///
    /// 2026-09-19 从元组换成 <see cref="FinancialFetchState"/>：多出来的 TargetDate/FetchedAt
    /// 是"上次冲着哪个报告期问的、什么时候问的"，抓取端靠它认出"问过了但数据源就是没有"。
    /// </summary>
    Dictionary<string, FinancialFetchState> GetFetchStateByCode();

    /// <summary>
    /// 单只股票的**全部报告期、全部科目**，按报告期降序（最新在前）。给"财务分析"用——它要同期
    /// 对比（本期 vs 去年同期）、单季拆解（本期累计 − 上期累计）和多期趋势，一次全取最省事。
    ///
    /// 这里可以逐只查，跟类注释里"不做逐只查询"的限制不冲突：那条针对的是**扫全市场**的场景
    /// （5000+ 次查询会把一次扫描拖到分钟级），而财务分析是用户点开某一只票时才跑，一次一只。
    /// </summary>
    List<FinancialSnapshot> GetAllByCode(string code);
}
