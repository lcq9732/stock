using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 计划项的**排期判据**（该不该跑 / 跑没跑过 / 什么时候跑）。
///
/// 为什么这块值得单独测：它是纯函数、没有 IO，测起来便宜；而它错了是**静默的**——
/// 界面显示正常、日志没有异常，只有对着"数据日期"和"当前时间"才看得出来。
/// 2026-09-01 一天之内在这里踩了两个坑，都是用户发现的：
///   ① 跨午夜的长任务把第二天整个吃掉（【拉取全部】18:00 开工、次日凌晨收工，
///      LastEnd 落在新的一天 → 新的一天被判"已经跑过"，数据永远停在前一天）；
///   ② 月度任务一次都没跑成（判据写死 day.Day == DayOfMonth，而队列轮到它时
///      已经是 2 号凌晨了 → 静默跳过，下个月重演）。
/// </summary>
public class FetchPlanScheduleTests
{
    private static DateTime D(string s) => DateTime.Parse(s);

    /// <summary>
    /// 造一个"挂在组上"的任务（2026-09-02 分组之后：重复规则和触发时刻都在**组**上，
    /// 任务通过 Owner 读它们）。这些排期判据本身没变，变的只是取值来源。
    /// </summary>
    private static FetchPlanItem InGroup(FetchActionId action, RepeatKind repeat,
        TimeOnly? notBefore = null, int dayOfMonth = 1, DayOfWeek weekday = DayOfWeek.Monday,
        RunPacing pacing = RunPacing.Immediate)
    {
        var g = new FetchPlanGroup
        {
            Name = "测试组", Enabled = true, Repeat = repeat, Pacing = pacing,
            DayOfMonth = dayOfMonth, Weekday = weekday, NotBefore = notBefore,
        };
        var item = new FetchPlanItem { Action = action, Enabled = true, Owner = g };
        g.Items.Add(item);
        return item;
    }

    private static FetchPlanItem Monthly(int dayOfMonth = 1, TimeOnly? notBefore = null)
        => InGroup(FetchActionId.FetchIndustry, RepeatKind.Monthly, notBefore, dayOfMonth);

    // ══ 月度：错过当天要能补跑 ════════════════════════════════════════════

    [Theory]
    [InlineData("2026-09-01", true)]   // 应跑日当天
    [InlineData("2026-09-02", true)]   // 错过了，补跑
    [InlineData("2026-09-15", true)]   // 月中仍待办
    [InlineData("2026-09-30", true)]   // 月末仍待办
    public void 月度_应跑日或之后都算待办(string day, bool expected)
        => Assert.Equal(expected, Monthly().IsDueOn(D(day)));

    [Fact]
    public void 月度_上个月跑过不算本月跑过()
    {
        var it = Monthly(notBefore: new TimeOnly(18, 0));
        it.LastStart = D("2026-08-01 18:05");
        it.LastEnd = D("2026-08-01 18:20");
        it.LastOutcome = RunOutcome.Ok;

        Assert.False(it.AlreadyRanOn(D("2026-09-02")));
    }

    [Fact]
    public void 月度_本期跑过之后本月不再跑_下月又待办()
    {
        var it = Monthly(notBefore: new TimeOnly(18, 0));
        it.LastStart = D("2026-09-01 18:05");
        it.LastEnd = D("2026-09-01 18:20");
        it.LastOutcome = RunOutcome.Ok;

        Assert.True(it.AlreadyRanOn(D("2026-09-02")));
        Assert.True(it.AlreadyRanOn(D("2026-09-20")));
        Assert.True(it.AlreadyRanOn(D("2026-10-01 17:59")));   // 10 月这一期还没到点，仍属 9 月那期
        Assert.False(it.AlreadyRanOn(D("2026-10-01 18:00")));  // 到点了，新一期
    }

