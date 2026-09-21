using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【个股日K·前复权】（2026-09-21 从编排器迁到新框架，见 doc/bar-tasks-migration-design.md）——
/// 日常选股看盘用的主力价格序列，每只从自己上次抓到那天续抓（所以能自动补断档）。
///
/// ════ 只有它开漂移检测 ════
/// 前复权的基准是"最新价"，那只票一除权，**整条历史都会变**。所以抓的时候把窗口向前放宽
/// <see cref="BarWritePlanner.DriftCheckLookbackDays"/> 天（不多花请求，数据源一页固定 640 根），
/// 拿回来的跟库里逐根比：对不上就按新基准覆盖这一页，并把这只票记进【重取前复权】的名单，
/// 更早的历史交给那一项在空闲时慢慢补。后复权/不复权不需要——它们的基准不随分红变。
///
/// ════ 两个模式 ════
/// · <b>增量</b>：每只按自己的水位线续到今天。日常用它，**只有它会自动补断档**。
/// · <b>只抓某一天</b>（原【补指定历史日】）：不看水位线、只把那一天补上。
///   ⚠ 但"本地一根都没有"的票是例外：那多半是刚上市被名册顺带发现的新股，
///   只抓一天会让它孤零零挂着一根，所以给它一个回看窗口一次抓齐
///   （数据源本来也只会返回上市日之后的数据，不会多给）。
/// </summary>
public sealed class StockDayBarTask(
    FetchPaths paths,
    BarSourceHolder sourceHolder,
    IManifestStore manifestStore,
    int batchSize = BarFetchTaskBase.DefaultBatchSize) : BarFetchTaskBase(paths, sourceHolder)
{
    public override FetchActionId Id => FetchActionId.StepStockDayBars;

    protected override string TaskId => RetryTaskIds.StockDayBars;

    /// <summary>四类待办都由本任务自己补（2026-09-21，见 doc/bar-tasks-migration-design.md §10）。</summary>
    public override bool HandlesBacklog => true;

    /// <summary>行里没填「新标的补 N 年」时用的年数。</summary>
    private const int DefaultLookbackYears = 3;

    private readonly int _batchSize = batchSize > 0 ? batchSize : DefaultBatchSize;

    private string? _nothingToDoReason;
    private int _planned;

    protected override async IAsyncEnumerable<IReadOnlyList<CodeBars>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        ResetRun();
        _nothingToDoReason = null;
        _planned = 0;
        using var _ = ForwardSourceStatus();

        // 【只补待办】：四类待办都归它自己补。⚠ missing_day 那份名单是**三个口径并成一份**
        // 记在它名下的（见 SqliteDayCompletenessAuditor.CheckBars），所以要传三个口径——
        // 已经齐了的那条线会在水位线判定里跳过、不发请求。
        if (args.Mode == FetchMode.FillBacklog)
        {
            await foreach (var b in FillBacklogAsync(
                manifestStore, [Granularity.Day, Granularity.DayHfq, Granularity.DayRaw], ct))
                yield return b;
            yield break;
        }

        bool exactDay = args.Mode == FetchMode.SpecificDay;
        var day = args.Day?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;
        int lookbackYears = args.LookbackYears is > 0 ? args.LookbackYears.Value : DefaultLookbackYears;

        // 名册和逐只查水位线都是同步 IO，推线程池——骨架不替子类推，首个 await 之前干这些会冻住界面。
        var codes = await Task.Run(LocalStockCodes, ct);
        var plan = await Task.Run(
            () => exactDay ? PlanForDay(codes, day) : PlanIncremental(codes, DateTime.Today, lookbackYears), ct);

        _planned = plan.Count;
        if (plan.Count == 0)
        {
            _nothingToDoReason = $"个股日K·前复权：{codes.Count} 只本地都已是最新";
            Report($"{_nothingToDoReason}，这一轮无需抓取（一个请求都没发）。");
            yield break;
        }

        Report(exactDay
            ? $"按天抓取个股前复权日K：{day:yyyy-MM-dd}，共 {codes.Count} 只里有 {plan.Count} 只要抓"
              + "（用本地名册，不重新扫全市场）"
            : $"个股日K·前复权：{codes.Count} 只里有 {plan.Count} 只要抓、{codes.Count - plan.Count} 只本地已是最新"
              + $"（按各自水位线跳过，不发请求），每批 {_batchSize} 只...");

        int batchIndex = 0, done = 0;
        foreach (var batch in plan.Chunk(_batchSize))
        {
            ct.ThrowIfCancellationRequested();
            var got = await FetchBatchAsync(
                batch.Select(b => (b.Code, Granularity.Day, b.Start, b.End, true)), ct);
            batchIndex++;
            done += batch.Length;
            ReportBatch(batchIndex, "个股日K·前复权：抓取中", done, plan.Count);
            yield return got;
        }
    }

    /// <summary>增量：每只按自己的水位线续到今天，已经追上的直接跳过、不发请求。</summary>
    private List<(string Code, DateTime Start, DateTime End)> PlanIncremental(
        IReadOnlyList<string> codes, DateTime end, int lookbackYears)
    {
        var list = new List<(string, DateTime, DateTime)>(codes.Count);
        foreach (var code in codes)
        {
            var start = IncrementalStart(code, Granularity.Day, end, lookbackYears);
            if (start.Date <= end.Date) list.Add((code, start, end));
            else CountSkipped();
        }
        return list;
    }

    /// <summary>
    /// 只抓某一天：不看水位线，直接查那一天本地有没有、是不是收盘后确认的。
    /// 早于今天的日期一旦有记录就必然是最终值（过去的交易日不会再变），
    /// <see cref="IncrementalWindowCalculator.IsConfirmedFinal"/> 对它们天然成立。
    /// </summary>
    private List<(string Code, DateTime Start, DateTime End)> PlanForDay(
        IReadOnlyList<string> codes, DateTime day)
    {
        var list = new List<(string, DateTime, DateTime)>(codes.Count);
        foreach (var code in codes)
        {
            var latest = Bars.GetLatestBarInfo(code, Granularity.Day);
            DateTime start;
            if (latest == null)
            {
                // 本地完全没有这只票的K线（多半是刚才扫名册顺带发现的新股）——不只抓这一天，
                // 而是给一个回看窗口一次抓齐它到目前为止的全部历史（见类注释 ⚠）。
                start = day.AddYears(-DefaultLookbackYears);
            }
            else
            {
                var existing = Bars.Query(code, Granularity.Day, day, day).FirstOrDefault();
                start = existing != null && IncrementalWindowCalculator.IsConfirmedFinal(existing.FetchedAt, day)
                    ? day.AddDays(1) : day;
            }
            if (start.Date <= day.Date) list.Add((code, start, day));
            else CountSkipped();
        }
        return list;
    }

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        SaveFailedTodo(manifestStore);
        RecordDriftedForRepair(manifestStore);
        Report($"个股日K·前复权中断。已落库的 {RowsWritten} 行有效，"
             + "下轮按各自的水位线接着走（已抓到的票会被跳过）。");
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (BacklogMode) return Task.FromResult<TaskRunResult?>(
            FinishBacklog("个股日K·前复权", manifestStore));

        SaveFailedTodo(manifestStore);
        RecordDriftedForRepair(manifestStore);

        if (_nothingToDoReason is { } idle)
            return Task.FromResult<TaskRunResult?>(
                new TaskRunResult(TaskState.Completed, Errors, NothingToDo: true, idle));

        Report($"本项汇总：{Summarize()}");
        var summary = $"个股日K·前复权：{_planned} 只写入 {RowsWritten} 行"
                    + (FailedCount > 0 ? $"，{FailedCount} 只失败（已记进待办）" : "")
                    + $"，用时 {ElapsedText.Format(Sw.Elapsed)}。";
        Report(summary);

        if (AllAttemptedFailed)
            return Task.FromResult<TaskRunResult?>(
                new TaskRunResult(TaskState.Failed, Errors, NothingToDo: false, summary));

        return Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, Errors, NothingToDo: RowsWritten == 0, summary));
    }
}
