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
    private const string Reports = @"C:\Chingli\Git\stock\publish\data\annual-reports";
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
    public void 断片_只剩通用词的名字不许落库()
    {
        // 真机第一轮跑出来的脏数据，一个个钉住：这几个都是排版把前面的专名截掉之后剩下的，
        // 总长刚好 6 字压着下限过了清洗。
        foreach (var code in new[] { "600271", "600998", "688009", "000157" })
        {
            var pdf = Pdf(code);
            if (pdf == null) continue;

            var subs = SubsidiaryParser.Default.Parse(pdf, code, Period);
            foreach (var bad in new[] { "信息有限公司", "医药有限公司", "科技有限公司" })
                Assert.DoesNotContain(subs, x => x.Name == bad);
        }
    }

    [Theory]
    // 断片：去掉机构后缀只剩行业通用词，没有任何专名
    [InlineData("科技有限公司", false)]
    [InlineData("信息有限公司", false)]
    [InlineData("医药有限公司", false)]
    [InlineData("实业有限公司", false)]
    [InlineData("投资有限公司", false)]
    // ⚠ 短品牌名的**正规公司**，一个都不许杀
    [InlineData("华纺股份有限公司", true)]      // 600448，去后缀只剩"华纺"2 字
    [InlineData("比亚迪股份有限公司", true)]    // 002594，只剩"比亚迪"3 字
    [InlineData("上汽集团有限公司", true)]
    // 正常的长名字
    [InlineData("江阴长电先进封装有限公司", true)]
    [InlineData("中国建筑第六工程局有限公司", true)]
    // 断片：开头就是残缺的
    [InlineData("州）有限公司", false)]
    // 正文句子和表头
    [InlineData("本公司持有华东医药温州有限公司", false)]
    [InlineData("子公司名称", false)]
    public void 清洗判据(string raw, bool shouldPass)
    {
        // ⚠ 这组用例是为了钉住一个**差点上线的错判据**，而且说明了为什么不能靠样本测。
        //
        // 断片规则先写成"去掉机构后缀后不足 4 字就扔"，32 份 PDF 测试全绿——
        // 可它会误杀 "华纺股份有限公司"(剩"华纺")、"比亚迪股份有限公司"(剩"比亚迪")。
        // 全市场跑必然出事，只是那批样本里恰好没有这类公司。
        // 事后确认：v5 名单 493 条里，去后缀后不足 4 字的**一条都没有**——
        // 也就是说，拿这批样本永远测不出那个 bug。判据是纯函数，就该喂字符串直接测。
        var got = SubsidiaryParser.Clean(raw);
        if (shouldPass)
            Assert.False(string.IsNullOrEmpty(got), $"「{raw}」被误杀了");
        else
            Assert.True(string.IsNullOrEmpty(got), $"「{raw}」不该通过，却得到「{got}」");
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