    [Fact]
    public void 月度_补跑时看到的应跑时刻已经过去()
    {
        // 引擎据此判断"早该跑了，立刻开跑"，而不是再等一个周期
        var it = Monthly(notBefore: new TimeOnly(18, 0));
        Assert.True(it.DueTimeOn(D("2026-09-02")) < D("2026-09-02"));
    }

    [Theory]
    [InlineData("2026-09-29", false)]  // 9月只有30天，31号落到30号
    [InlineData("2026-09-30", true)]
    [InlineData("2026-10-30", false)]
    [InlineData("2026-10-31", true)]
    public void 月度_31号在小月落到月末(string day, bool expected)
        => Assert.Equal(expected, Monthly(31).IsDueOn(D(day)));

    // ══ 跨午夜：长任务不能吃掉第二天 ══════════════════════════════════════

    [Fact]
    public void 跨午夜_昨天开工今天凌晨收工_今天仍要跑()
    {
        var it = InGroup(FetchActionId.StepStockDayBars, RepeatKind.EveryWorkday, new TimeOnly(18, 0));
        it.LastStart = D("2026-08-31 18:00");    // 周一 18:00 开工
        it.LastEnd = D("2026-09-01 03:16");      // 周二 03:16 收工
        it.LastOutcome = RunOutcome.Ok;

        // 判据要传**当时的时刻**而不是光秃秃一个日期：一轮的归属看「当期锚点」
        //（最近一个已经过去的 18:00），不看自然日。
        Assert.True(it.AlreadyRanOn(D("2026-09-01 03:20")));    // 刚收工那会儿还是同一轮，别再来一遍
        Assert.False(it.AlreadyRanOn(D("2026-09-01 18:00")));   // 到周二 18:00 就是新一轮了，照跑
    }

    [Fact]
    public void 跨午夜_没设不早于也一样()
    {
        var it = InGroup(FetchActionId.StepBoards, RepeatKind.EveryWorkday);
        it.LastStart = D("2026-08-31 20:00");
        it.LastEnd = D("2026-09-01 02:00");
        it.LastOutcome = RunOutcome.Ok;

        Assert.False(it.AlreadyRanOn(D("2026-09-01")));
    }

    [Fact]
    public void 当天开工跑完_当天不再跑_次日照跑()
    {
        var it = InGroup(FetchActionId.StepStockDayBars, RepeatKind.EveryWorkday, new TimeOnly(18, 0));
        it.LastStart = D("2026-09-01 18:05");
        it.LastEnd = D("2026-09-01 23:00");
        it.LastOutcome = RunOutcome.Ok;

        Assert.True(it.AlreadyRanOn(D("2026-09-01 23:30")));    // 当晚跑完了，别再来一轮
        Assert.True(it.AlreadyRanOn(D("2026-09-02 09:00")));    // 次日白天仍属那一轮，还是不跑
        Assert.False(it.AlreadyRanOn(D("2026-09-02 18:00")));   // 次日到点，新一轮
    }

    // ══ 每周：本周内补跑，跨周重置 ════════════════════════════════════════

    private static FetchPlanItem Weekly()
        => InGroup(FetchActionId.FetchShareholder, RepeatKind.Weekly, new TimeOnly(20, 0),
                   weekday: DayOfWeek.Monday);

    [Theory]
    [InlineData("2026-08-31", true)]   // 周一，应跑日
    [InlineData("2026-09-02", true)]   // 周三，补跑
    [InlineData("2026-09-06", true)]   // 周日，本周最后一天仍待办
    public void 每周_应跑日或之后都算待办(string day, bool expected)
        => Assert.Equal(expected, Weekly().IsDueOn(D(day)));

    [Fact]
    public void 每周_本周跑过之后本周不再跑_下周又待办()
    {
        var it = Weekly();
        it.LastStart = D("2026-08-31 20:05");
        it.LastEnd = D("2026-08-31 20:30");
        it.LastOutcome = RunOutcome.Ok;

        Assert.True(it.AlreadyRanOn(D("2026-09-02")));
        // 下周一 20:00 之前仍属上一期（上周跑过了）；到点之后才是新一期
        Assert.True(it.AlreadyRanOn(D("2026-09-07 19:59")));
        Assert.False(it.AlreadyRanOn(D("2026-09-07 20:00")));
    }

