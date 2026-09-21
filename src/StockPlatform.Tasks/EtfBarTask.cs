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
/// 【ETF日K】（2026-09-21 从编排器迁到新框架，见 doc/bar-tasks-migration-design.md）——
/// 全市场 ETF 的前复权日线（<c>day</c>），名单走新浪、K线走所选源，逐只按水位线增量。
///
/// ⚠ ETF 在 Bar 表里的 code 是 <c>sh510300</c> 这种**带前缀的 8 位符号**，跟个股不一样
/// （见 project_bar_code_prefix：传错了是静默 0 行，报成"没有日K"）。
///
/// ════ 名单那道闸 ════
/// 名单接口被限流/半途断连时**不报错、只少给**，拿半截名单去跑的后果是那些没在名单里的 ETF
/// 这一轮完全不被处理、而日志看起来一切正常。判据在 <see cref="EtfListGuard"/>：
/// 比库里存量少 5% 以上就改用存量名单跑（这一轮发现不了新上市的 ETF，但不会漏处理存量）。
///
/// ════ 跟【ETF日K·不复权】的关系 ════
/// 那是另一项（<see cref="EtfRawBarTask"/>），抓的是 <c>day_raw</c>、水位线独立。
/// 回测吃的是不复权那条线，这一条是界面展示和名单维护用的。
/// </summary>
public sealed class EtfBarTask(
    FetchPaths paths,
    BarSourceHolder sourceHolder,
    IStockListProvider etfListProvider,
    IManifestStore manifestStore,
    int batchSize = BarFetchTaskBase.DefaultBatchSize) : BarFetchTaskBase(paths, sourceHolder)
{
    public override FetchActionId Id => FetchActionId.StepEtfBars;

    protected override string TaskId => RetryTaskIds.EtfBars;

    /// <summary>四类待办都由本任务自己补（2026-09-21）。⚠ 它的空洞名单里可能混着
    /// <c>day</c> 和 <c>day_raw</c>（ETF 的空洞不分口径全记在这个 taskId 名下），
    /// 补的时候按每一段自己的 Gran 走——见 <c>BarFetchTaskBase.FillGapAsync</c>。</summary>
    public override bool HandlesBacklog => true;

    /// <summary>它只有前复权一路，日志里按标的类型叫。</summary>
    protected override string BacklogLabel => "ETF";

    /// <summary>行里没填「新标的补 N 年」时用的年数。</summary>
    private const int DefaultLookbackYears = 3;

    private readonly int _batchSize = batchSize > 0 ? batchSize : DefaultBatchSize;

    /// <summary>这一轮**根本没开工**的理由（数据源不可达那种，今天恢复了还该再来）。</summary>
    private string? _skippedReason;

    /// <summary>这一轮**没活可干**（标的都已是最新）。跟上面那个不是一回事，见 <see cref="OnCompletedAsync"/>。</summary>
    private string? _nothingToDoReason;

    private int _planned;

    protected override async IAsyncEnumerable<IReadOnlyList<CodeBars>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        ResetRun();
        _skippedReason = _nothingToDoReason = null;
        _planned = 0;
        using var _ = ForwardSourceStatus();

        if (args.Mode == FetchMode.FillBacklog)
        {
            await foreach (var b in FillBacklogAsync(manifestStore, [Granularity.Day], ct)) yield return b;
            yield break;
        }

        Report("正在获取全市场ETF列表...");

        // 库里的存量名单——既是那道闸的标尺，也是名单取不到时的兜底。同步 IO，推线程池。
        var local = await Task.Run(() =>
            SqliteStockMetaUpsert.GetByTypes(Paths.CurrentDb, SqliteStockMetaUpsert.TypeEtf)
                .Select(x => new StockListEntry(x.Code, x.Name)).ToList(), ct);

        List<StockListEntry> etfs;
        try { etfs = await etfListProvider.GetAllStocksAsync(ProgressSink, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Errors.Add($"获取ETF列表失败：{ex.Message}"); etfs = []; }

        bool fromLocal = false;
        switch (EtfListGuard.Judge(etfs.Count, local.Count))
        {
            case EtfListGuard.Decision.Abort:
                _skippedReason = "ETF列表为空且库里也没有存量（接口可能不可达/被限流）";
                Report($"{_skippedReason}，本轮跳过ETF。");
                yield break;

            case EtfListGuard.Decision.UseLocal:
                Errors.Add($"ETF名单只拿到 {etfs.Count} 只、库里存量有 {local.Count} 只，判定为半截名单——"
                         + "本轮改用库里存量名单跑增量（这一轮发现不了新上市的ETF）");
                Report($"⚠ ETF名单疑似被截断（{etfs.Count} < {local.Count}），改用库里存量 {local.Count} 只。");
                etfs = local;
                fromLocal = true;
                break;
        }

        // ETF 名称写进 StockMeta（type=etf），让"查询"页能搜到。
        // 走兜底时名字本来就是从这张表读出来的，没必要再写回去。
        if (!fromLocal)
            await Task.Run(() =>
            {
                lock (SqliteWriteGate.Local)
                    SqliteStockMetaUpsert.Upsert(Paths.CurrentDb,
                        etfs.Select(e => (e.Code, e.Name)), SqliteStockMetaUpsert.TypeEtf);
            }, ct);

        var end = DateTime.Today;
        int lookbackYears = args.LookbackYears is > 0 ? args.LookbackYears.Value : DefaultLookbackYears;

        // 先把每只的窗口算出来，分清"真要抓"和"本地已是最新"——全都不用抓时连一个请求都不发，
        // 日志也能说清楚（跟后复权/不复权那条路的口径一致）。
        var plan = await Task.Run(() => etfs
            .Select(e => (e.Code, Start: IncrementalStart(e.Code, Granularity.Day, end, lookbackYears)))
            .Where(w =>
            {
                if (w.Start.Date <= end.Date) return true;
                CountSkipped();
                return false;
            })
            .ToList(), ct);

        _planned = plan.Count;
        if (plan.Count == 0)
        {
            // ⚠ 这是「没活可干」，**不是**「没开工」——两者对计划引擎意义完全不同：
            //    Skipped ＝ "这轮被挡住了、今天恢复了还该再来"，于是引擎会立刻再排一次，
            //    而条件根本不会变（标的还是最新的），就成了空转——实测连打 5 轮之后
            //    才被"连着 5 轮瞬间跑完"那道护栏拦下（2026-09-21 验证时踩到）。
            //    真正该用 Skipped 的是下面那个 Abort（名单接口不可达）。
            _nothingToDoReason = $"ETF日K：{etfs.Count} 只本地都已是最新";
            Report($"{_nothingToDoReason}，这一轮无需抓取（一个请求都没发）。");
            yield break;
        }

        Report($"共 {etfs.Count} 只 ETF，其中 {plan.Count} 只要抓、{etfs.Count - plan.Count} 只本地已是最新"
             + $"（按各自水位线跳过，不发请求），每批 {_batchSize} 只...");

        int batchIndex = 0, done = 0;
        foreach (var batch in plan.Chunk(_batchSize))
        {
            ct.ThrowIfCancellationRequested();
            var got = await FetchBatchAsync(
                batch.Select(b => (b.Code, Granularity.Day, b.Start, end, false)), ct);
            batchIndex++;
            done += batch.Length;
            ReportBatch(batchIndex, "ETF日K：抓取中", done, plan.Count);
            yield return got;
        }
    }

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        SaveFailedTodo(manifestStore);
        Report($"ETF日K中断。已落库的 {RowsWritten} 行有效，下轮按各自的水位线接着走。");
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (BacklogMode) return Task.FromResult<TaskRunResult?>(FinishBacklog("ETF日K", manifestStore));

        SaveFailedTodo(manifestStore);

        // 「没开工」：名单接口不可达且库里没存量——今天恢复了还该再来，所以是 Skipped。
        if (_skippedReason is { } blocked)
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Skipped(blocked, Errors));

        // 「没活可干」：标的都已是最新——今天再来也是同一个结果，记成完成 + NothingToDo。
        if (_nothingToDoReason is { } idle)
            return Task.FromResult<TaskRunResult?>(
                new TaskRunResult(TaskState.Completed, Errors, NothingToDo: true, idle));

        Report($"本项汇总：{Summarize()}");
        var summary = $"ETF日K：{_planned} 只写入 {RowsWritten} 行"
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
