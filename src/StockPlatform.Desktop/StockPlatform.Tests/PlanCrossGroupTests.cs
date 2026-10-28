using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 同一个任务排在多个组里（2026-10-27，见 doc/plan-cross-group-duplicate-design.md）。
///
/// ════ 要守住的模型 ════
/// **调度只管什么时候跑，任务只管做自己的事。**
/// 一个任务放在哪个组，就按那个组的时间跑；要在不同时间跑两轮，就在两个组里各放一份。
/// 组的时间 = 这一份的执行时间，没有例外、没有隐式补充。
///
/// 这之前是两个毛病：① 同一动作不能出现在两个组（模板会把别组那份搬走）；
/// ② 「每工作日」的组会被**隐式**补一个 17:00 到点——「早间（开盘前）」组设的 08:15，
/// 实际每天跑两轮，而计划界面上看不出来（2026-10-26 的日志里那句
/// 「17:01:33 ▶ 开始【回购公告进展】」就是它）。
/// </summary>
public class PlanCrossGroupTests
{
    private static readonly DateTime 周五 = new(2026, 9, 4);

    private static FetchPlanGroup 组(string name, TimeOnly 不早于) => new()
    {
        Name = name, Enabled = true, Pacing = RunPacing.Immediate,
        Repeat = RepeatKind.EveryWorkday, NotBefore = 不早于,
    };

    private static FetchPlanItem 排一份(FetchPlanGroup g, FetchActionId action, DateTime? 上轮开始 = null)
    {
        var it = new FetchPlanItem { Action = action, Enabled = true, Owner = g };
        if (上轮开始 is { } s)
        {
            it.LastOutcome = RunOutcome.Ok;
            it.LastNothingToDo = true;
            it.LastStart = s;
            it.LastEnd = s.AddMinutes(10);
        }
        g.Items.Add(it);
        return it;
    }

    // ══════════ 一、两份各按自己组的时间走 ══════════

    /// <summary>
    /// 这就是【回购公告进展】要的样子：早间组 08:15 一份、日更组 17:30 一份，
    /// 早上那份跑完之后，**晚上那份照样该跑**——它们各记各的。
    /// </summary>
    [Fact]
    public void 两个组各一份_早上跑完不影响晚上那份()
    {
        var 早间 = 组("早间（开盘前）", new TimeOnly(8, 15));
        var 日更 = 组("每日收盘后", new TimeOnly(17, 30));
        var 早 = 排一份(早间, FetchActionId.StepPlanWatch, 周五.AddHours(8).AddMinutes(20));
        var 晚 = 排一份(日更, FetchActionId.StepPlanWatch);

        var 晚上十八点 = 周五.AddHours(18);
        Assert.True(早.AlreadyRanOn(晚上十八点));    // 早上那份：今天跑过了
        Assert.False(晚.AlreadyRanOn(晚上十八点));   // 晚上那份：还欠着，该跑
    }

    /// <summary>反过来：晚上那份跑了，第二天早上那份照样要跑。</summary>
    [Fact]
    public void 晚上跑完_第二天早上那份照样要跑()
    {
        var 早间 = 组("早间（开盘前）", new TimeOnly(8, 15));
        var 日更 = 组("每日收盘后", new TimeOnly(17, 30));
        var 早 = 排一份(早间, FetchActionId.StepPlanWatch, 周五.AddHours(8).AddMinutes(20));
        排一份(日更, FetchActionId.StepPlanWatch, 周五.AddHours(17).AddMinutes(40));

        var 下周一上午九点 = 周五.AddDays(3).AddHours(9);
        Assert.False(早.AlreadyRanOn(下周一上午九点));   // 新的一天，早上那份又欠着了
    }

    // ══════════ 二、设定时刻之外不再多出到点 ══════════