    // ══ 失败/跳过按自然日，不跟周期 ═══════════════════════════════════════

    [Fact]
    public void 失败_当天不重试_次日重试而不是等下个月()
    {
        // 月度项要是按周期算失败，失败一次就得等下个月，太狠
        var it = Monthly();
        it.LastStart = D("2026-09-02 10:00");
        it.LastEnd = D("2026-09-02 10:05");
        it.LastOutcome = RunOutcome.Failed;

        Assert.True(it.AlreadyFailedOn(D("2026-09-02")));
        Assert.False(it.AlreadyFailedOn(D("2026-09-03")));
        Assert.False(it.AlreadyRanOn(D("2026-09-03")));   // 失败不算跑过
    }

    [Fact]
    public void 跳过_不算已完成()
    {
        // 前置失败被跳过的，前置后来补上了就该有机会再跑（见 AlreadyRanOn 的注释）
        var it = Monthly();
        it.LastStart = D("2026-09-01 18:00");
        it.LastEnd = D("2026-09-01 18:00");
        it.LastOutcome = RunOutcome.Skipped;

        Assert.True(it.AlreadySkippedOn(D("2026-09-01")));
        Assert.False(it.AlreadyRanOn(D("2026-09-01")));
    }

    // ══ 其它重复类型 ═════════════════════════════════════════════════════

    [Theory]
    [InlineData("2026-09-01", true)]    // 周二
    [InlineData("2026-09-05", false)]   // 周六
    [InlineData("2026-09-06", false)]   // 周日
    public void 每工作日_周末不跑(string day, bool expected)
        => Assert.Equal(expected,
            InGroup(FetchActionId.StepStockDayBars, RepeatKind.EveryWorkday).IsDueOn(D(day)));

    [Fact]
    public void 手动项永远不自动跑()
        => Assert.False(InGroup(FetchActionId.OptimizeDatabase, RepeatKind.Manual).IsDueOn(D("2026-09-01")));

    /// <summary>没挂在任何组上的任务（理论上不该出现）按「手动」处理——绝不自作主张地跑。</summary>
    [Fact]
    public void 没有组的任务不跑()
        => Assert.False(new FetchPlanItem { Action = FetchActionId.StepLhb, Enabled = true }
            .IsDueOn(D("2026-09-01")));

    // ══ 分批补的任务：一轮只做一部分，不算这一期做完 ═══════════════════════

    /// <summary>
    /// 财务报表每轮上限 300 只、全市场要跨几天，所以"跑成功一轮"不等于这一期做完了——
    /// 判据是上一轮有没有报"没什么可做"。判错的后果很实在：算成做完就再也不补，
    /// 全市场 5000 多只永远停在第一批 300 只。
    /// </summary>
    [Fact]
    public void 分批补的任务_还有活干就不算这一期做完()
    {
        var it = InGroup(FetchActionId.FetchFinancials, RepeatKind.Monthly, pacing: RunPacing.WhenIdle);
        it.LastStart = D("2026-09-01 10:00");
        it.LastEnd = D("2026-09-01 11:30");
        it.LastOutcome = RunOutcome.Ok;

        it.LastNothingToDo = false;                        // 这一轮补了 300 只，还欠着
        Assert.False(it.AlreadyRanOn(D("2026-09-02")));

        it.LastNothingToDo = true;                         // 真的补齐了
        Assert.True(it.AlreadyRanOn(D("2026-09-02")));
    }

