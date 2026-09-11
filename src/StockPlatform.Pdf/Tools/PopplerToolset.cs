using System.Text;

namespace StockPlatform.Pdf.Tools;

/// <summary>
/// 找 poppler 家族的外部程序（<c>pdftotext.exe</c> / <c>pdftoppm.exe</c>）。
///
/// ════ 为什么单独一个对象 ════
/// 拆分前这件事挂在 PdfTextExtractor 上，而 PdfOcrExtractor 要用 pdftoppm 时得
/// 反过来调 <c>PdfTextExtractor.FindPopplerTool(...)</c>——两个提取器互相认识，
/// 只为了借一个"你找到的那个目录"。把"找工具"抽成独立职责之后，两个 source 各自
/// 依赖这一个对象，彼此不再打照面。
///
/// ════ 状态是实例的，不是进程的 ════
/// 探测结果（找到了哪个 exe、探没探过）原来是 static 字段，于是：测试里没法重置、
/// 没法并行、也没法在配置里直接指定路径绕过搜索。改成实例字段后这三件事一起解决。
/// </summary>
public sealed class PopplerToolset
{
    private readonly object _lock = new();
    private string? _pdftotext;
    private bool _searched;
    private string? _probeSample;

    /// <summary>
    /// 直接指定 pdftotext 的路径，跳过搜索和探测。
    /// 给配置文件和测试用；传 null（默认）就走自动搜索。
    /// </summary>
    public PopplerToolset(string? pdftotextPath = null)
    {
        if (!string.IsNullOrWhiteSpace(pdftotextPath))
        {
            _pdftotext = pdftotextPath;
            _searched = true;
        }
    }

    /// <summary>
    /// 给一份中文 PDF 当探测样本。
    ///
    /// 原来这是隐式的——第一次调 TryExtractLines 时偷偷把当时那份 pdfPath 记成样本
    /// （<c>_probeSample ??= pdfPath</c>），谁先调谁决定，读代码完全看不出来。
    /// 改成显式方法：调用方本来就手握一份中文年报，比内置任何样本都合适，说清楚就好。
    /// 已经搜索过之后再给样本不会重新探测（结果已缓存）。
    /// </summary>
    public void UseProbeSample(string pdfPath)
    {
        lock (_lock) { _probeSample ??= pdfPath; }
    }

    /// <summary>pdftotext.exe 的路径，找不到返回 null。结果缓存（找不到也缓存）。</summary>
    public string? PdfToText
    {
        get
        {
            lock (_lock)
            {
                if (_searched) return _pdftotext;
                _searched = true;
                return _pdftotext = Search();
            }
        }
    }

    /// <summary>
    /// poppler 家族里的其它工具（<c>pdftoppm.exe</c> 等），跟**已经验证过中文提取能力**的
    /// pdftotext 取同一个目录。这样 OCR 那条路不会误用 Git 自带的那个残缺版 poppler——
    /// 探测的活儿在 <see cref="PdfToText"/> 里已经干过一遍了。
    /// </summary>
    public string? Tool(string exeName)
    {
        var pdftotext = PdfToText;
        if (pdftotext == null) return null;
        var dir = Path.GetDirectoryName(pdftotext);
        if (dir == null) return null;
        var path = Path.Combine(dir, exeName);
        return File.Exists(path) ? path : null;
    }

    private string? Search()
    {
        // ⚠ **顺序很重要：完整 poppler 安装要排在 PATH 前面。**
        // Git for Windows 自带一个 mingw64\bin\pdftotext.exe，它**没带 poppler-data**
        // （CJK 的编码表），转中文年报会直接产出空文件——实测这一版对中国人保/青岛银行/
        // 建设银行全部返回空，而同样几份用 C:\Tools\poppler-24.08.0 那个完整安装一转就出来。
        // 早期版本先查 PATH，于是永远命中 Git 那个残缺版，兜底等于没接上。
        var candidates = new List<string>();

        // ① 完整发行包（结构是 <root>/Library/bin/ 或 <root>/bin/）
        foreach (var root in new[] { @"C:\Tools", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\" })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var d in Directory.GetDirectories(root, "poppler*"))
                {
                    candidates.Add(Path.Combine(d, "Library", "bin", "pdftotext.exe"));
                    candidates.Add(Path.Combine(d, "bin", "pdftotext.exe"));
                }
            }
            catch { }
        }

        // ② 程序自带的 tools 目录（想随程序分发时放这里）
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "tools", "pdftotext.exe"));
        candidates.Add(Path.Combine(baseDir, "tools", "poppler", "bin", "pdftotext.exe"));

        // ③ 最后才轮到 PATH（多半是 Git 那个残缺版，只当没有别的选择时的下策）
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try { candidates.Add(Path.Combine(dir.Trim(), "pdftotext.exe")); }
            catch { }
        }

        // **逐个实际试一遍中文提取能力**，别只看文件在不在——残缺版同样存在、同样能启动，
        // 只是转出来是空的。用调用方给的样本 PDF 探测，一次就够，结果缓存。
        foreach (var c in candidates)
        {
            if (!File.Exists(c)) continue;
            if (_probeSample == null) return c;          // 没样本可探测，只能先信它
            if (CanExtractChinese(c, _probeSample)) return c;
        }
        return null;
    }

    /// <summary>
    /// 试着用某个 pdftotext 转一份中文 PDF，看能不能真的吐出中文。
    /// 只看 exe 在不在是不够的——Git 自带那个残缺版照样存在、照样能启动，只是产出空文件。
    /// </summary>
    private static bool CanExtractChinese(string exe, string samplePdf)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"probe_{Guid.NewGuid():N}.txt");
        try
        {
            // 只转前 3 页，探测要快
            var r = ProcessRunner.Run(
                exe, $"-f 1 -l 3 -layout -enc UTF-8 \"{samplePdf}\" \"{tmp}\"", 30);
            // 跟正式提取一样**不看退出码**，只看有没有转出东西来
            if (!r.Completed || !File.Exists(tmp)) return false;
            var text = File.ReadAllText(tmp, Encoding.UTF8);
            // 中文年报前三页怎么也该有几百个汉字；残缺版这里会是 0
            return text.Count(c => c >= 0x4E00 && c <= 0x9FFF) > 100;
        }
        catch { return false; }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }
}
