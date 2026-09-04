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
    Action<PlanRunnerState> onState,
    Action? onRoundFinished = null,
    TimeSpan? hardBudgetOverride = null)
{
    /// <summary>测试专用：直接指定单项硬超时，跳过 <see cref="HardBudgetFor"/> 的换算。
    /// 生产代码一律不传——真实预算是分钟到小时级，单元测试等不起。</summary>
    private readonly TimeSpan? _hardBudgetOverride = hardBudgetOverride;

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

    // ── 单项硬超时（2026-09-04 新增）────────────────────────────────────────────
    // 缘起：调度循环是串行 await、绝不并发的，所以一项**卡住不返回**（不抛异常，就是不回来）
    // 会把整个循环堵死——不光当天后面的项不跑，第二天的轮次也开不了工，必须有人手动停一次。
    // 实测踩到过：【概念和行业板块】和【板块成分股】卡住后只能人工干预，于是这两项一度
    // 变成"只敢手动触发"。有了这道兜底它们才能放回自动计划。
    //
    // 这跟 deadline 是两回事：deadline 只给空闲项、而且是**建议性**的（靠任务自己收尾），
    // 定时项连这个都没有。这里是**强制**上限，到点就掐。
    //
    // 预算 = 这一项自己的实测中位数 × 4，再夹在 [30分钟, 8小时] 之间：
    //   · ×4 是给正常波动留的余量——抓取慢起来两三倍很常见，卡死是几十倍，两者分得开；
    //   · 下限 30 分钟：Estimate 只有 1 分钟的项（如【概念和行业板块】）×4 才 4 分钟，
    //     太紧了会把"这次网络特别慢"误杀成卡死；
    //   · 上限 8 小时：夜里跑的长任务（财务/资金流本身就要 2~3 小时）也得在早上之前松手，
    //     否则第二天照样开不了工——那就白做了。
    private const int HardBudgetFactor = 4;
    private static readonly TimeSpan MinHardBudget = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaxHardBudget = TimeSpan.FromHours(8);

    private TimeSpan HardBudgetFor(FetchPlanItem item)
    {
        if (_hardBudgetOverride is { } forced) return forced;
        var scaled = item.EffectiveEstimate * HardBudgetFactor;
        if (scaled < MinHardBudget) return MinHardBudget;
        if (scaled > MaxHardBudget) return MaxHardBudget;
        return scaled;
    }

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
                // 今天的定时项都跑完了：打一次汇总，并通知调用方"一轮结束"
                // （2026-09-02 新增 onRoundFinished：调用方拿它排一次自动重试。以前那件事挂在
                //  **每一项**跑完的 finally 里，两三项时无所谓，【拉取全部】拆成 13 项之后
                //  同一天会被重排十几次。）
                if (next == null && _hadWork)
                {
                    LogRoundSummary();
                    _hadWork = false;
                    try { onRoundFinished?.Invoke(); }
                    catch (Exception ex) { log($"（一轮收尾回调出错，不影响计划：{ex.Message}）"); }
                }

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
        // 这里不用区分真假取消：循环体里唯一会抛 OperationCanceledException 的是
        // Task.Delay(..., ct)，而它只在**真的**取消时抛；抓取本身的超时在
        // ExecuteOneAsync 里就被归成"这一项失败"了，传不到这一层。
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
        foreach (var item in plan.AllItems)
        {
            if (!IsDueNow(item, now)) continue;
            int p = TypePriority(item.Repeat);
            if (p < bestPriority) { best = item; bestPriority = p; }
        }
        return best;
    }

    /// <summary>
    /// 这一项现在就该跑：本期该跑、本期没跑过、今天没失败过、「不早于」已经到点，
    /// 而且它是**到点就跑**那一档——「空闲时补」的不进定时队列，走空窗那条路。
    /// </summary>
    private bool IsDueNow(FetchPlanItem item, DateTime now)
        => item.Pacing == RunPacing.Immediate && IsPending(item, now);

    /// <summary>
    /// 这一项这一期还欠着（不管到没到点）。
    /// 「空闲时补」的项也算欠着——只是它们不从 <see cref="FindDue"/> 那条路走，
    /// 而是等 <see cref="FindIdleTask"/> 在空窗里挑（见 <see cref="RunPacing"/>）。
    /// </summary>
    private bool IsPending(FetchPlanItem item, DateTime now)
    {
        if (!item.EffectiveEnabled) return false;
        if (item.Repeat == RepeatKind.Manual) return false;
        // 「当前这一轮」开工了没有。用锚点而不是"今天是不是应跑日"——18:00 那轮跑过午夜之后，
        // 剩下的项问"今天该跑吗"会被 9/3 18:00 挡住，白白推迟一天（2026-09-03 用户发现）。
        if (item.DueAnchorAt(now) is null) return false;
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
        foreach (var item in plan.AllItems)
        {
            // 「空闲时补」的项没有"到点"这回事，不参与"下一个到期时刻"的计算
            if (item.Pacing != RunPacing.Immediate) continue;
            if (!item.EffectiveEnabled || item.Repeat == RepeatKind.Manual) continue;

            // ⚠ 这里问的是**将来**几点到点，不能用 IsPending（那问的是"现在该不该跑"）。
            //    IsPending 要求「当期锚点」存在，而一份全新的计划在 18:00 之前根本没有锚点
            //    ——用它过滤的话，上午十点会答"今天没有要跑的了"，界面上等待时刻是空的，
            //    空闲项还会以为窗口无限长（2026-09-03 引入锚点时踩到）。
            //    这一层该用自然日的视角：今天是不是应跑日、今天的到点有没有过去。
            if (!item.IsDueOn(now)) continue;
            if (item.AlreadyFailedOn(now) || item.AlreadySkippedOn(now)) continue;

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
        foreach (var item in plan.AllItems)
        {
            if (item.Pacing != RunPacing.Immediate) continue;
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

        foreach (var item in plan.AllItems)
        {
            // 只挑「空闲时补」那一档，而且得是**这一期还欠着**的：到期了才补，补完这一期就停
            // （2026-09-02 起用 IsPending 判断；原来的「空闲时」是无脑每天反复跑）。
            if (item.Pacing != RunPacing.WhenIdle) continue;
            if (!IsPending(item, now)) continue;
            if (_idleNextAllowed.TryGetValue(item.Action, out var next) && now < next) continue;
            if (DependencyFailedToday(item)) continue;
            // 装不装得下按**实测**耗时判（EffectiveEstimate：跑过就用自己最近几轮的中位数，
            // 没跑过才用目录里手填的那个值）——拆细之后手填值只是个数量级。
            if (window.HasValue && !item.SupportsPartialRunNow && window.Value < item.EffectiveEstimate) continue;
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
        var depItem = plan.AllItems.FirstOrDefault(i => i.Action == dep);
        return depItem is { LastOutcome: RunOutcome.Failed } && depItem.LastEnd?.Date == DateTime.Today;
    }

    /// <summary>
    /// **软**前置今天还没成功跑过时说一声（2026-09-02 随【拉取全部】拆分新增）。
    ///
    /// 跟硬前置（<see cref="DependencyFailedToday"/>）不同：软前置缺了照样跑，只是结果会旧——
    /// 典型是"个股日K ← 股票名册"：名册没刷新就用库里上次那份名单，当天新上市的票不在里面。
    /// 这种事不该拦住执行，但**必须在日志里留一行**，否则第二天看少了几只票会找不到原因。
    ///
    /// 判据是"今天成功跑过没有"，不是"排没排"：软前置压根没排进计划（比如用户就是不想每天刷名册）
    /// 一样会提示，这正是想要的——提示的是数据新鲜度，不是配置错误。
    /// </summary>
    private void WarnIfSoftDependencyStale(FetchPlanItem item)
    {
        if (item.Info.SoftDependsOn is not { Count: > 0 } softs) return;

        var stale = new List<string>();
        foreach (var soft in softs)
        {
            var softItem = plan.AllItems.FirstOrDefault(i => i.Action == soft);
            bool ranOkToday = softItem is { LastOutcome: RunOutcome.Ok }
                              && (softItem.LastStart ?? softItem.LastEnd)?.Date == DateTime.Today;
            if (!ranOkToday) stale.Add(FetchTaskCatalog.Info(soft).Name);
        }
        if (stale.Count == 0) return;

        log($"　提示：【{item.Info.Name}】的前置【{string.Join("】【", stale)}】今天还没成功跑过，"
          + "这一项会用库里已有的数据继续跑（结果可能偏旧，比如漏掉当天新上市的标的）。");
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

        WarnIfSoftDependencyStale(item);

        item.LastStart = DateTime.Now;
        item.LastEnd = null;
        item.LastOutcome = RunOutcome.None;
        item.LastMessage = null;
        store.Save(plan);
        onState(new PlanRunnerState(true, item, null, null, $"正在执行【{info.Name}】"));
        log($"▶ 计划：开始【{info.Name}】（数据源 {info.DataSource}，预计 {Describe(item.EffectiveEstimate)}"
          + (item.RecentDurationsSec.Count > 0 ? "，按这一项自己最近几轮的实测算" : "") + "）"
          + (deadline.HasValue
                ? $"——空闲补一轮，要在 {deadline:HH:mm} 前收尾（后面有定时任务）"
                : item.Pacing == RunPacing.WhenIdle ? "——空闲补一轮" : ""));

        // 硬超时兜底：到点强制掐断这一项，让计划能自己往下走（见 HardBudgetFor 的说明）。
        // ⚠ CancellationToken 是**协作式**的：任务内部得真的在检查它才掐得动。
        //    卡在网络重试循环、卡在 foreach 里等 ct 的都能掐；要是卡在一个压根不接受 ct 的
        //    同步调用上（比如死等一把 SQLite 写锁），这道兜底也无能为力——那种得从任务内部修。
        var budget = HardBudgetFor(item);
        using var timeoutCts = new CancellationTokenSource(budget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var progress = new Progress<string>(log);
            var result = await execute(item, deadline, progress, linked.Token);
            foreach (var err in result.Errors) log($"错误：{err}");

            // 「根本没开工」不能记成完成（2026-09-04）：界面上会显示成绿勾"09:25 完成"，
            // 可它一行数据都没抓；更要命的是 AlreadyRanOn 只认 Ok，记成完成的话**今天就不再跑了**。
            // 记成 Skipped，等数据源的熔断过去，今天还有机会补上。
            if (result.SkippedReason is { } why)
            {
                Finish(item, RunOutcome.Skipped, result.Errors.Count, why);
                log($"⏸ 计划：【{info.Name}】本轮没开工——{why}。今天恢复之后还会再来。");
                return;
            }

            // 空闲项跑完排下一次：什么都没得做就歇久一点，别每 20 分钟去翻一遍库
            if (item.Pacing == RunPacing.WhenIdle)
                _idleNextAllowed[item.Action] =
                    DateTime.Now + (result.NothingToDo ? IdleNothingToDoCooldown : IdleCooldown);

            // 实测耗时喂给"耗时自学"（2026-09-02）：只记真干了活的轮次，
            // NothingToDo 那种秒回的不记，否则中位数被拉到接近 0，空闲调度会误以为什么都塞得下。
            if (!result.NothingToDo && item.LastStart is { } startedAt)
                item.RecordDuration(DateTime.Now - startedAt);

            // 这一轮到底把这一期做完了没有——分批补的任务（财务报表每轮 300 只）靠它判断
            // 还欠不欠着，见 FetchPlanItem.AlreadyRanOn。
            item.LastNothingToDo = result.NothingToDo;

            int errors = result.Errors.Count;
            // 存量进度优先显示（2026-09-04）：跨轮才做得完的活，"完成"说的只是这一轮，
            // 人真正想知道的是全库攒到什么程度了。
            Finish(item, RunOutcome.Ok, errors,
                result.NothingToDo ? "已经齐了，这一轮没什么可做"
                : result.Progress is { Length: > 0 } prog
                    ? (errors > 0 ? $"{prog}（{errors} 条错误）" : prog)
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
        // ⚠ 只有**真的按了停止**才算取消。HttpClient 超时抛的也是 OperationCanceledException
        //    （TaskCanceledException），要是不看 ct 就一律当成"用户停了"，一次网络抖动
        //    就会把整份计划掀掉——夜里没人看着，后面十几项全不跑了。所以这里认 ct，
        //    伪取消落到下面的 catch (Exception) 里记成"这一项失败"，计划继续往下走。
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Finish(item, RunOutcome.Cancelled, 0, "被手动停止");
            log($"⏹ 计划：【{info.Name}】被停止。");
            throw;
        }
        // 到点被硬超时掐断——算"这一项失败"，计划照常往下走。
        // 放在"用户停止"那一条之后：两者抛的都是 OperationCanceledException，
        // 靠 when 分别认自己的 token，别搞反（认错了会把手动停止当成超时、继续跑下一项）。
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            var msg = $"跑了 {Describe(budget)} 还没结束，已强制掐断（预计只要 {Describe(item.EffectiveEstimate)}）";
            Finish(item, RunOutcome.Failed, 1, msg);
            // 同下面的普通失败：空闲项要进冷却，否则每分钟回来撞同一个坑
            if (item.Pacing == RunPacing.WhenIdle)
                _idleNextAllowed[item.Action] = DateTime.Now + IdleCooldown;
            log($"⏱ 计划：【{info.Name}】{msg}。后面的项继续跑。"
              + "（老是超时说明它真卡住了，去日志里看最后停在哪一步）");
        }
        catch (Exception ex)
        {
            // 一项失败不拖累整份计划——记下来继续下一项，这是无人值守的前提
            Finish(item, RunOutcome.Failed, 1, ex.Message);
            // ⚠ 空闲项失败**也要进冷却**：它不像定时项那样"今天失败就不再来"，
            //    不挡一下就会每分钟重试一次，一直撞同一个错。
            if (item.Pacing == RunPacing.WhenIdle)
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
        var due = plan.AllItems.Where(i => i.EffectiveEnabled && i.IsDueOn(now)).ToList();
        if (due.Count == 0)
        {
            log("今天没有要跑的项（都没勾选，或重复规则不落在今天）。计划会待命，到点自动开始。");
            return;
        }

        // 「今天该跑」不等于「今天还要跑」：已经跑完的、今天失败了等明天的、前置没成而跳过的，
        // 都还留在今天的清单里，但**不会再执行**。不分开的话这份清单会写着"今天要跑 28 项"、
        // 预计加起来十几个小时——2026-09-02 换了新版 exe 接着跑那一晚就是这样，其实一多半
        // 在换版之前就做完了，看着像今晚还有一整夜的活（用户反馈）。
        var 已完成 = due.Where(i => i.AlreadyRanOn(now)).ToHashSet();
        var 已失败 = due.Where(i => !已完成.Contains(i) && i.AlreadyFailedOn(now)).ToHashSet();
        var 已跳过 = due.Where(i => !已完成.Contains(i) && !已失败.Contains(i) && i.AlreadySkippedOn(now)).ToHashSet();
        var 还要跑 = due.Where(i => !已完成.Contains(i) && !已失败.Contains(i) && !已跳过.Contains(i)).ToList();
        var 剩余预计 = 还要跑.Aggregate(TimeSpan.Zero, (sum, i) => sum + i.EffectiveEstimate);

        var head = new StringBuilder($"今天的计划共 {due.Count} 项");
        if (已完成.Count > 0) head.Append($"，已完成 {已完成.Count} 项");
        if (已失败.Count > 0) head.Append($"，失败等明天 {已失败.Count} 项");
        if (已跳过.Count > 0) head.Append($"，跳过 {已跳过.Count} 项");
        head.Append(还要跑.Count > 0
            ? $"，还要跑 {还要跑.Count} 项（串起来预计约 {Describe(剩余预计)}）："
            : "，今天没有还要跑的了：");

        var sb = new StringBuilder(head.ToString());
        foreach (var i in due)
        {
            sb.Append("\n    ");
            if (已完成.Contains(i))
                sb.Append(Pad("✔ 已完成", 10)).Append(i.Info.Name).Append(用时(i));
            else if (已失败.Contains(i))
                sb.Append(Pad("✘ 已失败", 10)).Append(i.Info.Name).Append("（今天不再重试，明天到点自己会来）");
            else if (已跳过.Contains(i))
                sb.Append(Pad("— 已跳过", 10)).Append(i.Info.Name).Append("（前置项没成）");
            else
                sb.Append(Pad(i.NotBefore.HasValue ? i.NotBefore.Value.ToString("HH\\:mm") : "接上一项", 10))
                  .Append(i.Info.Name)
                  .Append($"（{i.Info.DataSource}，预计 {Describe(i.EffectiveEstimate)}）");
        }
        log(sb.ToString());

        static string 用时(FetchPlanItem i)
            => i.LastStart is { } st && i.LastEnd is { } en ? $"（用时 {Describe(en - st)}）" : "";
    }

    /// <summary>
    /// 按**显示宽度**右补空格。不能用 String.Format 的 <c>,-10</c>——它按字符个数算，
    /// 而"接上一项"四个字在等宽终端里占八列，跟 "18:00" 的五列混排就永远对不齐。
    /// </summary>
    private static string Pad(string s, int width)
    {
        int w = s.Sum(c => c is >= (char)0x2E80 and <= (char)0xA4CF     // CJK 部首、汉字、假名
                             or >= (char)0x3000 and <= (char)0x303F     // 中文标点
                             or >= (char)0xFF00 and <= (char)0xFF60 ? 2 : 1);  // 全角
        return s + new string(' ', Math.Max(1, width - w));
    }

    private void LogRoundSummary()
    {
        var today = DateTime.Today;
        var ran = plan.AllItems.Where(i => i.LastEnd?.Date == today).ToList();
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