    /// <summary>
    /// **到点就跑的项，跑完一轮就是跑完了** —— 哪怕它在目录里标着"能分批"。
    ///
    /// 2026-09-02 实测炸过：判据当时只看目录里那个静态标记，于是【个股日K·不复权】
    /// （标着能分批、但当前是增量模式）每轮跑完都判成"还没做完"，而它在日更组是到点就跑、
    /// 没有空闲冷却挡着，主循环立刻又把它挑出来——4 秒一轮无限重跑，日志刷了几百轮。
    /// 「空闲时补」不会这样，它每轮跑完都进 20 分钟冷却。
    /// </summary>
    [Fact]
    public void 到点就跑的项_跑完一轮就算这一期做完()
    {
        var it = InGroup(FetchActionId.StepStockRawBars, RepeatKind.EveryWorkday,
                         new TimeOnly(18, 0), pacing: RunPacing.Immediate);
        it.LastStart = D("2026-09-02 19:36");
        it.LastEnd = D("2026-09-02 20:06");
        it.LastOutcome = RunOutcome.Ok;
        it.LastNothingToDo = false;          // 真抓了活（这正是当时死循环的那个状态）

        Assert.True(it.AlreadyRanOn(D("2026-09-02")), "到点就跑的项没有冷却，判不出'跑过了'就会立刻重跑");
    }

    /// <summary>
    /// 同一个动作换成「空闲时补 + 整段回补」时，"还有活就接着补"才成立——那时有冷却挡着，
    /// 不会变成死循环。
    /// </summary>
    [Fact]
    public void 空闲时补且真分批时_还有活就不算做完()
    {
        var it = InGroup(FetchActionId.StepStockRawBars, RepeatKind.EveryWorkday,
                         pacing: RunPacing.WhenIdle);
        it.Mode = FetchMode.FirstBackfill;   // 整段回补才是真的分批
        it.LastStart = D("2026-09-02 10:00");
        it.LastEnd = D("2026-09-02 10:30");
        it.LastOutcome = RunOutcome.Ok;
        it.LastNothingToDo = false;

        Assert.False(it.AlreadyRanOn(D("2026-09-02")));
    }

    /// <summary>不能分批的任务照常：跑成功一轮就是这一期做完了。</summary>
    [Fact]
    public void 不能分批的任务_跑成功一轮就算做完()
    {
        var it = InGroup(FetchActionId.FetchIndustry, RepeatKind.Monthly, pacing: RunPacing.WhenIdle);
        it.LastStart = D("2026-09-01 10:00");
        it.LastEnd = D("2026-09-01 10:05");
        it.LastOutcome = RunOutcome.Ok;
        it.LastNothingToDo = false;

        Assert.True(it.AlreadyRanOn(D("2026-09-02")));
    }

    /// <summary>「空闲时补」只是执行方式，不改变"这一期该不该跑"——那仍由重复规则说了算。</summary>
    [Fact]
    public void 空闲时补的项_周期判定跟到点就跑的一样()
    {
        var idle = InGroup(FetchActionId.FetchShareholder, RepeatKind.Monthly, pacing: RunPacing.WhenIdle);
        Assert.True(idle.IsDueOn(D("2026-09-02")));        // 1 号过了，本月仍待办
        Assert.Equal(RunPacing.WhenIdle, idle.Pacing);
    }

    // ══ 前置判据也得按轮次算，不能按自然日（2026-09-18）════════════════════
    //
    // 这一组盯的是同一个坑在**前置**上的重演：跨夜的轮次里"今天"会翻篇，
    // 而一轮的归属看的是当期锚点。两个方向都出过事：
    //   · 软前置 → 假警告：日更 18:00 开跑、前置 19:00 跑完，排到凌晨 02:25 的
    //     【当日完整性体检】被判成"前置今天没跑过"，日志挂一句"结果可能偏旧"，
    //     而那轮体检其实 11 项全齐（2026-09-18 真实日志）；
    //   · 硬前置 → 漏拦：前置昨晚 18:30 失败，凌晨排到的项判成"今天没失败过"照跑不误，
    //     可它要的数据压根没抓到。

