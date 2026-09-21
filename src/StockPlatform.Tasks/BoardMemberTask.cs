using System.Runtime.CompilerServices;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【板块成分股】（2026-09-21 从编排器迁到新框架，见 doc/remaining-tasks-migration-design.md §2.1）——
/// 逐板块抓成分股名单，**逐板块落库**。
///
/// ════ 一批＝一个板块 ════
/// 落库粒度本来就是一个板块（<c>ReplaceMembers</c> 一次一个），所以批边界是现成的。
/// 迁过来最大的收益也在这儿：这一项 1000 个板块 ≈ 2500 个 push2 请求、估时 20 分钟、
/// 限流下跨好几轮才抓得完，以前只能靠"连续失败熔断"或人点停止收尾，现在
/// <see cref="TaskRunArgs.MaxItems"/> / <see cref="TaskRunArgs.Deadline"/> 直接落在板块边界上。
///
/// ════ 两道闸 ════
/// ① **push2 熔断**：限流期间整轮不开工。⚠ 只拦走网络的那几条通道——terminal 读的是东财终端
///    落在本地的文件，一个请求都不发，被"东财接口限流中"挡住毫无道理（而且它恰恰是限流时
///    唯一还能用的路）。会撞上是因为终端那条也继承 <c>EastMoneyBoardFetcherBase</c>：
///    名单那一步退回 push2 失败时会给基类记上 PausedUntil，成分股这边跟着被拦。
/// ② **连续失败**：15 个连着失败就判定被限流、提前收尾（判据见 <see cref="ConsecutiveFailureGate"/>）。
///
/// ════ 先小后大 ════
/// 排序判据在 <see cref="BoardMemberPlanner"/>——大板块最贵也最容易失败，先把小的收干净。
/// </summary>
public sealed class BoardMemberTask(
    BoardFetcherHolder fetcherHolder,
    IBoardRepository repository) : FetchTaskBase<BoardMemberTask.BoardMembers>
{
    public override FetchActionId Id => FetchActionId.StepBoardMembers;

    /// <summary>一个板块的成分股名单。失败的不产出批（失败当场标进库，见 <c>MarkMembersFailed</c>）。</summary>
    public sealed record BoardMembers(string BoardCode, string BoardName, IReadOnlyList<string> Codes);

    private IBoardFetcher Fetcher => fetcherHolder.Current;

    private readonly List<string> _errors = [];
    private int _ok, _failed, _planned;
    private bool _throttled;
    private string? _skippedReason;
    private string? _progressText;

    protected override async IAsyncEnumerable<IReadOnlyList<BoardMembers>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _errors.Clear();
        _ok = _failed = _planned = 0;
        _throttled = false;
        _skippedReason = _progressText = null;

        // ── 闸①：push2 熔断（只拦走网络的通道，见类注释）──
        if (Fetcher is not EastMoneyTerminalBoardFetcher
            && Fetcher is EastMoneyBoardFetcherBase emb && emb.PausedUntil is { } until)
        {
            var mins = Math.Max(1, (int)Math.Ceiling((until - DateTime.Now).TotalMinutes));
            _skippedReason = $"东财行情接口限流熔断中，预计 {until:HH:mm} 恢复（还有约 {mins} 分钟）";
            Report($"{_skippedReason}，本轮不开工。已抓到的板块都在库里，恢复后接着抓没抓过的。");
            yield break;
        }

        var fetcher = Fetcher;
        void Forward(string s) => Report(s);
        fetcher.OnStatus += Forward;
        try
        {
            if (fetcher is EastMoneyBoardFetcherBase em3)
            {
                await em3.PrepareAsync(ct);
                var nic = em3.DescribeBinding();
                Report(em3.DescribeChannel() + (string.IsNullOrWhiteSpace(nic) ? "" : "；" + nic));
            }

            // 名单从库里读——列表那一项没跑也能干活，只是漏掉当天新增的板块（软依赖）。
            // 读库是同步 IO，推线程池（骨架不替子类推）。
            var (all, fresh) = await Task.Run(() =>
            {
                repository.EnsureSchema();
                return (repository.QueryBoards(), repository.GetBoardsWithFreshMembers(FreshSince));
            }, ct);

            if (all.Count == 0)
            {
                _skippedReason = "库里还没有板块名单（先跑【概念和行业板块】）";
                Report("库里还没有板块名单，先跑一次【概念和行业板块】再来抓成分股。");
                yield break;
            }

            var todo = BoardMemberPlanner.Plan(all, fresh);
            _planned = todo.Count;
            Report($"成分股：{fresh.Count} 个板块已是最近抓的，本轮需抓 {todo.Count} 个"
                 + $"（先小后大；其中 {BoardMemberPlanner.CountBig(todo)} 个是 "
                 + $"{BoardMemberPlanner.BigBoardThreshold} 只以上的大板块，排在最后）。");
            if (todo.Count == 0) yield break;

            int consecutiveFail = 0;
            for (int i = 0; i < todo.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var b = todo[i];

                List<string>? members = null;
                try
                {
                    members = await fetcher.FetchMembersAsync(b.BoardCode, ct);
                    consecutiveFail = 0;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _failed++;
                    consecutiveFail++;
                    await Task.Run(() => repository.MarkMembersFailed(b.BoardCode, "failed", ex.Message), CancellationToken.None);
                    if (_failed <= 5) _errors.Add($"板块「{b.Name}」({b.BoardCode}) 成分股抓取失败：{ex.Message}");
                    // 失败原因当场进日志：只写进库的 message 列的话，日志里就一句"成功 2、失败 3"，
                    // 人只知道坏了、不知道坏在哪，得去查库才看得到"只取到 20 只"这种一眼定位的信息。
                    Report($"　板块「{b.Name}」({b.BoardCode}) 失败：{ex.Message}");

                    // ── 闸②：连续失败 ──
                    if (ConsecutiveFailureGate.ShouldStop(consecutiveFail))
                    {
                        _throttled = true;
                        Report($"⚠ 连续 {consecutiveFail} 个板块失败，判定为已被限流，本轮提前收尾。"
                             + $"已成功 {_ok} 个，剩余 {todo.Count - i - 1} 个下轮继续。");
                        _errors.Add($"push2 限流，本轮成分股只抓到 {_ok}/{todo.Count} 个板块，剩余下轮继续。");
                        yield break;
                    }
                }

                // 每 5 个报一次（每个板块要 4~17 秒，20 个就是一两分钟不吭声——人判断"还在跑吗"
                // 全靠日志有没有新行）。心跳每个板块一次，免得被静默看门狗误判。
                if ((i + 1) % 5 == 0 || i + 1 == todo.Count)
                    Report($"成分股：{i + 1}/{todo.Count}（成功 {_ok}、失败 {_failed}） 刚抓完「{b.Name}」",
                           i + 1, todo.Count);
                else
                    ReportQuiet($"成分股：{i + 1}/{todo.Count}（成功 {_ok}、失败 {_failed}）", i + 1, todo.Count);

                if (members != null) yield return [new BoardMembers(b.BoardCode, b.Name, members)];
            }
        }
        finally { fetcher.OnStatus -= Forward; }
    }

    /// <summary>
    /// 「抓过多久算还新鲜」的时间线。挂在**通道**上——读本地文件那条不节流
    /// （<c>MemberFreshFor &lt;= 0</c>），把时间线推到 MaxValue，于是没有任何记录算新鲜、全部重抓。
    /// </summary>
    private DateTime FreshSince
    {
        get
        {
            var fresh = Fetcher.MemberFreshFor;
            return fresh <= TimeSpan.Zero ? DateTime.MaxValue : DateTime.Today - fresh;
        }
    }

    /// <summary>落一个板块的成分股。**这是断点的粒度**——停在哪都不会留半批数据。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<BoardMembers> batch, CancellationToken ct)
        => Task.Run(() =>
        {
            foreach (var b in batch)
            {
                repository.ReplaceMembers(b.BoardCode, b.Codes);
                _ok++;
            }
        }, ct);

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        // 用户点了停止：把现场交代清楚再退。每个板块是单独落库的，所以停在哪都不会留半批数据；
        // 人要知道的是"停之前做成了多少、下次从哪接"。
        Report($"板块成分股已停止：本轮成功 {_ok} 个（都已落库，不会丢）、失败 {_failed} 个，"
             + $"剩 {Math.Max(0, _planned - _ok - _failed)} 个没抓。"
             + "下次跑会自动跳过已抓好的，从没抓的接着来。");
        return Task.CompletedTask;
    }

    protected override async Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        // 「没开工」：限流熔断/没有板块名单——今天恢复了还该再来，所以是 Skipped。
        if (_skippedReason is { } blocked)
            return TaskRunResult.Skipped(blocked, _errors);

        // ⚠ 统计不能复用 FreshSince（2026-09-07 踩过）：那是**抓取判据**，不节流通道下等于
        //   MaxValue，拿它统计会把刚抓成功的 1031 个全算成"待重试"。
        var (pOk, pFailed, pNever) = await Task.Run(
            () => repository.GetMemberFetchProgress(
                BoardMemberPlanner.StatsSince(Fetcher.MemberFreshFor, DateTime.Today)), ct);
        int allBoards = pOk + pFailed + pNever;

        Report($"板块成分股完成：本轮成功 {_ok}、失败 {_failed}。"
             + $"全库成分股状态：最新 {pOk} 个、待重试 {pFailed} 个、从未抓过 {pNever} 个。"
             + (pFailed + pNever > 0 ? "（再跑一次会从没抓到的接着来）" : ""));

        // 存量进度带回界面：这活跨好几轮才做得完，光说"完成"人不知道还剩多少
        _progressText = allBoards > 0
            ? $"已抓 {pOk}/{allBoards}"
              + (pFailed > 0 ? $"，待重试 {pFailed}" : "")
              + (pNever > 0 ? $"，还剩 {pNever}" : "")
            : null;

        // 被限流提前收尾不是失败，也不是干净完成——记成跳过，排查好/等一阵还能再来。
        if (_throttled)
            return TaskRunResult.Skipped(
                $"板块成分股被限流，本轮只抓到 {_ok}/{_planned} 个", _errors) with { Progress = _progressText };

        return new TaskRunResult(TaskState.Completed, _errors,
                                 NothingToDo: _planned == 0, Progress: _progressText);
    }
}
