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
        bool fullBackfill = args.Mode.HasFlag(FetchMode.FirstBackfill);
        var day = args.Day?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;
        int lookbackYears = args.LookbackYears is > 0 ? args.LookbackYears.Value : DefaultLookbackYears;

        // 名册和逐只查水位线都是同步 IO，推线程池——骨架不替子类推，首个 await 之前干这些会冻住界面。
        var codes = await Task.Run(LocalStockCodes, ct);
        var plan = await Task.Run(() =>
            exactDay ? PlanForDay(codes, day)
            // ⚠ 整段回补用的是**另一份名册**（含退市股），见 BackfillCodes
            : fullBackfill ? PlanFullBackfill(BackfillCodes(), args)
            : PlanIncremental(codes, DateTime.Today, lookbackYears), ct);

        _planned = plan.Count;
        if (plan.Count == 0)
        {
            _nothingToDoReason = fullBackfill
                ? "个股日K·前复权：指定的年份区间内没有可补的"
                : $"个股日K·前复权：{codes.Count} 只本地都已是最新";
            Report($"{_nothingToDoReason}，这一轮无需抓取（一个请求都没发）。");
            yield break;
        }

        Report(exactDay
            ? $"按天抓取个股前复权日K：{day:yyyy-MM-dd}，共 {codes.Count} 只里有 {plan.Count} 只要抓"
              + "（用本地名册，不重新扫全市场）"
            : fullBackfill
            ? $"个股日K·前复权**整段回补**：{plan.Count} 只要抓（每只只补它自己缺的那一段）"
              + (args.OverwriteQfq
                  ? "，**覆盖重抓**（不看本地已有什么，整段按数据源当前基准重写，用来抹平复权基准接缝）"
                  : "（不看水位线，库里已有的行在写入判据里原样跳过）")
              + $"，每批 {_batchSize} 只..."
            : $"个股日K·前复权：{codes.Count} 只里有 {plan.Count} 只要抓、{codes.Count - plan.Count} 只本地已是最新"
              + $"（按各自水位线跳过，不发请求），每批 {_batchSize} 只...");

        int batchIndex = 0, done = 0;
        foreach (var batch in plan.Chunk(_batchSize))
        {
            ct.ThrowIfCancellationRequested();
            var got = await FetchBatchAsync(
                batch.Select(b => (b.Code, Granularity.Day, b.Start, b.End, true)), ct,
                // 「覆盖重抓」只在整段回补那一路有意义（见 TaskRunArgs.OverwriteQfq）
                overwrite: fullBackfill && args.OverwriteQfq);
            batchIndex++;
            done += batch.Length;
            ReportBatch(batchIndex, "个股日K·前复权：抓取中", done, plan.Count);
            yield return got;
        }
    }

    /// <summary>
    /// 整段回补的标的全集：个股 **加上退市股**。
    ///
    /// ⚠ 不能用 <c>LocalStockCodes()</c>：它走 <c>SqliteStockMetaUpsert.GetAll</c>，
    /// 而那个方法**只返回 type='stock'**（它被 20 多处共用，混进别的类型会污染选股候选池，
    /// 所以不能动它）。日更不轮询退市股是对的——数据源早就不给新数据了，那几百个请求必然落空；
    /// 但**往回补历史时必须带上它们**，否则回测只剩活下来的那些票，就是幸存者偏差。
    /// 老【拉取区间数据】把这件事单列成一段（<c>FetchDelistedForRangeAsync</c>），
    /// 不复权那一路也早就是这么取的（<see cref="StockAdjustedBarTask"/> 的 PendingBackfill）。
    /// </summary>
    private List<string> BackfillCodes()
        => SqliteStockMetaUpsert
            .GetByTypes(Paths.CurrentDb, SqliteStockMetaUpsert.TypeStock, SqliteStockMetaUpsert.TypeDelisted)
            .Select(x => x.Code).ToList();

    /// <summary>
    /// 首次整段回补：**不看水位线**，所有票都按同一个窗口抓。
    ///
    /// 「整段」＝ A股开市首日到今天，再按调用方给的年份区间收窄（<see cref="NarrowToYears"/>）——
    /// 【拉取区间数据】分派过来时就带着年份，单独跑这一项不带年份就是补到底。
    /// 数据源只会返回该票实际存在的日期，上市前那段自然是空。
    ///
    /// ⚠ 这里**不按只跳过**：缺的往往是**开头**而不是尾巴（日更那根按回看年数只填了最近 3 年），
    /// 按水位线跳过的话前面那几年永远补不到。重复抓不会重复写——库里已有的行在
    /// <see cref="BarWritePlanner"/> 里原样跳过（<see cref="TaskRunArgs.OverwriteQfq"/> 那条路除外，
    /// 它要的就是整段重写）。
    /// </summary>
    private List<(string Code, DateTime Start, DateTime End)> PlanFullBackfill(
        IReadOnlyList<string> codes, TaskRunArgs args)
    {
        var (start, end) = NarrowToYears(IncrementalWindowCalculator.AShareMarketOpen, DateTime.Today, args);
        if (start.Date > end.Date) return [];      // 年份区间落在窗口外，正常，不是错

        // 逐只算缺口：水位表 + 本地已覆盖 + 交易日历，见 PlanGaps。
        // 「覆盖重抓」那一路要整段重写，所以连水位也不看。
        var plan = PlanGaps(codes, Granularity.Day, start, end, ignoreFloor: args.OverwriteQfq, out int skipped);
        for (int i = 0; i < skipped; i++) CountSkipped();
        if (skipped > 0)
            Report($"其中 {skipped} 只这一段本地已经齐了、或数据源已探明没有更早数据，整只跳过、不发请求。");
        return plan;
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