    /// <summary>
    /// 造一个组、里面按顺序放两项——**前置跟被挡的项必须在同一组**，这正是真实的形状
    /// （日更组里【个股日K】排在【当日完整性体检】前面）。
    ///
    /// ⚠ 分成两个组测不出跨夜那条：<see cref="FetchPlanGroup.DueAnchorAt"/> 往回看昨晚那一轮时，
    /// 要求**这一组已经有项开工过**才认那个锚（没开过工就谈不上"当前在那一轮里"）。
    /// 一个只有被挡项自己的空组，昨晚一项都没跑，自然回 null。
    /// </summary>
    private static (FetchPlanItem Dep, FetchPlanItem Me) Pair(
        FetchActionId dep, FetchActionId me, TimeOnly notBefore)
    {
        var g = new FetchPlanGroup
        {
            Name = "日更", Enabled = true, Repeat = RepeatKind.EveryWorkday, NotBefore = notBefore,
        };
        var d = new FetchPlanItem { Action = dep, Enabled = true, Owner = g };
        var m = new FetchPlanItem { Action = me, Enabled = true, Owner = g };
        g.Items.Add(d);
        g.Items.Add(m);
        return (d, m);
    }

    [Fact]
    public void 跨午夜_前置昨晚跑成功_凌晨的项不该说它没跑过()
    {
        var (dep, me) = Pair(FetchActionId.StepStockDayBars, FetchActionId.StepDayCoverage, new TimeOnly(18, 0));
        dep.LastStart = D("2026-09-17 19:00");               // 昨晚这一轮跑的
        dep.LastEnd = D("2026-09-17 21:30");
        dep.LastOutcome = RunOutcome.Ok;

        var now = D("2026-09-18 02:25");                     // 同一轮，只是跨了午夜

        Assert.Equal(D("2026-09-17 18:00"), me.RoundAnchor(now));
        Assert.True(dep.RanOkSince(me.RoundAnchor(now)));
    }

    [Fact]
    public void 前置停在上一轮_这一轮要报它没跑过()
    {
        var (dep, me) = Pair(FetchActionId.StepStockDayBars, FetchActionId.StepDayCoverage, new TimeOnly(18, 0));
        dep.LastStart = D("2026-09-16 19:00");               // 上一轮跑的
        dep.LastEnd = D("2026-09-16 21:30");
        dep.LastOutcome = RunOutcome.Ok;

        Assert.False(dep.RanOkSince(me.RoundAnchor(D("2026-09-17 23:00"))));
    }

    [Fact]
    public void 跨午夜_硬前置昨晚失败_凌晨的项仍要被拦住()
    {
        var (dep, me) = Pair(FetchActionId.FetchFinancials, FetchActionId.BankRegulatory, new TimeOnly(18, 0));
        dep.LastStart = D("2026-09-17 18:10");
        dep.LastEnd = D("2026-09-17 18:30");
        dep.LastOutcome = RunOutcome.Failed;

        Assert.True(dep.FailedSince(me.RoundAnchor(D("2026-09-18 02:25"))));   // 同一轮，拦
        Assert.False(dep.FailedSince(me.RoundAnchor(D("2026-09-18 18:00"))));  // 新一轮，放行
    }

    [Fact]
    public void 手动项没有轮次_锚点退回当天零点()
    {
        // ⚠ DueAnchorAt 对手动项给的是 DateTime.MinValue，直接拿它当锚的话
        //    "任何时候跑过"都会算成本轮跑过——那比按自然日判还松。
        var manual = InGroup(FetchActionId.StepFullAudit, RepeatKind.Manual);
        var now = D("2026-09-18 02:25");

        Assert.Equal(now.Date, manual.RoundAnchor(now));

        var dep = InGroup(FetchActionId.StepStockDayBars, RepeatKind.Manual);
        dep.LastStart = D("2026-09-01 10:00");
        dep.LastEnd = D("2026-09-01 10:30");
        dep.LastOutcome = RunOutcome.Ok;
        Assert.False(dep.RanOkSince(manual.RoundAnchor(now)));   // 半个月前跑的，不算"这一轮"
    }
}