    /// <summary>
    /// 退役掉的那条规则：08:15 的组不会再在 17:00 长出第二个到点。
    /// 这是"组的时间 = 执行时间"的核心保证。
    /// </summary>
    [Fact]
    public void 早于收盘的组_不再被补一个收盘到点()
    {
        var g = 组("早间（开盘前）", new TimeOnly(8, 15));
        var it = 排一份(g, FetchActionId.StepPlanWatch, 周五.AddHours(8).AddMinutes(20));

        // 锚点一整天都停在 08:15，不会在 17:00 前移
        Assert.Equal(周五.AddHours(8).AddMinutes(15), it.DueAnchorAt(周五.AddHours(10)));
        Assert.Equal(周五.AddHours(8).AddMinutes(15), it.DueAnchorAt(周五.AddHours(17).AddMinutes(5)));
        Assert.True(it.AlreadyRanOn(周五.AddHours(17).AddMinutes(5)));
    }

    // ══════════ 三、模板不再偷走别组那份 ══════════

    /// <summary>
    /// 铺模板时，别的组里已经有同一个任务**不再被搬走**——那是人自己的安排。
    /// 模板只管把自己这一组配齐。
    /// </summary>
    [Fact]
    public void 铺模板_不搬走别组那份()
    {
        var plan = new FetchPlan();
        plan.Normalize();

        // Normalize 会把全部动作补进各自的默认组，先把日更组里那份拿掉，
        // 造出"只有自建组排了它"的局面——那正是会被老实现搬走的情形。
        var daily0 = plan.GroupOf(PlanGroupKind.Daily);
        foreach (var dup in daily0.Items.Where(i => i.Action == FetchActionId.FetchEarningsSchedule).ToList())
            daily0.Items.Remove(dup);

        var mine = 组("早间（开盘前）", new TimeOnly(8, 15));
        var keep = 排一份(mine, FetchActionId.FetchEarningsSchedule);
        plan.Groups.Add(mine);
        plan.LinkOwners();

        var (group, items, alsoElsewhere) = FetchPlanTemplates.Apply(plan, FetchPlanTemplates.Daily);

        // 自建组那份原地没动
        Assert.Contains(keep, mine.Items);
        Assert.Same(mine, keep.Owner);
        // 日更组里也配上了一份
        Assert.Contains(items, i => i.Action == FetchActionId.FetchEarningsSchedule);
        Assert.Same(group, items.First(i => i.Action == FetchActionId.FetchEarningsSchedule).Owner);
        // 并且报了一句"别处也有"
        Assert.Contains(FetchTaskCatalog.Info(FetchActionId.FetchEarningsSchedule).Name, alsoElsewhere);
    }

    /// <summary>反复铺同一个模板仍然安全：同组里不会出现第二份。</summary>
    [Fact]
    public void 反复铺模板_同组不会重复()
    {
        var plan = new FetchPlan();
        plan.Normalize();
        FetchPlanTemplates.Apply(plan, FetchPlanTemplates.Daily);
        var (group, _, _) = FetchPlanTemplates.Apply(plan, FetchPlanTemplates.Daily);

        foreach (var byAction in group.Items.GroupBy(i => i.Action))
            Assert.Single(byAction);
    }

    // ══════════ 四、迁移：老计划行为不变 ══════════

    /// <summary>
    /// 收盘到点退役之后，原先靠它每天跑两轮的项要在日更组补一份，否则就是**静默少跑一轮**。
    /// </summary>
    [Fact]
    public void 迁移_早于收盘的组里那些项_在日更组补一份()
    {
        var plan = new FetchPlan();
        plan.Normalize();
        var 早间 = 组("早间（开盘前）", new TimeOnly(8, 15));
        排一份(早间, FetchActionId.StepPlanWatch);
        plan.Groups.Add(早间);
        plan.LinkOwners();

        var daily = plan.GroupOf(PlanGroupKind.Daily);
        // 日更组里本来没有（Normalize 会把新动作补进默认组，所以先确认它此刻在不在）
        var 本来有 = daily.Items.Any(i => i.Action == FetchActionId.StepPlanWatch);

        var notes = plan.MigrateRetired();

        Assert.True(daily.Items.Any(i => i.Action == FetchActionId.StepPlanWatch));
        // 早间组那份原样留着
        Assert.True(早间.Items.Any(i => i.Action == FetchActionId.StepPlanWatch));
        if (!本来有)
            Assert.Contains(notes, n => n.Contains("补了一份"));
    }

