using System.Runtime.CompilerServices;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>一路接口一个月的抓取结果——落账的单位（行和"这个月问过了"一起记，停在哪都不丢）。</summary>
/// <param name="Fetched">接口返回了多少条，记进 SuspensionFetchMonth 备查。</param>
public sealed record SuspensionMonth(string Source, DateOnly Month, IReadOnlyList<SuspensionRow> Rows, int Fetched);

/// <summary>
/// 【停复牌】（2026-09-30，见 doc/suspension-design.md）——从沪深交易所官网取停复牌记录，
/// 给【当日完整性体检】【全库数据体检】解释"那天为什么没有日K"。
///
/// ════ 三路接口、按月问 ════
/// 上交所·股票、上交所·基金、深交所各一个 <see cref="ISuspensionProvider"/>，都是一个月一个请求。
/// 要问哪些月由 <see cref="SuspensionFetchPlan"/> 定：没问过的 + 最近两个月（还在停牌的复牌后才补结束日）；
/// 首次整段回补＝从 2004 年起全部重问。
///
/// ════ 取到的全存（2026-09-30 用户定）════
/// 可转债、LOF、B 股这些我们没有日K的也存，接口的每一列都有字段。体检按 Bar 代码查，用不上的自然查不到；
/// 以后要用（比如可转债停牌），或者要核对，不用再回官网重抓。
///
/// ════ 一路出错不连累另外两路 ════
/// 每一路各自 try，出错记下来接着跑下一路；已经落账的月份不会白跑（下轮从没问过的接着问）。
/// 有一路出过错整项就报失败，免得界面上一个绿勾盖住"深交所那路今天没取到"。
/// </summary>
public sealed class SuspensionTask(
    IReadOnlyList<ISuspensionProvider> providers,
    ISuspensionRepository repository) : FetchTaskBase<SuspensionMonth>
{
    /// <summary>每问这么多个月打一条进度（其余只喂看门狗）。</summary>
    private const int ReportEveryMonths = 24;

    public override FetchActionId Id => FetchActionId.StepSuspension;

    private readonly List<string> _errors = [];
    private readonly Dictionary<string, (int Months, int Rows)> _bySource = new(StringComparer.Ordinal);

    protected override async IAsyncEnumerable<IReadOnlyList<SuspensionMonth>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _errors.Clear();
        _bySource.Clear();
        bool all = args.Mode.HasFlag(FetchMode.FirstBackfill);
        var today = DateOnly.FromDateTime(DateTime.Today);

        await Task.Run(repository.EnsureSchema, ct);

        foreach (var p in providers)
        {
            var fetched = await Task.Run(() => repository.GetFetchedMonths(p.Source), ct);
            var months = SuspensionFetchPlan.Build(p.FirstMonth, fetched, today, all);
            Report($"{p.Label}停复牌：要问 {months.Count} 个月"
                 + (all ? "（首次整段回补，全部重问）" : $"（没问过的 + 最近 {SuspensionFetchPlan.RefreshRecentMonths} 个月）")
                 + "，一个月一个请求、一个月一批落库…");

            using var _ = ForwardStatus(h => p.OnStatus += h, h => p.OnStatus -= h);
            int done = 0, kept = 0;
            foreach (var month in months)
            {
                ct.ThrowIfCancellationRequested();
                SuspensionMonth? batch = null;
                try
                {
                    var rows = await p.GetMonthAsync(month, ct);
                    batch = new SuspensionMonth(p.Source, month, rows, rows.Count);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _errors.Add($"{p.Label} {month:yyyy-MM}：{ex.Message}");
                    Report($"　⚠ {p.Label} {month:yyyy-MM} 没取到（{ex.Message}），这一路先停，下轮从这个月接着问");
                    break;
                }

                done++;
                kept += batch.Rows.Count;
                ReportQuiet($"{p.Label} {month:yyyy-MM}：{batch.Rows.Count} 条", done, months.Count);
                if (done % ReportEveryMonths == 0 || done == months.Count)
                    Report($"　{p.Label} {done}/{months.Count} 个月（当前 {month:yyyy-MM}），共 {kept:N0} 条",
                        done, months.Count);
                yield return [batch];
            }
            _bySource[p.Label] = (done, kept);
        }
    }

    protected override Task SaveBatchAsync(IReadOnlyList<SuspensionMonth> batch, CancellationToken ct)
        => Task.Run(() =>
        {
            lock (SqliteWriteGate.Local)
                foreach (var m in batch)
                {
                    repository.Upsert(m.Rows);
                    repository.MarkMonthFetched(m.Source, m.Month, m.Fetched);
                }
        }, ct);

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        var summary = string.Join("；", _bySource.Select(kv => $"{kv.Key} {kv.Value.Months} 个月、{kv.Value.Rows:N0} 条"));
        Report($"停复牌完成：{(summary.Length > 0 ? summary : "没有要问的")}"
             + (_errors.Count > 0 ? $"；⚠ {_errors.Count} 路没取完（见上面），已落账的月份不会白跑" : "")
             + $"，用时 {ElapsedText.Format(stats.Elapsed)}。");

        return Task.FromResult<TaskRunResult?>(_errors.Count > 0
            ? new TaskRunResult(TaskState.Failed, _errors.ToList(), Progress: summary)
            : TaskRunResult.Ok(nothingToDo: stats.Items == 0, progress: summary));
    }
}
