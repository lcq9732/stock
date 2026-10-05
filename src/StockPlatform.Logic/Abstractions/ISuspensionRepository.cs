using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>停复牌记录的本地存取（Suspension / SuspensionFetchMonth 两张表，2026-09-30）。</summary>
public interface ISuspensionRepository
{
    void EnsureSchema();

    /// <summary>按 (source, market, code, <see cref="SuspensionRow.Key"/>) 覆盖写入。</summary>
    void Upsert(IReadOnlyList<SuspensionRow> rows);

    /// <summary>这一路接口已经问过的月份（每月 1 日）。回补中断后从这里接着问。</summary>
    HashSet<DateOnly> GetFetchedMonths(string source);

    /// <summary>记一笔"这个月问过了"（跟那个月的行一起落账，停在哪都不丢）。</summary>
    void MarkMonthFetched(string source, DateOnly month, int rows);

    /// <summary>
    /// 这些 Bar 代码（个股裸码、ETF 带市场前缀）的**全部**停复牌记录，按传进来的代码分组。
    /// 必须是全部而不是某段日期的：深交所长期停牌的「停牌」和「取消停牌」可能隔好几个月，
    /// 只取一段就配不上对。
    /// </summary>
    Dictionary<string, List<SuspensionRow>> GetByBarCodes(IReadOnlyCollection<string> barCodes);
}
