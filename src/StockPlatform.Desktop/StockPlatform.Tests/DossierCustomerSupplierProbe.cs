using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 拿**真库**跑一遍新加的两节（2026-09-16）。
///
/// <see cref="DossierCustomerSupplierTests"/> 用的是我自己造的临时库——造的数据只长成我
/// 想到的样子。真库里 53 万行是别人写的：NULL 金额、空对手名、指向已退市代码的 partner_code、
/// 同一期同一名次两条…… 这些形状单测里一个都不会出现。
///
/// 只读（Mode=ReadOnly，reader 自己就带），零改动。库不在就安静跳过——别人的机器和 CI 上
/// 没有这个文件，不该因此变红。
/// </summary>
public class DossierCustomerSupplierProbe
{
    private readonly ITestOutputHelper _out;
    public DossierCustomerSupplierProbe(ITestOutputHelper output) => _out = output;

    private const string Db = @"C:\Chingli\Git\stock\publish\data\local\current.sqlite";
    private const string Inbound = "被谁列为客户/供应商";
    private const string Outbound = "前五大客户与供应商";

    /// <summary>真库里挑的样本，各代表一种形状。</summary>
    public static TheoryData<string, string> Samples => new()
    {
        { "300750", "宁德时代——自己全匿名，反向边一堆（这两节存在的理由）" },
        { "600519", "贵州茅台——自己不披露，只有两条老的反向边" },
        { "601607", "上海医药——刚归一回来的那只，subsidiary 档最多" },
        { "601857", "中国石油——parent_group 档指向它，得能看出不是它本人" },
        { "000001", "平安银行——金融股，客户/供应商多半没有" },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void 真库上跑得出来(string code, string why)
    {
        if (!File.Exists(Db)) { _out.WriteLine("没有本地库，跳过"); return; }

        var dossier = new SqliteStockDossierReader(Db).Read(code);
        _out.WriteLine($"{code}  {why}\n");

        foreach (var title in new[] { Inbound, Outbound })
        {
            var s = dossier.Sections.Single(x => x.Title == title);
            Assert.Null(s.Error);                     // ⚠ 真数据上不许抛
            _out.WriteLine($"【{title}】{s.Rows.Count} 行"
                         + (s.Chart == null ? "，无图" : $"，图 {s.Chart.XLabels.Count} 个点"));
            _out.WriteLine("  " + string.Join(" | ", s.Columns.Select(c => c.Header)));
            foreach (var row in s.Rows.Take(8)) _out.WriteLine("  " + string.Join(" | ", row));
            if (s.Rows.Count > 8) _out.WriteLine($"  …… 另 {s.Rows.Count - 8} 行");

            // 每行的格子数必须等于列数，否则 DataGrid 会错位——这是最容易悄悄弄坏的不变量。
            Assert.All(s.Rows, r => Assert.Equal(s.Columns.Count, r.Length));
            // 序列长度必须等于 x 轴长度，否则图会错位（错位在图上看不出来）。
            if (s.Chart != null)
                Assert.All(s.Chart.Series, ser => Assert.Equal(s.Chart.XLabels.Count, ser.Values.Length));
            _out.WriteLine("");
        }
    }

    /// <summary>
    /// ⚠ 全库扫一遍匹配档：**不能有哪一档漏了中文标签**原样漏到界面上。
    /// 以后再加第七档，忘了配标签这条会红。
    /// </summary>
    [Fact]
    public void 库里出现过的匹配档都有中文标签()
    {
        if (!File.Exists(Db)) { _out.WriteLine("没有本地库，跳过"); return; }

        // 拿一只 subsidiary/parent_group/short/qualified 都沾得上的票不容易，所以直接扫表。
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Db};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT match_type FROM StockCustomerSupplier WHERE match_type IS NOT NULL;";
        var kinds = new List<string>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) kinds.Add(r.GetString(0));

        _out.WriteLine("库里出现过的档：" + string.Join(", ", kinds));
        var known = new[]
        {
            "全称精确", "简称精确", "全称+限定词", "归一化后相等", "子公司归并（弱）",
            "⚠ 母集团，非该上市公司本身",
        };

        // 反射不到私有方法就换个路子：这几档在样本票上都出现过，扫它们的「匹配档」列。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (code, _) in Samples.Select(x => ((string)x[0]!, (string)x[1]!)))
        {
            var d = new SqliteStockDossierReader(Db).Read(code);
            foreach (var title in new[] { Inbound, Outbound })
            {
                var s = d.Sections.Single(x => x.Title == title);
                int col = s.Columns.Count - 1;                 // 匹配档永远是最后一列
                foreach (var row in s.Rows)
                    if (row[col].Length > 0) seen.Add(row[col]);
            }
        }
        _out.WriteLine("样本票上见到的标签：" + string.Join(" / ", seen.OrderBy(x => x, StringComparer.Ordinal)));
        Assert.All(seen, label => Assert.Contains(label, known));
    }
}
