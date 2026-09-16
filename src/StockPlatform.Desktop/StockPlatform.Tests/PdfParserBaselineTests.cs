using System.Diagnostics;
using System.Text;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Models;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// PDF 工具层重构的**等价性基线**（2026-09-11）。
///
/// ════ 为什么要它 ════
/// 要把 PdfPig 取词 + 坐标聚类那套从 BankReportParser 里抽成独立项目 StockPlatform.Pdf，
/// 并把三个 static 类改成实例对象。这类重构最怕"看着一样、结果悄悄变了"——坐标聚类的
/// 阈值（行容差 5.0、字间距取字宽三成）是量出来的经验值，边界行为单元测试根本测不出来，
/// 只能拿**全量真实 PDF 跑一遍前后比对**。
///
/// 用法：重构**前**跑一次存基线，重构**后**再跑一次，两个文件 diff 必须为空。
///     $env:PDF_BASELINE_OUT = "...\before.txt"; dotnet test --filter PdfParserBaseline
///
/// ════ 为什么用环境变量门禁 ════
/// 它依赖本机 publish/data/reports 下的几百份 PDF，别的机器上没有；而且跑一轮要十几分钟。
/// 不设 PDF_BASELINE_OUT 就直接跳过，不拖累常规 dotnet test。
///
/// 注：allowOcr 传 false。OCR 一份要一分钟，而全库只有 2 份走这条路（数字被转曲）。
///     那条路径这次只是 static 改实例，单独人工验证，不进基线。
/// </summary>
public class PdfParserBaselineTests
{
    /// <summary>
    /// 重构前后都用默认装配（PdfPig -> pdftotext 兜底 + OCR），两次跑的是同一条链。
    /// 重构前这里写的是静态调用 BankReportParser.Xxx(...)，改成实例后只换了这一处。
    /// </summary>
    private static readonly BankReportParser Parser = BankReportParser.Default;

    private readonly ITestOutputHelper _out;
    public PdfParserBaselineTests(ITestOutputHelper output) => _out = output;

    private static readonly FinancialInstitutionKind[] Kinds =
    [
        FinancialInstitutionKind.Bank,
        FinancialInstitutionKind.Broker,
        FinancialInstitutionKind.Insurer,
    ];

    [Fact]
    public void 全量解析结果_写基线文件()
    {
        var outPath = Environment.GetEnvironmentVariable("PDF_BASELINE_OUT");
        if (string.IsNullOrWhiteSpace(outPath))
        {
            _out.WriteLine("未设 PDF_BASELINE_OUT，跳过。");
            return;
        }

        var root = Environment.GetEnvironmentVariable("PDF_BASELINE_REPORTS")
                   ?? @"C:\Chingli\Git\stock\publish\data\reports";
        if (!Directory.Exists(root))
        {
            _out.WriteLine($"报告目录不存在：{root}，跳过。");
            return;
        }

        // 排序保证两次跑的顺序一致——否则 diff 全是噪音
        var pdfs = Directory.GetFiles(root, "*.pdf", SearchOption.AllDirectories)
                            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        var limit = int.TryParse(Environment.GetEnvironmentVariable("PDF_BASELINE_LIMIT"), out var n)
                    ? n : int.MaxValue;
        if (pdfs.Count > limit) pdfs = pdfs.Take(limit).ToList();

        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        int done = 0;

        foreach (var pdf in pdfs)
        {
            // 相对路径：基线文件跟机器无关
            var rel = Path.GetRelativePath(root, pdf).Replace('\u005C', '/');
            var code = Path.GetFileName(Path.GetDirectoryName(pdf)) ?? "?";
            var date = DateTime.TryParse(Path.GetFileNameWithoutExtension(pdf), out var d)
                       ? d : new DateTime(2000, 1, 1);

            sb.Append("=== ").Append(rel).AppendLine();


            sb.Append("  DisclosedKeys: ").AppendLine(Safe(() =>
            {
                var ks = Parser.DisclosedKeys(pdf);
                return ks == null ? "<null>"
                     : string.Join(",", ks.OrderBy(k => k, StringComparer.Ordinal));
            }));

            foreach (var kind in Kinds)
            {
                sb.Append("  Parse[").Append(kind).Append("]: ").AppendLine(Safe(() =>
                {
                    var ms = Parser.Parse(pdf, code, date, kind, null,
                                                    default, allowOcr: false);
                    if (ms.Count == 0) return "<empty>";
                    // **不排序**：产出顺序本身也是行为，顺序变了同样要能看出来
                    var parts = ms.Select(m =>
                        $"{m.MetricKey}|{m.Basis}|{m.Value:R}|{m.StandardValue ?? "-"}"
                        + $"|p{m.SourcePage}|{m.Source}");
                    return ms.Count + " => " + string.Join(" ; ", parts);
                }));
            }

            if (++done % 20 == 0)
                _out.WriteLine($"{done}/{pdfs.Count}  已用 {sw.Elapsed.TotalMinutes:F1} 分钟");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        _out.WriteLine($"完成：{pdfs.Count} 份，{sw.Elapsed.TotalMinutes:F1} 分钟，写入 {outPath}");
    }

    /// <summary>异常也要进基线——"哪些 PDF 解析会抛"同样是要钉住的行为。</summary>
    private static string Safe(Func<string> f)
    {
        try { return f(); }
        catch (Exception ex) { return "EX:" + ex.GetType().Name; }
    }
}
