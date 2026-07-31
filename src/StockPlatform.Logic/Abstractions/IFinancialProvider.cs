using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>抓取一只股票**全部历史**的关键财务科目（利润表/资产负债表/现金流量表各一个请求）——
/// 给基本面因子用（FactorLab M4）。财报按季度披露，日常增量几乎零成本（见编排层的按报告期跳过逻辑）。</summary>
public interface IFinancialProvider
{
    event Action<string>? OnStatus;

    /// <summary>取该股上市以来全部报告期的关键科目（单位：元）。退市股同样可取。</summary>
    Task<List<FinancialValue>> GetAllAsync(string code, CancellationToken ct = default);
}
