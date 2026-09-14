using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 一只票的事件叙述怎么排（2026-09-14）。规则是用户定的：
/// **事项与事项之间按日期倒序，事项内部按日期正序**。
/// </summary>
public class WatchEventComposerTests
{
    private static WatchEvent E(string date, string category, string text,
        int? round = null, int indent = 0)
        => new(DateTime.Parse(date), category, text, round, Indent: indent);

    [Fact]
    public void 空输入_不炸()
        => Assert.Empty(WatchEventComposer.Compose([]));

    /// <summary>
    /// ★ 事项之间比的是**主行**（第一行）的日期，不是组内最新那条（2026-09-14 返工）。
    ///
    /// 回购组的主行是方案 07-25，组里最新的是 09-11 的首次回购。按组内最新排的话，
    /// 屏幕上左边那列日期读出来是 07-25 → 09-11 → 08-15，**不单调**，一眼就是错的。
    /// 人顺着主行那列读，主行日期就是这件事的日期，必须从上往下递减。
    /// </summary>
    [Fact]
    public void 组间按主行日期倒序()
    {
        var events = new[]
        {
            E("2026-07-25", WatchCategory.Buyback, "回购方案", round: 1),
            E("2026-09-11", WatchCategory.Buyback, "首次回购", round: 1, indent: 1),
            E("2026-08-15", WatchCategory.EarningsForecast, "业绩预告 预增"),
        };

        var ev = WatchEventComposer.Compose(events);

        // 业绩预告（主行 08-15）排在回购（主行 07-25）之前
        Assert.Equal("业绩预告 预增", ev[0].Text);
        Assert.Equal("回购方案", ev[1].Text);
        Assert.Equal("首次回购", ev[2].Text);
    }

    /// <summary>★ 主行那列日期必须**从上往下单调递减**（行业指标除外，它垫底）。</summary>
    [Fact]
    public void 主行日期单调递减()
    {
        var events = new[]
        {
            E("2026-07-25", WatchCategory.Buyback, "回购方案", round: 1),
            E("2026-09-03", WatchCategory.Buyback, "回购进展", round: 1, indent: 1),
            E("2026-09-11", WatchCategory.Buyback, "首次回购", round: 1, indent: 1),
            E("2026-09-11", WatchCategory.Quote, "跌破 MA20"),
            E("2026-08-04", WatchCategory.Dividend, "分红方案"),
        };

        var mains = WatchEventComposer.Compose(events)
            .Where(e => e.Indent == 0 && e.Category != WatchCategory.Indicator)
            .Select(e => e.Date)
            .ToList();

        Assert.Equal([new DateTime(2026, 9, 11), new DateTime(2026, 8, 4), new DateTime(2026, 7, 25)], mains);
    }

    /// <summary>★ 组内顺序原样保留——BuybackTimeline 已经排好了主行+缩进，这里不许重排。</summary>
    [Fact]
    public void 组内顺序原样保留()
    {
        var events = new[]
        {
            E("2026-07-30", WatchCategory.Buyback, "回购方案", round: 1),
            E("2026-07-31", WatchCategory.Buyback, "首次回购", round: 1, indent: 1),
            E("2026-09-11", WatchCategory.Buyback, "回购完毕", round: 1, indent: 1),
        };

        var ev = WatchEventComposer.Compose(events);

        Assert.Equal(["回购方案", "首次回购", "回购完毕"], ev.Select(x => x.Text));
    }

    /// <summary>★ 同一类的两轮回购是**两组**，各自独立参与排序，不会混成一团。</summary>
    [Fact]
    public void 两轮回购是两组()
    {
        var events = new[]
        {
            E("2026-07-16", WatchCategory.Buyback, "第1轮回购方案", round: 2),
            E("2026-08-01", WatchCategory.Buyback, "第1轮回购完毕", round: 2, indent: 1),
            E("2026-08-01", WatchCategory.Buyback, "第2轮回购方案", round: 1),
            E("2026-09-02", WatchCategory.Buyback, "第2轮回购进展", round: 1, indent: 1),
            E("2026-08-20", WatchCategory.ShareLift, "限售解禁"),
        };

        var ev = WatchEventComposer.Compose(events);

        // 按主行排：解禁（08-20）→ 第 2 轮（方案 08-01）→ 第 1 轮（方案 07-16）
        Assert.Equal("限售解禁", ev[0].Text);
        Assert.Equal("第2轮回购方案", ev[1].Text);
        Assert.Equal("第2轮回购进展", ev[2].Text);
        Assert.Equal("第1轮回购方案", ev[3].Text);
        Assert.Equal("第1轮回购完毕", ev[4].Text);
    }

    /// <summary>
    /// ★ 行业指标一律垫底，哪怕它是今天的。
    ///
    /// 碳酸锂指数天天更新，按日期排就天天霸占第一行，把回购、解禁这些**真正发生在
    /// 这家公司身上**的事顶下去。它是影响这只票的外部环境，不是这只票出了什么事。
    /// </summary>
    [Fact]
    public void 行业指标垫底_不参与日期竞争()
    {
        var events = new[]
        {
            E("2026-09-11", WatchCategory.Indicator, "碳酸锂指数 338.85（较 09-10 -3.62%）"),
            E("2026-07-25", WatchCategory.Buyback, "回购方案", round: 1),
            E("2026-08-04", WatchCategory.Dividend, "分红方案"),
        };

        var ev = WatchEventComposer.Compose(events);

        Assert.Equal(WatchCategory.Dividend, ev[0].Category);     // 08-04
        Assert.Equal(WatchCategory.Buyback, ev[1].Category);      // 07-25
        Assert.Equal(WatchCategory.Indicator, ev[2].Category);    // 09-11 却排最后
    }

    /// <summary>指标之间仍然按日期倒序——垫底的是整块，块内不乱。</summary>
    [Fact]
    public void 多条指标之间仍按日期倒序()
    {
        var events = new[]
        {
            E("2026-09-21", WatchCategory.Indicator, "指标A"),
            E("2026-09-11", WatchCategory.Quote, "跌破 MA20"),
        };

        var ev = WatchEventComposer.Compose(events);

        Assert.Equal(WatchCategory.Quote, ev[0].Category);
        Assert.Equal(WatchCategory.Indicator, ev[1].Category);
    }

    /// <summary>一条不落——排序不许吞掉事件。</summary>
    [Fact]
    public void 事件一条不少()
    {
        var events = new[]
        {
            E("2026-01-01", WatchCategory.Dividend, "分红"),
            E("2026-02-01", WatchCategory.Lhb, "龙虎榜"),
            E("2026-03-01", WatchCategory.Quote, "跌破 MA20"),
            E("2026-04-01", WatchCategory.HolderChange, "股东减持"),
        };

        Assert.Equal(4, WatchEventComposer.Compose(events).Count);
    }
}
