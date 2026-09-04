using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>板块行情快照的本地存取。板块数据每次抓取整体覆盖（不像 Bar 那样按日累积）——"热点"本身
/// 就是"当下的"，只保留最近一次快照即可，所以是 ReplaceAll 语义而非增量。</summary>
public interface IBoardRepository
{
    void EnsureSchema();

    /// <summary>
    /// 用一次抓取的结果整体替换本地板块数据（清空 Board/BoardMember 再写入）。
    /// <b>传入空集合是空操作</b>——板块是快照，抓取失败时保留库里上一次的数据才是对的，
    /// 不能因为这轮拿回来个空列表就把库清空。
    /// </summary>
    void ReplaceAll(IEnumerable<Board> boards);

    /// <summary>
    /// 只更新板块列表本身，<b>不动成分股</b>（2026-09-03）。
    ///
    /// 成分股改成逐个板块去 push2 取之后，"板块列表"和"成分股"就不再是一次抓取的产物了：
    /// 列表 10 个请求就能刷新，成分股要 2500 个请求、往往跨多轮才跑得完。用 ReplaceAll 会把
    /// 上几轮辛苦抓到的成分股一并清掉。
    /// </summary>
    void UpsertBoards(IEnumerable<Board> boards);

    // ── 板块列表的页级断点（2026-09-04）──
    //
    // 病根：push2 限流下一轮往往抓到第 5 页就被拒，而原来是"这一类整轮作废、下轮从第 1 页重来"，
    // 于是每轮白烧 5 页配额、再在同一个地方被拒，**永远到不了第 6 页**。
    //
    // 解法要同时满足两件互相打架的事：半截列表得存下来（否则没法续），但半截列表又不能进正表
    // （Board 是快照语义，"没出现＝已下架"会连成分股一起删掉，而成分股跨好几轮才攒得齐）。
    // 所以中间隔一道**暂存区**：抓一页存一页，等某一类凑齐了再整体搬进正表。
    // 这样正表要么是旧的完整快照、要么是新的完整快照，不会出现半新半旧的中间态。

    /// <summary>这一类下次从第几页接着抓；没有断点就是 null（该开新一轮）。</summary>
    (DateTime RunStartedAt, int NextPage, int Total, int FetchedCount)? GetListState(BoardType type);

    void SaveListState(BoardType type, DateTime runStartedAt, int nextPage, int total, int fetchedCount);
    void ClearListState(BoardType type);

    /// <summary>抓到的一页板块先进暂存区，正表一动不动。</summary>
    void StageBoards(IEnumerable<Board> boards);

    /// <summary>暂存区里这一类攒了多少个——跟接口自报的 total 比，判断凑齐没有。</summary>
    int CountStaged(BoardType type);

    /// <summary>
    /// 把暂存区里这一类整体搬进正表：清僵尸 + 写入 + 清空暂存区，一个事务里做完。
    /// ⚠ 只在确认**凑齐了**之后调。
    /// </summary>
    (int Committed, int Pruned) CommitStaged(BoardType type);

    /// <summary>丢掉暂存区里这一类的内容——开新一轮之前调。</summary>
    void ClearStaged(BoardType type);

    /// <summary>替换单个板块的成分股，并记下抓取状态。逐板块落库才能断点续传。</summary>
    void ReplaceMembers(string boardCode, IReadOnlyList<string> stockCodes);

    /// <summary>记一次失败/空结果，让下一轮知道这个板块还得重试。</summary>
    void MarkMembersFailed(string boardCode, string status, string message);

    /// <summary>
    /// 成分股在 <paramref name="since"/> 之后成功抓过的板块代码集合——下一轮据此跳过。
    /// 只认 status='ok'，失败和空结果都要重试。
    /// </summary>
    HashSet<string> GetBoardsWithFreshMembers(DateTime since);

    /// <summary>成分股抓取进度统计：(成功, 失败, 从未抓过)。</summary>
    (int Ok, int Failed, int Never) GetMemberFetchProgress(DateTime since);

    /// <summary>读板块列表，按涨跌幅从高到低排序。type 为 null 时返回全部。</summary>
    List<Board> QueryBoards(BoardType? type = null);

    /// <summary>读某个板块的成分股代码。</summary>
    List<string> QueryMembers(string boardCode);

    /// <summary>
    /// 回填板块的涨跌幅/成交额（2026-09-03）。
    ///
    /// 这两个值**不再从数据源取，改成本地算**：板块指数合成那一步已经用成分股日K等权算出了
    /// 板块指数（含成交额），最新一根就是当日板块行情。这样既不依赖只能人工过验证的
    /// push2 接口，口径也跟板块K线天然一致——不会出现"热度页一个口径、K线另一个口径"。
    /// </summary>
    void UpdateQuotes(IEnumerable<(string BoardCode, double ChangePct, double Amount)> quotes);

    /// <summary>反查：股票代码 → 它所属的概念/题材板块名称列表（只含 Concept 类型）。一次 JOIN 查询
    /// 建好整张映射，供自选股等需要"这只票在哪些概念板块里"的地方一次性取用，避免逐板块查询。</summary>
    Dictionary<string, List<string>> GetConceptBoardsByStock();

    /// <summary>本地板块快照的抓取时刻（没有数据时为 null），用于界面显示"数据截至"。</summary>
    DateTime? GetLatestAsOf();
}
