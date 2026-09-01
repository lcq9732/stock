using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 用 OCR（Tesseract）把 PDF 的**指定几页**渲染成图再认成文字，作为
/// <see cref="PdfTextExtractor"/> 之后的**第三级兜底**（2026-08-30 新增）。
///
/// ════ 为什么还要第三级 ════
/// 前两级（PdfPig → pdftotext）解决的是"字体读不动"，而中国人保 2025 年报/2026 中报是另一种
/// 病：**排版时把所有阿拉伯数字和 % 号转成了矢量曲线**（转曲），文本层里只剩中文。实测
///     2025-06-30.pdf：汉字 72950、数字 21998、% 号 435    ← 正常
///     2026-06-30.pdf：汉字 71875、数字    28、% 号   0    ← 数字全没了
/// 同一页的内容流里，2026 中报有 12214 个路径绘制操作、2025 中报只有 562 个——数字是"画"上去的。
/// 这种 PDF 任何文本提取器都救不了（Adobe 自己也提不出来），只能把页面渲染成图 OCR。
/// 全库 349 份 PDF 扫下来，只有这 2 份是这个毛病，所以这条路是**窄口径兜底**，不是常规路径。
///
/// ════ 只 OCR 几页，不 OCR 整份 ════
/// 一页要 4~5 秒（300dpi 渲染 + 识别），三百页的年报全跑要 20 分钟。但要的指标就在两三页里，
/// 而**中文文本层是好的**——正好拿它定位："哪几页出现了指标名"由调用方算好传进来。
///
/// ════ OCR 的数不能直接当真 ════
/// 实测表格页（"综合偿付能力充足率(%) 246.6 237.2 …"）识别得完全正确，但正文叙述里
/// "综合成本率94.5%，" 被认成了 "94.59%6," ——% 号跟后面的全角逗号粘在一起容易多认一位。
/// 所以 OCR 出来的值一律标 source='ocr' 单独存，并列进「待手工回填清单」让人核对一次，
/// 见 ManualFillWorklist。**表格页优先**这一点由现有的两遍扫描天然保证（第一遍只认行首标签）。
/// </summary>
public static class PdfOcrExtractor
{
    private static string? _tesseract;
    private static bool _searched;

    /// <summary>OCR 用的语言：简体中文 + 英文（数字走 eng 那套字形模型）。</summary>
    private const string Languages = "chi_sim+eng";

    /// <summary>
    /// 渲染分辨率。300 是实测出来的甜点：150 的小数点会糊，400 反而更差
    /// （"94.5%，" 在 400dpi 下认成 "94.59%6,"，300dpi 的表格页则完全正确）。
    /// </summary>
    private const int Dpi = 300;

    /// <summary>
    /// 找 tesseract.exe。**必须带 chi_sim 语言包才算可用**——只装了 eng 的话中文标签一个都认不出来，
    /// 认出一堆数字却对不上任何指标名，比不做还糟。结果缓存（找不到也缓存）。
    /// </summary>
    public static string? FindExecutable()
    {
        if (_searched) return _tesseract;
        _searched = true;

        var candidates = new List<string>
        {
            @"C:\Program Files\Tesseract-OCR\tesseract.exe",
            @"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe",
            @"C:\Tools\Tesseract-OCR\tesseract.exe",
            Path.Combine(AppContext.BaseDirectory, "tools", "tesseract.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try { candidates.Add(Path.Combine(dir.Trim(), "tesseract.exe")); } catch { }
        }

        foreach (var c in candidates)
        {
            if (!File.Exists(c)) continue;
            if (!HasChineseLanguage(c)) continue;
            return _tesseract = c;
        }
        return _tesseract = null;
    }

    /// <summary>OCR 这条路整体可用与否：tesseract（带中文包）和 pdftoppm 缺一不可。</summary>
    public static bool IsAvailable() =>
        FindExecutable() != null && PdfTextExtractor.FindPopplerTool("pdftoppm.exe") != null;

    /// <summary>缺什么的一句话说明，给进度日志用（都齐了返回空串）。</summary>
    public static string UnavailableReason() =>
        FindExecutable() == null
            ? "没找到带 chi_sim 语言包的 tesseract.exe（装 Tesseract-OCR 时勾选 Chinese Simplified 即可）"
            : PdfTextExtractor.FindPopplerTool("pdftoppm.exe") == null
                ? "没找到 poppler 的 pdftoppm.exe（跟 pdftotext 同一个安装包）"
                : "";

    private static bool HasChineseLanguage(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = "--list-langs",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            // --list-langs 把清单写到 stdout 还是 stderr 各版本不一样，两边都收
            var outText = proc.StandardOutput.ReadToEnd();
            var errText = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(15_000)) { try { proc.Kill(true); } catch { } return false; }
            return (outText + errText).Contains("chi_sim", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// OCR 指定的几页，返回 (页码, 行文本)。任何一步不可用返回 null；个别页失败就跳过那页。
    /// </summary>
    /// <param name="pages">1 起的页码，调用方按"哪几页出现了指标名"挑好，别整份传进来。</param>
    /// <param name="progress">每页报一次——一页 4~5 秒，不报会像卡死。</param>
    public static List<(int Page, string Text)>? TryOcrPages(
        string pdfPath, IReadOnlyList<int> pages,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var tess = FindExecutable();
        var toppm = PdfTextExtractor.FindPopplerTool("pdftoppm.exe");
        if (tess == null || toppm == null || pages.Count == 0) return null;

        var workDir = Path.Combine(Path.GetTempPath(), $"ocr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var result = new List<(int Page, string Text)>();
            for (int i = 0; i < pages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                int page = pages[i];
                progress?.Invoke($"      OCR 第 {page} 页（{i + 1}/{pages.Count}）...");

                var img = Path.Combine(workDir, $"p{page}");
                // -singlefile：输出就叫 p{page}.png，不用猜 pdftoppm 会给页号补几位零
                // -gray：财报是黑字白底，灰度识别不比彩色差，图小一半、快一截
                if (!Run(toppm, $"-f {page} -l {page} -singlefile -r {Dpi} -gray -png \"{pdfPath}\" \"{img}\"", 120))
                    continue;
                var png = img + ".png";
                if (!File.Exists(png)) continue;

                var outBase = Path.Combine(workDir, $"t{page}");
                // --psm 6 = "一整块统一排版的文字"。财报的指标表和正文都吃这个模式；
                // 默认的 psm 3（自动分栏）会把表格的标签列和数值列拆成两段，标签和值就对不上了。
                if (!Run(tess, $"\"{png}\" \"{outBase}\" -l {Languages} --psm 6", 180)) continue;
                var txt = outBase + ".txt";
                if (!File.Exists(txt)) continue;

                foreach (var raw in File.ReadAllText(txt, Encoding.UTF8).Split('\n'))
                {
                    var line = Tidy(raw);
                    if (line.Length > 0) result.Add((page, line));
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
    /// 不清理的话：① 虽然标签正则本身允许字符间空白、还能匹配上，但"标签必须在行首 6 字内"
    /// 那条判据会失效（行首几个字被空格撑开），表格行会被第一遍扫描漏掉；
    /// ② 标签到值的距离也会被撑大，超过 MaxLabelToValueGap 就取不到值了。
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

    /// <summary>跑一个外部程序，成功返回 true。stdout/stderr 必须读走，否则管道满了会死锁。</summary>
    private static bool Run(string exe, string args, int timeoutSeconds)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.OutputDataReceived += static (_, _) => { };
            proc.ErrorDataReceived += static (_, _) => { };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            if (!proc.WaitForExit(timeoutSeconds * 1000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }
}
