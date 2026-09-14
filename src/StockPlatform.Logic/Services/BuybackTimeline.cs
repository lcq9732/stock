using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 把一只票的全部回购公告拆成**一轮一轮**，再叙述成事件行（2026-09-14）。纯计算，可单测。
///
/// ════ 为什么必须按轮拆 ════
/// 一只票可以做好几轮回购。实测永和股份 12 条公告跨两轮：
/// <code>
/// 07-16 方案 → 07-21 首次回购 → 07-25 进展 → 08-01 完毕      ← 第 1 轮
/// 08-01 方案 → 08-04 首次回购 → 08-06 进展 → … → 09-02 进展  ← 第 2 轮（同一天接着发）
/// </code>
/// 不拆的话两轮的金额会读成一条连续的线，"完毕"之后又冒出"首次回购"更是读不懂。
///
/// ════ 同一天可能有两份方案 ════
/// 物产金轮 07-28 发方案（上限 12 元）、07-30 又发一份（上限 14 元）——那是**方案修订**
/// （价格上限随除权调整）。规则：同一轮里**取最后一份方案**的参数，前面的不另起一轮。
/// </summary>
public static class BuybackTimeline
{
    /// <summary>同一轮里超过这么多条进展就折叠成"首次…→ 末条"，中间省略。</summary>
    public const int FoldThreshold = 5;

    /// <summary>
    /// 拆轮 + 叙述。<paramref name="rows"/> 是这只票的全部回购公告（顺序不限）。
    ///
    /// 返回顺序（用户 2026-09-14 定的）：
    ///   · **轮与轮之间倒序** —— 最近那轮排最前；
    ///   · **轮内部正序**，方案是主行（Indent=0），执行过程缩进跟在后面（Indent=1）。
    /// <code>
    /// 2026-08-01  第2轮回购方案  计划 2~3 亿
    ///    2026-08-04  首次回购
    ///    2026-09-02  回购进展
    /// 2026-07-16  第1轮回购方案  计划 2~3 亿
    ///    2026-07-21  首次回购
    ///    2026-08-01  回购完毕
    /// </code>
    /// 一件事的来龙去脉顺着时间读才通顺，倒着读"完毕→进展→首次"是反直觉的。
    /// </summary>
    public static IReadOnlyList<WatchEvent> Build(IReadOnlyList<PlanAnnouncement> rows)
    {
        if (rows.Count == 0) return [];

        var sorted = rows.OrderBy(r => r.AnnounceDate)
                         .ThenBy(r => StageOrder(r.Stage))
                         .ToList();

        // ── 按轮切分 ──
        // 新一轮的起点＝一份「方案」公告，但**紧挨着的连续方案算同一轮**（方案修订）。
        //
        // ⚠ 同一天既收尾又开新轮时，靠 StageOrder 把**收尾排在方案之前**来保证
        // 那条收尾归上一轮（见 StageOrder 的注释，2026-09-14 实测踩到）。
        var rounds = new List<List<PlanAnnouncement>>();
        bool prevWasProposal = false;
        foreach (var r in sorted)
        {
            bool isProposal = r.Stage == PlanStage.Proposal;
            if (rounds.Count == 0 || (isProposal && !prevWasProposal))
                rounds.Add([]);
            rounds[^1].Add(r);
            prevWasProposal = isProposal;
        }

        // 轮次编号按时间正序给（第 1 轮是最早那次），这样"第 2 轮"读起来符合直觉；
        // 但**输出时轮与轮倒序**——最近那轮排最前。轮内部保持正序，不排序。
        var byRound = new List<(DateTime Anchor, List<WatchEvent> Events)>();
        for (int i = 0; i < rounds.Count; i++)
        {
            var ev = DescribeRound(rounds[i], i + 1, rounds.Count > 1).ToList();
            if (ev.Count == 0) continue;
            // 轮之间比的是这一轮**主行**（方案那条）的日期，跟 WatchEventComposer 一个口径：
            // 屏幕上主行那列日期要从上往下递减，用组内最新排会读成乱序（2026-09-14 返工）。
            byRound.Add((ev[0].Date, ev));
        }

        return byRound.OrderByDescending(x => x.Anchor)
                      .SelectMany(x => x.Events)
                      .ToList();
    }

