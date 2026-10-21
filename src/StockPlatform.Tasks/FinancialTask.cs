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
/// ════ 每轮不设上限（2026-10-21 随挪进日更）════
/// 原来调度没给 <see cref="TaskRunArgs.MaxItems"/> 时按 300 只兜底。挪进日更后删了：
/// 日更"跑一轮就算今天做完"，留着上限的话披露高峰（一天 1200 只）要积压好几天；
/// 而当天没抓完的票本地报告期还是旧的，第二天照样被算进名单，漏不掉。
/// "一轮会跑很久"改成开工那行报只数和预计耗时（<see cref="FinancialFetchPlan.Describe"/>）。
/// 调度显式给了 MaxItems / Deadline 时仍照办（骨架负责）。
///
/// ════ 报错（2026-10-21 用户定）════
/// 抓到的立即入库；没抓到的报只数、不挡后面的任务；用户手动点【执行】或等明天日更都会再取。
///   · 单只失败：错误原文进运行日志和错误列表，继续下一只。本地报告期停在旧值，
///     下轮 <see cref="FinancialFetchPlanner"/> 自然把它算回名单——**不能记"问过了"**，
///     否则它会进冷却。
///   · 连续 <see cref="AbortAfterConsecutiveFailures"/> 只失败：认定数据源当前不通，本轮停下、记失败。
///     不停的话限速器会在熔断里一等十几分钟到两小时、一轮接一轮地等，没有上限之后能堵住后面所有任务。
///   · 抓回来了、但还没有已披露的那一期：数据源还没更新，不算错，明天再取；
///     披露超过 <see cref="FinancialFetchPlanner.SourceLagAlarm"/> 还没有的才报错。
/// </summary>
public sealed class FinancialTask(
    FetchPaths paths,
    IFinancialProvider provider) : FetchTaskBase<FinancialValue>
{
    public override FetchActionId Id => FetchActionId.FetchFinancials;

    /// <summary>
    /// 连续几只失败就认定数据源不通、本轮收工。每只本身已含限速器的快速重试，
    /// 个别票接口报错不会连着出现 10 次；真不通时最多再浪费 10 只的时间。
    /// </summary>
    internal const int AbortAfterConsecutiveFailures = 10;

    private readonly SqliteFinancialRepository _repo = new(paths.CurrentDb);

    /// <summary>本轮的待抓清单——落库时要回头问它"这只票冲的是哪一期"（<c>TargetOf</c>）。</summary>
    private FinancialFetchPlan? _plan;

    /// <summary>本轮统计。</summary>
    private int _targetCount, _done, _wrote, _empty, _consecutiveFailures, _sourceLag, _sourceLagAlarm;
    private bool _aborted;
    private readonly List<string> _errors = [];
    private readonly Stopwatch _sw = new();

    protected override async IAsyncEnumerable<IReadOnlyList<FinancialValue>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _repo.EnsureSchema();
        _done = _wrote = _empty = _consecutiveFailures = _sourceLag = _sourceLagAlarm = 0;
        _aborted = false;
        _errors.Clear();
        _sw.Restart();

        // 名单要读全市场状态和日线水位（25GB 库上要一阵子），推到线程池——
        // 首个 await 之前的同步活会跑在调用方（可能是 UI）线程上，见 BankRegulatoryTask 的同款注释。
        var plan = await Task.Run(() => new FinancialFetchPlanner(paths).Plan(args.MaxItems), ct);
        _plan = plan;
        var targets = plan.ThisRun;
        _targetCount = targets.Count;
        Report(plan.Describe());
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
                    // ⚠ 不记"问过了"：本地报告期没动，下一轮自然再取（见类注释）
                    var msg = $"财报 {code}: {ex.Message}";
                    _errors.Add(msg);
                    Report($"✘ {msg}（已跳过，继续下一只）");
                }

                if (rows is { Count: > 0 })
                {
                    _consecutiveFailures = 0;
                    CountSourceLag(code, rows);
                    yield return rows;
                }
                else if (rows != null)
                {
                    _consecutiveFailures = 0;
                    // 请求成功但一行都没解析出来——多半是该股没有这些报表（新上市/特殊标的），
                    // 单独计数：这个数一大就说明映射出问题了，不能静默混在"成功"里。
                    //
                    // ⚠ 这条路不经过 SaveBatchAsync，所以得自己记一笔"问过了"，
                    //   否则这只票每轮都会被重新算进待抓名单（见 MarkAsked 的注释）。
                    _repo.MarkAsked(code, plan.TargetOf(code));
                    _empty++;
                }
                else if (++_consecutiveFailures >= AbortAfterConsecutiveFailures)
                {
                    _done++;
                    _aborted = true;
                    Report($"✘ 财务报表：数据源连续 {_consecutiveFailures} 只取不到，判断它当前不通，本轮停在第 {_done} 只。");
                    yield break;
                }

                _done++;
                // 日志每 20 只一行（顺序处理下单只约 12 秒，20 只≈4 分钟）。原来是 50 只，
                // 降速后那是 10 分钟一报，太稀疏，看着像卡住了。
                //
                // ⚠ 心跳是**每只**一次（2026-09-19 补）：20 只在降速下是 4~6 分钟，正顶着
                //   静默看门狗的 5 分钟上限——只按日志那个间隔出声，一轮抓满 300 只时就会
                //   在"一路正常抓"的状态下被判成卡死掐断。【资金净流入】09-18/09-19 连着
                //   被掐两轮就是这个原因，见 QuietWatchdog.IBeatOnlySink。
                {
                    var per = _sw.Elapsed.TotalSeconds / _done;
                    var left = TimeSpan.FromSeconds(per * (targets.Count - _done));
                    var text = $"财务报表进度 ({_done}/{targets.Count})，已写入 {_wrote:N0} 条"
                             + (_errors.Count > 0 ? $"，失败 {_errors.Count} 只" : "")
                             + (_empty > 0 ? $"，{_empty} 只无报表数据" : "")
                             + $"，已用时 {Fmt(_sw.Elapsed)}"
                             + (_done < targets.Count ? $"，预计还需 {Fmt(left)}" : "");
                    if (_done % 20 == 0 || _done == targets.Count)
                        Report(text, _done, targets.Count);
                    else
                        ReportQuiet(text, _done, targets.Count);
                }
            }
        }
        finally
        {
            provider.OnStatus -= Forward;
        }
    }

    /// <summary>
    /// 抓回来的最新期还没到已披露的那一期 ⇒ 数据源还没更新（2026-10-21）。
    /// 照常落库（老的期次可能有修订），只计数：刚披露的明天再取，拖过 <see cref="FinancialFetchPlanner.SourceLagAlarm"/> 的报错。
    /// </summary>
    private void CountSourceLag(string code, List<FinancialValue> rows)
    {
        if (_plan?.DisclosedOnByCode.TryGetValue(code, out var disclosedOn) != true) return;
        if (_plan.TargetOf(code) is not { } target) return;
        if (rows.Max(r => r.ReportDate) >= target) return;
        if (DateTime.Today - disclosedOn >= FinancialFetchPlanner.SourceLagAlarm) _sourceLagAlarm++;
        else _sourceLag++;
    }

    /// <summary>一批就是一只票的全部科目，整只替换。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<FinancialValue> batch, CancellationToken ct)
    {
        var code = batch[0].Code;
        // 带上"这一轮冲的是哪一期"：抓回来的报告期追不上它，就说明数据源那边就是没有，
        // 下一轮的 FinancialFetchPlanner 靠这一列把它冷却掉（刚披露的只冷却到当天）。
        _repo.ReplaceByCode(code, batch, _plan?.TargetOf(code));
        _wrote += batch.Count;
        return Task.CompletedTask;
    }

    protected override async Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (_targetCount == 0)
            return TaskRunResult.Ok(nothingToDo: true);

        // 用户 2026-10-21 定：只报只数，不列代码——要补就点【执行】，或者等明天日更。
        // 单只的错误原文已经逐只打进运行日志了（排查"为什么失败"用）。
        const string retryHint = "明天日更会自动再取，也可以点【执行】手工补";

        if (_aborted)
        {
            int left = _targetCount - _done;
            var why = $"数据源连续 {AbortAfterConsecutiveFailures} 只取不到，本轮停在第 {_done}/{_targetCount} 只"
                    + $"（已写入 {_wrote:N0} 条，还剩 {left} 只没取）；{retryHint}";
            Report($"✘ 财务报表：{why}");
            return new TaskRunResult(TaskState.Failed, [why]);
        }

        // 本轮跑完还剩多少——有 MaxItems 时才可能剩（不设上限之后平时恒为 0）。
        int stillPending = 0;
        try { stillPending = await Task.Run(() => new FinancialFetchPlanner(paths).Plan().AllPending.Count, ct); }
        catch (Exception) { /* 只是提示，查不到就不提 */ }

        int failed = _errors.Count;
        var summary = $"抓取 {_done} 只、写入 {_wrote:N0} 条"
                    + (failed > 0 ? $"，{failed} 只没取到（{retryHint}）" : "")
                    + (_empty > 0 ? $"，{_empty} 只无报表数据" : "")
                    + (_sourceLag > 0 ? $"，{_sourceLag} 只已披露、数据源还没有这一期（明天再取）" : "")
                    + (_sourceLagAlarm > 0
                        ? $"，⚠ {_sourceLagAlarm} 只披露已超过 {FinancialFetchPlanner.SourceLagAlarm.TotalDays:0} 天、数据源仍没有这一期"
                        : "");
        Report($"财务报表完成：{summary}，用时 {Fmt(_sw.Elapsed)}。"
               + (stillPending > 0 ? $"还有 {stillPending} 只没补，下一轮接着来。" : ""));

        var errors = new List<string>(_errors);
        if (_sourceLagAlarm > 0)
            errors.Add($"{_sourceLagAlarm} 只披露已超过 {FinancialFetchPlanner.SourceLagAlarm.TotalDays:0} 天、数据源仍没有这一期");
        // 汇总放进 Progress：计划状态列优先显示它，"N 只没取到"一眼看得见
        return new TaskRunResult(TaskState.Completed, errors, Progress: summary);
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} 小时 {t.Minutes} 分"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒"
        : $"{t.TotalSeconds:F1} 秒";
}
