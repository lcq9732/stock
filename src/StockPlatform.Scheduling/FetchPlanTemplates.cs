namespace StockPlatform.Scheduling;

/// <summary>模板里的一项：动作 + 抓哪一段。时刻和重复规则不在这儿——那是**组**的事。</summary>
public sealed record PlanTemplateItem(
    FetchActionId Action,
    FetchMode Mode = FetchMode.Incremental);

/// <summary>
/// 一份可以一键铺进计划的**组**配方（2026-09-02 随分组改造重写）。
/// 模板 = "一个组长什么样"：组名、重复规则、开始时刻，加上组里那串任务和它们的顺序。
/// </summary>
public sealed record PlanTemplate(
    string Name,
    string Description,
    RepeatKind Repeat,
    TimeOnly? NotBefore,
    IReadOnlyList<PlanTemplateItem> Items,
    int DayOfMonth = 1,
    RunPacing Pacing = RunPacing.Immediate)
{
    public override string ToString() => Name;
}

/// <summary>
/// 计划模板（2026-09-02 改造）。
///
/// ════ 分组之后模板还有什么用 ════
/// 默认计划已经带好了五个组，所以模板不再是"第一次用的必经之路"，而是**恢复和补齐**的工具：
/// 组被删了、任务被自己挪乱了、或者想把某一整套重新铺一遍时用它。
///
/// ⚠ 铺进去的仍然是一串**独立的任务**：模板是编辑期的排版动作，执行引擎只看见组和任务，
/// 根本不知道它们曾经属于同一个模板。
/// </summary>
public static class FetchPlanTemplates
{
    /// <summary>
    /// 每天产生的新数据那一组：原【拉取全部】的 13 步 + 板块 + 财报预约日，末尾接补漏与本地重算。
    ///
    /// 前 13 步直接照 <see cref="FetchTaskCatalog.FetchAllSteps"/> 生成，**不手抄一遍**：
    /// 手抄的话往每日流程里加了新步骤却忘了加进模板，按模板铺出来的计划就会静默少一步
    /// （比如漏掉当日覆盖率体检，那就再也没人发现数据源盘后没更新完了）。
    /// </summary>
    public static readonly PlanTemplate Daily = new(
        "每日收盘后",
        "每天产生的新数据：K线 → 资金与交易 → 收尾与合成 → 补漏与重算。每工作日 18:00 到点就跑。",
        RepeatKind.EveryWorkday, new TimeOnly(18, 0),
        // 顺序集中定义在 FetchTaskCatalog.DailyOrder，跟默认计划共用同一份——
        // 两边各抄一遍的话，往日更里加了新步骤却只改了一处，用模板恢复出来的计划就会静默少一步。
        // 模式按 DefaultModeOf 给（【ETF换手率校正】在日更里要用彻底重查，否则只报告不写库）。
        FetchTaskCatalog.DailyOrder.Select(a => new PlanTemplateItem(a, FetchTaskCatalog.DefaultModeOf(a))).ToList());

    /// <summary>
    /// 按周期更新、晚几天没关系的那一组。整组「空闲时补」：财务报表每轮只能 300 只、
    /// 全市场要跨几天才啃得完，设成"每月 1 号跑一轮"根本补不完。
    /// </summary>
    public static readonly PlanTemplate Periodic = new(
        "季度定期",
        "行业分类 → 指数成分/权重/映射 → 股东 → 分红 → 财务报表 → 监管指标 → PDF重解析 → 手工数据。每月 1 号到期，空闲时慢慢补。",
        RepeatKind.Monthly, null,
        // 顺序集中在 FetchTaskCatalog.PeriodicOrder，跟默认计划共用同一份
        FetchTaskCatalog.PeriodicOrder.Select(a => new PlanTemplateItem(a)).ToList(),
        Pacing: RunPacing.WhenIdle);

    /// <summary>
    /// 想起来才做的一次性活：先体检看看缺不缺 → 决定要不要往回补更早的历史 → 补完再建一次索引。
    /// 默认手动，不自动跑。
    /// </summary>
    public static readonly PlanTemplate OnDemand = new(
        "按需启动",
        "全库数据体检 → 按年份区间往回补历史 → 优化数据库。都是手动触发的一次性活。",
        RepeatKind.Manual, null,
        FetchTaskCatalog.OnDemandOrder.Select(a => new PlanTemplateItem(a)).ToList());

    public static readonly IReadOnlyList<PlanTemplate> All = [Daily, Periodic, OnDemand];

    /// <summary>
    /// 把模板铺成一个组，返回 (那个组, 组里最终的那串任务, 从别的组搬过来的任务名)。
    ///
    /// 规则：
    ///   · **同名组已经在计划里** → 用它（重复规则和时刻保持原样，那是用户调过的）；
    ///     没有就新建一个，按模板的规则和时刻，插在计划末尾。
    ///   · 模板里的任务按模板顺序放进这个组。**一个动作只能待在一个组**，
    ///     所以在别处的会被搬过来——这是"我要这一套"的明确意思；搬了哪些会报告出来，
    ///     不闷声干。
    ///   · 已经在这个组里的就地更新模式，不重复添加。
    /// 反复点是安全的：第二次点，组里还是这一串、顺序也一样。
    /// </summary>
    public static (FetchPlanGroup Group, List<FetchPlanItem> Items, List<string> MovedIn) Apply(
        FetchPlan plan, PlanTemplate template)
    {
        var group = plan.Groups.FirstOrDefault(g => g.Name == template.Name);
        if (group == null)
        {
            group = new FetchPlanGroup
            {
                Name = template.Name,
                Enabled = true,
                Repeat = template.Repeat,
                NotBefore = template.NotBefore,
                DayOfMonth = template.DayOfMonth,
            };
            plan.Groups.Add(group);
        }

        var items = new List<FetchPlanItem>();
        var movedIn = new List<string>();

        foreach (var t in template.Items)
        {
            var existing = plan.AllItems.FirstOrDefault(i => i.Action == t.Action);
            if (existing != null && existing.Owner != null && existing.Owner != group)
                movedIn.Add(FetchTaskCatalog.Info(t.Action).Name);

            var item = existing ?? new FetchPlanItem { Action = t.Action };
            existing?.Owner?.Items.Remove(existing);
            item.Enabled = true;
            item.Mode = t.Mode;
            item.Owner = group;
            items.Add(item);
        }

        // 模板没提到、但本来就在这个组里的项留在后面（用户自己加的，不该被模板挤掉）
        var rest = group.Items.Where(i => !items.Contains(i)).ToList();
        group.Items = [.. items, .. rest];
        plan.LinkOwners();
        return (group, items, movedIn);
    }
}
