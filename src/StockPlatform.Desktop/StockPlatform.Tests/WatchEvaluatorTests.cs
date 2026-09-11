using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 求值判据（见 doc/watch-item-design.md §4.3）。
/// 重点是那个**自然日序列**的坑：行业指标周末沿用周五的值，跟"上一行"比会恒等于 0。
/// </summary>
public class WatchEvaluatorTests
{
    private static WatchItem Item(string kind, string op, double? threshold = null) => new()
    {
        Code = "300750", Name = "宁德时代", Kind = kind, Op = op,
        Threshold = threshold, Reason = "测试项", Priority = "A",
    };

    // ── stage 跃迁 ──

    [Fact]
    public void stage没变_不触发()
    {
        var hit = WatchEvaluator.Evaluate(
            Item(WatchKind.PlanStage, WatchOp.StageChange),
            new WatchReading(new DateTime(2026, 8, 31), StageText: "进展（尚未实施）",
                PrevStageText: "进展（尚未实施）"));
        Assert.Null(hit);
    }

    /// <summary>★ 这就是"回购到底买没买"那个信号本身。</summary>
    [Fact]
    public void stage从尚未实施变成首次回购_触发()
    {
        var hit = WatchEvaluator.Evaluate(
            Item(WatchKind.PlanStage, WatchOp.StageChange),
            new WatchReading(new DateTime(2026, 9, 15), StageText: "首次回购（已回购 1.2 亿元）",
                PrevStageText: "进展（尚未实施）"));

        Assert.NotNull(hit);
        Assert.Contains("首次回购", hit!.Message);
        Assert.Contains("尚未实施", hit.Message);   // 前后都要在消息里，才看得出变化
        Assert.Equal(new DateTime(2026, 9, 15), hit.TriggerTradeDate);
    }

    [Fact]
    public void 第一次见到stage_也要报一次现状()
    {
        var hit = WatchEvaluator.Evaluate(
            Item(WatchKind.PlanStage, WatchOp.StageChange),
            new WatchReading(new DateTime(2026, 9, 3), StageText: "进展（尚未实施）"));
        Assert.NotNull(hit);
        Assert.Contains("现状", hit!.Message);
    }

    // ── 自然日序列 ──

    /// <summary>
    /// ★ 真实数据形状：EMI00662659 碳酸锂指数。周末沿用周五值，工作日也常连续同值。
    /// 「上一个不同的值」必须跳过所有重复，否则环比在周末恒等于 0。
    /// </summary>
    [Fact]
    public void 自然日序列_取上一个不同的值而不是上一行()
    {
        List<(DateTime, double)> series =
        [
            (new DateTime(2026, 9, 3), 384.71),
            (new DateTime(2026, 9, 4), 377.07),
            (new DateTime(2026, 9, 5), 377.07),   // 周六，沿用
            (new DateTime(2026, 9, 6), 377.07),   // 周日，沿用
            (new DateTime(2026, 9, 7), 356.69),
            (new DateTime(2026, 9, 8), 356.69),
            (new DateTime(2026, 9, 9), 351.59),
            (new DateTime(2026, 9, 10), 351.59),  // 工作日也重复
        ];

        var r = WatchEvaluator.ReadLatestDistinct(series);

        Assert.Equal(351.59, r.Value);
        Assert.Equal(356.69, r.PrevValue);        // 不是 351.59（上一行）
        Assert.Equal(new DateTime(2026, 9, 10), r.TradeDate);
    }

    [Fact]
    public void 整段都是同一个值_没有上一个不同值()
    {
        List<(DateTime, double)> series =
        [
            (new DateTime(2026, 9, 9), 100.0),
            (new DateTime(2026, 9, 10), 100.0),
        ];
        var r = WatchEvaluator.ReadLatestDistinct(series);
        Assert.Equal(100.0, r.Value);
        Assert.Null(r.PrevValue);
    }

    [Fact]
    public void 空序列_不炸()
        => Assert.Null(WatchEvaluator.ReadLatestDistinct([]).Value);

    /// <summary>接上真实数据：碳酸锂从 356.69 跌到 351.59，无阈值的 CrossDown 该报跌幅。</summary>
    [Fact]
    public void 指标下跌_报出跌幅()
    {
        var hit = WatchEvaluator.Evaluate(
            Item(WatchKind.IndustryIndicator, WatchOp.CrossDown),
            new WatchReading(new DateTime(2026, 9, 10), 351.59, 356.69));

        Assert.NotNull(hit);
        Assert.Contains("-1.4%", hit!.Message);
    }

    [Fact]
    public void 指标上涨_CrossDown不触发()
        => Assert.Null(WatchEvaluator.Evaluate(
            Item(WatchKind.IndustryIndicator, WatchOp.CrossDown),
            new WatchReading(new DateTime(2026, 9, 10), 360.0, 351.59)));

    // ── 跌破均线 ──

    /// <summary>值是对均线的偏离率（%），阈值 0：由正转负＝跌破。</summary>
    [Fact]
    public void 由上到下穿越均线_触发()
    {
        var hit = WatchEvaluator.Evaluate(
            Item(WatchKind.PriceMA, WatchOp.CrossDown, 0),
            new WatchReading(new DateTime(2026, 9, 10), -1.2, 0.5));
        Assert.NotNull(hit);
    }

    /// <summary>★ 一直在均线下方是**状态**不是事件——不该每天报一次。</summary>
    [Fact]
    public void 一直在均线下方_不重复触发()
        => Assert.Null(WatchEvaluator.Evaluate(
            Item(WatchKind.PriceMA, WatchOp.CrossDown, 0),
            new WatchReading(new DateTime(2026, 9, 10), -2.0, -1.2)));

    // ── 阈值 ──

    [Fact]
    public void 低于阈值_触发()
    {
        var hit = WatchEvaluator.Evaluate(
            Item(WatchKind.IndustryIndicator, WatchOp.Lt, 360),
            new WatchReading(new DateTime(2026, 9, 10), 351.59));
        Assert.NotNull(hit);
        Assert.Contains("351.59", hit!.Message);
    }

    [Fact]
    public void 取不到值_不触发()
        => Assert.Null(WatchEvaluator.Evaluate(
            Item(WatchKind.IndustryIndicator, WatchOp.Lt, 360), new WatchReading(null)));

    [Fact]
    public void 停用的观察项_不触发()
    {
        var item = Item(WatchKind.IndustryIndicator, WatchOp.Lt, 360);
        item.Enabled = false;
        Assert.Null(WatchEvaluator.Evaluate(item, new WatchReading(new DateTime(2026, 9, 10), 1)));
    }

    // ── Manual ──

    /// <summary>
    /// ★ Manual 是"库里确实没这个数据"的诚实出口（如月度装车份额）。
    /// 它只说"该去查了"，**绝不说"已触发"**——假装能自动判会让人以为没报就是安全的。
    /// </summary>
    [Fact]
    public void Manual项_只提醒不判定()
    {
        var item = Item(WatchKind.Manual, WatchOp.Remind);
        item.Reason = "月度装车份额 < 45%";

        var hit = WatchEvaluator.Evaluate(item, new WatchReading(null));

        Assert.NotNull(hit);
        Assert.Contains("需人工核对", hit!.Message);
        Assert.Contains("库里没有这个数据源", hit.Message);
    }
}
