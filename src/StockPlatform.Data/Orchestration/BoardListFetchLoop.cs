using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// 抓板块列表的循环 —— **按页续传 + 暂存区提交**（2026-09-04 重写）。
///
/// 抽出来单独放是因为它藏过两个代价很大的 bug，而 FetchOrchestrator 有 17 个必需依赖、测不动。
///
/// ════ 病根 1：部分成果被整批丢掉 ════
/// 最早的写法是"概念 + 行业都抓完，最后写一次库"。于是每轮都是——概念板块 504 个完整抓到，
/// 接着抓行业时被限流抛异常，**写库那行根本执行不到**，504 个跟着一起丢。试多少次丢多少次。
///
/// ════ 病根 2：每轮从第 1 页重来，永远到不了第 6 页 ════
/// push2 限流下一轮往往抓到第 5 页就被拒。而"这一类整轮作废、下轮从头再来"意味着：
/// 每轮白烧 5 页配额，然后**在同一个地方**被拒。第 6 页那 4 个板块永远也拿不到。
///
/// ════ 现在的做法 ════
/// 抓一页 → 存进暂存区（正表一动不动）→ 记下"下次从第几页接着抓"。
/// 等某一类凑齐了（暂存区条数 ＝ 接口自报的 total），才在一个事务里整体搬进正表。
///
/// 为什么非要中间这道暂存区：半截列表得存下来（否则没法续），但半截列表**绝不能进正表**——
/// Board 是快照语义，"这一轮没出现的板块＝已下架"会连成分股一起删掉，而成分股是逐板块抓的、
/// 约 2500 个请求、跨好几轮才攒得齐。走暂存区之后，正表要么是旧的完整快照、要么是新的完整快照，
/// 不会出现半新半旧的中间态。
/// </summary>
public static class BoardListFetchLoop
{
    /// <summary>翻页的硬上限，防止接口异常时无限翻下去。504 个板块 6 页就够，20 页余量很足。</summary>
    private const int MaxPages = 20;

    /// <summary>
    /// 断点放多久算过期。板块列表是当天的行情快照，隔夜的半截数据没有接着抓的价值——
    /// 那时候该重开一轮，拿当天的数。
    /// </summary>
    private static readonly TimeSpan StateExpiry = TimeSpan.FromHours(12);

    /// <param name="fetchPage">抓某一类的第 N 页，返回（这一页的板块, 接口自报总数, 是不是最后一页）。</param>
    /// <returns>本轮总共提交了多少个、哪几类还没凑齐。</returns>
    public static async Task<(int Committed, List<string> Unfinished)> RunAsync(
        Func<BoardType, int, CancellationToken, Task<(List<Board> Items, int Total, bool IsLastPage)>> fetchPage,
        IBoardRepository repo,
        Action<string> report,
        Action<string> addError,
        CancellationToken ct = default)
    {
        int committedTotal = 0;
        var unfinished = new List<string>();

        // 遍历所有类型（2026-09-06 加地域时改）——写死两个的话，新加的类型会静悄悄地漏掉。
        foreach (var type in Enum.GetValues<BoardType>())
        {
            var label = type.Label();
            ct.ThrowIfCancellationRequested();

            // ── 决定这一类从第几页开始 ──
            var state = repo.GetListState(type);
            int startPage = 1;
            var runStartedAt = DateTime.Now;

            if (state is { } st && DateTime.Now - st.RunStartedAt < StateExpiry)
            {
                startPage = st.NextPage;
                runStartedAt = st.RunStartedAt;
                report($"{label}板块：接着上次的断点，从第 {startPage} 页开始"
                     + $"（已攒 {repo.CountStaged(type)} 个，接口报总共 {st.Total} 个）。");
            }
            else
            {
                // 没有断点，或者断点是隔夜的——重开一轮。旧的暂存内容要丢掉，
                // 否则会跟今天的数据混在一起，凑出来的"完整名单"其实半新半旧。
                if (state != null)
                    report($"{label}板块：上次的断点是 {state.Value.RunStartedAt:M-d HH:mm} 的，隔太久了，重开一轮。");
                repo.ClearStaged(type);
                repo.ClearListState(type);
            }

            int total = 0;
            bool finished = false;
            string? stopReason = null;

            for (int page = startPage; page <= MaxPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var (items, pageTotal, isLast) = await fetchPage(type, page, ct);
                    if (pageTotal > 0) total = pageTotal;

                    if (items.Count > 0)
                    {
                        // 抓一页存一页：这就是断点续传的粒度，也是限流下唯一不浪费配额的做法
                        foreach (var b in items) b.AsOf = runStartedAt;
                        repo.StageBoards(items);
                    }

                    var staged = repo.CountStaged(type);
                    repo.SaveListState(type, runStartedAt, page + 1, total, staged);
                    report($"{label}板块 第 {page} 页：+{items.Count} 个（已攒 {staged}"
                         + (total > 0 ? $"/{total}" : "") + "）");

                    if (isLast || (total > 0 && staged >= total)) { finished = true; break; }
                }
                catch (OperationCanceledException)
                {
                    report($"{label}板块抓取已停止。已攒 {repo.CountStaged(type)} 个在暂存区（不会丢），"
                         + "下次从断点接着抓。");
                    throw;
                }
                catch (Exception ex)
                {
                    // 这一页没拿到——**前面几页的成果都在暂存区里**，下轮从这一页接着来。
                    // 这正是限流下最要紧的一条：配额不能浪费在已经拿到的页上。
                    stopReason = ex.Message;
                    repo.SaveListState(type, runStartedAt, page, total, repo.CountStaged(type));
                    break;
                }
            }

            var stagedNow = repo.CountStaged(type);

            if (finished && stagedNow > 0 && (total <= 0 || stagedNow >= total))
            {
                // 凑齐了才提交：这一下才会动正表（写入 + 清掉本轮没出现过的板块）
                var (committed, pruned) = repo.CommitStaged(type);
                repo.ClearListState(type);
                committedTotal += committed;
                report($"{label}板块已更新：{committed} 个"
                     + (pruned > 0 ? $"（清掉 {pruned} 条下架板块的残留）" : "") + "。");
            }
            else
            {
                unfinished.Add(label);
                var why = stopReason != null ? $"（{stopReason}）" : "";
                addError($"{label}板块列表没抓完{why}：已攒 {stagedNow}"
                       + (total > 0 ? $"/{total}" : "") + " 个，下轮从断点接着抓。");
                report($"⚠ {label}板块没凑齐{why}——已攒 {stagedNow}"
                     + (total > 0 ? $"/{total}" : "") + " 个存在暂存区，"
                     + "正表保持上一次的完整快照不动，下轮接着抓剩下的。");
            }
        }

        return (committedTotal, unfinished);
    }
}
