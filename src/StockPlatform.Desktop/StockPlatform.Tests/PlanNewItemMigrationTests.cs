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
