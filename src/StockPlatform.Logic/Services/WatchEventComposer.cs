using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 把一只票各路事项的事件**排成最终叙述**（2026-09-14）。纯计算，可单测。
///
/// ════ 排序规则（用户定的）════
///   · **事项与事项之间按日期倒序** —— 比的是各自**主行**那个日期；
///   · **事项内部按日期正序**，主行不缩进、后续进展缩进一级。
///
/// ⚠ 组间比的是 <b>主行</b>（第一行）的日期，不是组内最新那条（2026-09-14 返工）。
/// 一开始按组内最新排，理由是"上周刚买了一笔不该沉底"——但屏幕上左边那列日期读出来是
/// <c>07-25 → 09-03 → 09-11 → 09-11 → 08-04</c>，**不是单调的**，一眼就觉得排错了。
/// 人是顺着主行那列日期读的：主行日期就是这件事的日期，它必须从上往下递减。
///
/// <code>
/// 2026-10-08  限售解禁  100万股，占流通 10%
/// 2026-08-15  业绩预告  预增，净利同比 54~59%
/// 2026-07-25  回购方案  计划 200~400 亿，上限 573 元   ← 主行 07-25，所以排在 08-15 之后
///    2026-09-03  回购进展  尚未实施                  ← 组内仍是正序，缩进
///    2026-09-11  首次回购  累计 2 亿元
/// 2026-09-11  碳酸锂指数 338.85（较 09-10 -3.62%）    ← 行业指标一律垫底，见下
/// </code>
///
/// **行业指标是唯一的例外：一律排最后**（所以它的日期不参与上面那条单调性）。
/// 它天天更新，按日期排就天天霸占第一行，把回购、解禁这些真正发生在这家公司身上的事顶下去。
/// 它是影响这只票的**外部环境**，不是这只票出了什么事。
/// </summary>
public static class WatchEventComposer
{
    /// <summary>
    /// 按上面的规则排。<paramref name="events"/> 里每个事项的内部顺序**原样保留**
    /// （<see cref="BuybackTimeline"/> 已经排好了主行+缩进），这里只决定组与组的先后。
    /// </summary>
    public static IReadOnlyList<WatchEvent> Compose(IEnumerable<WatchEvent> events)
    {
        // 分组键＝(类别, 轮次)：一只票的两轮回购是两组，各自独立参与排序。
        // 锚＝**主行**（第一行）的日期——屏幕上那列日期必须从上往下递减，见类注释。
        var groups = events
            .GroupBy(e => (e.Category, e.Round))
            .Select(g => (Anchor: g.First().Date, Items: g.ToList()))
            // ⚠ 行业指标一律沉到底，不参与日期竞争（2026-09-14）：
            // 它天天更新，按日期排就天天霸占第一行，把回购、解禁这些**真正发生在这家公司身上**
            // 的事顶下去。它是"影响这只票的外部环境"，不是"这只票出了什么事"，读的顺序也该在后。
            .OrderBy(g => g.Items[0].Category == WatchCategory.Indicator ? 1 : 0)
            .ThenByDescending(g => g.Anchor)
            .ToList();

        return groups.SelectMany(g => g.Items).ToList();
    }

    /// <summary>
    /// 这只票所有事件里最紧要的那个档。界面上一股一行，得有个代表档。
    /// A &lt; B &lt; C，取最高的。
    /// </summary>
    public static string TopPriority(IEnumerable<string> priorities)
    {
        var all = priorities.ToList();
        if (all.Contains("A")) return "A";
        if (all.Contains("B")) return "B";
        return all.Count > 0 ? "C" : "";
    }
}
