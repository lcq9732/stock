using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 回购时间线的拆轮与叙述（2026-09-14）。
/// 三个用例都是**库里真实的票**，各暴露一个坑：
///   · 永和股份 —— 12 条跨两轮，"完毕"之后又"首次回购"
///   · 物产金轮 —— 同一天两份方案（价格上限从 12 改到 14），那是修订不是新一轮
///   · 乐鑫科技 —— 两轮都很短，不该触发折叠
/// </summary>
public class BuybackTimelineTests
{
    private static PlanAnnouncement P(string date, string stage,
        double? cumAmount = null, double? lo = null, double? hi = null, double? cap = null)
        => new()
        {
            Code = "000000", Kind = PlanKind.Buyback, Stage = stage,
            AnnounceDate = DateTime.Parse(date),
            CumAmount = cumAmount, PlanAmountLow = lo, PlanAmountHigh = hi, PlanCapPrice = cap,
        };

    [Fact]
    public void 空输入_不炸()
        => Assert.Empty(BuybackTimeline.Build([]));

    /// <summary>★ 永和股份：08-01 完毕之后同一天又发新方案，必须拆成两轮。</summary>
    [Fact]
    public void 完毕之后又发方案_拆成两轮()
    {
        var rows = new[]
        {
            P("2026-07-16", PlanStage.Proposal, lo: 2e8, hi: 3e8),
            P("2026-07-21", PlanStage.FirstBuy),
            P("2026-08-01", PlanStage.Done),
            P("2026-08-01", PlanStage.Proposal, lo: 2e8, hi: 3e8),   // 第 2 轮同一天起
            P("2026-08-04", PlanStage.FirstBuy),
            P("2026-09-02", PlanStage.Progress),
        };

        var ev = BuybackTimeline.Build(rows);

        // 两轮都要出现轮次标记，否则读者分不清"完毕之后怎么又首次回购"
        Assert.Contains(ev, e => e.Text.Contains("第1轮"));
        Assert.Contains(ev, e => e.Text.Contains("第2轮"));
        // 第 2 轮的方案日期是 08-01
        Assert.Contains(ev, e => e.Text.Contains("第2轮回购方案") && e.Date == new DateTime(2026, 8, 1));
    }

    /// <summary>★ 物产金轮：同一轮里两份方案是**修订**（上限 12→14），取后一份，不另起一轮。</summary>
    [Fact]
    public void 连续两份方案是修订_算同一轮且取后一份()
    {
        var rows = new[]
        {
            P("2026-07-28", PlanStage.Proposal, lo: 0.5e8, hi: 1e8, cap: 12),
            P("2026-07-30", PlanStage.Proposal, lo: 0.5e8, hi: 1e8, cap: 14),   // 修订版
            P("2026-07-31", PlanStage.FirstBuy, cumAmount: 0.03e8),
            P("2026-09-11", PlanStage.Done, cumAmount: 0.60e8),
        };

        var ev = BuybackTimeline.Build(rows);

        // 只有一轮 → 不带轮次标记
        Assert.DoesNotContain(ev, e => e.Text.Contains("第1轮"));
        Assert.DoesNotContain(ev, e => e.Text.Contains("第2轮"));

        // 方案只出现一次，且是修订后的 14 元
        var proposals = ev.Where(e => e.Text.Contains("回购方案")).ToList();
        Assert.Single(proposals);
        Assert.Contains("14", proposals[0].Text);
        Assert.DoesNotContain("12", proposals[0].Text);
    }

    /// <summary>
    /// ★ 事项内部按日期**正序**，方案是主行、执行过程缩进跟在后面（用户 2026-09-14 定的）。
    /// 一件事的来龙去脉顺着时间读才通顺，倒着读"完毕→进展→首次"是反直觉的。
    /// </summary>
    [Fact]
    public void 轮内正序且执行过程缩进()
    {
        var rows = new[]
        {
            P("2026-07-30", PlanStage.Proposal, cap: 14),
            P("2026-07-31", PlanStage.FirstBuy, cumAmount: 0.03e8),
            P("2026-09-11", PlanStage.Done, cumAmount: 0.60e8),
        };

        var ev = BuybackTimeline.Build(rows);

        // 方案打头、不缩进
        Assert.Equal(new DateTime(2026, 7, 30), ev[0].Date);
        Assert.Equal(0, ev[0].Indent);
        Assert.Contains("回购方案", ev[0].Text);

        // 执行过程正序、缩进一级
        Assert.Equal(new DateTime(2026, 7, 31), ev[1].Date);
        Assert.Equal(new DateTime(2026, 9, 11), ev[2].Date);
        Assert.All(ev.Skip(1), e => Assert.Equal(1, e.Indent));
    }

