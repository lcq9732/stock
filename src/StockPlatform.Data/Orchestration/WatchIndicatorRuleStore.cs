using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// <c>data/watch-indicator-rules.json</c> 的读取 + 首次写模板。见 doc/watch-item-design.md §5、§6。
///
/// ════ 为什么规则是配置不是代码 ════
/// 「哪个板块该看哪个指标」会**随认识变化频繁调整**——今天觉得锂电该看碳酸锂，明天发现还该看
/// 六氟磷酸锂。改一条要重编译发版不可接受。
///
/// ════ 为什么是 JSONC ════
/// 跟 <see cref="FetcherSettings"/> 同一套路数（<c>feedback_config_self_documenting</c>）：
/// 读取允许 <c>//</c> 注释和末尾逗号；文件不存在时写一份**每条可选规则都是"去掉 // 就能用"的
/// 完整行**的模板，照着复制就完成配置，不用去猜板块码和指标码怎么填。
///
/// ════ 坏文件不抛异常 ════
/// 读不了就返回空规则 + 一条告警。配置坏了不该拦住整个日更计划跑不动；而空规则传到
/// <c>ReplaceRuleLinks</c> 是空操作，会**保留上一轮的映射**，不会把表清空。
/// 但必须告警——否则就是静默失效，跟这套设计里反复要躲的那个坑一模一样。
/// </summary>
public static class WatchIndicatorRuleStore
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>读规则。返回 (规则, 告警)；读不到/坏掉一律返回空规则 + 告警，不抛。</summary>
    public static (List<BoardIndicatorRule> Rules, List<string> Warnings) Read(string path)
    {
        var warnings = new List<string>();
        var rules = new List<BoardIndicatorRule>();
        try
        {
            if (!File.Exists(path))
            {
                warnings.Add($"规则文件不存在：{path}（已写入模板，去掉 // 即可启用）");
                return (rules, warnings);
            }

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                warnings.Add($"规则文件是空的：{path}");
                return (rules, warnings);
            }

            using var doc = JsonDocument.Parse(text, Options);
            if (!doc.RootElement.TryGetProperty("BoardIndicatorRules", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                warnings.Add("规则文件里没有 BoardIndicatorRules 数组，本轮 0 条规则。");
                return (rules, warnings);
            }

            int index = 0;
            foreach (var item in arr.EnumerateArray())
            {
                index++;
                var boardCode = Str(item, "BoardCode");
                var boardName = Str(item, "BoardName");
                var reason = Str(item, "Reason");

                var ids = new List<string>();
                if (item.TryGetProperty("IndicatorIds", out var idArr) && idArr.ValueKind == JsonValueKind.Array)
                    foreach (var v in idArr.EnumerateArray())
                        if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                            ids.Add(s);

                if (ids.Count == 0)
                {
                    warnings.Add($"规则 #{index}「{boardName}」({boardCode}) 没写 IndicatorIds，跳过。");
                    continue;
                }
                rules.Add(new BoardIndicatorRule(boardCode, boardName, ids, reason));
            }
        }
        catch (Exception ex)
        {
            // 解析失败要报清楚是哪个文件坏了，否则只会看到"0 条规则"，查不到源头
            warnings.Add($"规则文件读不了（{ex.Message}）：{path}，本轮 0 条规则，库里保留上一轮的映射。");
            return ([], warnings);
        }
        return (rules, warnings);
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    /// <summary>
    /// 文件不存在时写一份模板。**已存在就一个字都不动**——这个文件是人手改的，
    /// 程序改写它会丢掉注释（JSON 序列化器不保留注释），那正是 <see cref="FetcherSettings"/>
    /// 类注释里记的那次事故。
    /// </summary>
    public static void EnsureTemplate(string path)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Template, new System.Text.UTF8Encoding(true));
    }

    /// <summary>
    /// 模板。**每条规则都是完整的、去掉 <c>//</c> 就能用的配置**，不是字段说明。
    ///
    /// ════ 哪些默认启用 ════
    /// 分界线是**有没有实证支撑**，不是"是不是判断"：
    /// · 默认启用的那两条修的是**已证明的缺陷**——东财自动映射只给上游资源股挂原材料价格，
    ///   实测锂电池板块 33 只成分股里只有 1 只有映射、碳酸锂指数只挂给 6 只上游锂矿、
    ///   宁德时代一个都没有，而锂价正是电池厂的核心成本变量。
    /// · 注释掉的那条是**反例留档**：整车产销量是行业总需求，跟"某家在电池里占多少份额"
    ///   是两件事——份额掉了而整车销量涨着，它一点信号都不给。
    ///
    /// 全部默认注释掉是不行的：那等于这一项上线后零效果，而且人不知道要来改这个文件，
    /// 就成了**静默失效**——正是这套设计反复要躲的那类问题。
    /// </summary>
    private const string Template = """
        {
          // ══ 板块 → 该盯的行业指标 ══
          // 见 doc/watch-item-design.md。这张表回答的是"这只票该盯什么外部环境变量"。
          //
          // 为什么需要它：东财自带的映射（StockIndustryIndicator）只给**上游资源股**挂原材料价格。
          // 实测锂电池板块 33 只成分股里只有 1 只有映射，碳酸锂指数只挂给 6 只上游锂矿，
          // 宁德时代一个指标都没挂——而锂价正是它的核心成本变量。
          //
          // 怎么填：
          //   BoardCode     东财板块码，查库 SELECT board_code,name FROM Board;
          //                 也可以 SELECT * FROM StockIndustryEm WHERE code='300750';
          //   IndicatorIds  指标码，查库 SELECT indicator_id,name,frequency FROM IndustryIndicator;
          //                 顺序即优先级（第一个 weight=1）
          //   BoardName     只为了让这个文件能读懂，匹配一律按 BoardCode
          //
          // 规则顺序即优先级：同一只票被多条命中时**保留先命中的那条**，所以越具体的放越前面
          // （"锂电池 BK1303" 要排在 "电池 BK1033" 前面）。
          //
          // 指标码或板块码写错不会静默失效——跑完会在日志里逐条告警。
          //
          // 不想要某条就把它注释掉重跑：规则派生的行每轮整组重建，撤掉规则它自然就没了。
          // 你手工往 StockWatchIndicator 里加的行（origin='manual'）不受这个文件影响。

          "BoardIndicatorRules": [

            // 锂电池（三级行业）：中游电池厂，碳酸锂是主要原材料成本。
            // 默认启用——东财自带映射只给上游资源股挂原材料价，这个板块 33 只成分股里
            // 只有 1 只有映射、宁德时代一个都没有，这条就是来补这个洞的。覆盖约 107 只票。
            { "BoardCode": "BK1303", "BoardName": "锂电池", "IndicatorIds": ["EMI00662659"], "Reason": "碳酸锂是主要原材料成本" },

            // 电池（二级行业，比锂电池粗一级，放在它后面兜底）
            { "BoardCode": "BK1033", "BoardName": "电池", "IndicatorIds": ["EMI00662659"], "Reason": "碳酸锂是主要原材料成本" },

            // ── 下面是**反例**，默认注释掉，留着说明什么样的映射不该挂 ──
            // 整车产销量是「行业总需求」，跟「某家在电池里占多少份额」是两件事：
            // 份额掉了而整车销量涨着，这个指标一点信号都不给。要盯份额得另找数据源。
            // { "BoardCode": "BK1015", "BoardName": "汽车整车", "IndicatorIds": ["EMI00100216"], "Reason": "整车产销是行业总需求" },

          ]
        }

        """;
}
