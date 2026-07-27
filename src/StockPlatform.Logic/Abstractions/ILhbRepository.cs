using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>龙虎榜的本地存取（Lhb 表）——按交易日累积保留历史（INSERT OR IGNORE，同 K线/资金流的按日
/// 累积语义），不覆盖。</summary>
public interface ILhbRepository
{
    void EnsureSchema();

    /// <summary>写入一批龙虎榜记录（已存在的 (trade_date, stock_code, reason) 忽略）。</summary>
    void InsertOrIgnore(IEnumerable<LhbRow> rows);

    /// <summary>本地已有龙虎榜的最新交易日（没有数据为 null），供界面显示与判断从哪天续抓。</summary>
    DateTime? GetLatestTradeDate();

    /// <summary>本地已有龙虎榜数据的全部交易日集合——供"一键补齐每日历史"跳过已有的日子、不重复请求。</summary>
    HashSet<DateOnly> GetTradeDates();
}
