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

    /// <summary>
    /// **整日替换**：删掉这一天的全部行、写入本批（同一事务），返回写入行数。
    ///
    /// ⚠ 必须传这一天**买卖两侧的全部行**——只传一侧等于把另一侧永久删掉。
    /// 2026-09-17 取代了原来的 <c>Upsert</c>，理由见实现类的注释（主键末列 <c>seq</c> 是位次，
    /// 靠它 UPSERT 会堆副本）。
    /// </summary>
    int ReplaceForDay(DateTime day, IReadOnlyList<LhbSeat> rows);

    /// <summary>本地已有的最新交易日，增量水位线（没有数据时为 null）。</summary>
    DateTime? GetLatestTradeDate();

    int Count();

    /// <summary>某只股票某天的席位明细（买卖两边，按净额倒序）。</summary>
    List<LhbSeat> QueryByStock(string code, DateTime tradeDate);

    /// <summary>某个营业部的上榜记录，按日期倒序——自建游资库、算席位胜率用。</summary>
    List<LhbSeat> QueryBySeat(string seatCode, int limit = 200);
}
