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
/// 【指数日K】（2026-09-21 从编排器迁到新框架，见 doc/bar-tasks-migration-design.md）。
///
/// ════ 它不只是"十几条指数" ════
/// 它是全库的**交易日锚**：「最近一个已收盘交易日是哪天」就是看上证指数最新一根日线
/// （快照类数据的归属日、当日覆盖率体检都靠它）。所以这一项不能关。
///
/// ════ 两个模式 ════
/// · <b>增量</b>：每条从自己的水位线续到今天，日常用它。
/// · <b>首次整段回补</b>：不看水位线、也不看回看年数，从开市首日抓起
///   （<see cref="IncrementalWindowCalculator.AShareMarketOpen"/>）。
///   **往清单里加了新指数之后必须跑一次**——水位线只往后走，新指数第一次被增量抓到的
///   只有回看年数那几年，之后就钉在最新一根上，再也不会回头补前面（2026-09-09 加深证综指等
///   三条时踩到：只抓到 3 年，而龙虎榜的偏离值要拿深证综指当基准回溯到 2004）。
///   重复抓不会覆盖已有数据（走 InsertOrRefreshUnconfirmed），多花的只是翻页请求。
///
/// ════ 一批＝一条指数 ════
/// 一共才十几条，按只切批最直观，也让 <c>MaxItems</c>/<c>Deadline</c> 有落点。
/// 不做批内并发：条数少，串行跑日志也好读（迁移前就是 foreach 串行的）。
/// </summary>
public sealed class IndexBarTask(
    FetchPaths paths,
    BarSourceHolder sourceHolder,
    IManifestStore manifestStore) : BarFetchTaskBase(paths, sourceHolder)
{
    public override FetchActionId Id => FetchActionId.StepIndexBars;

    protected override string TaskId => RetryTaskIds.IndexBars;

    /// <summary>四类待办都由本任务自己补（2026-09-21）。</summary>
    public override bool HandlesBacklog => true;

    /// <summary>它只有前复权一路，日志里按标的类型叫。</summary>
    protected override string BacklogLabel => "指数";

    /// <summary>行里没填「新标的补 N 年」时用的年数。</summary>
    private const int DefaultLookbackYears = 3;

    private bool _fullBackfill;

    protected override async IAsyncEnumerable<IReadOnlyList<CodeBars>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        ResetRun();
        _fullBackfill = args.Mode.HasFlag(FetchMode.FirstBackfill);
        using var _ = ForwardSourceStatus();

        if (args.Mode == FetchMode.FillBacklog)
        {
            await foreach (var b in FillBacklogAsync(manifestStore, [Granularity.Day], ct)) yield return b;
            yield break;
        }

        var all = MarketIndexCatalog.All;
        Report(_fullBackfill
            ? $"正在**整段回补**大盘指数K线（{all.Count} 个，从 "
              + $"{IncrementalWindowCalculator.AShareMarketOpen:yyyy-MM-dd} 起，不看水位线）..."
            : $"正在抓取大盘指数K线（{all.Count} 个：{string.Join("、", all.Select(i => i.Name))}）...");

        // 指数名称写进 StockMeta（type=index）——让"查询"页能按名称/代码搜到指数
        // （不影响个股选股，那边扫的是 6 位纯数字）。同步 IO，推线程池。
        await Task.Run(() =>
        {
            lock (SqliteWriteGate.Local)
                SqliteStockMetaUpsert.Upsert(Paths.CurrentDb,
                    all.Select(i => (i.Symbol, i.Name)), SqliteStockMetaUpsert.TypeIndex);
        }, ct);

        var today = DateTime.Today;
        int lookbackYears = args.LookbackYears is > 0 ? args.LookbackYears.Value : DefaultLookbackYears;

        // 整段回补：逐只算缺口（水位表 + 本地已覆盖 + 交易日历，见 PlanGaps）。
        // 指数只有十来个，一次算好查表用，省得在循环里反复开库。
        Dictionary<string, (DateTime Start, DateTime End)> gaps = [];
        if (_fullBackfill)
        {
            var (ws, we) = NarrowToYears(IncrementalWindowCalculator.AShareMarketOpen, today, args);
            if (ws.Date <= we.Date)
                gaps = (await Task.Run(() => PlanGaps(all.Select(i => i.Symbol).ToList(), Granularity.Day,
                                                      ws, we, ignoreFloor: false, out int _skip), ct))
                       .ToDictionary(g => g.Code, g => (g.Start, g.End), StringComparer.Ordinal);
        }

        int done = 0;
        foreach (var (symbol, name) in all)
        {
            ct.ThrowIfCancellationRequested();

            // 整段回补忽略水位线**和**回看年数：这个模式存在的意义就是"一次补到底"，
            // 还要人先把那个格子改成 25、跑完再改回 3，正是它要消灭的麻烦。
            // 数据源只会返回该指数实际存在的日期，早于发布日的部分自然是空。
            var (start, end) = _fullBackfill
                // 缺口表里没有这只＝它这一段已经齐了（或数据源已探明没有），给个空区间让下面跳过
                ? gaps.TryGetValue(symbol, out var g) ? g : (today.AddDays(1), today)
                : (await Task.Run(() => IncrementalStart(symbol, Granularity.Day, today, lookbackYears), ct), today);

            done++;
            if (start.Date > end.Date)
            {
                CountSkipped();
                ReportBatch(done, $"指数日K：{name} 本地已是最新，不发请求", done, all.Count);
                continue;
            }

            Report($"正在抓取 {symbol}");
            var got = await FetchOneAsync(symbol, Granularity.Day, start, end, driftCheck: false, ct);
            ReportBatch(done, $"指数日K：{name}", done, all.Count);
            if (got != null) yield return [got];
        }
    }

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        SaveFailedTodo(manifestStore);
        Report($"指数日K中断。已落库的 {RowsWritten} 行有效，下轮按各自的水位线接着走。");
        return Task.CompletedTask;
    }

    protected override async Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (BacklogMode) return FinishBacklog("指数日K", manifestStore);

        SaveFailedTodo(manifestStore);

        // 整段回补多半是"加了新指数"之后跑的，人要的就是"它到底补到哪年了"这个答案——
        // 翻日志数行数太费劲，直接逐条报出来。
        if (_fullBackfill)
        {
            foreach (var (symbol, name) in MarketIndexCatalog.All)
            {
                var earliest = await Task.Run(() => EarliestLocal(symbol, Granularity.Day), ct);
                Report($"　{name}（{symbol}）：本地最早 {earliest:yyyy-MM-dd}");
            }
        }

        Report($"本项汇总：{Summarize()}");
        var summary = $"指数日K：写入 {RowsWritten} 行"
                    + (FailedCount > 0 ? $"，{FailedCount} 条失败（已记进待办）" : "")
                    + $"，用时 {ElapsedText.Format(Sw.Elapsed)}。";
        Report(summary);

        if (AllAttemptedFailed)
            return new TaskRunResult(TaskState.Failed, Errors, NothingToDo: false, summary);

        return new TaskRunResult(TaskState.Completed, Errors, NothingToDo: RowsWritten == 0, summary);
    }
}
