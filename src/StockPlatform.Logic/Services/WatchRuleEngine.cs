using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>规则跑一轮的输入——全部由调用方从库和 json 里读好喂进来，引擎自己不碰任何 IO。</summary>
/// <param name="ActiveCodes">主动仓（watchlist.json）里的票 → 名称。</param>
/// <param name="CoreCodes">底仓（core-positions.json）里的票 → 名称。</param>
/// <param name="OpenPlans">**还没结束**的方案（<c>PlanAnnouncement</c> 里有「方案」且其后无「完毕/终止」）。</param>
/// <param name="WatchIndicators">每只票该盯的行业指标（<c>StockWatchIndicator</c>，M1 的产物）。</param>
public sealed record WatchRuleInput(
    IReadOnlyDictionary<string, string> ActiveCodes,
    IReadOnlyDictionary<string, string> CoreCodes,
    IReadOnlyDictionary<string, PlanAnnouncement> OpenPlans,
    IReadOnlyDictionary<string, List<WatchIndicatorLink>> WatchIndicators);

/// <summary>一轮重算的结果。</summary>
/// <param name="Items">重算后**应有的全部观察项**（派生的 + 原样带回的手写项）。</param>
/// <param name="Added">本轮新挂上的（派生）。</param>
/// <param name="Expired">本轮摘掉的（派生）——L1 的核心价值就在这一项。</param>
public sealed record WatchRuleResult(
    IReadOnlyList<WatchItem> Items,
    IReadOnlyList<WatchItem> Added,
    IReadOnlyList<WatchItem> Expired);

