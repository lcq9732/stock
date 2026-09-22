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
/// 【重取前复权】（2026-09-22 从 <c>FetchOrchestrator.RunRepairQfqAsync</c> 迁到新框架）——
/// 把除权后基准变了的股票，前复权历史**整段按数据源当前基准重写**。
///
/// ════ 为什么要有这件事 ════
/// 数据源的前复权是"原价 − 之后累计分红送配"，基准随抓取时点变化：某只票一分红，它全部
/// 历史的前复权值就都变了。而本地历史是分批入库的，于是同一只股票不同时间段落在不同基准上，
/// 接缝处出现假跳空（实测有股票虚增 50%）。后复权/不复权不受影响。
///
/// 名单由【个股日K·前复权】在日常抓取中顺带比对出来（窗口向前放宽 400 天不多花请求），
/// 判据在 <see cref="QfqRepairPlanner.SelectForRepair"/>。
///
/// ════ 迁移改掉了什么（这是这次迁移的全部意义）════
/// 老实现是 <c>Task.WhenAll</c> 把整批丢给限流器，**名单的划账在 WhenAll 之后**、
/// 而取消是直接冒泡出去的 —— 于是被停止（空闲窗口到点、限流打断、用户按停止）时
/// <c>PendingQfqRepairCodes</c> 一个都不更新，**已经按新基准重写完的票下一轮全部重抓一遍**。
/// 每只十年多页、约 4 秒，名单长的时候这就是十几分钟白跑。
///
/// 现在：一批 <see cref="DefaultBatchSize"/> 只，**每批存完立刻划账**（见 <see cref="SaveBatchAsync"/>），
/// 停在哪里前面的批都算数。<see cref="TaskRunArgs.MaxItems"/>／<c>Deadline</c> 也就跟着
/// 自动落在批边界上，界面那边"按每只 4 秒估本轮能跑几只"的限量可以撤了。
///
/// ════ 一批 ＝ 10 只 ════
/// 比另外几项的 30 只小：这里每只都是整段十年、多页请求（约 4 秒），10 只一批≈40 秒，
/// 收尾粒度够细。批大小不是并发度——真正的闸是数据源的限流器（腾讯 3 并发/1 秒）。
///
/// ════ 失败的票怎么办 ════
/// **不写失败名单**：名单本身就是待办，失败＝这一批里没有它＝不划掉＝下一轮自然重来
/// （见 <see cref="RetryTaskIds.QfqRepair"/>）。也不调 <c>RecordDriftedForRepair</c>——
/// 这一项是消费名单的，往里记等于自己喂自己。
/// </summary>
public sealed class QfqRepairTask(
    FetchPaths paths,
    BarSourceHolder sourceHolder,
    IManifestStore manifestStore,
    int batchSize = QfqRepairTask.DefaultRepairBatchSize) : BarFetchTaskBase(paths, sourceHolder)
{
    /// <summary>一批几只。见类注释「一批 ＝ 10 只」。</summary>
    public const int DefaultRepairBatchSize = 10;

    /// <summary>名单里的票本地一根都没有时（理论上不该出现）回看几年。</summary>
    private const int DefaultLookbackYears = 3;

    public override FetchActionId Id => FetchActionId.RepairQfq;

    protected override string TaskId => RetryTaskIds.QfqRepair;

    private readonly int _batchSize = batchSize > 0 ? batchSize : DefaultRepairBatchSize;

    private string? _nothingToDoReason;
    private int _planned, _repaired, _left;

    protected override async IAsyncEnumerable<IReadOnlyList<CodeBars>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        ResetRun();
        _nothingToDoReason = null;
        _planned = _repaired = 0;
        using var _ = ForwardSourceStatus();

        int lookbackYears = args.LookbackYears is > 0 ? args.LookbackYears.Value : DefaultLookbackYears;

        // 读名单 + 一次 GROUP BY 拿全部最早日，都是同步 IO——骨架不替子类推线程池，
        // 首个 await 之前干这些会把界面整段冻死（feedback_task_must_offload_heavy_sync）。
        var plan = await Task.Run(() => Plan(DateTime.Today, lookbackYears), ct);
        _left = plan.Count;

        if (plan.Count == 0)
        {
            _nothingToDoReason = "待重取前复权的名单是空的——没有股票的复权基准发生过漂移";
            Report($"{_nothingToDoReason}，这一轮没什么可做（一个请求都没发）。");
            yield break;
        }

        _planned = plan.Count;
        Report($"待重取前复权 {plan.Count} 只——每只从本地最早一根按数据源当前基准整段重写，"
             + $"每批 {_batchSize} 只（每批存完就从名单划掉，中途停不会白跑）...", 0, plan.Count);

        int batchIndex = 0, done = 0;
        foreach (var batch in plan.Chunk(_batchSize))
        {
            ct.ThrowIfCancellationRequested();
            // driftCheck: false + overwrite: true —— 覆盖那一路本来就是"全都以新基准为准"，
            // 再比对一遍毫无意义（判据那边看到 overwrite 就直接返回，见 BarWritePlanner.Plan）。
            var got = await FetchBatchAsync(
                batch.Select(b => (b.Code, Granularity.Day, b.Start, b.End, false)), ct, overwrite: true);
            batchIndex++;
            done += batch.Length;
            ReportBatch(batchIndex, "重取前复权：抓取中", done, plan.Count);
            yield return got;
        }
    }

    /// <summary>
    /// 这一轮要重取哪些票、各自哪一段。名单顺序原样保留（它本来就是按代码排好的），
    /// 于是被 <c>MaxItems</c>／<c>Deadline</c> 截断时下一轮从名单前头接着走。
    /// </summary>
    private List<(string Code, DateTime Start, DateTime End)> Plan(DateTime today, int lookbackYears)
    {
        List<string> pending;
        Dictionary<string, DateTime> earliest;
        lock (SqliteWriteGate.Local)
        {
            pending = manifestStore.Load().PendingQfqRepairCodes.ToList();
            if (pending.Count == 0) return [];
            earliest = Bars.GetEarliestPeriodStartByCode(Granularity.Day);
        }

        var list = new List<(string, DateTime, DateTime)>(pending.Count);
        foreach (var code in pending)
        {
            var (start, end) = QfqRepairPlanner.WindowFor(
                earliest.TryGetValue(code, out var e) ? e : null, today, lookbackYears);
            list.Add((code, start, end));
        }
        return list;
    }

    /// <summary>
    /// 落一批，**存完立刻从名单划掉这一批**。
    ///
    /// ⚠ 顺序不能反：先划账后落库的话，写库失败时名单上已经没有它了，那只票的接缝再也没人修
    /// （跟【分档资金流快照】记页号进度同一条纪律）。
    ///
    /// 划掉的是 <paramref name="batch"/> 里有的票——抓失败的根本不在这里（<c>FetchBatchAsync</c>
    /// 把它们滤掉了），所以留在名单上下轮重来，正是要的行为。"请求成功但返回 0 行"的票算做完：
    /// 那多半是已退市、数据源不再给了，不划掉它就永远挂在名单上。
    ///
    /// 同样包 <c>Task.Run</c>：读改写 manifest 是同步 IO，而骨架的 <c>await foreach</c> 续体
    /// 可能回到 UI 线程。**故意不带 ct**——取消时这一批已经落库了，账必须记上。
    /// </summary>
    protected override async Task SaveBatchAsync(IReadOnlyList<CodeBars> batch, CancellationToken ct)
    {
        await base.SaveBatchAsync(batch, ct);

        var repaired = batch.Select(b => b.Code).Distinct(StringComparer.Ordinal).ToList();
        if (repaired.Count == 0) return;

        _left = await Task.Run(() => Drop(repaired));
        _repaired += repaired.Count;
    }

    /// <summary>把这些票从待重取名单里划掉，返回还剩几只。</summary>
    private int Drop(IReadOnlyCollection<string> repaired)
    {
        lock (SqliteWriteGate.Local)
        {
            var manifest = manifestStore.Load();
            var done = repaired.ToHashSet(StringComparer.Ordinal);
            manifest.PendingQfqRepairCodes =
                manifest.PendingQfqRepairCodes.Where(c => !done.Contains(c)).ToList();
            manifestStore.Save(manifest);
            return manifest.PendingQfqRepairCodes.Count;
        }
    }

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        // 名单已经按批划过了，这里只汇报——不调 SaveFailedTodo（名单即待办，见类注释）。
        Report($"重取前复权中断：本轮已按新基准重写 {_repaired} 只（{RowsWritten} 行），"
             + $"名单里还剩 {_left} 只，下一轮接着从名单前头走（做完的不会重来）。");
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (_nothingToDoReason is { } idle)
            return Task.FromResult<TaskRunResult?>(
                new TaskRunResult(TaskState.Completed, Errors, NothingToDo: true, idle));

        Report($"本项汇总：{Summarize()}");
        var summary = $"重取前复权：{_repaired}/{_planned} 只已按新基准重写（{RowsWritten} 行）"
                    + (FailedCount > 0 ? $"，{FailedCount} 只失败（留在名单上、下轮重来）" : "")
                    + (_left > 0 ? $"，还剩 {_left} 只" : "，名单已清空")
                    + $"，用时 {ElapsedText.Format(Sw.Elapsed)}。";
        Report(summary);

        if (AllAttemptedFailed)
            return Task.FromResult<TaskRunResult?>(
                new TaskRunResult(TaskState.Failed, Errors, NothingToDo: false, summary));

        return Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, Errors, NothingToDo: RowsWritten == 0, summary));
    }
}
