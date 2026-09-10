using System.Diagnostics;
using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【拉取财务报表】（2026-09-10 从 FetchOrchestrator 迁成新式任务，判据见
/// doc/full-audit-task-migration-design.md §0）。三张报表的关键科目，FactorLab M4 基本面因子的数据基础。
///
/// ════ 一批＝一只票 ════
/// 一只票抓 3 张报表（约 12 秒）→ 立刻整只落库（<c>ReplaceByCode</c>）。这个粒度是被逼出来的：
/// 落库语义本来就是"按股票整只删了重插"，批粒度跟它对齐，中途停在哪都是"这只票要么完整要么没动"。
///
/// ════ ⚠ 必须顺序处理，不能并发 ════
/// （2026-08-27 踩过的坑，迁移后照样成立）每只票要抓 3 张表，每张表都要重新抢限速器的信号量。
/// 原来同时启动 300 个任务，信号量只有 1 个名额且队列 FIFO，于是变成
/// 「票A表1 → 票B表1 → … → 票300表1 → 才轮到 票A表2」——300 只票齐头并进、谁都差一张表，
/// **谁都写不进库**。实测发出 420 个请求、零条写入、零错误，看着像卡死其实在正常跑。
/// 骨架的流式枚举天然是顺序的，别改成 Task.WhenAll。
///
/// ════ 每轮上限交给框架 ════
/// 老实现自己写了 <c>MaxFinancialFetchPerRun</c> + <c>maxCount</c> 参数，迁移时删掉，
/// 改用 <see cref="TaskRunArgs.MaxItems"/>（一批＝一只票，语义正好对上）和 <c>Deadline</c>。
/// 调度没给上限时仍按 <see cref="FinancialFetchPlanner.MaxPerRun"/> 兜底——
/// 不兜的话一轮就是全市场 1.7 万个请求、几小时，把别的任务全挡在门外。
///
/// ════ 失败不中断整轮 ════
/// 单只失败记进错误列表继续下一只：本地报告期停在旧值，下轮 <see cref="FinancialFetchPlanner"/>
/// 自然把它重新算进待抓名单（自愈，不需要失败名单表）。
/// </summary>
public sealed class FinancialTask(
    FetchPaths paths,
    IFinancialProvider provider) : FetchTaskBase<FinancialValue>
{
    public override FetchActionId Id => FetchActionId.FetchFinancials;

    private readonly SqliteFinancialRepository _repo = new(paths.CurrentDb);

    /// <summary>本轮统计。</summary>
    private int _targetCount, _done, _wrote, _empty;
    private readonly List<string> _errors = [];
    private readonly Stopwatch _sw = new();

    protected override async IAsyncEnumerable<IReadOnlyList<FinancialValue>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _repo.EnsureSchema();
        _done = _wrote = _empty = 0;
        _errors.Clear();
        _sw.Restart();

        int cap = args.MaxItems is > 0 ? args.MaxItems.Value : FinancialFetchPlanner.MaxPerRun;
        var plan = new FinancialFetchPlanner(paths).Plan(cap);
        var targets = plan.ThisRun;
        _targetCount = targets.Count;
        Report(plan.Describe(cap));
        if (targets.Count == 0) yield break;

        void Forward(string s) => Report(s);
        provider.OnStatus += Forward;
        try
        {
            foreach (var code in targets)
            {
                ct.ThrowIfCancellationRequested();

                // yield 不能待在 try/catch 里，所以先把这一只的结果接住，出了 try 再吐出去。
                List<FinancialValue>? rows = null;
                try
                {
                    rows = await provider.GetAllAsync(code, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _errors.Add($"财报 {code}: {ex.Message}");
                }

                if (rows is { Count: > 0 })
                {
                    yield return rows;
                }
                else if (rows != null)
                {
                    // 请求成功但一行都没解析出来——多半是该股没有这些报表（新上市/特殊标的），
                    // 单独计数：这个数一大就说明映射出问题了，不能静默混在"成功"里。
                    _empty++;
                }

                _done++;
                // 每 20 只报一次（顺序处理下单只约 12 秒，20 只≈4 分钟）。原来是 50 只，
                // 降速后那是 10 分钟一报，太稀疏，看着像卡住了。
                if (_done % 20 == 0 || _done == targets.Count)
                {
                    var per = _sw.Elapsed.TotalSeconds / _done;
                    var left = TimeSpan.FromSeconds(per * (targets.Count - _done));
                    Report($"财务报表进度 ({_done}/{targets.Count})，已写入 {_wrote:N0} 条"
                           + (_errors.Count > 0 ? $"，失败 {_errors.Count} 只" : "")
                           + (_empty > 0 ? $"，{_empty} 只无报表数据" : "")
                           + $"，已用时 {Fmt(_sw.Elapsed)}"
                           + (_done < targets.Count ? $"，预计还需 {Fmt(left)}" : ""),
                           _done, targets.Count);
                }
            }
        }
        finally
        {
            provider.OnStatus -= Forward;
        }
    }

    /// <summary>一批就是一只票的全部科目，整只替换。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<FinancialValue> batch, CancellationToken ct)
    {
        var code = batch[0].Code;
        _repo.ReplaceByCode(code, batch);
        _wrote += batch.Count;
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (_targetCount == 0)
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Ok(nothingToDo: true));

        // 本轮跑完还剩多少——必须显式报出来。有每轮上限在，"完成"两个字很容易被读成
        // "全部补齐了"，实际可能只补了 5%。
        int stillPending = 0;
        try { stillPending = new FinancialFetchPlanner(paths).Plan().AllPending.Count; }
        catch (Exception) { /* 只是提示，查不到就不提 */ }

        Report($"财务报表完成：抓取 {_done} 只、写入 {_wrote:N0} 条、失败 {_errors.Count} 只"
               + (_empty > 0 ? $"、{_empty} 只无报表数据" : "")
               + (_errors.Count > 0 ? "（失败的下次运行会自动重试）" : "")
               + $"，用时 {Fmt(_sw.Elapsed)}。"
               + (stillPending > 0
                   ? $"⚠ 还有 {stillPending} 只没补——勾选界面上的【空闲时自动补财务】可以让它在程序空着时"
                     + "自己一轮一轮补完，或者再点一次。已抓的不会重抓。"
                   : "全部已补齐（报告期和科目集版本都是最新）。"));

        return Task.FromResult<TaskRunResult?>(new TaskRunResult(TaskState.Completed, _errors.ToList()));
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} 小时 {t.Minutes} 分"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒"
        : $"{t.TotalSeconds:F1} 秒";
}