    /// <summary>★ 轮与轮之间倒序——最近那轮排最前，但每轮内部仍是正序。</summary>
    [Fact]
    public void 轮间倒序_轮内正序()
    {
        var rows = new[]
        {
            P("2026-07-16", PlanStage.Proposal, lo: 2e8, hi: 3e8),
            P("2026-07-21", PlanStage.FirstBuy),
            P("2026-08-01", PlanStage.Done),
            P("2026-08-01", PlanStage.Proposal, lo: 2e8, hi: 3e8),
            P("2026-08-04", PlanStage.FirstBuy),
            P("2026-09-02", PlanStage.Progress),
        };

        var ev = BuybackTimeline.Build(rows);

        // 第 2 轮（较新）整体排在第 1 轮之前
        var i2 = ev.ToList().FindIndex(e => e.Text.Contains("第2轮"));
        var i1 = ev.ToList().FindIndex(e => e.Text.Contains("第1轮"));
        Assert.True(i2 < i1, "较新的第 2 轮该排在前面");

        // 第 2 轮内部正序：方案(08-01) → 首次(08-04) → 进展(09-02)
        Assert.Equal(new DateTime(2026, 8, 1), ev[0].Date);
        Assert.Equal(new DateTime(2026, 8, 4), ev[1].Date);
        Assert.Equal(new DateTime(2026, 9, 2), ev[2].Date);
    }

    /// <summary>进展不超过 5 条时逐条列出，不折叠。</summary>
    [Fact]
    public void 进展不多_不折叠()
    {
        var rows = new[]
        {
            P("2026-07-30", PlanStage.Proposal, cap: 14),
            P("2026-07-31", PlanStage.FirstBuy, cumAmount: 0.03e8),
            P("2026-08-04", PlanStage.Progress, cumAmount: 0.07e8),
            P("2026-09-11", PlanStage.Done, cumAmount: 0.60e8),
        };

        var ev = BuybackTimeline.Build(rows);

        Assert.Equal(4, ev.Count);                                  // 方案 + 3 条执行
        Assert.DoesNotContain(ev, e => e.Text.Contains("略"));
    }

    /// <summary>★ 超过 5 条折叠成首尾，中间省略——中间那些"截至X日累计Y亿"信息量重复。</summary>
    [Fact]
    public void 进展超过五条_折叠成首尾()
    {
        var rows = new List<PlanAnnouncement> { P("2026-07-01", PlanStage.Proposal, cap: 10) };
        rows.Add(P("2026-07-05", PlanStage.FirstBuy, cumAmount: 0.1e8));
        for (int i = 1; i <= 5; i++)
            rows.Add(P($"2026-08-{i:00}", PlanStage.Progress, cumAmount: 0.1e8 * (i + 1)));

        var ev = BuybackTimeline.Build(rows);

        // 方案 + 首条 + "中间略" + 末条 = 4 行，顺序仍是正序
        Assert.Equal(4, ev.Count);
        Assert.Contains("回购方案", ev[0].Text);
        Assert.Contains("首次回购", ev[1].Text);                     // 起点保留
        Assert.Contains("略", ev[2].Text);                          // 中间折叠
        Assert.Contains("0.6 亿", ev[3].Text);                      // 末条的累计量保留
        Assert.True(ev[1].Date < ev[3].Date, "折叠后仍是正序");
    }

    /// <summary>★ 金额 0（公告明说没买）和 null（没抽到）在叙述里必须分得开。</summary>
    [Fact]
    public void 零和null的叙述不同()
    {
        var zero = BuybackTimeline.Build([P("2026-09-03", PlanStage.Progress, cumAmount: 0)]);
        var nul = BuybackTimeline.Build([P("2026-09-03", PlanStage.Progress)]);

        Assert.Contains("尚未实施", zero[0].Text);
        Assert.Contains("金额未读出", nul[0].Text);
    }

    /// <summary>
    /// ★ 库里只有进展、没有方案（美的/格力那种：方案发在抓取窗口之前）——
    /// 第一条要提成主行，不能整组悬空缩进。
    /// </summary>
    [Fact]
    public void 没有方案_第一条提为主行()
    {
        var rows = new[]
        {
            P("2026-07-09", PlanStage.Milestone, cumAmount: 61.28e8),
            P("2026-07-20", PlanStage.Progress, cumAmount: 67.16e8),
            P("2026-09-03", PlanStage.Progress, cumAmount: 80.2e8),
        };

        var ev = BuybackTimeline.Build(rows);

        Assert.Equal(0, ev[0].Indent);                      // 起头那条不缩进
        Assert.All(ev.Skip(1), e => Assert.Equal(1, e.Indent));
    }

    /// <summary>有方案时第一条执行仍然缩进——提升只在没有主行时发生。</summary>
    [Fact]
    public void 有方案_执行仍然缩进()
    {
        var rows = new[]
        {
            P("2026-07-25", PlanStage.Proposal, lo: 4e8, hi: 8e8),
            P("2026-09-14", PlanStage.FirstBuy, cumAmount: 0.12e8),
        };

        var ev = BuybackTimeline.Build(rows);

        Assert.Equal(0, ev[0].Indent);
        Assert.Equal(1, ev[1].Indent);
    }

    /// <summary>只有方案、还没开始买（宁德那种）也要能叙述。</summary>
    [Fact]
    public void 只有方案没有执行()
    {
        var ev = BuybackTimeline.Build([P("2026-07-25", PlanStage.Proposal, lo: 200e8, hi: 400e8, cap: 573)]);

        var e = Assert.Single(ev);
        Assert.Contains("计划 200~400 亿", e.Text);
        Assert.Contains("573", e.Text);
    }
}
