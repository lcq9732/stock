using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 分档资金流的本地存取（2026-09-03 新增）。
///
/// 跟现有的 <c>NetInflow</c> 是同一件事的不同精度：那张表每行只有主力净额合计，
/// 这张拆成超大单/大单/中单/小单的净额与净占比。
/// </summary>
public interface INetInflowDetailRepository
{
    void EnsureSchema();

    int Upsert(IEnumerable<NetInflowDetail> items);

    /// <summary>
    /// 这只票是否已在 <paramref name="since"/> 之后抓过——断点续传用。
    /// 接口是滚动 120 天窗口、没有增量入口，只能按"抓取时刻"判断，不能按数据日期。
    /// </summary>
    bool HasFreshData(string code, DateTime since);

    /// <summary>
    /// 每只票上次抓取的时刻。用来把队排成"最久没抓的先抓"——
    /// 光按代码顺序的话，每天零点一过就又从头开始，靠后的票永远轮不到。
    /// </summary>
    Dictionary<string, DateTime> GetLastFetchedAt();

    /// <summary>
    /// 每只票在库里有多少行（2026-09-06）。判断"这只票的 120 天历史补齐了没有"用它。
    ///
    /// 为什么不能再用 <see cref="GetLastFetchedAt"/> 判：全市场快照通道（push2delay）每天
    /// 会把**所有**票的 fetched_at 刷成今天，于是"今天没抓过"这个判据恒为假，
    /// 逐股补历史那条路会一只都不抓——而历史恰恰只有它补得了。
    /// 行数不受快照影响：快照一天只加一行，补齐一只票要 120 行。
    /// </summary>
    Dictionary<string, int> GetRowCountByCode();

    int Count();
    int CountCodes();

    List<NetInflowDetail> Query(string code, int limit = 120);
}
