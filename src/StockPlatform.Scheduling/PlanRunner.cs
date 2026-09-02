using System.IO;
using System.Text;
using StockPlatform.Data.Orchestration;

namespace StockPlatform.Scheduling;

/// <summary>计划执行到哪一步了——界面顶部那行状态。</summary>
public sealed record PlanRunnerState(
    bool Running,
    FetchPlanItem? Current,
    /// <summary>正在等某项到点时，等的是哪一项、等到几点。</summary>
    FetchPlanItem? Waiting,
    DateTime? WaitUntil,
    string Text);

/// <summary>
/// 计划执行引擎（2026-08-31 新增）——按顺序、按时间把计划里的任务一项项跑掉，人不用守着。
///
/// ════ 为什么是严格串行 ════
/// 任务之间共用限流器、SQLite 写锁和界面状态；更要命的是**大部分任务打的是同一家服务器**
/// （新浪：股东/分红/指数成分/龙虎/资金流/财务/板块）。限流器各自独立**不等于**配额独立——
/// 财务报表接口按 3并发/1秒 跑到 100 多个请求就被返回 HTTP 456、整轮 350 个请求零成功
/// （见 App.xaml.cs 里 financialProvider 那段）。所以并行只会更快撞墙，收益（十几分钟）
/// 远小于风险（整轮报废 + 封 40 分钟）。2026-08-31 评估后明确不做并行。
///
/// ════ 时间语义：不早于，不是准时 ════
/// 每项的 NotBefore 是"不早于这个点才开始"。上一项超时的话后面顺延，绝不抢跑、绝不并发。
///
/// ════ 主循环为什么是"每分钟重新评估"而不是"算好整条时间轴再依次等" ════
/// 因为计划是可以边跑边改的（把某项停掉、改个时间、加一项）。每次只决定"下一步做什么"、
/// 最多等一分钟就重新评估，改动一分钟内生效；而算死一整条时间轴就得处理各种失效重排。
/// 空转的代价只是每分钟一次纯内存判断。
///
/// ════ 跑完不退出 ════
/// 一轮跑完继续待命（不打日志），到明天该跑的时刻自然接着跑。这样"程序启动后自动开始"
/// 加上程序常开，就是真正的无人值守。要停就点【停止】。
/// </summary>
public sealed class PlanRunner(
    FetchPlan plan,
    FetchPlanStore store,
    FetchPaths paths,
    Func<FetchPlanItem, DateTime?, IProgress<string>, CancellationToken, Task<FetchResult>> execute,
    Action<string> log,
    Action<PlanRunnerState> onState)
{
    /// <summary>没有可跑的项时的重扫间隔——也是"改了计划多久生效"的上限。</summary>
    private static readonly TimeSpan Reevaluate = TimeSpan.FromMinutes(1);

    // ── 「空闲时」那类项的节奏（原来是【手动】页那个复选框里的常量，2026-08-31 搬进来）──

    /// <summary>两轮之间至少隔这么久。财务报表一轮 300 只约 90 分钟，跑完歇一会儿再来，
    /// 给数据源的配额留恢复余量。</summary>
    private static readonly TimeSpan IdleCooldown = TimeSpan.FromMinutes(20);

    /// <summary>这一轮"本来就没什么可做"（NothingToDo）之后歇更久——没必要每 20 分钟就去查一遍几 GB 的库。</summary>
    private static readonly TimeSpan IdleNothingToDoCooldown = TimeSpan.FromHours(3);

    /// <summary>要给下一个定时项留的安全余量：空闲项必须在它开始前这么久收尾。</summary>
    private static readonly TimeSpan IdleSafetyMargin = TimeSpan.FromMinutes(5);

    /// <summary>空窗短于这个数就不塞空闲项了——刚热身完就得收尾，不值当。</summary>
    private static readonly TimeSpan IdleMinWindow = TimeSpan.FromMinutes(10);

    /// <summary>空闲项各自的下一次可跑时刻（跑完 + 冷却）。只活在内存里，重启后重新开始。</summary>
    private readonly Dictionary<FetchActionId, DateTime> _idleNextAllowed = [];

    /// <summary>上一次评估时有没有可跑的项——用来判断"一轮刚跑完"，只在那一刻打一次汇总。</summary>
    private bool _hadWork;

    public async Task RunAsync(CancellationToken ct)
    {
        log("===== 开始执行计划 =====");
        LogTodayPlan();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = DateTime.Now;

                // ① 有没有**已经到点**的定时项？有就跑，跑多久算多久，后面顺延。
                var due = FindDue(now);
                if (due != null)
                {
                    _hadWork = true;
                    await ExecuteOneAsync(due, null, ct);
                    continue;
                }

                // ② 没有 —— 下一个到点时刻是什么时候（可能今天已经没有了）
                var next = NextDueTime(now);
                if (next == null && _hadWork) { LogRoundSummary(); _hadWork = false; }

                // ③ 空窗交给「空闲时」那类项。有下一个到点时刻就必须在它之前收尾。
                DateTime? deadline = next is { } t ? t - IdleSafetyMargin : null;
                var filler = FindIdleTask(now, deadline);
                if (filler != null) { await ExecuteOneAsync(filler, deadline, ct); continue; }

                // ④ 没得跑，睡一会儿再看。最多睡一分钟，这样中途改计划能很快生效。
                if (next is { } dueAt)
                {
                    var waiting = FindWaitingItem(now, dueAt);
                    var wait = dueAt - now;
                    onState(new PlanRunnerState(true, null, waiting, dueAt,
                        waiting is null
                            ? $"等 {dueAt:HH:mm}"
                            : $"等 {dueAt:HH:mm} 开始【{waiting.Info.Name}】（还有约 {Math.Ceiling(wait.TotalMinutes)} 分钟）"));
                    await Task.Delay(wait < Reevaluate ? wait : Reevaluate, ct);
                }
                else
                {
                    onState(new PlanRunnerState(true, null, null, null,
                        "计划已在待命——今天没有待执行的项了，到点会自动接着跑"));
                    await Task.Delay(Reevaluate, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            log("===== 计划执行已停止（手动） =====");
            throw;
        }
        finally
        {
            onState(new PlanRunnerState(false, null, null, null, ""));
        }
    }

    /// <summary>
    /// 按**频次优先级**排的顺序：数字越小越优先。
    ///
    /// 为什么按频次排（2026-09-02 用户定的）：频次越高的，时效性要求越硬。
    /// 【每工作日】的晚一天就缺一天数据，补不回来；【每月】的 1 号该跑、5 号才跑，
    /// 抓到的内容一模一样。所以让日更的插队，低频的让路——反正它们晚点没损失。
    ///
    /// 【仅一次】排第二：用户特意设成一次性的，多半是临时想尽快跑一趟。
    /// </summary>
    private static int TypePriority(RepeatKind kind) => kind switch
    {
        RepeatKind.EveryWorkday => 0,
        RepeatKind.Once => 1,
        RepeatKind.Weekly => 2,
        RepeatKind.Monthly => 3,
        _ => int.MaxValue,          // 手动/空闲不走这条路
    };

    /// <summary>
    /// 挑当下**已经该跑**的那一项：先按频次优先级分档，同档内按**表格里的排列顺序**。
    ///
    /// ⚠ 只返回「不早于」已经到点的。没到点的一律不返回——这是跟旧版最大的区别。
    /// 旧版返回的是"按表格顺序的第一个待办项"，哪怕它还要等好几个小时；主循环拿到它就只能干等，
    /// 后面那些**明明已经到点**的项全被堵住（2026-09-02 用户反馈：程序在等【拉取全部】到 18:00，
    /// 下面几个每月 1 号的任务就一直不跑）。
    ///
    /// 现在的模型是：**这不是一条队列，是一组各自到期的任务**；优先级和排列顺序只用来决定
    /// "同时到期时谁先跑"。谁到点了谁就有资格跑，不用陪着别人等。
    /// </summary>
    private FetchPlanItem? FindDue(DateTime now)
    {
        FetchPlanItem? best = null;
        int bestPriority = int.MaxValue;

        // 顺着表格从上往下走：同优先级时先遇到的胜出，所以天然就是"档内按排列顺序"
        foreach (var item in plan.Items)
        {
            if (!IsDueNow(item, now)) continue;
            int p = TypePriority(item.Repeat);
            if (p < bestPriority) { best = item; bestPriority = p; }
        }
        return best;
    }

    /// <summary>这一项现在就该跑：本期该跑、本期没跑过、今天没失败过，而且「不早于」已经到点。</summary>
    private bool IsDueNow(FetchPlanItem item, DateTime now)
        => IsPending(item, now) && item.DueTimeOn(now) <= now;

    /// <summary>这一项这一期还欠着（不管到没到点）。</summary>
    private bool IsPending(FetchPlanItem item, DateTime now)
    {
        if (!item.Enabled) return false;
        if (item.Repeat is RepeatKind.WhenIdle or RepeatKind.Manual) return false;
        if (!item.IsDueOn(now)) return false;
        if (item.AlreadyRanOn(now)) return false;
        // 失败过的今天不再自动重来——同一个错误连着撞几十次没有意义，而且会卡住后面所有项。
        // 要重试就手动触发那一项，或者靠后台自动重试（它有自己的时机和停止条件）。
        if (item.AlreadyFailedOn(now)) return false;
        // 前置今天失败了：第一次放过去让 ExecuteOneAsync 记一条"跳过"并说明原因，之后就静默
        // 掠过——否则每分钟一轮评估就会往报告里刷一行。前置后来补跑成功的话，这里自然放行。
        if (DependencyFailedToday(item) && item.AlreadySkippedOn(now)) return false;
        return true;
    }

    /// <summary>
    /// 还欠着、但**要等到将来某个时刻**才能跑的项里，最早的那个到点时刻。没有就返回 null。
    /// 用来决定空窗有多长、以及该睡多久。
    /// </summary>
    private DateTime? NextDueTime(DateTime now)
    {
        DateTime? best = null;
        foreach (var item in plan.Items)
        {
            if (!IsPending(item, now)) continue;
            var due = item.DueTimeOn(now);
            if (due > now && (best is null || due < best)) best = due;
        }
        return best;
    }

    /// <summary>界面上"在等谁"要显示的那一项：等到 <paramref name="dueAt"/> 那一刻能跑的、优先级最高的。</summary>
    private FetchPlanItem? FindWaitingItem(DateTime now, DateTime dueAt)
    {
        FetchPlanItem? best = null;
        int bestPriority = int.MaxValue;
        foreach (var item in plan.Items)
        {
            if (!IsPending(item, now) || item.DueTimeOn(now) != dueAt) continue;
            int p = TypePriority(item.Repeat);
            if (p < bestPriority) { best = item; bestPriority = p; }
        }
        return best;
    }

    /// <summary>
    /// 挑一个「空闲时」的项来填当下这段空闲。挑不到就返回 null。
    ///
    /// <paramref name="deadline"/> 是"最晚必须结束的时刻"：还有定时项在等就传它的开始时刻
    /// 减去安全余量，今天没有了就传 null（爱跑多久跑多久）。
    ///
    /// ⚠ 只挑空闲项。定时项不从这儿走——它们到点了自然会被 <see cref="FindDue"/> 挑中，
    /// 而且**跑多久算多久、后面顺延**，不受 deadline 约束。这是两类任务的根本区别：
    /// 定时项是"到点必须做的事"，空闲项是"有空才做的填充"。
    ///
    /// 三道门槛，都是原来那个"空闲时自动补财务"里验证过的经验：
    ///   ① **冷却**——上一轮跑完要歇一会儿（NothingToDo 的歇更久），别连着撞数据源配额；
    ///   ② **窗口够不够**——太短就不开工，刚热身就得收尾不值当；
    ///   ③ **装不装得下**——能分轮跑的（财务报表）只要窗口过了最低门槛就行，剩多少做多少；
    ///      不能分轮的必须窗口 ≥ 它的预计耗时，免得跑到一半被定时任务打断。
    /// </summary>
    private FetchPlanItem? FindIdleTask(DateTime now, DateTime? deadline)
    {
        TimeSpan? window = deadline.HasValue ? deadline.Value - now : null;
        if (window.HasValue && window.Value < IdleMinWindow) return null;

        foreach (var item in plan.Items)
        {
            if (!item.Enabled || item.Repeat != RepeatKind.WhenIdle) continue;
            if (_idleNextAllowed.TryGetValue(item.Action, out var next) && now < next) continue;
            if (DependencyFailedToday(item)) continue;
            if (window.HasValue && !item.Info.SupportsPartialRun && window.Value < item.Info.Estimate) continue;
            return item;
        }
        return null;
    }

    /// <summary>
    /// 前置动作**今天跑过而且失败了**。今天压根没跑（没勾选、或重复规则不落在今天）不算失败——
    /// 那多半是刻意的：财务报表交给【空闲时自动补财务】在后台补，监管指标照样能用已有的数据跑。
    /// </summary>
    private bool DependencyFailedToday(FetchPlanItem item)
    {
        if (item.Info.DependsOn is not { } dep) return false;
        var depItem = plan.Items.FirstOrDefault(i => i.Action == dep);
        return depItem is { LastOutcome: RunOutcome.Failed } && depItem.LastEnd?.Date == DateTime.Today;
    }

    private async Task ExecuteOneAsync(FetchPlanItem item, DateTime? deadline, CancellationToken ct)
    {
        var info = item.Info;

        if (DependencyFailedToday(item))
        {
            var depName = FetchTaskCatalog.Info(info.DependsOn!.Value).Name;
            Finish(item, RunOutcome.Skipped, 0,
                $"跳过：前置的【{depName}】今天失败了，现在跑也取不到要的数");
            log($"⏭ 跳过【{info.Name}】——前置的【{depName}】今天失败了。"
              + $"（前置补跑成功之后，这一项今天还会再有机会。）");
            return;
        }

        item.LastStart = DateTime.Now;
        item.LastEnd = null;
        item.LastOutcome = RunOutcome.None;
        item.LastMessage = null;
        store.Save(plan);
        onState(new PlanRunnerState(true, item, null, null, $"正在执行【{info.Name}】"));
        log($"▶ 计划：开始【{info.Name}】（数据源 {info.DataSource}，预计 {Describe(info.Estimate)}）"
          + (deadline.HasValue
                ? $"——空闲补一轮，要在 {deadline:HH:mm} 前收尾（后面有定时任务）"
                : item.Repeat == RepeatKind.WhenIdle ? "——空闲补一轮" : ""));

        try
        {
            var progress = new Progress<string>(log);
            var result = await execute(item, deadline, progress, ct);
            foreach (var err in result.Errors) log($"错误：{err}");

            // 空闲项跑完排下一次：什么都没得做就歇久一点，别每 20 分钟去翻一遍库
            if (item.Repeat == RepeatKind.WhenIdle)
                _idleNextAllowed[item.Action] =
                    DateTime.Now + (result.NothingToDo ? IdleNothingToDoCooldown : IdleCooldown);

            int errors = result.Errors.Count;
            Finish(item, RunOutcome.Ok, errors,
                result.NothingToDo ? "已经齐了，这一轮没什么可做"
                : errors > 0 ? $"完成，但有 {errors} 条错误" : "完成");
            log($"✔ 计划：【{info.Name}】完成，用时 {Describe(item.LastEnd!.Value - item.LastStart!.Value)}"
              + (errors > 0 ? $"（{errors} 条错误，详见上面的日志）" : ""));

            // "仅一次"的跑成功就自动取消勾选，免得明天又来一遍
            if (item.Repeat == RepeatKind.Once)
            {
                item.Enabled = false;
                log($"　【{info.Name}】设的是「仅一次」，已自动取消勾选。");
                store.Save(plan);
            }
        }
        catch (OperationCanceledException)
        {
            Finish(item, RunOutcome.Cancelled, 0, "被手动停止");
            log($"⏹ 计划：【{info.Name}】被停止。");
            throw;
        }
        catch (Exception ex)
        {
            // 一项失败不拖累整份计划——记下来继续下一项，这是无人值守的前提
            Finish(item, RunOutcome.Failed, 1, ex.Message);
            // ⚠ 空闲项失败**也要进冷却**：它不像定时项那样"今天失败就不再来"，
            //    不挡一下就会每分钟重试一次，一直撞同一个错。
            if (item.Repeat == RepeatKind.WhenIdle)
                _idleNextAllowed[item.Action] = DateTime.Now + IdleCooldown;
            log($"✘ 计划：【{info.Name}】失败：{ex.Message}（不影响后面的项，继续）");
        }
    }

    private void Finish(FetchPlanItem item, RunOutcome outcome, int errorCount, string message)
    {
        item.LastEnd = DateTime.Now;
        item.LastOutcome = outcome;
        item.LastErrorCount = errorCount;
        item.LastMessage = message;
        store.Save(plan);
        AppendReport(item, message);
    }

    /// <summary>
    /// 往当日报告里追加一行。无人值守跑完，第二天早上看这一份就够，不用在几千行运行日志里翻。
    /// 写失败不影响执行。
    /// </summary>
    private void AppendReport(FetchPlanItem item, string message)
    {
        try
        {
            Directory.CreateDirectory(paths.LogArchiveDir);
            var mark = item.LastOutcome switch
            {
                RunOutcome.Ok => "✔",
                RunOutcome.Failed => "✘",
                RunOutcome.Skipped => "⏭",
                RunOutcome.Cancelled => "⏹",
                _ => "·",
            };
            // 被跳过的项没有开始时刻（压根没执行），这时两边都用结束时刻，别在报告里留个空档
            var start = item.LastStart ?? item.LastEnd;
            var span = item.LastStart.HasValue && item.LastEnd.HasValue
                ? Describe(item.LastEnd.Value - item.LastStart.Value) : "—";
            var line = $"{start:HH:mm} → {item.LastEnd:HH:mm}  {mark} {item.Info.Name,-14} "
                     + $"{span,-10} {message}";
            File.AppendAllText(paths.PlanReportPath(DateTime.Today), line + Environment.NewLine,
                new UTF8Encoding(true));
        }
        catch
        {
            // 报告只是方便复盘，写不下来不值得中断计划
        }
    }

    private void LogTodayPlan()
    {
        var now = DateTime.Now;
        var due = plan.Items.Where(i => i.Enabled && i.IsDueOn(now)).ToList();
        if (due.Count == 0)
        {
            log("今天没有要跑的项（都没勾选，或重复规则不落在今天）。计划会待命，到点自动开始。");
            return;
        }
        var sb = new StringBuilder($"今天要跑 {due.Count} 项：");
        foreach (var i in due)
            sb.Append($"\n    {(i.NotBefore.HasValue ? $"{i.NotBefore:HH\\:mm}" : "接上一项"),-8} "
                    + $"{i.Info.Name}（{i.Info.DataSource}，预计 {Describe(i.Info.Estimate)}）");
        log(sb.ToString());
    }

    private void LogRoundSummary()
    {
        var today = DateTime.Today;
        var ran = plan.Items.Where(i => i.LastEnd?.Date == today).ToList();
        if (ran.Count == 0) return;
        int ok = ran.Count(i => i.LastOutcome == RunOutcome.Ok);
        int failed = ran.Count(i => i.LastOutcome == RunOutcome.Failed);
        int skipped = ran.Count(i => i.LastOutcome == RunOutcome.Skipped);
        log($"===== 今天的计划跑完了：成功 {ok} 项"
          + (failed > 0 ? $"、失败 {failed} 项" : "")
          + (skipped > 0 ? $"、跳过 {skipped} 项" : "")
          + $"。明细见 {paths.PlanReportPath(today)} =====");
    }

    private static string Describe(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}小时{t.Minutes}分"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}分钟"
        : $"{(int)t.TotalSeconds}秒";
}
