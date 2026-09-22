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

    /// <summary>
    /// 每只票在 <b>某一段交易日窗口内</b>有多少行（2026-09-11）。
    ///
    /// 排队判据只能用这个、不能用上面那个全表计数：这张表是**累积**的（每天快照追一行），
    /// 全表行数迟早超过任何固定门槛，判据于是恒为假、一只都不排——而接口只给最近约 120 个
    /// 交易日，能不能补齐说的从来只是那个窗口里的事。
    /// </summary>
    Dictionary<string, int> GetRowCountByCode(DateTime from, DateTime to);

    int Count();
    int CountCodes();

    List<NetInflowDetail> Query(string code, int limit = 120);

    /// <summary>
    /// 全市场快照这一天**已经抓到手的页号**（2026-09-21）。
    ///
    /// 东财的配额实测一轮只放过约 16 页，而全市场约 60 页。原来"任一页失败就整轮不落库"的
    /// 做法在这个配额下永远攒不满——每轮抓 16 页、每轮全扔。记住已抓的页，下一轮只补缺的。
    ///
    /// 为什么不能从 <c>NetInflowDetail</c> 的行反推页号：停牌股整行不写库，页边界对不齐。
    /// </summary>
    HashSet<int> GetSnapshotPages(DateTime day);

    /// <summary>
    /// 记下"这一天的这些页抓到了"。<paramref name="rowsByPage"/> 是页号→这页拿到几行，
    /// 行数只用来事后对账（"第 37 页只有 3 行"一眼能看出那页是被截断的）。
    /// </summary>
    void MarkSnapshotPages(DateTime day, IReadOnlyDictionary<int, int> rowsByPage, DateTime fetchedAt);
}
