using System.Text.RegularExpressions;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 把年报里的**交易对手名**还原成 A 股代码（2026-09-08）。纯计算，不碰库、不联网。
///
/// ════ 只做两档，刻意的 ════
///   精确    ：去空白后的全称一字不差
///   归一化  ：两边都做「去空白 → 去括号内容 → 去公司后缀」之后相等
///
/// <b>不做"含简称"的模糊匹配</b>。实测过：加上它命中率从 7.3% 升到 12.5%，只多 5 个百分点，
/// 却会造出一批"看着像、其实不是"的边。产业链数据有错边比没有更糟——没有的时候你知道自己
/// 不知道，有错边的时候你会照着它做判断。
///
/// ════ 实测的天花板 ════
/// 3500 行抽样里真名 1418 条（40.5%），这两档命中 104 条＝真名的 <b>7.3%</b>，
/// 折算约 1865 条边/年。没命中的绝大多数是**非上市小公司**（"苏州同成化工"），
/// 那是数据本身的性质，不是算法不够好。剩下有提升空间的是"上市公司的子公司"
/// （"中国建筑第六工程局"），但那要靠参控股公司名单，东财 F10 里没有这份数据。
/// </summary>
public static class PartnerNameMatcher
{
    /// <summary>匹配结果的档次，落库进 <c>match_type</c>。</summary>
    public const string Exact = "exact";
    public const string Normalized = "normalized";

    /// <summary>
    /// 第三档（2026-09-11）：对手名是**某家上市公司的子公司**，归并到母公司代码。
    /// 名单来自年报「合并财务报表范围」那张表，见 CompanySubsidiary。
    ///
    /// ⚠ **置信度低于前两档**，因为它是个假设：子公司跟你做生意不等于母公司跟你做生意。
    ///   前两档是"这两个名字指同一个法人主体"，这一档是"这两个法人有控制关系"——
    ///   完全不同的断言。单独标一档就是为了用的时候能把它们分开。
    /// </summary>
    public const string Subsidiary = "subsidiary";

    /// <summary>
    /// 匿名披露的对手名——**不参与匹配**。
    /// 万一真有公司叫"第一名"，也不能让"第一名"这种占位符去撞上它。
    /// </summary>
    private static readonly Regex Anonymous = new(
        // ⚠ 数字有两种写法，两种都要认："前5名客户" 和 "前五名客户" 都是占位符。
        //   只写 \d 的话中文那种会漏掉，然后跑去跟真公司名做匹配。
        @"^(第[\d一二三四五六七八九十百]+名|客户\s*\d+|供应商\s*\d+|其余(客户|供应商)|" +
        @"前[\d一二三四五六七八九十]+名.*|合计|" +
        @"客户[A-Za-z]|供应商[A-Za-z]|客户[一二三四五六七八九十]|供应商[一二三四五六七八九十]|" +
        @"单位\d+|[A-Z]公司)$",
        RegexOptions.Compiled);

    private static readonly Regex Bracketed = new(@"[（(][^）)]*[）)]", RegexOptions.Compiled);

    private static readonly Regex Suffix = new(
        @"(股份)?有限(责任)?公司$|集团有限公司$|有限公司$|集团$|公司$", RegexOptions.Compiled);

    /// <summary>去掉所有空白（含全角空格）。精确匹配比的就是这个。</summary>
    public static string Compact(string s) => s.Replace(" ", "").Replace("\t", "")
                                               .Replace("　", "").Replace("\r", "").Replace("\n", "");

    /// <summary>归一化：去空白 → 去括号内容 → 去公司后缀。</summary>
    public static string Normalize(string s) => Suffix.Replace(Bracketed.Replace(Compact(s), ""), "");

    public static bool IsAnonymous(string name)
        => string.IsNullOrWhiteSpace(name) || Anonymous.IsMatch(name.Trim());

