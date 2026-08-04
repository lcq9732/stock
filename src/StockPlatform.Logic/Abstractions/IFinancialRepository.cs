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
}
