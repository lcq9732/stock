using System.Diagnostics;
using System.Text;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 用外部的 <c>pdftotext</c>（Poppler）把 PDF 转成文本，作为 PdfPig 读不动时的**兜底方案**
/// （2026-08-29 新增）。
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
/// 正好能复用现有的标签匹配逻辑，不必为这条路径另写一套解析。
///
/// ════ 找不到就退回原路 ════
/// pdftotext 是可选的外部程序，不打包进发布产物。找不到时本类直接返回 null，解析退回
/// PdfPig 的结果（也就是维持现状），不会因为缺它而报错。
/// </summary>
public static class PdfTextExtractor
{
    private static string? _cachedPath;
    private static bool _searched;

    /// <summary>探测用的样本 PDF（第一次调用 <see cref="TryExtractLines"/> 时自动设成那份文件）。</summary>
    private static string? _probeSample;

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
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = $"-f 1 -l 3 -layout -enc UTF-8 \"{samplePdf}\" \"{tmp}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            // 同样要读走 stderr，理由见 TryExtractLines 里的说明
            proc.OutputDataReceived += static (_, _) => { };
            proc.ErrorDataReceived += static (_, _) => { };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            if (!proc.WaitForExit(30_000)) { try { proc.Kill(true); } catch { } return false; }
            if (!File.Exists(tmp)) return false;
            var text = File.ReadAllText(tmp, Encoding.UTF8);
            // 中文年报前三页怎么也该有几百个汉字；残缺版这里会是 0
            return text.Count(c => c >= 0x4E00 && c <= 0x9FFF) > 100;
        }
        catch { return false; }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    /// <summary>
    /// 找 pdftotext.exe。按"环境变量 PATH → 常见安装位置 → 程序自带的 tools 目录"依次找，
    /// 结果缓存（找不到也缓存，避免每份 PDF 都白找一遍）。
    /// </summary>
    public static string? FindExecutable()
    {
        if (_searched) return _cachedPath;
        _searched = true;

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
            if (_probeSample == null) return _cachedPath = c;   // 没样本可探测，只能先信它
            if (CanExtractChinese(c, _probeSample)) return _cachedPath = c;
        }

        return _cachedPath = null;
    }

    /// <summary>
    /// poppler 家族里的其它工具（<c>pdftoppm.exe</c> 等），跟**已经验证过中文提取能力**的
    /// pdftotext 取同一个目录。这样 OCR 那条路（<see cref="PdfOcrExtractor"/>）不会误用
    /// Git 自带的那个残缺版 poppler——探测的活儿在 <see cref="FindExecutable"/> 里已经干过一遍了。
    /// </summary>
    public static string? FindPopplerTool(string exeName)
    {
        var pdftotext = FindExecutable();
        if (pdftotext == null) return null;
        var dir = Path.GetDirectoryName(pdftotext);
        if (dir == null) return null;
        var path = Path.Combine(dir, exeName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// 转成按行切好的文本；不可用或失败返回 null。
    /// <paramref name="timeoutSeconds"/> 到点强杀——个别几百页的年报可能很慢，不能无限等。
    /// </summary>
    public static List<(int Page, string Text)>? TryExtractLines(string pdfPath, int timeoutSeconds = 120)
    {
        // 拿本次要处理的 PDF 当探测样本——它一定是中文年报，比内置任何样本都合适。
        _probeSample ??= pdfPath;
        var exe = FindExecutable();
        if (exe == null) return null;

        var tmp = Path.Combine(Path.GetTempPath(), $"pdftotext_{Guid.NewGuid():N}.txt");
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                // -layout 保留列对齐；-enc UTF-8 免得默认按本地代码页输出
                Arguments = $"-layout -enc UTF-8 \"{pdfPath}\" \"{tmp}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;

            // ⚠ **重定向了就必须把流读掉，否则会死锁。**
            // pdftotext 对字体有问题的 PDF 会往 stderr 狂刷警告（中国人保那份每页都报
            // "Mismatch between font type and embedded font file"）。父进程不读，管道缓冲区
            // 一满子进程就卡住写不动，这边 WaitForExit 干等到超时、把它杀掉、返回 null——
            // 表现就是"手动在终端跑 0.6 秒出结果，程序里却说读不出文本层"。
            // 异步读走并丢弃即可，我们只要输出文件。
            proc.OutputDataReceived += static (_, _) => { };
            proc.ErrorDataReceived += static (_, _) => { };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            if (!proc.WaitForExit(timeoutSeconds * 1000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            if (!File.Exists(tmp)) return null;

            var text = File.ReadAllText(tmp, Encoding.UTF8);
            if (text.Length < 200) return null;      // 基本是空的，没救回来

            // pdftotext 用换页符 \f 分页，据此还原页码——source_page 要能对回原文人工核对。
            var result = new List<(int Page, string Text)>();
            var pages = text.Split('\f');
            for (int i = 0; i < pages.Length; i++)
                foreach (var raw in pages[i].Split('\n'))
                {
                    var l = raw.TrimEnd('\r').Trim();
                    if (l.Length > 0) result.Add((i + 1, l));
                }
            return result;
        }
        catch { return null; }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }
}
