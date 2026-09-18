using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 分红对账的**第二个源**（2026-09-18）——用来发现主源（新浪）漏掉了什么。
///
/// 为什么需要第二个源：分红是复权因子的输入，**缺一条除权记录，那只票的复权序列整段错、
/// 而且不报错**。这类静默错误没有任何现成的体检能发现（行都在、值也都对，就是少一条事件），
/// 只能拿另一个源比一遍。2026-09-18 第一次对账就查出 1,050 条缺口，
/// 其中 1,043 条是北交所——新浪对北交所覆盖不全（920061/920547/833171/430047 实测都返回
/// "暂时没有数据"）。
///
/// ⚠ 它是**补充**不是替代：东财那张表退市股全空（2,516 条只有我们有），
/// 2025/2026 的新记录也滞后。两边互补，谁都不能当权威——所以对账**只加不删**。
/// 见 doc/dividend-reconcile-design.md。
/// </summary>
public interface IDividendCrossCheckSource
{
    string SourceName { get; }

    event Action<string>? OnStatus;

    /// <summary>
    /// 全市场**已实施且有除权日**的分红记录，按年切片流式产出（一年一批）。
    /// 只要这一类是因为对账只关心"会不会影响复权"——预案没有除权日，影响不了。
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<DividendRow>> StreamImplementedAsync(
        int fromYear, int toYear, CancellationToken ct = default);
}
