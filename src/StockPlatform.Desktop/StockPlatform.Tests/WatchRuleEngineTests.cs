using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// L1 挂/摘的判据（见 doc/watch-item-design.md §2、§5）。
///
/// 重点测的是**摘**不是挂——L1 的核心价值就是"条件消失了自动撤下来"，
/// 而手工清单之所以会烂，正是因为人一定会忘了摘。
/// </summary>
public class WatchRuleEngineTests
{
    private static WatchRuleInput Input(
        Dictionary<string, string>? active = null,
        Dictionary<string, string>? core = null,
        Dictionary<string, PlanAnnouncement>? plans = null,
        Dictionary<string, List<WatchIndicatorLink>>? inds = null)
        => new(active ?? [], core ?? [], plans ?? [], inds ?? []);

    private static PlanAnnouncement Plan(string code) => new()
    {
        Code = code, Kind = PlanKind.Buyback, Stage = PlanStage.Proposal,
        AnnounceDate = new DateTime(2026, 7, 25), PlanCapPrice = 573,
        PlanAmountLow = 200e8, PlanAmountHigh = 400e8,
    };

    private static readonly Dictionary<string, string> Catl = new() { ["300750"] = "宁德时代" };

    [Fact]
    public void 有未结束的方案_挂上A档进展监控()
    {
        var r = WatchRuleEngine.Rebuild([], Input(
            active: Catl, plans: new() { ["300750"] = Plan("300750") }));

        var item = Assert.Single(r.Items.Where(i => i.Kind == WatchKind.PlanStage));
        Assert.Equal("A", item.Priority);
        Assert.Equal(WatchOrigin.Derived, item.Origin);
        Assert.Contains("573", item.Reason);
        Assert.Contains(r.Added, i => i.Kind == WatchKind.PlanStage);

        // ★ 计划花多少钱是判断"这家有多认真"的第一个数，不能只写价格上限
        //（2026-09-11 用户反馈）
        Assert.Contains("计划 200~400 亿", item.Reason);
    }

    /// <summary>资金区间缺失（没抽到）时不能显示成"计划 ~0 亿"，整段省掉。</summary>
    [Fact]
    public void 方案没有资金区间_理由里就不提()
    {
        var plan = Plan("300750");
        plan.PlanAmountLow = null;
        plan.PlanAmountHigh = null;

        var r = WatchRuleEngine.Rebuild([], Input(
            active: Catl, plans: new() { ["300750"] = plan }));

        var item = r.Items.Single(i => i.Kind == WatchKind.PlanStage);
        Assert.DoesNotContain("计划", item.Reason);
        Assert.Contains("573", item.Reason);   // 有的那个照样写
    }

    /// <summary>★ L1 的核心：方案不在 OpenPlans 里了（已完毕/终止），观察项要自动消失。</summary>
    [Fact]
    public void 方案结束后_进展监控自动摘掉()
    {
        var first = WatchRuleEngine.Rebuild([], Input(
            active: Catl, plans: new() { ["300750"] = Plan("300750") }));
        Assert.Contains(first.Items, i => i.Kind == WatchKind.PlanStage);

        // 第二轮：方案已完毕，OpenPlans 空了
        var second = WatchRuleEngine.Rebuild(first.Items.ToList(), Input(active: Catl));

        Assert.DoesNotContain(second.Items, i => i.Kind == WatchKind.PlanStage);
        Assert.Contains(second.Expired, i => i.Kind == WatchKind.PlanStage);
    }

    /// <summary>★ 硬边界：手写项任何规则都不许碰，哪怕票已经不在自选里了。</summary>
    [Fact]
    public void 手写项_重算后原样还在()
    {
        var manual = new WatchItem
        {
            Code = "300750", Name = "宁德时代", Layer = WatchLayer.L2, Origin = WatchOrigin.Manual,
            Kind = WatchKind.Manual, Expr = "装车份额", Op = WatchOp.Remind,
            Reason = "月度装车份额 < 45%", Thesis = "回购是情绪转折点",
        };

        // 输入全空——连这只票都不在自选里了
        var r = WatchRuleEngine.Rebuild([manual], Input());

        var kept = Assert.Single(r.Items);
        Assert.Equal(WatchOrigin.Manual, kept.Origin);
        Assert.Equal("回购是情绪转折点", kept.Thesis);
        Assert.Empty(r.Expired);
    }

    /// <summary>派生项撞上手写项的同一个 (票,取值器,参数) 时，保留手写的——它的理由是人写的。</summary>
    [Fact]
    public void 派生撞上手写_保留手写()
    {
        var manual = new WatchItem
        {
            Code = "300750", Origin = WatchOrigin.Manual,
            Kind = WatchKind.PlanStage, Expr = PlanKind.Buyback, Reason = "我自己盯的回购",
        };

        var r = WatchRuleEngine.Rebuild([manual], Input(
            active: Catl, plans: new() { ["300750"] = Plan("300750") }));

        var item = Assert.Single(r.Items.Where(i => i.Kind == WatchKind.PlanStage));
        Assert.Equal(WatchOrigin.Manual, item.Origin);
        Assert.Equal("我自己盯的回购", item.Reason);
    }

