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
/// 【个股日K·后复权】和【个股日K·不复权】（2026-09-21 从编排器迁到新框架）——
/// 同一段逻辑、口径参数化，所以是一个类注册两次。
///
/// ════ 这两路跟前复权有三处不同 ════
/// ① **不做漂移检测**：它们的基准不随分红变，比对纯属浪费请求和 CPU。
/// ② **两道防空跑的闸**（用户 2026-07-30 反馈"取不到数据就该直接停"）：
///    起飞前先探一只；跑起来之后失败率过高就中止本轮（判据在 <see cref="HfqProbeGate"/>）。
///    没有这两道，接口一挂就是对着几千只票空跑几小时、一行数据都拿不到。
/// ③ **数据源不支持就整项跳过**：新浪只有前复权一种口径。
///
/// ════ 还有一个模式：首次整段回补 ════
/// 把每只补到跟前复权一样长（不复权实测约 24700 个请求、2 小时出头，跑完一次基本不用再管）。
/// 判据是 <see cref="RawBarCompletenessRule"/> 的**两头都要比**——只比尾巴的话，
/// 日更那根会先把最近 3 年填上、判据归零显示「已补齐」，前面 7 年再也没人补
/// （2026-09-01 实测 5781 只里有 5232 只卡在 3 年）。
///
/// ⚠ 2026-09-22 起**后复权也走这条路**（原来限死只有不复权能用）：判据比的是"跟前复权一样长"，
/// 跟口径无关，后复权一样会因为日更只填最近 3 年而卡在前面那几年。
/// 顺带支持年份区间（<see cref="TaskRunArgs.YearStart"/>），【拉取区间数据】分派过来时带着。
///
/// ════ 日志密度 ════
/// 迁移前不复权那一路**逐只**打"正在抓取 xxx"（后复权不打），全市场一轮就是几千行，
/// 而日志走 UI 线程。现在两路统一按批打（每 10 批一行），心跳每批一次——
/// 这是新框架的统一规矩，见 <c>BarFetchTaskBase.ReportBatch</c>。
/// </summary>
public sealed class StockAdjustedBarTask(
    FetchPaths paths,
    BarSourceHolder sourceHolder,
    IManifestStore manifestStore,
    FetchActionId id,
    string granularity,
    int batchSize = BarFetchTaskBase.DefaultBatchSize) : BarFetchTaskBase(paths, sourceHolder)
{
    public override FetchActionId Id => id;

    protected override string TaskId => RetryTaskIds.ForGranularity(granularity);

    /// <summary>四类待办都由本任务自己补（2026-09-21）。</summary>
    public override bool HandlesBacklog => true;

    /// <summary>失败名单没记口径，用这个（见 <c>BarFetchTaskBase.OwnGranularity</c>）。</summary>
    protected override string OwnGranularity => granularity;

    /// <summary>行里没填「新标的补 N 年」时用的年数。</summary>
    private const int DefaultLookbackYears = 3;

    private readonly int _batchSize = batchSize > 0 ? batchSize : DefaultBatchSize;

    /// <summary>口径的中文名，日志里用。</summary>
    private string Kind => granularity == Granularity.DayRaw ? "不复权" : "后复权";

    private string? _skippedReason;
    private string? _nothingToDoReason;
    private bool _aborted;
    private int _planned, _leftAfterRun;
    private bool _fullBackfill;

    protected override async IAsyncEnumerable<IReadOnlyList<CodeBars>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        ResetRun();
        _skippedReason = _nothingToDoReason = null;
        _aborted = false;
        _planned = _leftAfterRun = 0;
        _fullBackfill = args.Mode.HasFlag(FetchMode.FirstBackfill);
        using var _ = ForwardSourceStatus();

        if (args.Mode == FetchMode.FillBacklog)
        {
            // ⚠ 这里**不能**先判 SupportsHfq 就整项跳过：补待办那一路要按类逐条判——
            //   数据源不支持时名单和 Tries 必须原样留着（见 BarFetchTaskBase.CanFetch）。
            await foreach (var b in FillBacklogAsync(manifestStore, [granularity], ct)) yield return b;
            yield break;
        }

        if (!Source.Fetcher.SupportsHfq)
        {
            _nothingToDoReason = $"数据源 {Source.Name} 不提供{Kind}";
            Report($"（{_nothingToDoReason}，跳过回测用的{Kind}日线——要补请把数据源切到 Tencent）");
            yield break;
        }

        int lookbackYears = args.LookbackYears is > 0 ? args.LookbackYears.Value : DefaultLookbackYears;
        var today = DateTime.Today;

        List<(string Code, DateTime Start, DateTime End)> plan;
        if (_fullBackfill)
        {
            plan = await Task.Run(() => PlanFullBackfill(today, args), ct);
        }
        else
        {
            var codes = await Task.Run(LocalStockCodes, ct);
            plan = await Task.Run(() => PlanIncremental(codes, today, lookbackYears), ct);
            _planned = plan.Count;
            if (plan.Count == 0)
            {
                _nothingToDoReason = $"{Kind}日K：{codes.Count} 只本地都已是最新";
                Report($"{_nothingToDoReason}，这一轮无需抓取（一个请求都没发）。");
                yield break;
            }
            Report($"开始抓{Kind}日K：{codes.Count} 只里有 {plan.Count} 只要抓、"
                 + $"{codes.Count - plan.Count} 只本地已是最新（按各自水位线跳过，不发请求）。"
                 + $"回测专用；前复权已有的不受影响，每批 {_batchSize} 只...");
        }

        _planned = plan.Count;
        if (plan.Count == 0)
        {
            _nothingToDoReason = $"{Kind}日线已经跟前复权一样齐了";
            Report($"{_nothingToDoReason}，这一轮没什么可做。");
            yield break;
        }

        // ── 起飞前探一只 ──
        // 接口挂了/被限流/换了返回格式时立刻停这一轮并说清楚原因，而不是对着几千只票空跑几小时。
        if (!await ProbeAsync(plan[0].Code, today, ct)) yield break;

        int batchIndex = 0, done = 0;
        foreach (var batch in plan.Chunk(_batchSize))
        {
            ct.ThrowIfCancellationRequested();
            var got = await FetchBatchAsync(
                batch.Select(b => (b.Code, granularity, b.Start, b.End, false)), ct);
            batchIndex++;
            done += batch.Length;
            ReportBatch(batchIndex, $"{Kind}日K：抓取中", done, plan.Count);
            yield return got;

            // ── 跑起来之后那道闸 ──
            // 判据在 HfqProbeGate：完成够多了、失败率还这么高，就是接口出事了，别再往下跑。
            if (HfqProbeGate.ShouldAbort(done, FailedCount))
            {
                _aborted = true;
                Report($"⚠ {Kind}连续失败（已完成 {done} 只、失败 {FailedCount} 只），主动中止本轮{Kind}抓取。"
                     + "前复权数据不受影响，排查好数据源后重跑即可。");
                yield break;
            }
        }
    }

    /// <summary>探一只：拿它试抓最近一年，抓不到就整轮不跑。</summary>
    private async Task<bool> ProbeAsync(string probeCode, DateTime today, CancellationToken ct)
    {
        try
        {
            var (_, probeBars) = await Source.Fetcher.FetchAsync(
                probeCode, granularity, today.AddYears(-1), today, ct);
            if (probeBars.Count == 0)
                throw new InvalidOperationException("接口返回空数据（可能是返回格式变了，或该代码已无数据）");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _skippedReason = $"{Kind}探测失败（用 {probeCode} 试抓最近一年）：{ex.Message}";
            Errors.Add(_skippedReason + "。本轮跳过，不做无谓的空跑——前复权数据不受影响，排查好接口后重跑即可。");
            Report("⚠ " + Errors[^1]);
            return false;
        }
    }

    /// <summary>增量：每只按**这个口径自己的**水位线续抓（所以前复权已是最新不影响它）。</summary>
    private List<(string Code, DateTime Start, DateTime End)> PlanIncremental(
        IReadOnlyList<string> codes, DateTime end, int lookbackYears)
    {
        var list = new List<(string, DateTime, DateTime)>(codes.Count);
        foreach (var code in codes)
        {
            var start = IncrementalStart(code, granularity, end, lookbackYears);
            if (start.Date <= end.Date) list.Add((code, start, end));
            else CountSkipped();
        }
        return list;
    }

    /// <summary>
    /// 首次整段回补：把每只补到跟前复权一样长（2026-09-22 起后复权也走这里）。
    ///
    /// ⚠ 窗口从**前复权的最早那天**起，不是"从不复权的水位线次日续"——缺的往往是**开头**
    /// 而不是尾巴（日更那根按回看年数只填了最近 3 年），从水位线往后续永远补不到前面那几年。
    /// 整段重抓不会重复写：库里已有的行在写入判据里原样跳过。
    /// </summary>
    private List<(string Code, DateTime Start, DateTime End)> PlanFullBackfill(DateTime today, TaskRunArgs args)
    {
        // ⚠ 下面四次全表 GROUP BY 加起来要几十秒，**先说一声**——否则点完【执行】日志一直不动，
        //   人以为没点上会反复点（2026-09-01 反馈）。
        Report($"正在统计还差哪些股票的{Kind}日线（要扫一遍全库的日线索引，通常几十秒，请稍等）…");

        var todo = PendingBackfill(today, out var dayEarliest);
        if (todo.Count == 0) return [];

        // 每只的窗口＝"它自己的前复权最早那天 ~ 今天"，按年份区间收窄；
        // **收窄之后**再在这个窗口里判缺口（水位表 + 本地已覆盖 + 交易日历）。
        //
        // ⚠ 顺序不能反（2026-09-22 踩过）：先在大窗口里算缺口、再收窄，算出来的是
        //   "区间之外那部分缺口"，收窄后仍非空——于是每轮都重新计划、抓回来一行都写不进去。
        //   实测填 2024 重跑，5995 只全部重抓、写入 0 行。
        //   判据走 YearGapCalculator，跟前复权那路同一个。
        var de = dayEarliest;
        var plan = PlanGaps(todo, granularity,
                            code => NarrowToYears(de[code], today, args),
                            ignoreFloor: false, out _);

        // ⚠ 报的是**这一轮真要抓的只数**，不是 todo.Count（2026-09-22 修）：
        //   todo 是按"跟前复权一样长"判的、看全历史；而这一轮只补收窄后的那几年。
        //   照 todo 报的话，同一项里会先说「还差 5995 只」、紧接着说「已经齐了，没什么可做」——
        //   数据是对的，但两句话打架，看的人会以为漏抓了。
        bool narrowed = args.YearStart is not null || args.YearEnd is not null;
        Report($"补{Kind}日线：{(narrowed ? $"指定区间内有 {plan.Count} 只要补" : $"还差 {plan.Count} 只")}"
             + (narrowed && todo.Count > plan.Count
                 ? $"（全历史口径还差 {todo.Count} 只，其余那些的缺口在这个区间之外）"
                 : "")
             + $"——{Kind}是原始成交价，抓过就永远有效，不会因为分红而失效。"
             + $"每批 {_batchSize} 只，到点或做满上限就收尾、下轮接着补。");
        if (plan.Count < todo.Count)
            Report($"其中 {todo.Count - plan.Count} 只在指定区间内没有可补的"
                 + "（年份区间之外，或数据源已探明没有更早数据），本轮跳过、不发请求。");
        return plan;
    }

    /// <summary>还差哪些票（判据在 <see cref="RawBarCompletenessRule"/>）。</summary>
    private List<string> PendingBackfill(DateTime today, out Dictionary<string, DateTime> dayEarliest)
    {
        // 只要个股和退市股：指数不除权、ETF 走自己那一项、板块指数是本地合成的。
        // 老库 type=NULL 的行算个股，GetByTypes 已经带上这条口径。
        var targets = SqliteStockMetaUpsert
            .GetByTypes(Paths.CurrentDb, SqliteStockMetaUpsert.TypeStock, SqliteStockMetaUpsert.TypeDelisted)
            .Select(x => x.Code).ToHashSet(StringComparer.Ordinal);

        var dayLatest = Bars.GetLatestPeriodStartByCode(Granularity.Day);
        dayEarliest = Bars.GetEarliestPeriodStartByCode(Granularity.Day);
        var rawLatest = Bars.GetLatestPeriodStartByCode(granularity);
        var rawEarliest = Bars.GetEarliestPeriodStartByCode(granularity);

        var de = dayEarliest;
        return dayLatest
            .Where(kv => targets.Contains(kv.Key)
                      && de.TryGetValue(kv.Key, out var e)
                      && !RawBarCompletenessRule.IsComplete(e, kv.Value,
                              rawEarliest.TryGetValue(kv.Key, out var re) ? re : null,
                              rawLatest.TryGetValue(kv.Key, out var rl) ? rl : null))
            .Select(kv => kv.Key)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
    }

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        SaveFailedTodo(manifestStore);
        Report($"{Kind}日K中断。已落库的 {RowsWritten} 行有效，下轮按各自的水位线接着走。");
        return Task.CompletedTask;
    }

    protected override async Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (BacklogMode) return FinishBacklog($"个股日K·{Kind}", manifestStore);

        SaveFailedTodo(manifestStore);

        // 「没开工」：探测失败——今天恢复了还该再来，所以是 Skipped（计划不会把它记成今天已完成）。
        if (_skippedReason is { } blocked)
            return TaskRunResult.Skipped(blocked, Errors);

        // 「没活可干」：数据源不支持这个口径，或标的都已是最新——今天再来也是同一个结果。
        if (_nothingToDoReason is { } idle)
            return new TaskRunResult(TaskState.Completed, Errors, NothingToDo: true, idle);

        Report($"本项汇总：{Summarize()}");

        string tail = "";
        if (_fullBackfill)
        {
            // 还差多少只——整段回补是分几轮跑完的，人要的就是"还有多远"。
            _leftAfterRun = await Task.Run(() => PendingBackfill(DateTime.Today, out _).Count, ct);
            tail = _leftAfterRun > 0 ? $"，还差 {_leftAfterRun} 只（下一轮继续）" : "，已全部齐了";
        }

        var summary = _aborted
            ? $"{Kind}日K已中止（完成 {_planned} 只里的一部分，失败 {FailedCount} 只）。"
            : $"{Kind}日K：{_planned} 只写入 {RowsWritten} 行"
              + (FailedCount > 0 ? $"，{FailedCount} 只失败（已记进待办）" : "")
              + tail + $"，用时 {ElapsedText.Format(Sw.Elapsed)}。";
        Report(summary);

        // 中止是"这一轮被掐了"，不是失败也不是完成——记成 Skipped，今天排查好了还能再跑。
        if (_aborted) return TaskRunResult.Skipped(summary, Errors);

        if (AllAttemptedFailed)
            return new TaskRunResult(TaskState.Failed, Errors, NothingToDo: false, summary);

        return new TaskRunResult(TaskState.Completed, Errors, NothingToDo: RowsWritten == 0, summary);
    }
}
