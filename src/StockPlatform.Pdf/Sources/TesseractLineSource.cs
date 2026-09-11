using System.Text;
using System.Text.RegularExpressions;
using StockPlatform.Pdf.Tools;

namespace StockPlatform.Pdf.Sources;

/// <summary>
/// 把 PDF 的**指定几页**渲染成图再 OCR 认成文字。
///
/// ════ 什么时候需要它 ════
/// 前两条路（PdfPig → pdftotext）解决的是"字体读不动"，而有一种 PDF 是另一种病：
/// **排版时把所有阿拉伯数字和 % 号转成了矢量曲线**（转曲），文本层里只剩中文。实测中国人保：
///     2025-06-30.pdf：汉字 72950、数字 21998、% 号 435    ← 正常
///     2026-06-30.pdf：汉字 71875、数字    28、% 号   0    ← 数字全没了
/// 同一页的内容流里，2026 中报有 12214 个路径绘制操作、2025 中报只有 562 个——数字是"画"上去的。
/// 这种 PDF 任何文本提取器都救不了（Adobe 自己也提不出来），只能渲染成图 OCR。
/// 全库 349 份 PDF 扫下来只有这 2 份是这个毛病，所以这是**窄口径兜底**，不是常规路径。
///
/// ════ 它不在 PdfLineExtractor 的兜底链上 ════
/// 因为触发条件不是"前面失败了"，而是"前面成功了、但数字缺了"——那是业务判断
/// （见 BankReportParser 的 LooksLikeDigitsStripped）。该 OCR 哪几页同样由业务侧算好，
/// 通过 <see cref="PdfExtractOptions.Pages"/> 传进来。所以本类由业务侧直接持有、按需调用。
///
/// ⚠ **必须传 Pages**。一页要 4~5 秒（300dpi 渲染 + 识别），三百页的年报全跑要 20 分钟。
///   不传就直接返回 null——宁可什么都不做，也不能悄悄跑上二十分钟。
///
/// ════ OCR 的数不能直接当真 ════
/// 实测表格页（"综合偿付能力充足率(%) 246.6 237.2 …"）识别得完全正确，但正文叙述里
/// "综合成本率94.5%，" 被认成了 "94.59%6," ——% 号跟后面的全角逗号粘在一起容易多认一位。
/// 所以调用方要把这条路出来的值单独标记、列进待人工核对的清单。
/// </summary>
public sealed class TesseractLineSource : IPdfLineSource
{
    /// <summary>
    /// 渲染分辨率。300 是实测出来的甜点：150 的小数点会糊，400 反而更差
    /// （"94.5%，" 在 400dpi 下认成 "94.59%6,"，300dpi 的表格页则完全正确）。
    /// </summary>
    private const int Dpi = 300;

    private readonly TesseractToolset _tesseract;
    private readonly PopplerToolset _poppler;      // 借 pdftoppm 把页面渲染成图

    public TesseractLineSource(TesseractToolset tesseract, PopplerToolset poppler)
    {
        _tesseract = tesseract;
        _poppler = poppler;
    }

    public string Name => "ocr";

    /// <summary>tesseract（带中文包）和 pdftoppm 缺一不可。</summary>
    public bool IsAvailable
        => _tesseract.Executable != null && _poppler.Tool("pdftoppm.exe") != null;

    /// <summary>缺什么的一句话说明，给进度日志用（都齐了返回空串）。</summary>
    public string UnavailableReason
        => _tesseract.Executable == null
            ? "没找到带 chi_sim 语言包的 tesseract.exe（装 Tesseract-OCR 时勾选 Chinese Simplified 即可）"
            : _poppler.Tool("pdftoppm.exe") == null
                ? "没找到 poppler 的 pdftoppm.exe（跟 pdftotext 同一个安装包）"
                : "";

    /// <inheritdoc/>
    public IReadOnlyList<PdfLine>? TryExtract(string pdfPath, PdfExtractOptions options,
                                              CancellationToken ct = default)
    {
        var tess = _tesseract.Executable;
        var toppm = _poppler.Tool("pdftoppm.exe");
        var pages = options.Pages;
        if (tess == null || toppm == null || pages is not { Count: > 0 }) return null;

        var progress = options.Progress;
        var workDir = Path.Combine(Path.GetTempPath(), $"ocr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var result = new List<PdfLine>();
            for (int i = 0; i < pages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                int page = pages[i];
                progress?.Invoke($"      OCR 第 {page} 页（{i + 1}/{pages.Count}）...");

                var img = Path.Combine(workDir, $"p{page}");
                // -singlefile：输出就叫 p{page}.png，不用猜 pdftoppm 会给页号补几位零
                // -gray：财报是黑字白底，灰度识别不比彩色差，图小一半、快一截
                if (!ProcessRunner.Run(toppm,
                        $"-f {page} -l {page} -singlefile -r {Dpi} -gray -png \"{pdfPath}\" \"{img}\"",
                        120).Ok)
                    continue;
                var png = img + ".png";
                if (!File.Exists(png)) continue;

                var outBase = Path.Combine(workDir, $"t{page}");
                // --psm 6 = "一整块统一排版的文字"。财报的指标表和正文都吃这个模式；
                // 默认的 psm 3（自动分栏）会把表格的标签列和数值列拆成两段，标签和值就对不上了。
                if (!ProcessRunner.Run(tess,
                        $"\"{png}\" \"{outBase}\" -l {TesseractToolset.Languages} --psm 6",
                        180).Ok)
                    continue;
                var txt = outBase + ".txt";
                if (!File.Exists(txt)) continue;

                foreach (var raw in File.ReadAllText(txt, Encoding.UTF8).Split('\n'))
                {
                    var line = Tidy(raw);
                    if (line.Length > 0) result.Add(new PdfLine(page, line));
                }
            }
            return result.Count > 0 ? result : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { try { Directory.Delete(workDir, recursive: true); } catch { } }
    }

    /// <summary>
    /// **把汉字之间的空格去掉**。chi_sim 模型输出的是"综 合 成 本 率 94.5"这种一字一空格的形式，
    /// 不清理的话：① 虽然标签正则本身允许字符间空白、还能匹配上，但"标签必须在行首几字内"
    /// 那类判据会失效（行首几个字被空格撑开），表格行会被漏掉；
    /// ② 标签到值的距离也会被撑大，超过距离上限就取不到值了。
    ///
    /// 只删**汉字与汉字之间**的空格：数字之间的空格是表格的列分隔，删了会把
    /// "246.6 237.2 189.3" 粘成一个数，绝不能动。
    /// </summary>
    private static string Tidy(string raw)
    {
        var s = raw.TrimEnd('\r').Trim();
        if (s.Length == 0) return "";
        // 汉字/中文标点 之间的空白：删掉
        s = Regex.Replace(s, @"(?<=[\u4e00-\u9fff（）、，。；：％%])[ \t]+(?=[\u4e00-\u9fff（）、，。；：％%])", "");
        // 剩下的多余空白压成单空格，跟 pdftotext -layout 那条路径的清洗保持一致
        return Regex.Replace(s, @"[ \t]{2,}", " ").Trim();
    }
}
