using StockPlatform.Data.Remote;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 年报子公司名单的解析（2026-09-11）。
///
/// 判据来自 python 侧 v3 提取器在同一批 PDF 上实测出来的结果（32 家非金融、可用率 91%），
/// 产品代码这边必须跑出同样的东西——两条实现各走各的（pdfplumber vs PdfPig），
/// 结果对得上才说明规则本身是对的，而不是某个库的巧合。
///
/// 依赖本机 publish/data/reports 下的 PDF，没有就跳过。
/// </summary>
public class SubsidiaryParserTests
{
    private const string Reports = @"C:\Chingli\Git\stock\publish\data\reports";
    private static readonly DateTime Period = new(2025, 12, 31);

    private readonly ITestOutputHelper _out;
    public SubsidiaryParserTests(ITestOutputHelper output) => _out = output;

    private static string? Pdf(string code)
    {
        var p = Path.Combine(Reports, code, "2025-12-31.pdf");
        return File.Exists(p) ? p : null;
    }

    [Fact]
    public void 窄表_华鲁恒升_提得出子公司且名字完整()
    {
        var pdf = Pdf("600426");
        if (pdf == null) { _out.WriteLine("没有 600426 的 PDF，跳过"); return; }

        var subs = SubsidiaryParser.Default.Parse(pdf, "600426", Period);
        _out.WriteLine($"提出 {subs.Count} 家：");
        foreach (var s in subs.Take(20))
            _out.WriteLine($"  p{s.SourcePage}  {s.Name}  {(s.HoldPct?.ToString("F2") ?? "-")}%");

        Assert.NotEmpty(subs);
        // 单元格内换行的名字必须拼成完整的一个——这正是纯文本行那条路做不到的
        Assert.Contains(subs, s => s.Name.Contains("华鲁恒升") && s.Name.EndsWith("公司"));
        // 断片一个都不许有
        Assert.DoesNotContain(subs, s => s.Name.StartsWith("州）") || s.Name.StartsWith("州)"));
    }

    [Fact]
    public void 宽表_比亚迪_也能提出来()
    {
        var pdf = Pdf("002594");
        if (pdf == null) { _out.WriteLine("没有 002594 的 PDF，跳过"); return; }

        var subs = SubsidiaryParser.Default.Parse(pdf, "002594", Period);
        _out.WriteLine($"提出 {subs.Count} 家：");
        foreach (var s in subs.Take(20)) _out.WriteLine($"  p{s.SourcePage}  {s.Name}");

        // 比亚迪那页没有线框，走的是坐标聚类。v1 只提出 3 家，改掉"数字必须紧跟名字"
        // 那条券商列序的判据之后是 13 家，所以这里要求明显多于 3。
        Assert.True(subs.Count > 5, $"只提出 {subs.Count} 家，坐标那条路可能又退化了");
    }

    [Fact]
    public void 清洗_正文句子和表头不许混进来()
    {
        foreach (var code in new[] { "600426", "002594" })
        {
            var pdf = Pdf(code);
            if (pdf == null) continue;

            var subs = SubsidiaryParser.Default.Parse(pdf, code, Period);
            foreach (var s in subs)
            {
                Assert.InRange(s.Name.Length, 6, 40);
                Assert.DoesNotContain("本公司", s.Name);
                Assert.DoesNotContain("名称", s.Name);     // 表头
                Assert.DoesNotContain(" ", s.Name);        // 空白应该在清洗时去干净
                Assert.DoesNotContain("\n", s.Name);
            }
        }
    }

    [Fact]
    public void 不认识的文件返回空_不抛()
    {
        var subs = SubsidiaryParser.Default.Parse(
            @"C:\不存在的目录\没有这份.pdf", "000001", Period);
        Assert.Empty(subs);
    }

    [Fact]
    public void 自己不算自己的子公司()
    {
        var pdf = Pdf("600426");
        if (pdf == null) { _out.WriteLine("没有 600426 的 PDF，跳过"); return; }

        // 母公司全称会出现在同一张表的表头或正文里，传进去就该被排掉
        const string self = "山东华鲁恒升化工股份有限公司";
        var subs = SubsidiaryParser.Default.Parse(pdf, "600426", Period, self);
        Assert.DoesNotContain(subs, s => s.Name == self);
    }
}