    /// <summary>重算要沿用旧的 ItemId，否则触发历史就跟观察项对不上了。</summary>
    [Fact]
    public void 重算沿用旧的ItemId()
    {
        var first = WatchRuleEngine.Rebuild([], Input(active: Catl));
        var id = first.Items.First(i => i.Kind == WatchKind.PriceMA).ItemId;

        var second = WatchRuleEngine.Rebuild(first.Items.ToList(), Input(active: Catl));

        Assert.Equal(id, second.Items.First(i => i.Kind == WatchKind.PriceMA).ItemId);
        Assert.Empty(second.Added);
        Assert.Empty(second.Expired);
    }

    /// <summary>
    /// ★ 底仓绝不能挂"跌破 MA20"——底仓不设止损，挂上去每天早上都会跳出来叫你砍底仓。
    /// 这正是 AnalyzerPaths.CorePositionPath 注释里那条"混一份数据会污染晨检"。
    /// </summary>
    [Fact]
    public void 底仓不挂跌破均线_主动仓才挂()
    {
        var coreOnly = WatchRuleEngine.Rebuild([], Input(core: Catl));
        Assert.DoesNotContain(coreOnly.Items, i => i.Kind == WatchKind.PriceMA);
        Assert.Contains(coreOnly.Items, i => i.Kind == WatchKind.EventRecent && i.Expr == "Dividend");

        var activeOnly = WatchRuleEngine.Rebuild([], Input(active: Catl));
        Assert.Contains(activeOnly.Items, i => i.Kind == WatchKind.PriceMA);
    }

    /// <summary>一只票同时在两个列表里时算底仓——底仓纪律更宽，误判成主动仓会触发该忽略的卖出信号。</summary>
    [Fact]
    public void 同时在两个列表_按底仓算()
    {
        var r = WatchRuleEngine.Rebuild([], Input(active: Catl, core: Catl));
        Assert.All(r.Items, i => Assert.Equal(PositionKind.Core, i.Position));
        Assert.DoesNotContain(r.Items, i => i.Kind == WatchKind.PriceMA);
    }

    /// <summary>L0 兜底：在册的票都要挂上，不挑画像。</summary>
    [Fact]
    public void L0兜底事件_所有在册的票都挂()
    {
        var r = WatchRuleEngine.Rebuild([], Input(active: Catl));
        var l0 = r.Items.Where(i => i.Layer == WatchLayer.L0).ToList();

        Assert.Contains(l0, i => i.Expr == "ShareLift");
        Assert.Contains(l0, i => i.Expr == "EarningsForecast");
        Assert.All(l0, i => Assert.Equal(WatchOrigin.Builtin, i.Origin));
    }

    /// <summary>
    /// ★ 每条 L0 都必须带时间窗（2026-09-11 事故的根因）。
    /// 原来全是 <c>StageChange</c>、没有窗口，于是首次求值就把每张表的"最新一条"报了一遍——
    /// 一轮重算产出横跨 2011~2030 年的 380 多条触发，全是噪音。
    /// </summary>
    [Fact]
    public void L0每一条都必须带时间窗()
    {
        var l0 = WatchRuleEngine.Rebuild([], Input(active: Catl))
            .Items.Where(i => i.Layer == WatchLayer.L0).ToList();

        Assert.NotEmpty(l0);
        Assert.All(l0, i =>
        {
            Assert.Equal(WatchOp.Within, i.Op);
            Assert.NotNull(i.Threshold);
            Assert.True(i.Threshold > 0, $"{i.Expr} 的窗口必须是正数");
            // 两类时间语义必须明确归类，不能再有"一个取值器打天下"
            Assert.True(i.Kind is WatchKind.ScheduleAhead or WatchKind.EventRecent,
                $"{i.Expr} 的 Kind 是 {i.Kind}");
        });
    }

    /// <summary>解禁和预约披露是**未来日程**，其余是**已发生的事**——取值方式相反，不能混。</summary>
    [Theory]
    [InlineData("ShareLift", WatchKind.ScheduleAhead)]
    [InlineData("EarningsSchedule", WatchKind.ScheduleAhead)]
    [InlineData("EarningsForecast", WatchKind.EventRecent)]
    [InlineData("HolderChange", WatchKind.EventRecent)]
    [InlineData("Lhb", WatchKind.EventRecent)]
    public void L0各表归到正确的时间语义(string table, string expectedKind)
    {
        var item = WatchRuleEngine.Rebuild([], Input(active: Catl))
            .Items.Single(i => i.Layer == WatchLayer.L0 && i.Expr == table);

        Assert.Equal(expectedKind, item.Kind);
    }

    [Fact]
    public void 挂了行业指标_按weight排序挂上()
    {
        var r = WatchRuleEngine.Rebuild([], Input(
            active: Catl,
            inds: new()
            {
                ["300750"] =
                [
                    new("300750", "EMI00662659", 1, WatchIndicatorOrigin.Rule, "锂电池：碳酸锂"),
                ],
            }));

        var item = Assert.Single(r.Items.Where(i => i.Kind == WatchKind.IndustryIndicator));
        Assert.Equal("EMI00662659", item.Expr);
        Assert.Contains("碳酸锂", item.Reason);
    }

    /// <summary>不在任何列表里的票，不该产生任何派生项。</summary>
    [Fact]
    public void 没有持仓也没自选_不产出派生项()
    {
        var r = WatchRuleEngine.Rebuild([], Input());
        Assert.Empty(r.Items);
    }
}
