using System.IO;
using System.Reflection;

namespace StockPlatform.Analyzer;

/// <summary>
/// Code → 申万三级行业名称 (Shenwan level-3 industry name) lookup, loaded once from a bundled
/// static snapshot (Resources/IndustryMap.tsv) — NOT a live API call.
///
/// This exists because a live "which industry does this stock belong to" lookup turned out not
/// to be practically available: EastMoney's clist API has an f100 field documented as 行业, but
/// push2.eastmoney.com was too unreliable (network-limited/blocked repeatedly during
/// development — see doc/data-platform-design.md's EastMoney reliability notes) to depend on,
/// and Tencent's public endpoints (qt.gtimg.cn / the appstock kline API already used for bars)
/// don't expose industry classification at all in their documented fields. The user explicitly
/// chose a static table over depending on either.
///
/// Source: liuhuanyong/ChainKnowledgeGraph on GitHub (data/company_industry.json, Shenwan
/// industry classification), snapshotted 2026-07 — 4430 companies. This is a point-in-time
/// snapshot, not automatically refreshed: newly-listed stocks after the snapshot date, and all
/// 北交所 stocks (not covered by that source at all), show as unclassified. Replace
/// Resources/IndustryMap.tsv (format: "code\tindustry_name" per line) to refresh.
/// </summary>
public static class IndustryClassifier
{
    private const string UnknownLabel = "未分类";

    private static readonly Lazy<Dictionary<string, string>> Map = new(Load);

    public static string GetIndustry(string code) =>
        Map.Value.GetValueOrDefault(code, UnknownLabel);

    private static Dictionary<string, string> Load()
    {
        var result = new Dictionary<string, string>();
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("IndustryMap.tsv"));
        if (resourceName == null) return result;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return result;
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split('\t', 2);
            if (parts.Length != 2) continue;
            // Shenwan appends "Ⅰ/Ⅱ/Ⅲ" to a level-3 name only when it would otherwise collide
            // with its parent level-2 name — meaningless disambiguation noise for end users here.
            var name = parts[1].TrimEnd('Ⅰ', 'Ⅱ', 'Ⅲ');
            result[parts[0]] = name;
        }
        return result;
    }
}
