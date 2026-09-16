using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 从新浪列公告、下定期报告 PDF（2026-09-15 从 <see cref="BankReportFetcher"/> 抽出来）。
/// 链路：
///   ① 中报/年报列表页 → 公告条目和它的 detail id
///   ② detail 页 → 抠出 PDF 直链（列表页和详情页都只是索引，正文全在 PDF 里）
///   ③ 下载 PDF 到 <c>{targetDir}/{code}/{yyyy-MM-dd}.pdf</c>
///
/// ════ 它只做下载 ════
/// <b>不认识"银行""监管指标""子公司"，不持有任何 parser。</b>成功判据就是"文件存下来了"。
/// 拿到文件之后要解析成什么，是调用方的事：
///   · 【金融监管指标】→ <see cref="BankReportParser"/> 出监管指标
///   · 【子公司名单】  → <c>SubsidiaryParser</c> 出合并报表范围里的子公司
///
/// 抽出来之前这些混在一个类里，而且 <c>FetchOneAsync</c> 拿"解析出不出得来指标"当下载的
/// 成功判据——年报下载那条链没法复用（它一份都解析不出银行指标，会被当成下错文件）。
///
/// ════ 为什么只列年报和中报 ════
/// 一季报/三季报是简版，没有监管指标那几张表，也没有完整的合并报表范围。按 4 期去抓
/// 只会得到一堆空值，还会让状态表反复重试。
///
/// ════ 为什么 PDF 要留在本地 ════
/// 解析规则一定会改（各家版式有差异、以后想多抓两个指标），留着就能本地重放，不用重新下载；
/// 而且新浪那个文件服务器的 URL 里带内网 IP 和端口
/// （<c>file.finance.sina.com.cn/211.154.219.97:9494/...</c>），老公告链接失效很常见。
/// </summary>
public class SinaReportIndex
{
    private readonly HttpClient _http;
    private readonly RateLimiter _pageLimiter;
    private readonly RateLimiter _fileLimiter;

    public event Action<string>? OnStatus
    {
        add { _pageLimiter.OnStatus += value; _fileLimiter.OnStatus += value; }
        remove { _pageLimiter.OnStatus -= value; _fileLimiter.OnStatus -= value; }
    }