    /// <summary>
    /// 建索引。<paramref name="companies"/> 是 (代码, 全称) 列表。
    ///
    /// ⚠ 同一个全称会命中多个代码——**A 股和 B 股是同一家公司**，全称一模一样。
    ///   实测："京东方科技集团股份有限公司"同时对应 000725(A) 和 200725(B)。
    ///   不处理的话结果取决于哪条后写入，而我们要的永远是 A 股那个。
    /// </summary>
    public static (Dictionary<string, string> ByFull, Dictionary<string, string> ByNorm)
        BuildIndex(IEnumerable<(string Code, string FullName)> companies)
    {
        var byFull = new Dictionary<string, string>(StringComparer.Ordinal);
        var byNorm = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (code, full) in companies)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(full)) continue;
            Put(byFull, Compact(full), code);
            Put(byNorm, Normalize(full), code);
        }
        return (byFull, byNorm);

        static void Put(Dictionary<string, string> map, string key, string code)
        {
            if (key.Length == 0) return;
            if (!map.TryGetValue(key, out var existing)) { map[key] = code; return; }
            map[key] = Preferred(existing, code);
        }
    }

    /// <summary>
    /// 同一全称的两个代码，选哪个。B 股（<c>200</c>/<c>900</c> 开头）让位给 A 股。
    /// 都不是 B 股就保留先到的——结果必须跟输入顺序无关，否则不可复现。
    /// </summary>
    public static string Preferred(string a, string b)
    {
        bool ba = IsBShare(a), bb = IsBShare(b);
        if (ba && !bb) return b;
        if (bb && !ba) return a;
        return string.CompareOrdinal(a, b) <= 0 ? a : b;
    }

    private static bool IsBShare(string code)
        => code.StartsWith("200", StringComparison.Ordinal)
        || code.StartsWith("900", StringComparison.Ordinal);

    /// <summary>
    /// 匹配一个对手名。返回 (代码, 档次)；匹配不上返回 (null, null)——**不猜**。
    /// </summary>
    public static (string? Code, string? MatchType) Match(
        string partnerName,
        Dictionary<string, string> byFull,
        Dictionary<string, string> byNorm,
        Dictionary<string, string>? bySubsidiary = null)
    {
        if (IsAnonymous(partnerName)) return (null, null);

        var name = partnerName.Trim();
        if (byFull.TryGetValue(Compact(name), out var c1)) return (c1, Exact);
        if (byNorm.TryGetValue(Normalize(name), out var c2)) return (c2, Normalized);

        // ⚠ 子公司这一档**必须排在最后**：一个名字如果本身就是上市公司（前两档命中），
        //   那它就是它自己，不该被归并到谁的名下。只有前两档都不认识才轮到这一档。
        if (bySubsidiary != null)
        {
            if (bySubsidiary.TryGetValue(Compact(name), out var c3)) return (c3, Subsidiary);
            if (bySubsidiary.TryGetValue(Normalize(name), out var c4)) return (c4, Subsidiary);
        }
        return (null, null);
    }

    /// <summary>
    /// 给子公司名单建索引：全称和归一化两个 key 都指向母公司代码，装进同一个字典
    /// （两种 key 不会撞——归一化只会更短，撞了也是同一家）。
    /// </summary>
    public static Dictionary<string, string> BuildSubsidiaryIndex(
        IEnumerable<(string Name, string ParentCode)> subsidiaries)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, parent) in subsidiaries)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parent)) continue;
            var full = Compact(name);
            var norm = Normalize(name);
            // 同一个 key 落到不同母公司 = 歧义，**两边都删掉**。硬挑一个就是在造错边。
            if (full.Length > 0) Put(map, full, parent);
            if (norm.Length > 0 && norm != full) Put(map, norm, parent);
        }

        // 把冲突标记（空串）清掉——歧义的 key 一个都不留
        return map.Where(kv => kv.Value.Length > 0)
                  .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        static void Put(Dictionary<string, string> map, string key, string parent)
        {
            if (map.TryGetValue(key, out var existing) && existing != parent)
                map[key] = "";          // 冲突标记，下面统一清掉
            else map[key] = parent;
        }
    }
}