    /// <summary>没启用的项不补——它本来就没在跑，补了只会凭空多出一行。</summary>
    [Fact]
    public void 迁移_没启用的不补()
    {
        var plan = new FetchPlan();
        plan.Normalize();
        var daily = plan.GroupOf(PlanGroupKind.Daily);
        foreach (var dup in daily.Items.Where(i => i.Action == FetchActionId.StepPlanWatch).ToList())
            daily.Items.Remove(dup);

        var 早间 = 组("早间（开盘前）", new TimeOnly(8, 15));
        var it = 排一份(早间, FetchActionId.StepPlanWatch);
        it.Enabled = false;
        plan.Groups.Add(早间);
        plan.LinkOwners();

        plan.MigrateRetired();

        Assert.DoesNotContain(daily.Items, i => i.Action == FetchActionId.StepPlanWatch);
    }

    /// <summary>
    /// 「本轮没开工」的记录是**每份各记各的**（2026-10-27 实机发现）。
    ///
    /// 执行器里那两张表（_deferred / _idleNextAllowed）原来按**动作**作键，跨组重复之后两份会串：
    /// 早间组那份因为东财限流记了「没开工」，日更组那份会被一并判成要等 15 分钟冷却；
    /// 反过来任一份跑成功又会把另一份的记录抹掉、让它不等冷却就重试。
    ///
    /// 这里用 <see cref="FetchPlanItem"/> 当键来钉住这件事：两份是不同对象，互不相等。
    /// 判据本身在 PlanRunner 内部（私有），所以这条测试守的是**前提**——
    /// 同一动作的两份必须是两个不同的实例，谁也不能把它们合并成一个。
    /// </summary>
    [Fact]
    public void 同一动作的两份_是互不相等的两个实例()
    {
        var 早间 = 组("早间（开盘前）", new TimeOnly(8, 15));
        var 日更 = 组("每日收盘后", new TimeOnly(17, 30));
        var a = 排一份(早间, FetchActionId.StepPlanWatch);
        var b = 排一份(日更, FetchActionId.StepPlanWatch);

        Assert.NotSame(a, b);
        Assert.NotEqual(a, b);                       // 默认引用相等，别哪天给它加上值相等
        var d = new Dictionary<FetchPlanItem, DateTime> { [a] = DateTime.Now };
        Assert.True(d.ContainsKey(a));
        Assert.False(d.ContainsKey(b));              // 记了 a 不等于记了 b
        Assert.Equal(a.Action, b.Action);            // 但动作确实是同一个
    }

    /// <summary>设定时刻本来就在 17:00 之后的组不参与——它从来没享受过那个补档。</summary>
    [Fact]
    public void 迁移_收盘后的组不参与()
    {
        var plan = new FetchPlan();
        plan.Normalize();
        var daily = plan.GroupOf(PlanGroupKind.Daily);
        foreach (var dup in daily.Items.Where(i => i.Action == FetchActionId.StepPlanWatch).ToList())
            daily.Items.Remove(dup);

        var 夜间 = 组("夜里再来一遍", new TimeOnly(22, 0));
        排一份(夜间, FetchActionId.StepPlanWatch);
        plan.Groups.Add(夜间);
        plan.LinkOwners();

        plan.MigrateRetired();

        Assert.DoesNotContain(daily.Items, i => i.Action == FetchActionId.StepPlanWatch);
    }
}
