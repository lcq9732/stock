using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 下载银行财报 PDF 并解析监管指标（2026-08-29 新增）。链路：
///   ① 新浪的中报/年报列表页 → 找到公告条目和它的 detail id
///   ② detail 页 → 抠出 PDF 直链（列表页和详情页都只是索引，正文全在 PDF 里）
///   ③ 下载 PDF 到 <c>publish/data/reports/{code}/{yyyy-MM-dd}.pdf</c>
///   ④ 交给 <see cref="BankReportParser"/> 解析
///
/// ════ 为什么只抓年报和中报 ════
/// 一季报/三季报是简版，没有"补充财务比率""资产质量指标""资本充足率指标"那几张表。按 4 期去抓
/// 只会得到一堆空值，还会让状态表反复重试。
///
/// ════ 为什么 PDF 要留在本地 ════
/// 解析规则一定会改（各行版式有差异、以后想多抓两个指标），留着就能本地重放，不用重新下载
/// 42 家 × N 期；而且新浪那个文件服务器的 URL 里带内网 IP 和端口
/// （<c>file.finance.sina.com.cn/211.154.219.97:9494/...</c>），老公告链接失效很常见。
/// 体积可控：42 家 × 2 期/年 × 约 3MB ≈ 250MB/年。
/// </summary>
public class BankReportFetcher
{
    private readonly HttpClient _http;
    private readonly RateLimiter _pageLimiter;
    private readonly RateLimiter _fileLimiter;
    private readonly string _cacheDir;

    public event Action<string>? OnStatus
    {
        add { _pageLimiter.OnStatus += value; _fileLimiter.OnStatus += value; }
        remove { _pageLimiter.OnStatus -= value; _fileLimiter.OnStatus -= value; }
    }

