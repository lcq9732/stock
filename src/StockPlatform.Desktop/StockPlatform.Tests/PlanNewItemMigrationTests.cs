using StockPlatform.Scheduling;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// **老计划文件遇到新任务项**会怎样（2026-09-07）。
///
/// 这条路跟"默认计划"是两回事，而且只有它才是真实用户会走的：
/// 用户手上的 fetch-plan.json 是几个月前生成的、顺序也自己调过，新版本加了任务项之后，
/// 靠的是 <see cref="FetchPlan.Normalize"/> 把缺的项补进去。
///
/// 默认计划那几条测试（PlanTemplateTests）覆盖不到这里——它们从
/// <c>CreateDefault()</c> 出发，本来就带着全部项。补漏逻辑漏了，那几条照样全绿，
/// 而真实用户升级后**新功能根本不出现在界面上**。
/// </summary>
public class PlanNewItemMigrationTests
{
    private readonly ITestOutputHelper _out;
    public PlanNewItemMigrationTests(ITestOutputHelper o) => _out = o;

    /// <summary>造一份"老"计划：拿默认计划，把某一项整个摘掉，模拟它还没被加进来的那个版本。</summary>
    private static FetchPlan PlanWithout(FetchActionId missing)
    {
        var plan = FetchPlan.CreateDefault();
        plan.Normalize();
        foreach (var g in plan.Groups)
            for (int i = g.Items.Count - 1; i >= 0; i--)
                if (g.Items[i].Action == missing) g.Items.RemoveAt(i);
        return plan;
    }

    /// <summary>
    /// ⭐ **换过组的动作要搬家**（2026-09-19）。<see cref="FetchPlan.Normalize"/> 只补计划里没有的
    /// 动作，已有的一律保持原位；而界面上又没有"移到别的组"这个操作（2026-09-02 做了又撤）。
    /// 所以模板改了组而老计划不动的话，这一项会永远留在旧组里，且界面上看不出任何异常——
    ///【补全退市名单】就会每月才跑一轮，而它的产出是后面所有"逐只抓"的项的输入名单。
    /// </summary>
    [Fact]
    public void 老计划里换过组的项会被挪到新组()
    {
        // 造一份"老"计划：它还在季度组（2026-09-19 之前的模板就是这样）
        var plan = FetchPlan.CreateDefault();
        plan.Normalize();
        var daily = plan.GroupOf(PlanGroupKind.Daily);
        var periodic = plan.GroupOf(PlanGroupKind.Periodic);
        var item = daily.Items.Single(i => i.Action == FetchActionId.StepDelistedSupplement);
        daily.Items.Remove(item);
        periodic.Items.Insert(4, item);

        var notes = plan.MigrateRetired();
        plan.Normalize();

        Assert.DoesNotContain(FetchActionId.StepDelistedSupplement, periodic.Items.Select(i => i.Action));
        Assert.Contains(FetchActionId.StepDelistedSupplement, daily.Items.Select(i => i.Action));
        Assert.Contains(notes, n => n.Contains("补全退市名单"));
    }

    /// <summary>
    /// 搬过去要落在**模板该在的位置**，不能图省事追加到末尾——顺序即执行顺序，
    /// 追加到末尾正好让它跑在所有"逐只抓"的项后面，那是它最该避免的位置
    /// （它的产出是那些项的输入名单）。
    /// </summary>
    [Fact]
    public void 挪过去的项落在模板顺序上而不是末尾()
    {
        var plan = FetchPlan.CreateDefault();
        plan.Normalize();
        var daily = plan.GroupOf(PlanGroupKind.Daily);
        var item = daily.Items.Single(i => i.Action == FetchActionId.StepDelistedSupplement);
        daily.Items.Remove(item);
        plan.GroupOf(PlanGroupKind.Periodic).Items.Add(item);

        plan.MigrateRetired();

        var order = daily.Items.Select(i => i.Action).ToList();
        int at = order.IndexOf(FetchActionId.StepDelistedSupplement);
        _out.WriteLine("日更组前 6 项：" + string.Join("、", order.Take(6)));
        Assert.True(at > order.IndexOf(FetchActionId.FetchTotalShares), "要排在总股本之后（名册刷新完才算得准）");
        Assert.True(at < order.IndexOf(FetchActionId.StepStockDayBars), "要排在K线族之前（新摘牌的票当晚就别再抓）");
    }

    /// <summary>搬完之后再加载一次是空操作——迁移不能每次都重排一遍用户的表。</summary>
    [Fact]
    public void 已经在新组的不再重复搬()
    {
        var plan = FetchPlan.CreateDefault();
        plan.Normalize();

        var notes = plan.MigrateRetired();

        Assert.DoesNotContain(notes, n => n.Contains("补全退市名单"));
    }

