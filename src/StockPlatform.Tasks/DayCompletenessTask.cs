using System.Diagnostics;
using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【当日完整性体检】（2026-09-17 从 <c>FetchOrchestrator.RunStepDayCoverageCheckAsync</c> 迁过来）。
///
/// ════ 为什么迁 ════
/// 跟【全库数据体检】那次破例是同一条判据：**它正要长大**。2026-09-16 它从"只查个股K线"
/// 扩成三段（K线三类标的 → 五张日更表 → 覆盖式快照），编排从 40 行涨到 150 行，
/// 而且后面还会继续加判据段。再堆在 <c>FetchOrchestrator</c> 里只会让那个文件更难拆。
///
/// 迁移顺带理清了一件事：判据和落账现在都在 <see cref="SqliteDayCompletenessAuditor"/>，
/// 这一项和【重新拉取失败】收尾时的那次重建**共用同一份**——不会出现"体检说齐了、
/// 重试那边还挂着单子"的分叉。
///
/// ════ 一批＝一段，但**四段是先全扫完、再逐段落盘的** ════
/// ① K线 / ② 日更表 / ③ 覆盖式快照 / ④ 指标新鲜度，各 yield 一次。
///
/// ⚠ 分段**没有**换来"中途停下来能保住前一段的结论"（2026-09-23 把这条说清楚）：
/// <see cref="SqliteDayCompletenessAuditor.RunSegments"/> 本身是懒枚举的，但这里用
/// <c>.ToList()</c> 在 <c>Task.Run</c> 里一次性枚举完——**这是故意的**。不 ToList 的话，
/// 枚举会发生在骨架 <c>await foreach</c> 的续体上，而那个续体可能回到 UI 线程
/// （见 feedback：任务的同步重活必须自己推线程池，骨架不替你推），
/// 几 GB 的 join 就会把界面整段冻死。
///
/// 所以实际时序是：**四段在线程池上全部扫完（几分钟）→ 才开始逐段落账**。
/// 中途取消＝这一轮一条待办都没落，下次重来。代价能接受，因为它纯查库、零请求，
/// 重跑就是再花那几分钟；分段现在换来的是落账按段写、日志按段报，不是断点。
///
/// 真要做到"扫完一段就落一段"，得把 <b>每一段各自包一次</b> <c>Task.Run</c>
/// （而不是整个 <c>RunSegments</c> 包一次），那样每段扫完就能 yield、又都在线程池上。
/// 没这么做是因为收益不抵改动——但别再照着旧注释以为它已经是断点式的了。
///
/// ════ 纯查库 ════
/// 一个请求都不发，但要扫几 GB（个股三口径各一次 join）。
/// </summary>
public sealed class DayCompletenessTask(
    FetchPaths paths,
    IManifestStore manifestStore) : FetchTaskBase<DayFinding>
{
    public override FetchActionId Id => FetchActionId.StepDayCoverage;

    private readonly Stopwatch _sw = new();
    private readonly List<DayFinding> _all = [];
    private DateTime? _day;

    protected override async IAsyncEnumerable<IReadOnlyList<DayFinding>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _all.Clear();
        _day = null;
        _sw.Restart();

        if (!File.Exists(paths.CurrentDb))
        {
            Report("本地还没有数据库，没什么可体检的。");
            yield break;
        }

        var auditor = new SqliteDayCompletenessAuditor(paths.CurrentDb);

        // ⚠ 整个查库过程推到线程池：RunSegments 是同步的，而且第一段就要对 Bar 表做三次 join。
        // 骨架不替子类推（见 FetchTaskBase 的类注释），在这儿 await 之前干重活会冻死界面。
        var segments = await Task.Run(() =>
        {
            var batches = auditor.RunSegments(out var day).ToList();
            return (Day: day, Batches: batches);
        }, ct);

        if (segments.Day is not { } latest)
        {
            Report("（跳过当日完整性体检：本地上证指数日线不足两根，没有交易日锚可用）");
            yield break;
        }

        _day = latest;
        Report($"当日完整性体检：查 {latest:yyyy-MM-dd}（K线三类标的 → 日更表 → 覆盖式快照）...");

        foreach (var batch in segments.Batches)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var f in batch.Where(f => f.Detail is { Length: > 0 }))
                Report($"⚠ {f.Detail}");
            _all.AddRange(batch);
            yield return batch;
        }
    }

    /// <summary>
    /// 一段的结论落一次账。写的是 manifest 而不是业务表——体检本身不产生数据，
    /// 它的产出就是"谁欠着什么"。
    /// </summary>
    protected override Task SaveBatchAsync(IReadOnlyList<DayFinding> batch, CancellationToken ct)
    {
        if (batch.All(f => f.Todo == DayTodoAction.None)) return Task.CompletedTask;

        var manifest = manifestStore.Load();
        SqliteDayCompletenessAuditor.Apply(manifest, batch);
        manifestStore.Save(manifest);
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (_day is not { } latest)
            return Task.FromResult<TaskRunResult?>(
                TaskRunResult.Ok(nothingToDo: true, progress: "没有交易日锚，这一轮没体检"));

        int problems = _all.Count(f => f.IsBad);
        var detail = string.Join("；", _all.Select(f => f.Summary));
        var summary = problems == 0
            ? $"当日完整性体检：{latest:yyyy-MM-dd} 全齐 —— {detail}"
            : $"当日完整性体检：{latest:yyyy-MM-dd} 有 {problems} 处不齐 —— {detail}";
        Report(summary + $"（用时 {_sw.Elapsed.TotalSeconds:F1} 秒）");

        // ⚠ 不齐**不算这一项失败**：体检干成了它该干的活——查出来、记下来。
        // 判成失败的话状态列会红在这一行，而该动手的是【重新拉取失败】和日志里点名的那几项。
        return Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, [], NothingToDo: problems == 0, summary));
    }
}
