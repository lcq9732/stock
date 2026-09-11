using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// 分档资金流「逐股补历史」的排队判据（2026-09-11 抽出来）。
///
/// 抽出来是因为同一份判据有两个调用方：任务本体（决定这一轮抓哪些票）和界面上那个
/// "还有 N 只历史不全"的计数。这个项目在"同一判据两处各写一份"上栽过
/// （见 <see cref="Sqlite.SqliteAdjSeriesAuditor"/> 的由来）——两处漂移之后，
/// 界面显示 0 而任务仍在抓、或者反过来，谁都不会发现。
///
/// ════ 判据：库里这只票有多少行，不是"今天抓过没有" ════
/// 全市场快照通道（push2delay）每天会给每只票都写一行，"今天抓过没有"恒为真——
/// 沿用老判据的话这一段会永远无事可做，而历史缺口只有它补得了。
/// 行数不会被快照带偏：快照一天加一行，补齐一只票要 120 行。
///
/// ════ 排序：最久没抓的先抓 ════
/// 2026-09-04 那个坑：按代码顺序排的话，每天零点一到又从 000001 开始，靠后的票永远轮不到。
/// 这条能继续成立，是因为快照写库时把 fetched_at 写成**行情时间**而不是"现在"——
/// 逐股抓过的票时间戳必然比它新，两者仍分得开。
/// </summary>
public static class MoneyFlowBackfillPlan
{
    /// <summary>
    /// 一只票的历史"补齐了"的行数门槛。
    ///
    /// 接口给的是最近约 120 个交易日，实测一只正常交易的票能拿回 115~120 行。取 100 是留余量：
    /// 停牌过几天的、上市不足半年的，行数本来就到不了 120，卡在 120 会让它们**每轮都被重抓**。
    /// 代价是上市不满 100 个交易日的新股仍会被反复排进队——但它们排在"最久没抓的"队尾，
    /// 一轮最多轮到一次，浪费几个请求，可以接受。
    /// </summary>
    public const int FullWindowRows = 100;

    /// <summary>
    /// 排出本轮的队。<paramref name="codes"/> 是本地**个股**名册
    /// （指数/ETF/板块不在这条路上，见 <see cref="LocalStockCodes"/>）。
    /// </summary>
    /// <returns>(待补队列（已按最久没抓排序）, 其中库里一行都没有的只数)。</returns>
    public static (List<string> Todo, int Never) Build(
        IReadOnlyCollection<string> codes, INetInflowDetailRepository repository)
    {
        var rowCounts = repository.GetRowCountByCode();
        var lastFetched = repository.GetLastFetchedAt();

        var todo = codes.Where(c => !rowCounts.TryGetValue(c, out var n) || n < FullWindowRows)
                        .OrderBy(c => lastFetched.TryGetValue(c, out var t) ? t : DateTime.MinValue)
                        .ToList();
        int never = codes.Count(c => !rowCounts.ContainsKey(c));
        return (todo, never);
    }

    /// <summary>本地已知的个股代码（<c>type='stock'</c>，指数/ETF/板块/退市股不在内）。</summary>
    public static List<string> LocalStockCodes(string dbPath)
        => SqliteStockMetaUpsert.GetAll(dbPath).Select(s => s.Code).ToList();
}