    /// <summary>
    /// 【ETF换手率校正】2026-09-23 从按需挪进日更。老计划里它在按需组、没启用、模式「增量」——
    /// 搬过去必须**同时**勾上启用、改成「彻底重查」：带着「增量」进日更就是天天只报告、从不写库。
    /// 用户明确要求了"改到日更"，这是 Regrouped 里唯一替用户改设置的一项。
    /// </summary>
    [Fact]
    public void ETF换手率校正从按需搬进日更_勾上启用_改成彻底重查()
    {
        var plan = FetchPlan.CreateDefault();
        plan.Normalize();
        var daily = plan.GroupOf(PlanGroupKind.Daily);
        var onDemand = plan.GroupOf(PlanGroupKind.OnDemand);
        var item = daily.Items.Single(i => i.Action == FetchActionId.StepEtfTurnoverFix);
        daily.Items.Remove(item);
        item.Enabled = false;
        item.Mode = FetchMode.Incremental;
        onDemand.Items.Add(item);   // 2026-09-23 之前的样子

        var notes = plan.MigrateRetired();
        plan.Normalize();

        Assert.DoesNotContain(FetchActionId.StepEtfTurnoverFix, onDemand.Items.Select(i => i.Action));
        var order = daily.Items.Select(i => i.Action).ToList();
        Assert.True(order.IndexOf(FetchActionId.StepEtfTurnoverFix) > order.IndexOf(FetchActionId.RebuildAdjSeries),
            "要排在【重算回测序列】之后（day_adj 的换手率先生成出来）");
        Assert.True(order.IndexOf(FetchActionId.StepEtfTurnoverFix) > order.IndexOf(FetchActionId.StepEtfRawBars),
            "要排在 ETF 日K 之后（检查的就是当天新抓的行）");
        Assert.True(item.Enabled);
        Assert.Equal(FetchMode.Thorough, item.Mode);
        Assert.Equal(FetchMode.Thorough, item.EffectiveMode);
        Assert.Contains(notes, n => n.Contains("ETF换手率校正") && n.Contains("彻底重查"));
    }

    /// <summary>搬过去之后用户自己取消勾选，下次加载不能又被勾回来——迁移只在"还在旧组"时动它。</summary>
    [Fact]
    public void ETF换手率校正已在日更_用户取消勾选后不会被重新勾上()
    {
        var plan = FetchPlan.CreateDefault();
        plan.Normalize();
        var item = plan.AllItems.Single(i => i.Action == FetchActionId.StepEtfTurnoverFix);
        item.Enabled = false;

        var notes = plan.MigrateRetired();

        Assert.False(item.Enabled);
        Assert.DoesNotContain(notes, n => n.Contains("ETF换手率校正"));
    }

    /// <summary>新建计划 / 用模板恢复：日更里的【ETF换手率校正】默认就是「彻底重查」，其余仍是增量。</summary>
    [Fact]
    public void 默认计划和日更模板里ETF换手率校正用彻底重查()
    {
        var plan = FetchPlan.CreateDefault();
        var item = plan.GroupOf(PlanGroupKind.Daily).Items.Single(i => i.Action == FetchActionId.StepEtfTurnoverFix);
        Assert.Equal(FetchMode.Thorough, item.Mode);
        Assert.Equal(FetchMode.Thorough,
            FetchPlanTemplates.Daily.Items.Single(i => i.Action == FetchActionId.StepEtfTurnoverFix).Mode);
        Assert.All(FetchPlanTemplates.Daily.Items.Where(i => i.Action != FetchActionId.StepEtfTurnoverFix),
            i => Assert.Equal(FetchMode.Incremental, i.Mode));
    }

    [Fact]
    public void 老计划升级后能补上行业景气指标()
    {
        var plan = PlanWithout(FetchActionId.StepIndustryIndicator);
        Assert.DoesNotContain(FetchActionId.StepIndustryIndicator, plan.AllItems.Select(i => i.Action));

        plan.Normalize();

        var daily = plan.GroupOf(PlanGroupKind.Daily).Items.Select(i => i.Action).ToList();
        _out.WriteLine("日更组末尾：" + string.Join("、", daily.TakeLast(4)));
        Assert.Contains(FetchActionId.StepIndustryIndicator, daily);
    }

    [Fact]
    public void 补进来的新项默认不启用()
    {
        // 这是有意的：新项没经过用户同意，不该自己跑起来。
        // 用户先用那一行的【执行】手工跑完首轮，看着没问题再勾启用。
        var plan = PlanWithout(FetchActionId.StepIndustryIndicator);
        plan.Normalize();

        var item = plan.AllItems.Single(i => i.Action == FetchActionId.StepIndustryIndicator);
        Assert.False(item.Enabled);
    }

    /// <summary>
    /// 每一个活跃项都得能被补回来。写成 Theory 遍历整份目录而不是只测新加的那个——
    /// 以后再加任务项时，这条会自动把它罩进去，不用记得回来补测试。
    /// </summary>
    [Fact]
    public void 任何活跃项被摘掉后都能补回来()
    {
        var missing = new List<string>();
        foreach (var info in FetchTaskCatalog.Active)
        {
            var plan = PlanWithout(info.Id);
            plan.Normalize();
            if (!plan.AllItems.Any(i => i.Action == info.Id))
                missing.Add($"{info.Id}（{info.Name}）");
        }
        Assert.True(missing.Count == 0, "这些项补不回来：" + string.Join("、", missing));
    }
}