/// <summary>
/// L1 派生：按画像自动**挂**观察项，条件消失自动**摘**。见 doc/watch-item-design.md §5。
/// **纯计算**，可直接单测。
///
/// ════ 核心价值是「摘」不是「挂」 ════
/// 手工清单没人更新。回购方案实施完毕后就不该再盯，**而这个摘除动作人一定会忘**。
/// 所以每轮都把派生项整组重建：算出来该有的就留着，不该有的自然就没了。
///
/// ════ 硬边界：手写项一行都不许碰 ════
/// <c>Origin=手写</c> 的项原样带回结果里，不参与重建。不划这条线，规则跑一次就把人写的冲掉了，
/// 而且跟 <c>StockWatchIndicator</c> 那个坑一样——**界面上看不出来**。
/// </summary>
public static class WatchRuleEngine
{
    /// <summary>
    /// 重算派生项。<paramref name="existing"/> 是库里现有的全部观察项（含手写的）。
    /// </summary>
    public static WatchRuleResult Rebuild(IReadOnlyList<WatchItem> existing, WatchRuleInput input)
    {
        var manual = existing.Where(i => i.Origin == WatchOrigin.Manual).ToList();
        var oldDerived = existing.Where(i => i.Origin != WatchOrigin.Manual).ToList();

        var derived = new List<WatchItem>();

        // 持仓层：一只票可能同时在两个列表里（不该，但数据上防一手）——底仓优先，
        // 因为底仓的纪律更宽（不设止损），误把底仓当主动仓会触发一堆该忽略的卖出信号。
        string PositionOf(string code) =>
            input.CoreCodes.ContainsKey(code) ? PositionKind.Core
            : input.ActiveCodes.ContainsKey(code) ? PositionKind.Active
            : PositionKind.None;

        var allCodes = new Dictionary<string, string>();
        foreach (var (c, n) in input.CoreCodes) allCodes[c] = n;
        foreach (var (c, n) in input.ActiveCodes) allCodes.TryAdd(c, n);

        foreach (var (code, name) in allCodes)
        {
            var pos = PositionOf(code);

            // ── 规则①：有未结束的方案 → 挂进展监控（A 档）──
            // 摘除条件是隐式的：方案一旦「完毕/终止」，就不在 OpenPlans 里了，
            // 下一轮重建时这条自然消失。**这就是自动摘除的全部实现**。
            if (input.OpenPlans.TryGetValue(code, out var plan))
            {
                // 方案的三个关键数都摆出来：**计划花多少钱**、价格上限、公告日。
                // 只写价格上限不够——"计划 4~8 亿"才是判断这家有多认真的第一个数
                //（2026-09-11 用户反馈）。方案参数由 GetOpenPlans 从「方案」那条补齐，
                // 因为最新一条通常是「进展」、它身上没有这些字段。
                var bits = new List<string> { $"{plan.AnnounceDate:yyyy-MM-dd} 公告" };
                if (plan.PlanAmountLow is { } lo && plan.PlanAmountHigh is { } hi && hi > 0)
                    bits.Add($"计划 {lo / 1e8:0.##}~{hi / 1e8:0.##} 亿");
                if (plan.PlanCapPrice is { } p) bits.Add($"价格上限 {p:0.##} 元");

                derived.Add(new WatchItem
                {
                    Code = code, Name = name, Layer = WatchLayer.L1, Origin = WatchOrigin.Derived,
                    Position = pos, Kind = WatchKind.PlanStage, Expr = plan.Kind,
                    Op = WatchOp.StageChange,
                    Reason = $"{plan.Kind}方案进行中（{string.Join("，", bits)}）",
                });
            }

            // ── 规则②：挂了行业指标 → 盯它（B 档）──
            if (input.WatchIndicators.TryGetValue(code, out var inds))
            {
                foreach (var ind in inds.OrderBy(i => i.Weight))
                {
                    derived.Add(new WatchItem
                    {
                        Code = code, Name = name, Layer = WatchLayer.L1, Origin = WatchOrigin.Derived,
                        Position = pos, Kind = WatchKind.IndustryIndicator, Expr = ind.IndicatorId,
                        Op = WatchOp.CrossDown,
                        Reason = ind.Reason,
                    });
                }
            }

            // ── 规则③：按持仓层挂各自的纪律项 ──
            if (pos == PositionKind.Active)
            {
                // 主动仓＝短线口径，回看 60 日、跌破 MA20 提示（跟短线法的 ±10% 对照，见
                // project_shortterm_focus）。底仓**绝不能**挂这条：底仓不设止损，
                // 挂上去每天早上都会跳出来叫你砍底仓。
                derived.Add(new WatchItem
                {
                    Code = code, Name = name, Layer = WatchLayer.L1, Origin = WatchOrigin.Derived,
                    Position = pos, Kind = WatchKind.PriceMA, Expr = "20",
                    // 取值是**对均线的偏离率**（%），所以阈值是 0：由正转负＝跌破。
                    // 见 SqliteWatchReadingSource.ReadPriceMa 的注释。
                    Op = WatchOp.CrossDown, Threshold = 0,
                    Reason = "跌破 MA20",
                });
            }
            else if (pos == PositionKind.Core)
            {
                // 底仓吃分红，盯的是分红能力不是价格。
                // 30 天窗口：分红方案从预案到实施跨度以月计，窗口太窄会在两次求值之间漏过去。
                derived.Add(new WatchItem
                {
                    Code = code, Name = name, Layer = WatchLayer.L1, Origin = WatchOrigin.Derived,
                    Position = pos, Kind = WatchKind.EventRecent, Expr = "Dividend",
                    Op = WatchOp.Within, Threshold = 30,
                    // ⚠ 措辞别写成"分红方案**变化**"（2026-09-11 改）：取值器取的是**最新一条**方案，
                    // 并没有跟上一版比较过。说成"变化"是在承诺一件没做的事——
                    // 真要判变化得存上一版方案再 diff，那是另一件事。
                    Reason = "分红方案",
                });
            }

            // ── 规则④：所有在册的票都挂 L0 兜底事件 ──
            // L0 是"所有票都有"，所以不按画像挑，挂就完了。
            foreach (var (kind, table, days, why) in L0Events)
            {
                derived.Add(new WatchItem
                {
                    Code = code, Name = name, Layer = WatchLayer.L0, Origin = WatchOrigin.Builtin,
                    Position = pos, Kind = kind, Expr = table,
                    Op = WatchOp.Within, Threshold = days, Reason = why,
                });
            }
        }

        // 派生项跟手写项撞同一个 (code, kind, expr) 时，**保留手写的**——
        // 它的 Reason/Thesis 是人写的，比规则生成的那句有信息量。跟 StockWatchIndicator 的
        // DO NOTHING 是同一个取舍。
        var manualKeys = manual.Select(Key).ToHashSet();
        derived = derived.Where(d => !manualKeys.Contains(Key(d))).ToList();

        var oldKeys = oldDerived.ToDictionary(Key, i => i);
        var newKeys = derived.ToDictionary(Key, i => i);

        // 沿用旧项的 ItemId 和创建时间：换了 id 的话触发历史就跟观察项对不上了。
        foreach (var d in derived)
            if (oldKeys.TryGetValue(Key(d), out var old))
            { d.ItemId = old.ItemId; d.CreatedDate = old.CreatedDate; d.Enabled = old.Enabled; }

        var added = derived.Where(d => !oldKeys.ContainsKey(Key(d))).ToList();
        var expired = oldDerived.Where(o => !newKeys.ContainsKey(Key(o))).ToList();

        var items = new List<WatchItem>(manual);
        items.AddRange(derived);
        return new WatchRuleResult(items, added, expired);
    }

