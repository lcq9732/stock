using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>规则跑一轮的结果：要写的行，加上配置有问题时的告警。</summary>
/// <param name="Links">本轮算出来的全部 <c>origin=rule</c> 行。</param>
/// <param name="Warnings">配置里指向不存在的板块/指标等——**必须报出来**，理由见类注释。</param>
public sealed record WatchIndicatorRuleResult(
    IReadOnlyList<WatchIndicatorLink> Links,
    IReadOnlyList<string> Warnings);

/// <summary>
/// L1 派生：把"板块 → 行业指标"的规则，铺成"个股 → 行业指标"的行。
/// 见 doc/watch-item-design.md §5。**纯计算，不碰库也不发请求**，所以能直接单测。
///
/// ════ 为什么要校验、而且校验失败要告警而不是静默跳过 ════
/// 配置里写错一个 <c>EMI00662659</c> 的字母，或者板块码打成了 BK1304，结果是**这条规则一行都不产出**。
/// 而"没产出"跟"这个板块本来就没成分股"在库里长得一模一样——没人会发现配置坏了，
/// 直到某天想不起来为什么宁德没挂上碳酸锂。所以指向不存在的东西一律进 <c>Warnings</c>。
///
/// 这跟【行业景气指标】那边"目录拿不全就整项放弃"是**不同的选择**：那边是快照，半批入库等于
/// 凭空少票；这边是派生，一条规则坏掉不该连累其余 N 条，所以是**逐条跳过 + 告警**。
/// </summary>
public static class WatchIndicatorRuleEngine
{
    /// <summary>
    /// 算出本轮应有的全部 <c>origin=rule</c> 行。
    /// </summary>
    /// <param name="boardMembers">
    /// 个股的板块归属，来自 <c>StockIndustryEm</c>：(6 位码, 板块码)。
    /// 一只票会出现多次（一/二/三级行业各一条），这里不关心层级——规则挂在哪一级就按哪一级匹配。
    /// </param>
    /// <param name="rules">配置里的规则。</param>
    /// <param name="knownIndicatorIds">
    /// <c>IndustryIndicator</c> 里真实存在的指标 ID。指标字典是东财给的，配置是人写的，**必须对一遍**。
    /// </param>
    public static WatchIndicatorRuleResult Build(
        IEnumerable<(string Code, string BoardCode)> boardMembers,
        IReadOnlyList<BoardIndicatorRule> rules,
        IReadOnlySet<string> knownIndicatorIds)
    {
        var warnings = new List<string>();

        // 板块 → 成分股。一次建好，避免每条规则都全表扫一遍。
        var byBoard = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, boardCode) in boardMembers)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(boardCode)) continue;
            if (!byBoard.TryGetValue(boardCode, out var list))
                byBoard[boardCode] = list = [];
            list.Add(code);
        }

        // (票, 指标) 去重：同一只票可能被多条规则命中（比如既在"电池"又在"锂电池"里），
        // **保留先命中的那条**——规则在配置里的顺序就是优先级，越靠前越具体。
        var seen = new HashSet<(string, string)>();
        var links = new List<WatchIndicatorLink>();

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.BoardCode))
            {
                warnings.Add($"规则「{rule.BoardName}」没填 BoardCode，跳过。");
                continue;
            }

            // 指标先校验：坏指标要报出来，哪怕这个板块根本没成分股也一样报——
            // 否则"板块空"会把"指标名写错"盖住，下次板块有票了才发现配置一直是坏的。
            var validIndicators = new List<string>();
            foreach (var indicatorId in rule.IndicatorIds)
            {
                if (knownIndicatorIds.Contains(indicatorId)) validIndicators.Add(indicatorId);
                else warnings.Add(
                    $"规则「{rule.BoardName}」({rule.BoardCode}) 指向的指标 {indicatorId} 在 IndustryIndicator 里不存在，跳过这个指标。");
            }

            if (!byBoard.TryGetValue(rule.BoardCode, out var members) || members.Count == 0)
            {
                warnings.Add($"规则「{rule.BoardName}」({rule.BoardCode}) 在 StockIndustryEm 里没有成分股，本轮产出 0 行。");
                continue;
            }

            if (validIndicators.Count == 0) continue;   // 已经在上面逐个报过了

            foreach (var code in members)
            {
                for (int i = 0; i < validIndicators.Count; i++)
                {
                    var key = (code, validIndicators[i]);
                    if (!seen.Add(key)) continue;
                    links.Add(new WatchIndicatorLink(
                        Code: code,
                        IndicatorId: validIndicators[i],
                        Weight: i + 1,
                        Origin: WatchIndicatorOrigin.Rule,
                        Reason: $"{rule.BoardName}：{rule.Reason}"));
                }
            }
        }

        return new WatchIndicatorRuleResult(links, warnings);
    }
}
