using System.Diagnostics;
using Microsoft.Data.Sqlite;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 含金量实测：这批年报提出的子公司名单，**能多连出多少条产业链边**（2026-09-11）。
///
/// 跟 scratchpad 里那个 python 版问同一个问题，但走的是**产品代码**
/// （SubsidiaryParser + PartnerNameMatcher），所以这个数字才是上线后真会拿到的。
///
/// ⚠ **只读库，一个字节都不写**。它不是常规回归测试，是个测算工具，
///   设 SUBSIDIARY_GAIN=1 才跑。
/// </summary>
public class SubsidiaryGainTests
{
    private const string Reports = @"C:\Chingli\Git\stock\publish\data\annual-reports";
    private const string Db = @"C:\Chingli\Git\stock\publish\data\local\current.sqlite";

    private readonly ITestOutputHelper _out;
    public SubsidiaryGainTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void 实测能多连出多少条边()
    {
        if (Environment.GetEnvironmentVariable("SUBSIDIARY_GAIN") != "1")
        {
            _out.WriteLine("未设 SUBSIDIARY_GAIN=1，跳过。");
            return;
        }
        if (!Directory.Exists(Reports) || !File.Exists(Db))
        {
            _out.WriteLine("本机没有年报目录或库，跳过。");
            return;
        }

        // ── ① 解析 ──────────────────────────────────────────────────────────
        using var conn = new SqliteConnection($"Data Source={Db};Mode=ReadOnly");
        conn.Open();

        var fullNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var abbr = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT code, full_name, abbr FROM CompanyProfile";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                fullNames[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
                abbr[r.GetString(0)] = r.IsDBNull(2) ? "" : r.GetString(2);
            }
        }

        var parser = SubsidiaryParser.Default;
        var sw = Stopwatch.StartNew();
        var all = new List<CompanySubsidiary>();
        int files = 0, empty = 0;

        foreach (var dir in Directory.GetDirectories(Reports).OrderBy(d => d, StringComparer.Ordinal))
        {
            var code = Path.GetFileName(dir);
            foreach (var pdf in Directory.GetFiles(dir, "*.pdf"))
            {
                if (!DateTime.TryParse(Path.GetFileNameWithoutExtension(pdf), out var d)) continue;
                files++;
                fullNames.TryGetValue(code, out var self);
                var items = parser.Parse(pdf, code, d, self);
                if (items.Count == 0) empty++;
                all.AddRange(items);
            }
        }

        // 需要逐条核对名单时，把它 dump 出来（设 SUBSIDIARY_DUMP=路径）
        if (Environment.GetEnvironmentVariable("SUBSIDIARY_DUMP") is { Length: > 0 } dump)
        {
            File.WriteAllLines(dump, all.OrderBy(x => x.Code, StringComparer.Ordinal)
                                         .ThenBy(x => x.Name, StringComparer.Ordinal)
                                         .Select(x => $"{x.Code}|{x.Name}|p{x.SourcePage}"));
            _out.WriteLine($"名单已 dump 到 {dump}");
        }

        _out.WriteLine($"解析 {files} 份，{sw.Elapsed.TotalSeconds:F0} 秒");
        _out.WriteLine($"  提出 {all.Count} 家子公司，{empty} 份版式认不出来"
                     + $"（可用率 {100.0 * (files - empty) / Math.Max(1, files):F0}%）");

        // ── ② 建索引（跟产品里完全一样的两层去歧义）────────────────────────
        var nameToParent = all.GroupBy(x => x.Name, StringComparer.Ordinal)
                              .Where(g => g.Select(x => x.Code).Distinct().Count() == 1)
                              .ToDictionary(g => g.Key, g => g.First().Code, StringComparer.Ordinal);
        var bySub = PartnerNameMatcher.BuildSubsidiaryIndex(
            nameToParent.Select(kv => (Name: kv.Key, ParentCode: kv.Value)));
        _out.WriteLine($"  去歧义后 {nameToParent.Count} 个名字 → 索引 {bySub.Count} 个 key");

        var idx = PartnerNameMatcher.BuildIndex(
            fullNames.Select(kv => (kv.Key, kv.Value)));

        // ── ③ 拿去匹配还没还原的对手名 ──────────────────────────────────────
        var todo = new List<string>();
        using (var c = conn.CreateCommand())
        {
            // ⚠ 扫**全部**对手名，不是只扫未还原的。跑过一次【客户与供应商】之后，
            //   第三档能连的都已经有 partner_code 了，按 IS NULL 查只会得到 0——
            //   那不是没产出，是活干完了。前两档在 Match 里天然优先，不会重复计。
            c.CommandText = "SELECT DISTINCT partner_name FROM StockCustomerSupplier "
                          + "WHERE partner_name IS NOT NULL AND partner_name <> ''";
            using var r = c.ExecuteReader();
            while (r.Read()) todo.Add(r.GetString(0));
        }
        _out.WriteLine($"\n库里的对手名（全部）：{todo.Count:N0} 个");

        var hits = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in todo)
        {
            var (code, type) = PartnerNameMatcher.Match(name, idx, bySub);
            // 只数第三档的——前两档本来就能连上，不是这次的增量
            if (code != null && type == PartnerNameMatcher.Subsidiary) hits[name] = code;
        }
        _out.WriteLine($"  第三档命中：{hits.Count} 个名字");

        // ── ④ 换算成边 ──────────────────────────────────────────────────────
        var newEdges = new HashSet<(string, string)>();
        int rows = 0;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT code, partner_name, is_supplier FROM StockCustomerSupplier";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                if (!hits.TryGetValue(r.GetString(1), out var p)) continue;
                rows++;
                var self = r.GetString(0);
                newEdges.Add(r.GetInt32(2) == 1 ? (p, self) : (self, p));
            }
        }

        var oldEdges = new HashSet<(string, string)>();
        using (var c = conn.CreateCommand())
        {
            // 只取前两档：第三档自己上一轮连的边不能算进"已有"，否则净贡献会被抹成 0
            c.CommandText = "SELECT code, partner_code, is_supplier FROM StockCustomerSupplier "
                          + "WHERE partner_code IS NOT NULL AND match_type <> 'subsidiary'";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var self = r.GetString(0);
                var p = r.GetString(1);
                oldEdges.Add(r.GetInt32(2) == 1 ? (p, self) : (self, p));
            }
        }

        var reallyNew = new HashSet<(string, string)>(newEdges);
        reallyNew.ExceptWith(oldEdges);

        _out.WriteLine($"\n═══ 结果 ═══");
        _out.WriteLine($"  回填 {rows:N0} 行");
        _out.WriteLine($"  新边 {newEdges.Count} 条，其中**全新** {reallyNew.Count} 条");
        _out.WriteLine($"  前两档的边 {oldEdges.Count:N0} 条 → 第三档净增 {100.0 * reallyNew.Count / Math.Max(1, oldEdges.Count):F1}%");

        _out.WriteLine("\n  命中样例：");
        foreach (var (n, p) in hits.Take(10))
            _out.WriteLine($"    {(n.Length > 34 ? n[..34] : n),-34} → {p} {(abbr.TryGetValue(p, out var a) ? a : "?")}");

        // ── 已经回填过的部分 ────────────────────────────────────────────────
        // ⚠ 上面查的是 partner_code IS NULL 的名字。跑过一次【客户与供应商】之后，
        //   第三档能连的都已经有 code 了，再测就是 0 —— 那不是回归，是活干完了。
        //   所以底线断言不能钉"新增 > 0"，得钉"这条路有产出"（新增 + 已回填）。
        int already;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT COUNT(*) FROM StockCustomerSupplier WHERE match_type = 'subsidiary'";
            already = Convert.ToInt32(c.ExecuteScalar() ?? 0);
        }
        _out.WriteLine($"  库里已由第三档回填的：{already:N0} 行");

        Assert.True(reallyNew.Count > 0 || already > 0,
            "第三档既没新增、库里也一行都没有 —— 整条路没走通");
    }
}
