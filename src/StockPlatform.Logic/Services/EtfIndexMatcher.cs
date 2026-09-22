namespace StockPlatform.Logic.Services;

/// <summary>
/// ETF → 指数的**名称匹配**（2026-09-22 从 <c>FetchOrchestrator.BuildEtfIndexMap</c> 抽出来）。
/// 纯计算、零 IO——抽出来是因为它有两个调用方（【ETF指数映射】那一项，和【指数成分/权重】末尾
/// 顺带重建一次），两边各写一份迟早分叉。
///
/// ════ 怎么匹配 ════
/// ETF 名称几乎都含指数名（"沪深300ETF华泰" → 沪深300），拿 <c>ETF</c> 之前那段去指数清单里找：
/// <list type="bullet">
/// <item><c>exact</c>：名称完全等于某个指数名；</item>
/// <item><c>contains</c>：互相包含；</item>
/// <item><c>unmatched</c>：都找不到——绝大多数是债券/货币/黄金 ETF，本来就没有 A股成分。</item>
/// </list>
///
/// ⚠ **长指数名优先**：不排序的话「中证500」会被「中证50」抢先命中。
/// </summary>
public static class EtfIndexMatcher
{
    /// <summary>一条匹配结果。<paramref name="IndexCode"/> 为 null 就是没匹配上。</summary>
    public readonly record struct EtfIndexMatch(string EtfCode, string? IndexCode, string MatchType);

    public const string Exact = "exact";
    public const string Contains = "contains";
    public const string Unmatched = "unmatched";

    /// <summary>
    /// <paramref name="etfs"/> 是本地的 (代码, 名称)；<paramref name="indexes"/> 是指数清单的 (代码, 名称)。
    /// 两边都由调用方给，这里不碰任何库。
    /// </summary>
    public static List<EtfIndexMatch> Match(
        IEnumerable<(string Code, string Name)> etfs,
        IEnumerable<(string Code, string Name)> indexes)
    {
        // ⚠ 长名在前：否则「中证500」会被「中证50」抢先命中
        var idx = indexes.OrderByDescending(i => i.Name.Length).ToList();

        var result = new List<EtfIndexMatch>();
        foreach (var e in etfs)
        {
            var core = e.Name.Split("ETF")[0].Trim();   // "ETF" 之前那段当指数名候选
            string? found = null;
            string matchType = Unmatched;
            if (core.Length >= 2)
            {
                var exact = idx.FirstOrDefault(i => i.Name == core);
                if (exact.Code != null) { found = exact.Code; matchType = Exact; }
                else
                {
                    // 两个字以下的指数名不参与包含匹配——太短会乱命中
                    var contains = idx.FirstOrDefault(
                        i => i.Name.Length >= 2 && (core.Contains(i.Name) || i.Name.Contains(core)));
                    if (contains.Code != null) { found = contains.Code; matchType = Contains; }
                }
            }
            result.Add(new EtfIndexMatch(e.Code, found, matchType));
        }
        return result;
    }
}