    static BankReportFetcher()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // 新浪页面是 GBK
    }

    /// <summary>
    /// ⚠ **两个限流器，因为是两个不同的服务器、配额也不同**：
    ///   · <paramref name="pageLimiter"/> 打 <c>vip.stock.finance.sina.com.cn</c>（公告列表页、
    ///     详情页）——动态页面，新浪对这一侧的配额敏感得多。项目里踩过的坑：财务报表接口按
    ///     3并发/1秒跑到 100 多个请求就被返回 HTTP 456，**整轮 350 个请求零成功**（见
    ///     App.xaml.cs 里 financialProvider 那段注释）。所以这里必须给保守值。
    ///   · <paramref name="fileLimiter"/> 打 <c>file.finance.sina.com.cn</c>（PDF 直链）——
    ///     静态文件服务器，配额独立且宽松，但单个文件有几 MB，不宜并发拉满。
    /// 混用一个限流器要么把 PDF 拖到极慢，要么把页面那侧玩脱，所以分开。
    /// </summary>
    /// <param name="cacheDir">PDF 缓存根目录（<c>data/reports</c>）。</param>
    public BankReportFetcher(RateLimiter pageLimiter, RateLimiter fileLimiter, string cacheDir,
        HttpClient? httpClient = null)
    {
        _pageLimiter = pageLimiter;
        _fileLimiter = fileLimiter;
        _cacheDir = cacheDir;
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.Timeout = TimeSpan.FromMinutes(3);   // PDF 有几 MB，30 秒不够
    }

    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    /// <summary>一条待处理的报告。</summary>
    public readonly record struct ReportRef(string Code, DateTime ReportDate, string Title, string DetailUrl);

    private const string SinaBase = "https://vip.stock.finance.sina.com.cn";

    /// <summary>中报列表页和年报列表页。两个页面的 URL 模式不一样，见 vCB_BulletinZhong / vCB_Bulletin。</summary>
    private static readonly (string UrlFormat, string Kind)[] ListPages =
    [
        (SinaBase + "/corp/go.php/vCB_BulletinZhong/stockid/{0}/page_type/zqbg.phtml", "中报"),
        (SinaBase + "/corp/go.php/vCB_Bulletin/stockid/{0}/page_type/ndbg.phtml", "年报"),
    ];

    private static readonly Regex DetailLink = new(
        @"href='(/corp/view/vCB_AllBulletinDetail\.php\?stockid=\d+&id=\d+)'[^>]*>([^<]+)</a>",
        RegexOptions.Compiled);

    private static readonly Regex PdfLink = new(
        @"(https?://file\.finance\.sina\.com\.cn/[^""'\s]+\.(?:PDF|pdf))", RegexOptions.Compiled);

    /// <summary>从标题解析报告期："2026年半年度报告"→2026-06-30，"2025年年度报告"→2025-12-31。</summary>
    private static readonly Regex TitleYear = new(@"(\d{4})\s*年", RegexOptions.Compiled);

    /// <summary>
    /// 标题必须**以"XXXX年年度报告"/"XXXX年半年度报告"结尾**才算正式报告正文。
    ///
    /// 早先只用 <c>title.Contains("年度报告")</c> 筛，结果把一堆同样含这四个字的公告当成年报下了下来：
    ///     「关于**落实**2024**年度报告**问询函的回复公告」
    ///     「2026**半年度报告**募集资金存放与实际使用情况的**专项报告**」
    /// 这些文件里根本没有监管指标表，翻遍了也找不到数——实测 353 份里有 9 份是这么下错的
    /// （国泰海通、东吴证券、招商证券等），白白进了手工回填清单让人去翻。
    ///
    /// 结尾允许跟一个括号后缀（"（A股）""（修订版）"这类是同一份报告的不同版本，要留），
    /// 但摘要版和英文版要排除——前者没有完整表格，后者标签是英文、匹配不上。
    /// </summary>
    private static bool IsRealReport(string title, string kind)
    {
        if (title.Contains("摘要") || title.Contains("英文") ||
            title.Contains("English", StringComparison.OrdinalIgnoreCase)) return false;

        var pattern = kind == "中报"
            ? @"\d{4}\s*年\s*半年度报告\s*(（[^）]*）|\([^)]*\))?\s*$"
            : @"\d{4}\s*年\s*年度报告\s*(（[^）]*）|\([^)]*\))?\s*$";
        return Regex.IsMatch(title, pattern);
    }

    /// <summary>
    /// 列出某只银行最近若干期的年报/中报。<paramref name="maxPerKind"/> 控制每类取几期。
    ///
    /// 默认 2 期是有讲究的：体检表只需要**当期 + 去年同期**（算同比和百分点差），2 期年报 +
    /// 2 期中报就够。取 3 期会让 PDF 数量和下载量直接多五成，而第三期基本用不上。
    /// </summary>
    public async Task<List<ReportRef>> ListReportsAsync(string code, int maxPerKind = 2, CancellationToken ct = default)
    {
        var refs = new List<ReportRef>();
        foreach (var (fmt, kind) in ListPages)
        {
            ct.ThrowIfCancellationRequested();
            string html;
            try { html = await FetchTextAsync(string.Format(fmt, code), Encoding.GetEncoding("GBK"), ct); }
            catch { continue; }   // 某一类列表页取不到不影响另一类

            int taken = 0;
            foreach (Match m in DetailLink.Matches(html))
            {
                var title = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                if (!IsRealReport(title, kind)) continue;

                var ym = TitleYear.Match(title);
                if (!ym.Success || !int.TryParse(ym.Groups[1].Value, out var year)) continue;
                var date = kind == "中报" ? new DateTime(year, 6, 30) : new DateTime(year, 12, 31);

                refs.Add(new ReportRef(code, date, title, SinaBase + m.Groups[1].Value));
                if (++taken >= maxPerKind) break;
            }
        }
        return refs;
    }

    /// <summary>
    /// 下载并解析一份报告。任何失败都返回带 status 的状态对象而不是抛异常——
    /// **失败必须留痕**，否则界面上"无数据"分不清是没抓、抓失败、还是这项本来就取不到。
    /// </summary>
    public async Task<(BankReportFetchState State, List<BankRegulatoryMetric> Metrics)> FetchOneAsync(
        ReportRef r, FinancialInstitutionKind kind = FinancialInstitutionKind.Bank,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var metrics = new List<BankRegulatoryMetric>();
        string? pdfUrl = null, pdfPath = null;
        try
        {
            var detail = await FetchTextAsync(r.DetailUrl, Encoding.GetEncoding("GBK"), ct);
            var pm = PdfLink.Match(detail);
            if (!pm.Success)
                return (State(r, "no_pdf", 0, "详情页里没有 PDF 链接", null, null), metrics);
            pdfUrl = pm.Groups[1].Value;

            var dir = Path.Combine(_cacheDir, r.Code);
            Directory.CreateDirectory(dir);
            pdfPath = Path.Combine(dir, $"{r.ReportDate:yyyy-MM-dd}.pdf");

            // 已经下过就不重下——重解析时这一步是免费的，这正是留着 PDF 的意义。
            if (!File.Exists(pdfPath) || new FileInfo(pdfPath).Length < 10_000)
            {
                // PDF 走 file 限流器（静态服务器、独立配额），别占用页面那侧的额度。
                var bytes = await _fileLimiter.RunAsync(() => DownloadAsync(pdfUrl, ct), ct);
                if (bytes.Length < 10_000)
                    return (State(r, "no_pdf", 0, $"下载到的文件只有 {bytes.Length} 字节", pdfUrl, null), metrics);
                await File.WriteAllBytesAsync(pdfPath, bytes, ct);
            }

            // 传进度进去：数字被转曲的 PDF 会走 OCR，一页 4~5 秒，不报会像卡死。
            metrics = BankReportParser.Parse(pdfPath, r.Code, r.ReportDate, kind, progress, ct);
            if (metrics.Count == 0)
                return (State(r, "no_match", 0, "PDF 有文本但没匹配到任何指标（版式可能变了）", pdfUrl, pdfPath), metrics);

            return (State(r, "ok", metrics.Count, null, pdfUrl, pdfPath), metrics);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            // BankReportParser 对"没有文本层"抛的就是这个——多半是扫描件，需要 OCR。
            return (State(r, "no_text", 0, ex.Message, pdfUrl, pdfPath), metrics);
        }
        catch (Exception ex)
        {
            return (State(r, "error", 0, ex.Message, pdfUrl, pdfPath), metrics);
        }
    }

    private static BankReportFetchState State(ReportRef r, string status, int count,
        string? msg, string? url, string? path) => new()
    {
        Code = r.Code, ReportDate = r.ReportDate, Status = status,
        MetricCount = count, Message = msg, PdfUrl = url, PdfPath = path,
    };

    private async Task<string> FetchTextAsync(string url, Encoding encoding, CancellationToken ct)
    {
        // HTML 页面走 page 限流器——这一侧是新浪反爬盯得最紧的地方。
        var bytes = await _pageLimiter.RunAsync(() => DownloadAsync(url, ct), ct);
        return encoding.GetString(bytes);
    }

    private async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri(SinaBase + "/");
        var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new RateLimitedException($"新浪返回 {(int)resp.StatusCode}，疑似触发反爬限流");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }
}
