using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>融资融券明细本地存取（MarginDetail 表）——按交易日累积保留历史(INSERT OR IGNORE)，同 K线/
/// 龙虎榜的按日累积语义。</summary>
public interface IMarginRepository
{
    void EnsureSchema();

    /// <summary>写入一批融资融券明细（已存在的 (trade_date, code) 忽略）。</summary>
    void InsertOrIgnore(IEnumerable<MarginDetailRow> rows);

    /// <summary>本地已有融资融券数据的最新交易日（没有为 null），供界面显示与判断从哪天续抓。</summary>
    DateTime? GetLatestTradeDate();

    /// <summary>某只标的的融资余额时间序列（按交易日升序）——供彬哥法判断"最新融资余额是否较上一交易日增长"。</summary>
    List<(DateTime TradeDate, double MarginBalance)> GetBalanceSeries(string code);

    /// <summary>本地已有融资余额数据的全部交易日集合——供"一键补齐每日历史"跳过已有的日子、不重复请求。</summary>
    HashSet<DateOnly> GetTradeDates();
}
