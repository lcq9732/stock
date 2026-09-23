using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>ETF 官方份额的本地存取（EtfShare 表，2026-09-23）。</summary>
public interface IEtfShareRepository
{
    void EnsureSchema();

    /// <summary>写入份额。同一 (market, code, trade_date) 覆盖——深交所 T 日晚间的值只是参考，要能被第二天的正式值替换。</summary>
    void Upsert(IReadOnlyList<EtfShareRow> rows);

    /// <summary>某个市场已经有份额的交易日（用来算"还差哪几天没拉"）。</summary>
    HashSet<DateOnly> GetDays(string market);

    /// <summary>表里出现过的全部 ETF（市场 + 6 位裸码）。</summary>
    List<(string Market, string Code)> GetCodes();

    /// <summary>一只 ETF 的全部份额，按日期索引（万份）。</summary>
    Dictionary<DateOnly, double> GetByCode(string market, string code);
}