    /// <summary>
    /// 限流分两把：HTML 页面那侧是新浪反爬盯得最紧的地方，PDF 走静态文件服务器、配额独立。
    /// 混用一把会让下载 PDF 的量把页面那侧的额度吃光。
    /// </summary>
    public SinaReportIndex(RateLimiter pageLimiter, RateLimiter fileLimiter,
                           HttpClient? httpClient = null)
    {
        _pageLimiter = pageLimiter;
        _fileLimiter = fileLimiter;
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

    /// <summary>一条候选公告。<paramref name="Title"/> 留着是为了出问题时能追是哪一份。</summary>
    public readonly record struct ReportRef(string Code, DateTime ReportDate, string Title, string DetailUrl);

    public const string KindAnnual = "年报";
    public const string KindInterim = "中报";

    private const string SinaBase = "https://vip.stock.finance.sina.com.cn";

    /// <summary>中报列表页和年报列表页。两个页面的 URL 模式不一样，见 vCB_BulletinZhong / vCB_Bulletin。</summary>
    private static readonly (string UrlFormat, string Kind)[] ListPages =
    [
        (SinaBase + "/corp/go.php/vCB_BulletinZhong/stockid/{0}/page_type/zqbg.phtml", KindInterim),
        (SinaBase + "/corp/go.php/vCB_Bulletin/stockid/{0}/page_type/ndbg.phtml", KindAnnual),
    ];

    private static readonly Regex DetailLink = new(
        @"href='(/corp/view/vCB_AllBulletinDetail\.php\?stockid=\d+&id=\d+)'[^>]*>([^<]+)</a>",
        RegexOptions.Compiled);

    private static readonly Regex PdfLink = new(
        @"(https?://file\.finance\.sina\.com\.cn/[^""'\s]+\.(?:PDF|pdf))", RegexOptions.Compiled);

    /// <summary>从标题解析报告期："2026年半年度报告"→2026-06-30，"2025年年度报告"→2025-12-31。</summary>
    private static readonly Regex TitleYear = new(@"(\d{4})\s*年", RegexOptions.Compiled);

    /// <summary>
    /// 标题里出现这些词就不是正文。
    ///
    /// ⚠ **H股/港股那两个是 2026-09-02 加的**：老规则只看结尾，把港版也放行了。港版指标表的
    ///   口径和排版跟 A 股版不同，解析不出来，于是记成 wrong_file——全库 21 条 wrong_file 里
    ///   一大批就是中行/工行/中信/浦发/太保这些 **A+H 两地上市**的银行保险。
    /// </summary>
    private static readonly string[] TitleBlockers =
    [
        "摘要", "英文", "English", "H股", "港股",
        "问询", "回复", "专项报告", "行动方案", "落实情况", "更正", "补充",
    ];

    /// <summary>
    /// 标题有多像"报告正文"：**-1 = 排除**，0/1/2 = 候选优先级（越小越可信）。
    ///
    /// ⚠ 这里只是**粗筛加排序**，不是终判。真正的判据由调用方定（比如"解析不解析得出指标"），
    /// 所以宁可多留几个候选逐个试，也不要在这里把对的那个排除掉。
    ///
    /// 为什么不能靠标题定生死，两个方向都吃过亏：
    ///   · **太松**会下错——「关于落实2024年度报告问询函的回复公告」也含"年度报告"（实测 353 份里 9 份）；
    ///   · **太严**会漏掉，而且是**静默**的——平安银行 2022 年半年报的标题是
    ///     「2022年半年度报告**2**」（末尾多个 2），严格正则要求以"报告"结尾，直接漏过，
    ///     那一期就无声无息地没有数据。太严比太松更危险：下错了会进手工回填清单让人看见，
    ///     漏掉了则什么痕迹都不留。
    ///
    /// 用例见 <c>ReportTitleRankTests</c>——2026-09-15 抽这个类之前补的，样本全是上面这些真实标题。
    /// </summary>
    internal static int TitleRank(string title, string kind)
    {
        foreach (var bad in TitleBlockers)
            if (title.Contains(bad, StringComparison.OrdinalIgnoreCase)) return -1;

        string core = kind == KindInterim ? @"\d{4}\s*年\s*半年度报告" : @"\d{4}\s*年\s*年度报告";

        // 0 档：干干净净以"XXXX年年度报告"结尾（允许「（A股）」「（修订版）」这类版本后缀）
        if (Regex.IsMatch(title, core + @"\s*(（[^）]*）|\([^)]*\))?\s*$")) return 0;
        // 1 档：正文标题带了别的零碎（"…年度报告2"、末尾跟编号/日期之类）
        if (Regex.IsMatch(title, core)) return 1;
        // 2 档：连"年度报告"都不完整，只是像（"2024年半年报"）。兜底，正常轮不到
        if (kind == KindInterim && Regex.IsMatch(title, @"\d{4}\s*年\s*半年报")) return 2;
        if (kind != KindInterim && Regex.IsMatch(title, @"\d{4}\s*年\s*年报")) return 2;
        return -1;
    }

    /// <summary>
    /// 列出最近若干期的年报/中报，每期只留最可信的一条。要多候选用 <see cref="ListCandidatesAsync"/>。
    /// </summary>
    public async Task<List<ReportRef>> ListReportsAsync(
        string code, int maxPerKind = 2, IReadOnlyCollection<string>? kinds = null,
        CancellationToken ct = default)
    {
        var byDate = await ListCandidatesAsync(code, maxPerKind, kinds, ct);
        return byDate.Values.Where(l => l.Count > 0).Select(l => l[0]).ToList();
    }

    /// <summary>
    /// 列出最近若干期的年报/中报，**每一期给出全部候选**（按 <see cref="TitleRank"/> 从可信到勉强排序）。
    ///
    /// 为什么要多候选：同一个报告期往往有好几条公告的标题都长得像正文，光看标题分不出哪份是。
    /// 中国银行 2025 年报就有「2025年年度报告」和「H股公告-2025年年度报告」两条，平安银行
    /// 还出现过「2022年半年度报告2」。与其把宝押在正则上，不如下载下来让调用方试。
    ///
    /// <paramref name="maxPerKind"/> 数的是**报告期**不是公告条数——同一期的其它候选仍然要收。
    /// <paramref name="kinds"/> 为 null 时年报中报都列；【子公司名单】只要年报，传
    /// <c>[KindAnnual]</c> 能省掉一半请求。
    /// </summary>
    public async Task<SortedDictionary<DateTime, List<ReportRef>>> ListCandidatesAsync(
        string code, int maxPerKind = 2, IReadOnlyCollection<string>? kinds = null,
        CancellationToken ct = default)
    {
        var byDate = new SortedDictionary<DateTime, List<ReportRef>>(
            Comparer<DateTime>.Create((a, b) => b.CompareTo(a)));   // 新的报告期排前面
        var rank = new Dictionary<(DateTime, string), int>();

        foreach (var (fmt, kind) in ListPages)
        {
            if (kinds != null && !kinds.Contains(kind)) continue;
            ct.ThrowIfCancellationRequested();
            string html;
            try { html = await FetchTextAsync(string.Format(fmt, code), Encoding.GetEncoding("GBK"), ct); }
            catch { continue; }   // 某一类列表页取不到不影响另一类

            var periods = new HashSet<DateTime>();
            foreach (Match m in DetailLink.Matches(html))
            {
                var title = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                int r = TitleRank(title, kind);
                if (r < 0) continue;

                var ym = TitleYear.Match(title);
                if (!ym.Success || !int.TryParse(ym.Groups[1].Value, out var year)) continue;
                var date = kind == KindInterim ? new DateTime(year, 6, 30) : new DateTime(year, 12, 31);

                // 期数够了就别再往里加新的报告期（同一期的其它候选还是要收）
                if (!periods.Contains(date) && periods.Count >= maxPerKind) continue;
                periods.Add(date);

                var url = SinaBase + m.Groups[1].Value;
                if (!byDate.TryGetValue(date, out var list)) byDate[date] = list = [];
                if (list.Any(x => x.DetailUrl == url)) continue;      // 同一条公告列了两遍
                list.Add(new ReportRef(code, date, title, url));
                rank[(date, url)] = r;
            }
        }

        foreach (var list in byDate.Values)
            list.Sort((a, b) => rank[(a.ReportDate, a.DetailUrl)].CompareTo(rank[(b.ReportDate, b.DetailUrl)]));
        return byDate;
    }

    /// <summary>下载一份报告的结果。<b>失败不抛异常，返回带 status 的结构</b>——失败必须留痕。</summary>
    /// <param name="Status">ok / already / no_pdf / too_small / error</param>
    public readonly record struct DownloadResult(
        string Status, string? Path, string? PdfUrl, string? Message)
    {
        public bool Ok => Status is "ok" or "already";
    }

    /// <summary>本地文件小于这个字节数就当没下成——新浪偶尔返回几百字节的错误页。</summary>
    public const int MinPdfBytes = 10_000;

    /// <summary>
    /// 算这份报告在本地的落点。**给调用方在联网之前先查文件用**——
    /// 【子公司名单】那种"改了解析规则要全量重跑"的场景，文件都在时必须一个请求都不发，
    /// 所以这个路径不能只有下载的时候才知道。
    /// </summary>
    public static string PathOf(string targetDir, string code, DateTime reportDate)
        => Path.Combine(targetDir, code, $"{reportDate:yyyy-MM-dd}.pdf");

    /// <summary>
    /// 下载一份报告的 PDF，落到 <c>{targetDir}/{code}/{yyyy-MM-dd}.pdf</c>。
    ///
    /// <b>已经下过就不重下</b>（返回 <c>already</c>）——重解析时这一步是免费的，
    /// 这正是把 PDF 留在本地的意义。
    ///
    /// ⚠ 限流异常（403/429 的 <see cref="RateLimitedException"/>）**照常往上抛**，不吞成一条
    ///   失败状态。被限流是"整条链该停下来歇会儿"，不是"这一份取不到"，两者的处理完全不同。
    /// </summary>
    public async Task<DownloadResult> DownloadPdfAsync(
        ReportRef r, string targetDir, CancellationToken ct = default)
    {
        var pdfPath = PathOf(targetDir, r.Code, r.ReportDate);
        if (File.Exists(pdfPath) && new FileInfo(pdfPath).Length >= MinPdfBytes)
            return new DownloadResult("already", pdfPath, null, null);

        string? pdfUrl = null;
        try
        {
            var detail = await FetchTextAsync(r.DetailUrl, Encoding.GetEncoding("GBK"), ct);
            var pm = PdfLink.Match(detail);
            if (!pm.Success)
                return new DownloadResult("no_pdf", null, null, "详情页里没有 PDF 链接");
            pdfUrl = pm.Groups[1].Value;

            // PDF 走 file 限流器（静态服务器、独立配额），别占用页面那侧的额度。
            var bytes = await _fileLimiter.RunAsync(() => DownloadAsync(pdfUrl, ct), ct);
            if (bytes.Length < MinPdfBytes)
                return new DownloadResult("too_small", null, pdfUrl,
                                          $"下载到的文件只有 {bytes.Length} 字节");

            Directory.CreateDirectory(Path.GetDirectoryName(pdfPath)!);
            await File.WriteAllBytesAsync(pdfPath, bytes, ct);
            return new DownloadResult("ok", pdfPath, pdfUrl, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (RateLimitedException) { throw; }
        catch (Exception ex)
        {
            return new DownloadResult("error", null, pdfUrl, ex.Message);
        }
    }

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