    /// <summary>观察项的身份＝(票, 取值器, 参数)。ItemId 是随机的，不能拿来比。</summary>
    private static string Key(WatchItem i) => $"{i.Code}|{i.Kind}|{i.Expr}";

    /// <summary>
    /// L0 兜底事件表。**零新增数据**——这些表早就全市场抓着了，L0 缺的只是"读它们"。
    /// 这也是为什么设计文档的数据模型里一张 L0 的表都没有。
    ///
    /// ⚠ **两类时间语义必须分开**（2026-09-11 实机踩出来的，见 WatchKind 的注释）：
    /// 原来五张表共用一个取值器、一律 <c>MAX(日期)</c> + <c>StageChange</c>，结果一轮重算
    /// 产出 380 多条横跨 2011~2030 年的触发——解禁取到了 2030 年那次（它是**日程表**，
    /// MAX 就是最远的未来），龙虎榜取到了 2011 年（那只是"十五年没上过榜"）。
    ///
    /// 窗口天数也是判据的一部分：**没有窗口，"最新一条"就不是事件、只是现状**。
    /// </summary>
    private static readonly (string Kind, string Table, double Days, string Why)[] L0Events =
    [
        // ⚠ Reason 是**界面上直接显示的那句话**，只写"盯的是什么"，别写设计注解。
        //    原来业绩预告那条写成"L0：业绩预告（比财报早一个月以上）"——那个括号是解释
        //    "为什么值得盯"，结果被读成"已经提早了？还是计划提早？"（2026-09-11 用户反馈）。
        //    为什么值得盯属于这里的代码注释，不属于界面。

        // ── 未来日程：取最近一次**将来**的，提前 N 天提醒 ──
        // 解禁提前 30 天：够看到"下个月有一批解禁"，又不会把一年后的事天天摆在眼前。
        (WatchKind.ScheduleAhead, "ShareLift",        30, "限售解禁"),
        // 预约披露日提前 7 天：知道"这周要出报"就够，提前一个月没意义。
        (WatchKind.ScheduleAhead, "EarningsSchedule",  7, "定期报告预约披露"),

        // ── 已发生的事：只报最近 N 天内发生的 ──
        // 它比正式财报早一个月以上，是提前量最大的基本面信号。7 天窗口跨长假也不漏。
        // ⚠ 别跟上面那条【定期报告预约披露】搞混：这条是**正式财报前的简要说明**（预增/预亏多少），
        //   那条是**正式财报哪天发**。两张表、两回事。
        (WatchKind.EventRecent,   "EarningsForecast",  7, "业绩预告"),
        (WatchKind.EventRecent,   "HolderChange",      7, "股东增减持"),
        // 龙虎榜窗口收到 3 天——它天天有，窗口一宽就成刷屏。
        (WatchKind.EventRecent,   "Lhb",               3, "龙虎榜上榜"),
    ];
}
