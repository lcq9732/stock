using System.Runtime.CompilerServices;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// K线任务的**待办那一半**（2026-09-21，见 doc/bar-tasks-migration-design.md §10）——
/// 2026-09-13 二期起待办按 (taskId, kind) 分域记，这里就是"谁欠的谁来补"落到K线上的实现。
///
/// ════ 四类待办，四种补法 ════
/// | 类 | 谁产生 | 怎么补 | 怎么复查 |
/// |---|---|---|---|
/// | <c>failed</c> | 任务自己抓失败 | 跟增量同一个动作，目标从名单来 | 这轮没失败就移出（<see cref="FailedTodoRule"/>） |
/// | <c>missing_day</c> | 【当日完整性体检】 | 窗口＝缺的那天→今天 | 下一次体检重建名单 |
/// | <c>gap</c> | 【全库数据体检】 | 按**区间**抓一次 | <c>FindGaps</c>，补满两轮进「确认没有」白名单 |
/// | <c>value</c> | 【全库数据体检】 | 按 Reason 分抓法 | 对应判据，**不进**白名单 |
///
/// ════ 三条不能丢的规矩 ════
/// ① **每批落账**（2026-09-07 用户拍的）：原来全部跑完才写一次，前复权那 2852 段跑了
///    1 小时 52 分、Tries 从 0 加到 1，可记账只在内存里——中途一停全白费，下轮又从 Tries=0
///    开始，那 8103 段永远收敛不进白名单。现在每批（500 段）跑完立刻落账。
/// ② **数据源不支持该口径时 Tries 一动不动**：后复权/不复权只有腾讯给，切到新浪时让它们空跑
///    两轮的后果是几千只票被永久打进「数据源确实没有」白名单。
/// ③ **口径按每一段自己的 <c>Gran</c> 走，不按任务猜**：ETF 的空洞不分口径全记在
///    <c>StepEtfBars</c> 名下（<c>FullAuditTask.TaskIdOfScope</c>），所以同一份名单里可能
///    同时有 <c>day</c> 和 <c>day_raw</c>。老代码拿 <c>pending[0].Gran</c> 当整份的口径，
///    混着的时候后一半会用错口径去抓、补完复查还是缺、两轮后被错判成"数据源确实没有"。
/// </summary>
public abstract partial class BarFetchTaskBase
{
    /// <summary>一次补多少段。太大一次 join 上千万行、内存和时间都难看；太小则来回开连接。</summary>
    private const int AuditBatchSize = 500;

    /// <summary>补两轮还拿不到，就判定"数据源确实没有"（多半是停牌），写进白名单、以后体检跳过。</summary>
    private const int AuditMaxTries = 2;

    /// <summary>值问题复查的截止线——跟体检那边的 <c>FullAuditTask.SettleDays</c> 是同一个 2 天。</summary>
    private const int ValueRecheckSettleDays = 2;

    /// <summary>本轮补待办都补了些什么（收尾那句汇总用）。</summary>
    protected readonly List<string> BacklogParts = [];

    /// <summary>这一轮跑的是【只补待办】。收尾那套要分开写——补待办的结局是"补了哪几类"，
    /// 不是"抓了几只写了几行"。</summary>
    protected bool BacklogMode { get; private set; }

    /// <summary>
    /// 补这个任务欠着的全部待办。
    /// </summary>
    /// <param name="store">待办记在 manifest 里。</param>
    /// <param name="missingDayGrans">
    /// <c>missing_day</c> 那份名单要补哪几个口径。
    /// ⚠ 个股那份是**三个口径并成一份**记在 <c>StepStockDayBars</c> 名下的
    /// （见 <c>SqliteDayCompletenessAuditor.CheckBars</c>：重补时已经齐了的那条线会在水位线
    /// 判定里直接跳过、不发请求），所以前复权任务要传三个；其余每项传自己那一个。
    /// </param>
    protected async IAsyncEnumerable<IReadOnlyList<CodeBars>> FillBacklogAsync(
        IManifestStore store, IReadOnlyList<string> missingDayGrans,
        [EnumeratorCancellation] CancellationToken ct)
    {
        BacklogParts.Clear();
        BacklogMode = true;
        var manifest = await Task.Run(store.Load, ct);

        var failed = (manifest.Todo(TaskId, RetryTodoKind.Failed)?.Targets ?? [])
            .Select(t => t.Code).Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var missingDay = manifest.Todo(TaskId, RetryTodoKind.MissingDay);
        var gap = (manifest.Todo(TaskId, RetryTodoKind.Gap)?.Targets ?? []).ToList();
        var value = (manifest.Todo(TaskId, RetryTodoKind.ValueIssue)?.Targets ?? []).ToList();

        if (failed.Count == 0 && missingDay is not { Targets.Count: > 0 }
            && gap.Count == 0 && value.Count == 0)
            yield break;   // 调用方据此报"没有欠着的待办"

        var today = DateTime.Today;

        // ── ① 失败名单：跟增量同一个动作，只是目标从名单来 ──
        if (failed.Count > 0)
        {
            if (!CanFetch(OwnGranularity, $"失败名单 {failed.Count} 只"))
            {
                BacklogParts.Add($"失败名单 {failed.Count} 只跳过（数据源不支持该口径）");
            }
            else
            {
                Report($"重新拉取上次失败的{BacklogLabel}K线，共 {failed.Count} 只，数据源：{Source.Name}");
                var plan = await Task.Run(() =>
                {
                    var list = new List<(string, string, DateTime, DateTime, bool)>();
                    foreach (var c in failed)
                    {
                        // 失败的票水位线可能很旧（一直失败），也可能压根没有（第一次就失败）——
                        // 后一种用默认回看年数兜底，这条路没有输入框。
                        var start = IncrementalStart(c, OwnGranularity, today, 3);
                        if (start.Date <= today.Date) list.Add((c, OwnGranularity, start, today, false));
                        else
                        {
                            // 已经被别的路补到最新了：不发请求，但**要记一笔"碰过"**，
                            // 否则它永远移不出失败名单（见 MarkAttempted）。
                            MarkAttempted(c);
                            CountSkipped();
                        }
                    }
                    return list;
                }, ct);
                await foreach (var b in FetchWindowsAsync(plan, "补失败名单", ct)) yield return b;
                BacklogParts.Add($"{BacklogLabel}K线 {failed.Count} 只");
            }
        }

        // ── ② 当天还缺着（不是失败，是数据源当时还没出这些标的的当天数据）──
        if (missingDay is { Targets.Count: > 0 } && missingDay.Day is { } missDate)
        {
            var codes = missingDay.Targets.Select(t => t.Code).ToList();
            Report($"补 {missDate:yyyy-MM-dd} 还缺的K线，共 {codes.Count} 只"
                 + $"（上一轮不是失败，是数据源当时还没出这些标的的当天数据），数据源：{Source.Name}");

            foreach (var gran in missingDayGrans)
            {
                if (!CanFetch(gran, $"{missDate:MM-dd} 那天的 {codes.Count} 只")) continue;

                // 前复权：窗口取"缺的那天 → 今天"。
                // 后复权/不复权：按**它们各自的水位线**算——已经补齐的那条线会在判定里跳过、
                // 不发请求（老编排层走 HfqWatermarkWindow 就是这个意思）。
                var windows = await Task.Run(() => codes
                    .Select(c => (c, gran,
                                  gran == Granularity.Day ? missDate : IncrementalStart(c, gran, today, 3),
                                  today, false))
                    .Where(w => w.Item3.Date <= today.Date).ToList(), ct);
                await foreach (var b in FetchWindowsAsync(windows, $"补当天·{GranLabel(gran)}", ct))
                    yield return b;
            }
            BacklogParts.Add($"{missDate:MM-dd} 当天 {codes.Count} 只");
        }

        // ── ③④ 历史空洞 / 值问题 ──
        // 这两类**自己抓自己存**（不走骨架的流式落库）：它们必须"抓完这一批立刻复查"，
        // 而骨架是先 yield 再存，复查会跑在存之前，一律判成"还缺"。
        if (gap.Count > 0) await FillGapAsync(store, gap, ct);
        if (value.Count > 0) await FillValueAsync(store, value, ct);
    }

