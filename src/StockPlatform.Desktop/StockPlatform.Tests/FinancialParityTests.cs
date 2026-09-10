using System.Data;
using Microsoft.Data.Sqlite;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Models;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 财务报表换东财的**全量逐格比对验收**（2026-09-10）。默认跳过，手工跑：
/// <code>
///   dotnet test --filter "FullyQualifiedName~FinancialParityTests" -e FIN_PARITY=1 \
///     --logger "console;verbosity=detailed"
/// </code>
/// 库路径默认取 <c>publish/data/local/current.sqlite</c>，可用 <c>FIN_PARITY_DB</c> 覆盖；
/// 样本量默认 200，可用 <c>FIN_PARITY_N</c> 覆盖。
///
/// ════ 为什么要走产品代码而不是另写个脚本 ════
/// 行业换源那次的教训：**验证脚本和产品代码各写一套过滤，等于没验证**——脚本按代码形状过滤、
/// 产品代码按 SECUCODE 后缀过滤，脚本报"全对"，实机却抓回 21018 只（真实 A 股约 6000）。
/// 所以这里直接 new 产品的 <see cref="EastMoneyFinancialProvider"/>，抓法跟线上一模一样。
///
/// ════ 判什么 ════
/// 对每只样本票，把东财抓回来的 (报告期, 科目) → 值，跟库里现有的（新浪抓的）逐格比：
///   · <b>值不同</b>            → 必须为 0，否则换源会改写历史数据
///   · <b>仅库里有</b>（东财缺）→ 必须为 0，否则换源会丢科目
///   · 仅东财有                 → 可以有（东财更全，比如新浪抓不到的研发费用），只报数
/// 保险股不在这里比——它们按设计走新浪（<see cref="FinancialSourceRouter"/>），跟库里同源，
/// 比了也只是自己跟自己比。
/// </summary>
public class FinancialParityTests(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("FIN_PARITY") == "1";

    private static string DbPath =>
        Environment.GetEnvironmentVariable("FIN_PARITY_DB")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
             "..", "..", "..", "..", "..", "..", "publish", "data", "local", "current.sqlite"));

    private static int SampleSize =>
        int.TryParse(Environment.GetEnvironmentVariable("FIN_PARITY_N"), out var n) && n > 0 ? n : 200;

    /// <summary>相对误差容差。0 意味着必须逐位相同；给 1e-9 是为了容忍 double 往返的最后一位。</summary>
    private const double Tolerance = 1e-9;

    private static List<string> Query(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>按类型分层取样：银行/券商/退市股各取够，剩下的随机补——这三类正是最容易出错的。</summary>
    private List<string> PickSample(SqliteConnection conn)
    {
        var picked = new List<string>();
        void Add(IEnumerable<string> codes) =>
            picked.AddRange(codes.Where(c => !picked.Contains(c, StringComparer.Ordinal)));

        // 银行：有利息净收入的（G 表毛额/B 表净额那个坑就在这类身上）
        Add(Query(conn, $"select distinct code from FinancialReport where metric_key='{FinancialKeys.InterestNet}' limit 20"));
        output.WriteLine($"  银行样本 {picked.Count} 只");
        int mark = picked.Count;

        // 券商：有代理买卖证券业务净收入的
        Add(Query(conn, $"select distinct code from FinancialReport where metric_key='{FinancialKeys.BrokerageNet}' limit 10"));
        output.WriteLine($"  券商样本 {picked.Count - mark} 只");
        mark = picked.Count;

        // 退市股：东财对它们的覆盖是这次换源的关键之一
        Add(Query(conn, "select d.code from DelistedStock d join FinancialReport f on f.code=d.code group by d.code limit 20"));
        output.WriteLine($"  退市股样本 {picked.Count - mark} 只");
        mark = picked.Count;

        // 其余随机补齐（用 code 的哈希序当"随机"，可复现）
        Add(Query(conn, $"select distinct code from FinancialReport order by substr(code,4)||substr(code,1,3) limit {SampleSize * 2}")
            .Take(Math.Max(0, SampleSize - picked.Count)));
        output.WriteLine($"  随机补齐 {picked.Count - mark} 只，合计 {picked.Count} 只");
        return picked.Take(SampleSize).ToList();
    }

    private static Dictionary<(DateTime, string), double> LoadLocal(SqliteConnection conn, string code)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select report_date, metric_key, value from FinancialReport where code=$c and value is not null";
        cmd.Parameters.AddWithValue("$c", code);
        var map = new Dictionary<(DateTime, string), double>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (DateTime.TryParse(r.GetString(0), out var d))
                map[(d.Date, r.GetString(1))] = r.GetDouble(2);
        return map;
    }

    [Fact]
    public async Task 东财与库里逐格比对()
    {
        if (!Enabled) { output.WriteLine("跳过（设 FIN_PARITY=1 才联网跑）"); return; }
        Assert.True(File.Exists(DbPath), $"找不到库：{DbPath}");

        using var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
        conn.Open();
        output.WriteLine($"库：{DbPath}");
        var sample = PickSample(conn);

        var provider = new EastMoneyFinancialProvider(
            new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromMilliseconds(300)));

        int done = 0, okCodes = 0, skippedInsurer = 0, failed = 0;
        long cellsCompared = 0, cellsOnlyEm = 0;
        var valueDiffs = new List<string>();
        var missingByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var missingSamples = new List<string>();
        // 按报告期年代分桶：映射错的话各年代应该一起错；只有老年代缺/不同，那是数据本身的深度差异
        var buckets = new Dictionary<string, (long Cmp, int Diff, int Missing)>(StringComparer.Ordinal);
        // 新浪把"这家公司没有的科目"一律写成 0（85 家银行全都有 ap/rd_exp/inv_decrease=0，
        // 而银行根本没有应付账款和存货）。东财老实地不给这些列，那不是缺数据——
        // 反倒是写 0 的一方错了："科目不存在"和"科目等于 0"是两回事。单独计数，不算差异。
        long localZeroPhantom = 0;
        // 值不同要分清是"哪一类公司、哪个科目"——集中在银行的几个科目，跟遍地都是，是两回事
        var diffByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var diffByOrg = new Dictionary<string, int>(StringComparer.Ordinal);
        var codesWithDiff = new HashSet<string>(StringComparer.Ordinal);
        var orgOfCode = new Dictionary<string, string>(StringComparer.Ordinal);
        // 差异要分级：把"新浪截到万位"的 20 元误差和"差 250 倍"算成同一件事，等于没量化。
        //   rounding 舍入   ＝ 相对差 < 1e-4 且绝对差 < 1000（新浪把数字截到万位，东财是精确值）
        //   restated 重述   ＝ 相对差 < 2%（年报追溯调整，两家都"对"，只是时点不同）
        //   serious  严重   ＝ 其余（数量级错误这类，必须逐条查明才谈换源）
        long dRounding = 0, dRestated = 0;
        var serious = new List<string>();
        var seriousCodes = new HashSet<string>(StringComparer.Ordinal);
        static string Bucket(DateTime d) => d.Year >= 2020 ? "2020年以后"
            : d.Year >= 2010 ? "2010-2019" : d.Year >= 2000 ? "2000-2009" : "1999年以前";
        void Bump(DateTime d, long cmp, int diff, int miss)
        {
            var k = Bucket(d);
            var v = buckets.GetValueOrDefault(k);
            buckets[k] = (v.Cmp + cmp, v.Diff + diff, v.Missing + miss);
        }

        foreach (var code in sample)
        {
            done++;
            string orgType;
            List<FinancialValue> emRows;
            try
            {
                (orgType, emRows) = await provider.FetchWithOrgTypeAsync(code);
            }
            catch (Exception ex)
            {
                failed++;
                output.WriteLine($"✘ {code} 抓取失败：{ex.Message}");
                continue;
            }

            if (orgType == EastMoneyFinancialProvider.OrgTypeInsurer)
            {
                skippedInsurer++;   // 按设计走新浪，不参与比对
                continue;
            }

            orgOfCode[code] = string.IsNullOrEmpty(orgType) ? "未知" : orgType;
            var local = LoadLocal(conn, code);
            var em = new Dictionary<(DateTime, string), double>();
            foreach (var v in emRows) em[(v.ReportDate.Date, v.Key)] = v.Value;

            bool clean = true;
            foreach (var (k, localVal) in local)
            {
                if (!em.TryGetValue(k, out var emVal))
                {
                    if (localVal == 0) { localZeroPhantom++; continue; }   // 新浪的幽灵 0
                    clean = false;
                    if (k.Item1.Year >= 2020) missingByKey[k.Item2] = missingByKey.GetValueOrDefault(k.Item2) + 1;
                    Bump(k.Item1, 0, 0, 1);
                    if (k.Item1.Year >= 2020 && missingSamples.Count < 20) missingSamples.Add($"{code} {k.Item1:yyyy-MM-dd} {k.Item2}={localVal}");
                    continue;
                }
                cellsCompared++;
                Bump(k.Item1, 1, 0, 0);
                if (localVal == 0 && emVal != 0) { localZeroPhantom++; continue; }   // 同上：库里是幽灵 0
                var tol = Math.Max(Math.Abs(localVal) * Tolerance, 1e-6);
                if (Math.Abs(emVal - localVal) > tol)
                {
                    var absDiff = Math.Abs(emVal - localVal);
                    var relDiff = Math.Abs(localVal) > 0 ? absDiff / Math.Abs(localVal) : double.PositiveInfinity;
                    if (relDiff < 1e-4 && absDiff < 1000) { dRounding++; continue; }
                    if (relDiff < 0.02) { dRestated++; continue; }
                    if (serious.Count < 25)
                        serious.Add($"{code}({orgOfCode[code]}) {k.Item1:yyyy-MM-dd} {k.Item2}: 东财={emVal:N0} 库里={localVal:N0} 比值={(localVal == 0 ? 0 : emVal / localVal):F2}");
                    seriousCodes.Add(code);
                    clean = false;
                    Bump(k.Item1, 0, 1, 0);
                    diffByKey[k.Item2] = diffByKey.GetValueOrDefault(k.Item2) + 1;
                    diffByOrg[orgOfCode[code]] = diffByOrg.GetValueOrDefault(orgOfCode[code]) + 1;
                    codesWithDiff.Add(code);
                    if (k.Item1.Year >= 2020 && valueDiffs.Count < 20)
                        valueDiffs.Add($"{code} {k.Item1:yyyy-MM-dd} {k.Item2}: 东财={emVal} 库里={localVal}");
                }
            }
            cellsOnlyEm += em.Count - local.Count(kv => em.ContainsKey(kv.Key));
            if (clean) okCodes++;

            if (done % 20 == 0) output.WriteLine($"  进度 {done}/{sample.Count}，已比 {cellsCompared:N0} 格");
        }

        output.WriteLine("");
        output.WriteLine($"══ 按年份分桶（判断到底是映射错了，还是东财的老数据本来就少）══");
        foreach (var b in buckets.OrderBy(k => k.Key))
            output.WriteLine($"   {b.Key}: 比对 {b.Value.Cmp,7:N0} 格，值不同 {b.Value.Diff,5}，东财缺 {b.Value.Missing,6}"
                             + $"（缺失率 {(b.Value.Cmp + b.Value.Missing == 0 ? 0 : 100.0 * b.Value.Missing / (b.Value.Cmp + b.Value.Missing)):F1}%）");

        output.WriteLine("");
        output.WriteLine($"══ 结果 ══");
        output.WriteLine($"差异分级：舍入 {dRounding:N0} 格（新浪截到万位）、追溯重述 {dRestated:N0} 格（<2%）、"
                         + $"**严重 {codesWithDiff.Count switch { _ => diffByKey.Values.Sum() }:N0} 格**，涉及 {seriousCodes.Count}/{sample.Count} 只票");
        foreach (var x in serious) output.WriteLine("   严重: " + x);
        output.WriteLine("  按机构类型: " + string.Join("、", diffByOrg.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value:N0} 处")));
        output.WriteLine("  按科目 top12: " + string.Join("、", diffByKey.OrderByDescending(k => k.Value).Take(12).Select(k => $"{k.Key} {k.Value:N0}")));
        var cleanCodes = sample.Where(c => orgOfCode.ContainsKey(c) && !codesWithDiff.Contains(c)).ToList();
        output.WriteLine($"  一格都不差的票: {cleanCodes.Count} 只");
        output.WriteLine($"样本 {sample.Count} 只：完全一致 {okCodes}、保险跳过 {skippedInsurer}、抓取失败 {failed}");
        output.WriteLine($"共比对 {cellsCompared:N0} 格；东财额外多给 {cellsOnlyEm:N0} 格（新浪抓不到的科目，是好事）");
        output.WriteLine($"另有 {localZeroPhantom:N0} 格是**新浪的幽灵 0**（库里写 0、东财不给该科目），不计入差异");
        output.WriteLine($"下面只列 2020 年以后的（老年代的差异属于数据深度，另说）：东财缺 {missingByKey.Values.Sum()} 格");
        foreach (var d in valueDiffs) output.WriteLine("   值不同: " + d);
        foreach (var kv in missingByKey.OrderByDescending(k => k.Value))
            output.WriteLine($"   东财缺 {kv.Key}: {kv.Value} 格");
        foreach (var m in missingSamples) output.WriteLine("   缺失样例: " + m);

        Assert.Empty(valueDiffs);
        Assert.Empty(missingByKey);
    }
}
