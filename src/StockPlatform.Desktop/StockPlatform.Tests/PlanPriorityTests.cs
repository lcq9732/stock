using System.Reflection;
using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// **调度决策**：每一轮该挑哪一项来跑。跟 <see cref="FetchPlanScheduleTests"/> 分工不同——
/// 那边测的是单个任务"该不该跑"，这边测的是一堆任务放一起"先跑谁"。
///
/// 优先级按**频次**排：每工作日 &gt; 仅一次 &gt; 每周 &gt; 每月 &gt; 空闲（2026-09-02 用户定）。
/// 道理是频次越高时效性要求越硬——日更的晚一天就缺一天数据补不回来，
/// 每月 1 号的 5 号才跑，抓到的内容一模一样。
///
/// 这一层原来完全没有测试，而 2026-09-02 用户报的"程序在等【拉取全部】、下面月度任务全不跑"
/// 恰恰出在这里：旧版按表格顺序返回第一个待办项（哪怕它还要等几小时），
/// 后面已经到点的项就全被堵住了。
/// </summary>
public class PlanPriorityTests
{
    private static DateTime D(string s) => DateTime.Parse(s);

    /// <summary>2026-09-02 是周三，工作日。</summary>
    private static readonly DateTime Wed10Am = D("2026-09-02 10:00");

    private static FetchPlanItem Item(
        FetchActionId action, RepeatKind repeat, TimeOnly? notBefore = null, int dayOfMonth = 1)
        => new()
        {
            Action = action,
            Enabled = true,
            Repeat = repeat,
            NotBefore = notBefore,
            DayOfMonth = dayOfMonth,
            Weekday = DayOfWeek.Monday,
        };

    /// <summary>PlanRunner 的挑选逻辑是私有的，用反射调——不值得为了测试把它公开。</summary>
    private static FetchPlanItem? PickDue(FetchPlan plan, DateTime now)
        => (FetchPlanItem?)Invoke(plan, "FindDue", now);

    private static DateTime? NextDue(FetchPlan plan, DateTime now)
        => (DateTime?)Invoke(plan, "NextDueTime", now);

    private static object? Invoke(FetchPlan plan, string method, DateTime now)
    {
        // 只调私有的挑选方法，不会真跑起来——store/paths/execute 给什么都行
        var runner = new PlanRunner(
            plan,
            store: null!,
            paths: null!,
            execute: (_, _, _, _) => Task.FromResult(new StockPlatform.Data.Orchestration.FetchResult()),
            log: _ => { },
            onState: _ => { });
        var m = typeof(PlanRunner).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return m.Invoke(runner, [now]);
    }

    private static FetchPlan PlanOf(params FetchPlanItem[] items)
        => new() { Items = [.. items] };

    // ══ 核心：已经到点的不该被"还没到点的"堵住 ═══════════════════════════

    [Fact]
    public void 队首还没到点_后面已到点的月度项照跑()
    {
        // 用户 2026-09-02 报的原始场景：【拉取全部】不早于 18:00，现在才 10 点；
        // 下面几个每月 1 号的任务早就该跑了，不该陪着一起等。
        var plan = PlanOf(
            Item(FetchActionId.FetchAll, RepeatKind.EveryWorkday, new TimeOnly(18, 0)),
            Item(FetchActionId.FetchIndustry, RepeatKind.Monthly),
            Item(FetchActionId.FetchShareholder, RepeatKind.Monthly));

        var picked = PickDue(plan, Wed10Am);

        Assert.NotNull(picked);
        Assert.Equal(FetchActionId.FetchIndustry, picked!.Action);   // 月度项里排在前面的那个
    }

    [Fact]
    public void 队首到点之后_日更项抢在月度项前面()
    {
        var plan = PlanOf(
            Item(FetchActionId.FetchAll, RepeatKind.EveryWorkday, new TimeOnly(18, 0)),
            Item(FetchActionId.FetchIndustry, RepeatKind.Monthly));

        var picked = PickDue(plan, D("2026-09-02 18:30"));

        Assert.Equal(FetchActionId.FetchAll, picked!.Action);
    }

    // ══ 优先级顺序 ═══════════════════════════════════════════════════════

    [Fact]
    public void 优先级_每工作日最先()
    {
        // 表格顺序故意反着放，验证挑的是优先级而不是位置
        var plan = PlanOf(
            Item(FetchActionId.FetchDividend, RepeatKind.Monthly),
            Item(FetchActionId.FetchShareholder, RepeatKind.Weekly),
            Item(FetchActionId.FetchBoards, RepeatKind.Once),
            Item(FetchActionId.FetchAll, RepeatKind.EveryWorkday));

        Assert.Equal(FetchActionId.FetchAll, PickDue(plan, Wed10Am)!.Action);
    }

