using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>ETF 官方份额的本地存取（EtfShare 表，2026-09-23）。</summary>
public interface IEtfShareRepository
{
    void EnsureSchema();

    /// <summary>写入一天（或几天）的份额。同一 (code, trade_date) 覆盖——官方值不会变，覆盖只是幂等。</summary>
    void Upsert(IReadOnlyList<EtfShareRow> rows);

    /// <summary>已经有份额的交易日（用来算"还差哪几天没拉"）。</summary>
    HashSet<DateOnly> GetDays();

    /// <summary>表里出现过的全部 ETF 代码（6 位裸码）。</summary>
    List<string> GetCodes();

    /// <summary>一只 ETF 的全部份额，按日期索引（万份）。</summary>
    Dictionary<DateOnly, double> GetByCode(string code);
}