    private static IEnumerable<WatchEvent> DescribeRound(
        List<PlanAnnouncement> round, int roundNo, bool showRound)
    {
        var tag = showRound ? $"第{roundNo}轮" : "";
        var result = new List<WatchEvent>();

        // ① 方案那一行：同一轮里**取最后一份**（前面的是被修订掉的旧版）
        var proposal = round.LastOrDefault(r => r.Stage == PlanStage.Proposal);
        if (proposal is not null)
        {
            var bits = new List<string>();
            if (proposal.PlanAmountLow is { } lo && proposal.PlanAmountHigh is { } hi && hi > 0)
                bits.Add($"计划 {lo / 1e8:0.##}~{hi / 1e8:0.##} 亿");
            if (proposal.PlanCapPrice is { } cap) bits.Add($"价格上限 {cap:0.##} 元");
            result.Add(new WatchEvent(proposal.AnnounceDate, WatchCategory.Buyback,
                $"{tag}回购方案" + (bits.Count > 0 ? "  " + string.Join("，", bits) : "")));
        }

        // ② 执行过程：首次回购 / 进展 / 达标 / 完毕 / 终止。
        //    **缩进一级、按时间正序**跟在方案后面——顺着读才看得出这轮买了多少、买到哪一步。
        var moves = round.Where(r => r.Stage != PlanStage.Proposal).ToList();
        if (moves.Count == 0) return result;

        if (moves.Count <= FoldThreshold)
        {
            foreach (var m in moves)
                result.Add(new WatchEvent(m.AnnounceDate, WatchCategory.Buyback, Describe(m), Indent: 1));
            return Promote(result, proposal);
        }

        // 超过阈值就折叠掉**中间**那些——它们的信息量是重复的（都是"截至X日累计Y亿"，
        // 末条已经包含了全部累计量）。但首尾保留、顺序不变，读起来仍是一条线。
        result.Add(new WatchEvent(moves[0].AnnounceDate, WatchCategory.Buyback,
            Describe(moves[0]), Indent: 1));
        result.Add(new WatchEvent(moves[^2].AnnounceDate, WatchCategory.Buyback,
            $"（中间 {moves.Count - 2} 次进展略）", Indent: 1));
        result.Add(new WatchEvent(moves[^1].AnnounceDate, WatchCategory.Buyback,
            Describe(moves[^1]), Indent: 1));
        return Promote(result, proposal);
    }

    /// <summary>
    /// 这一轮没有方案公告时，把**第一条执行**提成主行。
    ///
    /// ⚠ 2026-09-14 实机踩到：美的、格力库里只有进展没有方案（方案发在抓取窗口之前），
    /// 整组全是缩进行，界面上看就是一截悬空的缩进——缩进本来是"这是上一条的后续"的意思，
    /// 上面没有那一条，缩进就在骗人。提一条上来，读者至少知道这组从哪儿起头。
    /// </summary>
    private static List<WatchEvent> Promote(List<WatchEvent> result, PlanAnnouncement? proposal)
    {
        if (proposal is null && result.Count > 0) result[0] = result[0] with { Indent = 0 };
        return result;
    }

    /// <summary>一条执行记录的叙述。⚠ 金额 0 和 null 不是一回事，见 PlanAnnouncement。</summary>
    private static string Describe(PlanAnnouncement r)
    {
        var name = r.Stage switch
        {
            PlanStage.FirstBuy => "首次回购",
            PlanStage.Milestone => "回购达标",
            PlanStage.Done => "回购完毕",
            PlanStage.Terminated => "回购终止",
            _ => "回购进展",
        };
        var amt = r.CumAmount switch
        {
            null => "金额未读出",
            0 => "尚未实施",
            _ => $"累计 {r.CumAmount / 1e8:0.##} 亿元",
        };
        var asOf = r.AsOfDate is { } d ? $"截至 {d:MM-dd} " : "";
        return $"{name}  {asOf}{amt}";
    }

    /// <summary>
    /// 同一天多条公告时的次序。
    ///
    /// ⚠ **收尾（完毕/终止）必须排在方案之前**（2026-09-14 实测踩到）：
    /// 永和股份 08-01 当天先给第 1 轮发「完毕」、又给第 2 轮发「方案」。
    /// 要是把方案排在前面，那条完毕就会落到第 2 轮的开头，读出来是
    /// "第2轮：方案 → 完毕 → 首次回购"——完全不通。
    /// 收尾在前，它就正确地归到上一轮，方案干净地开启新一轮。
    /// </summary>
    private static int StageOrder(string stage) => stage switch
    {
        PlanStage.Done => 0,
        PlanStage.Terminated => 1,
        PlanStage.Proposal => 2,
        PlanStage.FirstBuy => 3,
        PlanStage.Progress => 4,
        PlanStage.Milestone => 5,
        _ => 9,
    };
}