    [Fact]
    public void 优先级_没有日更时轮到仅一次()
    {
        var plan = PlanOf(
            Item(FetchActionId.FetchDividend, RepeatKind.Monthly),
            Item(FetchActionId.FetchShareholder, RepeatKind.Weekly),
            Item(FetchActionId.FetchBoards, RepeatKind.Once));

        Assert.Equal(FetchActionId.FetchBoards, PickDue(plan, Wed10Am)!.Action);
    }

    [Fact]
    public void 优先级_每周先于每月()
    {
        var plan = PlanOf(
            Item(FetchActionId.FetchDividend, RepeatKind.Monthly),
            Item(FetchActionId.FetchShareholder, RepeatKind.Weekly));

        Assert.Equal(FetchActionId.FetchShareholder, PickDue(plan, Wed10Am)!.Action);
    }

    [Fact]
    public void 同优先级_按表格排列顺序()
    {
        var plan = PlanOf(
            Item(FetchActionId.FetchIndexCons, RepeatKind.Monthly),
            Item(FetchActionId.FetchIndustry, RepeatKind.Monthly),
            Item(FetchActionId.FetchDividend, RepeatKind.Monthly));

        Assert.Equal(FetchActionId.FetchIndexCons, PickDue(plan, Wed10Am)!.Action);
    }

    // ══ 「不早于」的语义不能被优先级破坏 ═════════════════════════════════

    [Fact]
    public void 高优先级也不能提前跑()
    {
        // 日更项优先级最高，但没到 18:00 就是不能跑——否则「不早于」形同虚设
        var plan = PlanOf(Item(FetchActionId.FetchAll, RepeatKind.EveryWorkday, new TimeOnly(18, 0)));

        Assert.Null(PickDue(plan, Wed10Am));
        Assert.Equal(D("2026-09-02 18:00"), NextDue(plan, Wed10Am));
    }

    [Fact]
    public void 下一个到点时刻取最早的那个()
    {
        var plan = PlanOf(
            Item(FetchActionId.FetchAll, RepeatKind.EveryWorkday, new TimeOnly(22, 0)),
            Item(FetchActionId.RetryFailed, RepeatKind.EveryWorkday, new TimeOnly(19, 0)));

        Assert.Equal(D("2026-09-02 19:00"), NextDue(plan, Wed10Am));
    }

    [Fact]
    public void 都跑完了就没有下一个到点时刻()
    {
        var it = Item(FetchActionId.FetchAll, RepeatKind.EveryWorkday, new TimeOnly(9, 0));
        it.LastStart = D("2026-09-02 09:05");
        it.LastEnd = D("2026-09-02 09:30");
        it.LastOutcome = RunOutcome.Ok;

        Assert.Null(PickDue(PlanOf(it), Wed10Am));
        Assert.Null(NextDue(PlanOf(it), Wed10Am));
    }

    // ══ 不参与自动调度的 ═════════════════════════════════════════════════

    [Fact]
    public void 空闲项和手动项不进定时调度()
    {
        // 空闲项走 FindIdleTask 那条路（填空窗、受 deadline 约束），手动项压根不自动跑
        var plan = PlanOf(
            Item(FetchActionId.FetchFinancials, RepeatKind.WhenIdle),
            Item(FetchActionId.FetchDay, RepeatKind.Manual));

        Assert.Null(PickDue(plan, Wed10Am));
        Assert.Null(NextDue(plan, Wed10Am));
    }

    [Fact]
    public void 没勾选的不跑()
    {
        var it = Item(FetchActionId.FetchAll, RepeatKind.EveryWorkday);
        it.Enabled = false;

        Assert.Null(PickDue(PlanOf(it), Wed10Am));
    }

    [Fact]
    public void 今天失败过的不再自动重来()
    {
        var it = Item(FetchActionId.FetchAll, RepeatKind.EveryWorkday);
        it.LastStart = D("2026-09-02 09:00");
        it.LastEnd = D("2026-09-02 09:10");
        it.LastOutcome = RunOutcome.Failed;

        Assert.Null(PickDue(PlanOf(it), Wed10Am));
    }

    [Fact]
    public void 周末_日更项让位给月度项()
    {
        // 2026-09-05 是周六：日更项不该跑，月度项照跑
        var plan = PlanOf(
            Item(FetchActionId.FetchAll, RepeatKind.EveryWorkday),
            Item(FetchActionId.FetchIndustry, RepeatKind.Monthly));

        Assert.Equal(FetchActionId.FetchIndustry, PickDue(plan, D("2026-09-05 10:00"))!.Action);
    }
}
