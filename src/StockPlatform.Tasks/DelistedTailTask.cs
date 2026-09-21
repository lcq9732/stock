using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【退市股收尾】（2026-09-21 从编排器迁到新框架，K线六项的最后一步）——
/// 刷新退市名单，并给"本地跟踪过、但最后一根K线还早于终止日"的票补完最后那几天。
///
/// ════ 补的是一个真实存在的数据黑洞 ════
/// 一只股票退市后，日常清单里它还在、数据源却已经不再给新数据；而它**最后几个交易日**
/// （退市整理期，往往正是跌得最惨、对回测最关键那段）当时没抓到，之后就永久缺失了。
/// 筛选判据在 <see cref="DelistedTailPlanner"/>，正常情况下结果是 0 只。
///
/// ════ 顺序是刻意的 ════
/// 这一项**排在日更的各项之后**：它会把这些票标成 <c>type='delisted'</c>，
/// 于是下一轮起它们不再被日常轮询（省掉几百个必然落空的请求），而本轮已经取好的抓取清单不受影响。
///
/// ════ 三个口径都要补 ════
/// 前复权是界面看的，后复权/不复权是回测吃的——缺了这几天，这只退市股的回测序列就断在这儿。
/// 三条线的水位线各自独立，窗口分开算（见 <see cref="DelistedTailPlanner.WindowFor"/>）。
///
/// ⚠ **"已尝试过"的标记只给没失败的打**：停牌后才退市的股票（K线止于停牌日、永远早于终止日）
/// 没有这个标记就会每天徒劳重抓；而失败的要是也打了标记，那几天就永久补不回来了。
/// </summary>
public sealed class DelistedTailTask(
    FetchPaths paths,
    BarSourceHolder sourceHolder,
    IManifestStore manifestStore,
    IDelistedListProvider delistedListProvider,
    int batchSize = BarFetchTaskBase.DefaultBatchSize) : BarFetchTaskBase(paths, sourceHolder)
{
    public override FetchActionId Id => FetchActionId.StepDelistedTails;

    protected override string TaskId => RetryTaskIds.DelistedTails;

    /// <summary>待办由本任务自己补（2026-09-21）。它只会有失败名单那一类——
    /// 空洞/值问题是按标的类型和口径归属的，落不到这个 taskId 上。</summary>
    public override bool HandlesBacklog => true;

    /// <summary>它只有前复权一路，日志里按标的类型叫。</summary>
    protected override string BacklogLabel => "退市股";

    /// <summary>完全没有该口径历史时，从终止日往前回看几年。</summary>
    private const int DefaultLookbackYears = 3;

    private readonly int _batchSize = batchSize > 0 ? batchSize : DefaultBatchSize;

    private SqliteDelistedRepository Delisted => _delisted ??= new SqliteDelistedRepository(Paths.CurrentDb);
    private SqliteDelistedRepository? _delisted;

    private string? _nothingToDoReason;
    private List<DelistedTailPlanner.Target> _pending = [];

    protected override async IAsyncEnumerable<IReadOnlyList<CodeBars>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        ResetRun();
        _nothingToDoReason = null;
        _pending = [];
        using var _ = ForwardSourceStatus();

        if (args.Mode == FetchMode.FillBacklog)
        {
            await foreach (var b in FillBacklogAsync(manifestStore, [Granularity.Day], ct)) yield return b;
            yield break;
        }

        Report("刷新退市名单（顺带补新退市股缺失的最后几天K线）...");

        List<DelistedStockRow> all;
        try { all = await delistedListProvider.GetAllAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 名单取不到只记一条错、不影响本轮别的项（老行为：算这一轮跑完了，只是带个错）。
            Errors.Add($"刷新退市名单失败（跳过，不影响本轮K线）：{ex.Message}");
            _nothingToDoReason = "退市名单没取到";
            Report($"⚠ {Errors[^1]}");
            yield break;
        }

        // 名单入库 + 标 type='delisted'（这两步是这一项的另一半产出，哪怕没有尾巴要补也得做）。
        // 同步 IO，推线程池。
        await Task.Run(() =>
        {
            lock (SqliteWriteGate.Local)
            {
                Delisted.Upsert(all);
                SqliteStockMetaUpsert.Upsert(Paths.CurrentDb,
                    all.Select(r => (r.Code, r.Name)), SqliteStockMetaUpsert.TypeDelisted);
            }
        }, ct);

        var (pending, latestHfq, latestRaw) = await Task.Run(() =>
        {
            var tailPending = Delisted.GetTailPendingCodes();
            var latestDay = Bars.GetLatestPeriodStartByCode(Granularity.Day);
            var p = DelistedTailPlanner.SelectPending(all, tailPending, latestDay);
            return (p, Bars.GetLatestPeriodStartByCode(Granularity.DayHfq),
                       Bars.GetLatestPeriodStartByCode(Granularity.DayRaw));
        }, ct);

        _pending = pending;
        if (pending.Count == 0)
        {
            _nothingToDoReason = $"退市名单已刷新（{all.Count} 只），没有需要补最后几天的退市股";
            Report($"{_nothingToDoReason}。");
            yield break;
        }

        Report($"退市名单已刷新（{all.Count} 只），其中 {pending.Count} 只本地缺最后几天，正在补...");

        // ── ① 前复权 ──
        await foreach (var batch in RunLegAsync(Granularity.Day, "前复权",
                           pending.Select(t => (t.Code, t.DayStart, t.DelistDate)).ToList(), probe: false, ct))
            yield return batch;

        // ── ②③ 后复权 / 不复权 ──
        // 这两条线**保留"起飞前探一只"那道闸**（2026-09-21 用户定：等价优先）。
        // ⚠ 探的是最近一年——退市久了的票那一年本来就没有数据，探测会判失败、整条腿跳过。
        //   实际影响很小：这里的 pending 基本都是刚退市的（最后几天就在最近一年内），
        //   但这是个已知的脆弱点，记在这儿。
        foreach (var (gran, kind, latest) in new[]
                 {
                     (Granularity.DayHfq, "后复权", latestHfq),
                     (Granularity.DayRaw, "不复权", latestRaw),
                 })
        {
            var windows = pending.Select(t =>
            {
                var (s, e) = DelistedTailPlanner.WindowFor(
                    t.DelistDate, latest.TryGetValue(t.Code, out var l) ? l : null, DefaultLookbackYears);
                return (t.Code, s, e);
            }).ToList();

            await foreach (var batch in RunLegAsync(gran, kind, windows, probe: true, ct))
                yield return batch;
        }
    }

    /// <summary>跑一个口径：（可选）先探一只，再按批抓。</summary>
    private async IAsyncEnumerable<IReadOnlyList<CodeBars>> RunLegAsync(
        string granularity, string kind,
        IReadOnlyList<(string Code, DateTime Start, DateTime End)> windows, bool probe,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var todo = windows.Where(w => w.Start.Date <= w.End.Date).ToList();
        if (todo.Count == 0)
        {
            Report($"　退市股{kind}：本地都已补到终止日，不发请求。");
            yield break;
        }

        if (probe && !Source.Fetcher.SupportsHfq)
        {
            Report($"　（数据源 {Source.Name} 不提供{kind}，跳过这一条线——要补请把数据源切到 Tencent）");
            yield break;
        }

        if (probe && !await ProbeAsync(todo[0].Code, granularity, kind, ct)) yield break;

        int batchIndex = 0, done = 0;
        foreach (var batch in todo.Chunk(_batchSize))
        {
            ct.ThrowIfCancellationRequested();
            var got = await FetchBatchAsync(
                batch.Select(b => (b.Code, granularity, b.Start, b.End, false)), ct);
            batchIndex++;
            done += batch.Length;
            ReportBatch(batchIndex, $"退市股{kind}：抓取中", done, todo.Count);
            yield return got;
        }
    }

    /// <summary>探一只：抓不到就整条线跳过，不做无谓的空跑。</summary>
    private async Task<bool> ProbeAsync(string probeCode, string granularity, string kind, CancellationToken ct)
    {
        var today = DateTime.Today;
        try
        {
            var (_, bars) = await Source.Fetcher.FetchAsync(probeCode, granularity, today.AddYears(-1), today, ct);
            if (bars.Count == 0)
                throw new InvalidOperationException("接口返回空数据（可能是返回格式变了，或该代码已无数据）");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Errors.Add($"退市股{kind}探测失败（用 {probeCode} 试抓最近一年）：{ex.Message}。"
                     + "本轮跳过这一条线——前复权不受影响。");
            Report("⚠ " + Errors[^1]);
            return false;
        }
    }

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        SaveFailedTodo(manifestStore);
        // ⚠ 中断时**一个标记都不打**：三条线只跑了一部分，打了标记那几只就再也不会被补了。
        Report($"退市股收尾中断。已落库的 {RowsWritten} 行有效；没补完的下轮还会被挑出来。");
        return Task.CompletedTask;
    }

    protected override async Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (BacklogMode) return FinishBacklog("退市股收尾", manifestStore);

        SaveFailedTodo(manifestStore);

        if (_nothingToDoReason is { } idle)
            return new TaskRunResult(TaskState.Completed, Errors, NothingToDo: true, idle);

        // "已尝试过"的标记：成功的、以及"确实没有更多数据"的都算（停牌到退市的票本来就不会再有K线）；
        // **失败的不打**，留到下次重试——三条线任一失败就算失败。
        var failed = FailedCodes;
        var marked = _pending.Select(t => t.Code).Where(c => !failed.Contains(c)).ToList();
        if (marked.Count > 0)
            await Task.Run(() => { lock (SqliteWriteGate.Local) Delisted.MarkTailFetched(marked); }, ct);

        Report($"本项汇总：{Summarize()}");
        var head = string.Join("、", _pending.Take(5).Select(t => $"{t.Code} {t.Name}"))
                 + (_pending.Count > 5 ? " 等" : "");
        var summary = $"退市股收尾完成（{_pending.Count} 只：{head}）：写入 {RowsWritten} 行"
                    + (failed.Count > 0 ? $"，{failed.Count} 只失败（没打已尝试标记，下轮重试）" : "")
                    + $"，用时 {ElapsedText.Format(Sw.Elapsed)}。";
        Report(summary);

        if (AllAttemptedFailed)
            return new TaskRunResult(TaskState.Failed, Errors, NothingToDo: false, summary);

        return new TaskRunResult(TaskState.Completed, Errors, NothingToDo: RowsWritten == 0, summary);
    }
}
