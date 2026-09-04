using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 龙虎榜营业部席位明细的本地存取（2026-09-03 新增）。
///
/// 跟现有的 <c>ILhbRepository</c> 是**不同粒度**、不是替换：那张表只有"某天某股上榜了、
/// 原因、成交额"，这张才有买卖前五营业部名单——龙虎榜真正的信息量在"是谁在买"。
/// </summary>
public interface ILhbSeatRepository
{
    void EnsureSchema();

    int Upsert(IEnumerable<LhbSeat> items);

    /// <summary>本地已有的最新交易日，增量水位线（没有数据时为 null）。</summary>
    DateTime? GetLatestTradeDate();

    int Count();

    /// <summary>某只股票某天的席位明细（买卖两边，按净额倒序）。</summary>
    List<LhbSeat> QueryByStock(string code, DateTime tradeDate);

    /// <summary>某个营业部的上榜记录，按日期倒序——自建游资库、算席位胜率用。</summary>
    List<LhbSeat> QueryBySeat(string seatCode, int limit = 200);
}