    /// <summary>
    /// 【只补待办】那一轮的结局。
    ///
    /// 一类都没补 → <c>NothingToDo</c>，**不是 <c>Skipped</c>**：待办是空的这件事今天再来也一样，
    /// 写成 Skipped 会让计划引擎立刻再排一次、空转（见 <see cref="TaskRunResult.Skipped"/>）。
    /// </summary>
    protected TaskRunResult FinishBacklog(string label, IManifestStore store)
    {
        SaveFailedTodo(store);

        if (BacklogParts.Count == 0)
        {
            var idle = $"{label}没有欠着的待办";
            Report($"{idle}。");
            return new TaskRunResult(TaskState.Completed, Errors, NothingToDo: true, idle);
        }

        var line = $"{label}·补待办：{string.Join("、", BacklogParts)}，写入 {RowsWritten} 行"
                 + (FailedCount > 0 ? $"，{FailedCount} 只仍失败（留在待办里）" : "")
                 + $"，用时 {ElapsedText.Format(Sw.Elapsed)}。";
        Report(line);
        return new TaskRunResult(TaskState.Completed, Errors, NothingToDo: false, line);
    }

    /// <summary>这一批窗口按批抓、按批 yield（走骨架的流式落库）。</summary>
    private async IAsyncEnumerable<IReadOnlyList<CodeBars>> FetchWindowsAsync(
        IReadOnlyList<(string Code, string Gran, DateTime Start, DateTime End, bool Drift)> windows,
        string phase, [EnumeratorCancellation] CancellationToken ct)
    {
        if (windows.Count == 0) yield break;
        int batchIndex = 0, done = 0;
        foreach (var batch in windows.Chunk(DefaultBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var got = await FetchBatchAsync(batch.Select(b => (b.Code, b.Gran, b.Start, b.End, b.Drift)), ct);
            batchIndex++;
            done += batch.Length;
            ReportBatch(batchIndex, $"{phase}：抓取中", done, windows.Count);
            yield return got;
        }
    }

    /// <summary>
    /// 后复权/不复权只有腾讯给。数据源不支持时整条原样留着、**Tries 一动不动**——
    /// 空跑两轮的后果是几千只票被永久打进「数据源确实没有」白名单。
    /// </summary>
    private bool CanFetch(string gran, string what)
    {
        if (gran == Granularity.Day || Source.Fetcher.SupportsHfq) return true;
        Report($"（{what} 先留着：数据源 {Source.Name} 不提供{GranLabel(gran)}，"
             + "要补请把数据源切到 Tencent 再跑一次）");
        return false;
    }

    /// <summary>这个任务自己的口径。<c>failed</c> 名单没记口径，用它。</summary>
    protected virtual string OwnGranularity => Granularity.Day;

    /// <summary>补待办的日志里怎么称呼这一项。默认按口径叫（个股三口径各叫各的），
    /// ETF/指数/退市股按标的类型叫——它们只有前复权一路，说"前复权K线"读着别扭。</summary>
    protected virtual string BacklogLabel => GranLabel(OwnGranularity);

    private static string GranLabel(string gran) => gran switch
    {
        Granularity.DayHfq => "后复权",
        Granularity.DayRaw => "不复权",
        Granularity.DayAdj => "回测序列",
        _ => "前复权",
    };
}
