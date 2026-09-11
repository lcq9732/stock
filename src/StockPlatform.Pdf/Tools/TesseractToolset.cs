namespace StockPlatform.Pdf.Tools;

/// <summary>
/// 找 <c>tesseract.exe</c>。跟 <see cref="PopplerToolset"/> 一样，只负责"找得到吗"，
/// 不负责"怎么用"——用它干活的是 <see cref="Sources.TesseractLineSource"/>。
/// </summary>
public sealed class TesseractToolset
{
    /// <summary>OCR 用的语言：简体中文 + 英文（数字走 eng 那套字形模型）。</summary>
    public const string Languages = "chi_sim+eng";

    private readonly object _lock = new();
    private string? _exe;
    private bool _searched;

    /// <summary>直接指定路径，跳过搜索和语言包检查。给配置文件和测试用。</summary>
    public TesseractToolset(string? tesseractPath = null)
    {
        if (!string.IsNullOrWhiteSpace(tesseractPath))
        {
            _exe = tesseractPath;
            _searched = true;
        }
    }

    /// <summary>
    /// tesseract.exe 的路径，找不到返回 null。
    ///
    /// **必须带 chi_sim 语言包才算找到**——只装了 eng 的话中文标签一个都认不出来，
    /// 认出一堆数字却对不上任何指标名，比不做还糟。结果缓存（找不到也缓存）。
    /// </summary>
    public string? Executable
    {
        get
        {
            lock (_lock)
            {
                if (_searched) return _exe;
                _searched = true;
                return _exe = Search();
            }
        }
    }

    private static string? Search()
    {
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
            return c;
        }
        return null;
    }

    private static bool HasChineseLanguage(string exe)
    {
        var r = ProcessRunner.Run(exe, "--list-langs", 15);
        if (!r.Completed) return false;
        // --list-langs 把清单写到 stdout 还是 stderr 各版本不一样，两边都收
        return (r.StdOut + r.StdErr).Contains("chi_sim", StringComparison.OrdinalIgnoreCase);
    }
}
