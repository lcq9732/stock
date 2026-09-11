using System.Text;
using StockPlatform.Pdf.Tools;

namespace StockPlatform.Pdf.Sources;

/// <summary>
/// 第二条路：用外部的 <c>pdftotext</c>（Poppler）把 PDF 转成文本。
///
/// ════ 为什么需要它 ════
/// PdfPig 对部分 CJK 字体无能为力，实测报错集中在两类：
///   · <c>Could not find the referenced CMap: Adobe-CNS1-7 / Adobe-GB1-6</c>
///     ——它不自带 Adobe 的 CJK CMap 资源，0.1.2 版也没有开关可以跳过
///   · <c>The head table is required</c>——内嵌 TrueType 字体缺 head 表
/// 而这些 PDF **并不是扫描件**：查 PDF 内部结构，交通银行有 338 个字体定义（174 个 Type0）、
/// 中国人保 24 个，文本层都在。同样这几份用 pdftotext 一转就出来了（交行 17 万中文字）。
///
/// ════ 为什么用 -layout ════
/// 它按原始坐标保留列对齐，输出直接就是"标签 值 值 值"的行，比自己做坐标聚类还准：
///     成本收入比 5      29.30    29.90    30.04    29.65    27.67
/// 所以这条路**不需要**再做坐标聚类，直接按换行切就是行。
///
/// ════ 找不到就当自己不可用 ════
/// pdftotext 是可选的外部程序，不打包进发布产物。找不到时 <see cref="IsAvailable"/> 为 false，
/// 组合器直接跳过这一级，不会因为缺它而报错。
/// </summary>
public sealed class PopplerLineSource : IPdfLineSource
{
    private readonly PopplerToolset _poppler;
    private readonly int _timeoutSeconds;

    /// <param name="timeoutSeconds">到点强杀——个别几百页的年报可能很慢，不能无限等。</param>
    public PopplerLineSource(PopplerToolset poppler, int timeoutSeconds = 120)
    {
        _poppler = poppler;
        _timeoutSeconds = timeoutSeconds;
    }

    public string Name => "poppler";

    public bool IsAvailable => _poppler.PdfToText != null;

    public string UnavailableReason => IsAvailable
        ? ""
        : "没找到能处理中文的 pdftotext.exe（装完整版 poppler，别用 Git 自带的那个残缺版）";

    /// <inheritdoc/>
    public IReadOnlyList<PdfLine>? TryExtract(string pdfPath, PdfExtractOptions options,
                                              CancellationToken ct = default)
    {
        // 拿本次要处理的 PDF 当探测样本——它一定是中文年报，比内置任何样本都合适。
        // （这一步必须在取 PdfToText 之前：探测结果一旦缓存就不再重算。）
        _poppler.UseProbeSample(pdfPath);
        var exe = _poppler.PdfToText;
        if (exe == null) return null;

        var tmp = Path.Combine(Path.GetTempPath(), $"pdftotext_{Guid.NewGuid():N}.txt");
        try
        {
            // -layout 保留列对齐；-enc UTF-8 免得默认按本地代码页输出
            var r = ProcessRunner.Run(exe, $"-layout -enc UTF-8 \"{pdfPath}\" \"{tmp}\"",
                                      _timeoutSeconds);
            // ⚠ **不看退出码**，只看有没有转出东西来。pdftotext 对字体有问题的 PDF 会报非 0
            //   （中国人保那份每页都刷 "Mismatch between font type and embedded font file"），
            //   但文本照样转出来了——改成看退出码会让这条兜底路径对它们直接失效。
            if (!r.Completed || !File.Exists(tmp)) return null;

            var text = File.ReadAllText(tmp, Encoding.UTF8);
            if (text.Length < 200) return null;      // 基本是空的，没救回来

            return Split(text, options);
        }
        catch { return null; }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    /// <summary>
    /// 按页和行切开。pdftotext 用换页符 \f 分页，据此还原页码——
    /// 解析出来的值要能对回原文让人工核对，页码不能丢。
    /// </summary>
    private static List<PdfLine> Split(string text, PdfExtractOptions options)
    {
        var only = options.Pages is { Count: > 0 } ? new HashSet<int>(options.Pages) : null;
        var result = new List<PdfLine>();
        var pages = text.Split('\f');

        for (int i = 0; i < pages.Length; i++)
        {
            int pageNo = i + 1;
            if (only != null && !only.Contains(pageNo)) continue;

            var lines = new List<string>();
            foreach (var raw in pages[i].Split('\n'))
            {
                var l = raw.TrimEnd('\r').Trim();
                if (l.Length > 0) lines.Add(l);
            }
            if (lines.Count == 0) continue;

            // 页面筛选。-layout 的输出本身就是成行的，所以把整页拼回去交给判据。
            // 这一步原来写在业务解析类里（它替这条 source 补做筛页），是典型的职责外溢，
            // 拆分时下沉到这里——每条 source 各自对自己的产出负责。
            if (options.PageFilter != null
                && !options.PageFilter(string.Join('\n', lines))) continue;

            foreach (var l in lines) result.Add(new PdfLine(pageNo, l));
        }
        return result;
    }
}
