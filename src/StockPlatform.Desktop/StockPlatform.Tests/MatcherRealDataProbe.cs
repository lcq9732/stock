using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 拿**真库**跑一遍消歧，把各档数量打出来（2026-09-15）。只读，零请求。
///
/// ════ 为什么要有这个 ════
/// 上面那些用例喂的都是合成名字，而这个项目栽过一次：子公司解析的"断片判据"在 32 份真样本上
/// 全绿，却会杀掉「华纺股份有限公司」这种短品牌名——**样本里恰好没有**。判据类的改动，
/// 合成用例证明不了它在十万个真名字上的行为。
///
/// 库不在就跳过（CI 和别人的机器上没有这个文件），所以它不会变成一条脆的测试。
/// </summary>
public class MatcherRealDataProbe
{
    private readonly ITestOutputHelper _out;
    public MatcherRealDataProbe(ITestOutputHelper output) => _out = output;

    private const string Db = @"C:\Chingli\Git\stock\publish\data\local\current.sqlite";

    [Fact]
    public void 真库上各档的数量()
    {
        // 库不在就安静退出——CI 和别人的机器上没有这个文件，不该因此变红
        if (!File.Exists(Db)) { _out.WriteLine("本机没有 current.sqlite，跳过"); return; }

        using var conn = new SqliteConnection($"Data Source={Db};Mode=ReadOnly");
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=15000;";
            pragma.ExecuteNonQuery();
        }

        var companies = new List<(string, string, string?)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT code, full_name, abbr FROM CompanyProfile WHERE full_name <> '';";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                companies.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        }
        if (companies.Count == 0) { _out.WriteLine("公司档案是空的，跳过"); return; }

        var index = PartnerNameMatcher.BuildIndex(companies);

        var subs = new List<(string, string)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name, code FROM CompanySubsidiary;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) subs.Add((r.GetString(0), r.GetString(1)));
        }
        var bySub = PartnerNameMatcher.BuildSubsidiaryIndex(subs);

        var names = new List<string>(120_000);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT partner_name FROM StockCustomerSupplier "
                            + "WHERE partner_name IS NOT NULL AND partner_name <> '' AND rank <= 5;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(0));
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tiers = new Dictionary<string, int>(StringComparer.Ordinal);
        var sample = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var (code, type) = PartnerNameMatcher.Match(name, index, bySub);
            if (code == null || type == null) continue;
            tiers[type] = tiers.GetValueOrDefault(type) + 1;
            var list = sample.TryGetValue(type, out var l) ? l : sample[type] = [];
            if (list.Count < 3) list.Add($"{name} → {code}");
        }
        sw.Stop();

        _out.WriteLine($"对手名 {names.Count} 个，公司 {companies.Count} 家，子公司索引 {bySub.Count} 键");
        _out.WriteLine($"耗时 {sw.ElapsedMilliseconds} ms\n");
        foreach (var (t, n) in tiers.OrderByDescending(x => x.Value))
        {
            _out.WriteLine($"  {t,-14} {n,6}");
            foreach (var s in sample[t]) _out.WriteLine($"        {s}");
        }
        _out.WriteLine($"\n  合计命中 {tiers.Values.Sum()}");

        // ⚠ 性能也要钉：全量重匹会在每次规则升版时跑一遍，几十秒可以，几分钟不行。
        //   前缀切片查字典（而不是遍历六千个全称）就是为这个——遍历法实测要两分钟。
        Assert.True(sw.ElapsedMilliseconds < 30_000,
                    $"全量匹配花了 {sw.ElapsedMilliseconds}ms，太慢了");

        // 各档都该有货。任何一档掉到 0 都说明那一档实际上是死代码。
        foreach (var tier in new[]
                 {
                     PartnerNameMatcher.Exact, PartnerNameMatcher.Short,
                     PartnerNameMatcher.Qualified, PartnerNameMatcher.ParentGroup,
                     PartnerNameMatcher.Subsidiary,
                 })
            Assert.True(tiers.GetValueOrDefault(tier) > 0, $"「{tier}」这一档一个都没命中，是死代码？");
    }
}
