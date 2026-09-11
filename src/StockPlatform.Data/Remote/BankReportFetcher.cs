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

    /// <summary>解析器。默认用共享装配；测试可以注入只带某一条 source 的组合。</summary>
    private readonly BankReportParser _parser;

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
        HttpClient? httpClient = null, BankReportParser? parser = null)
    {
        _parser = parser ?? BankReportParser.Default;
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
    /// <summary>
    /// 标题**明显不可能是报告正文**的那些词。命中一个就直接排除，不下载。
    ///
    /// H股 是 2026-09-02 加的，实测抓到的元凶：中国银行的年报列表里
    /// 「2025年年度报告」和「H股公告-2025年年度报告」**两条都以"年度报告"结尾**，
    /// 老规则只看结尾，把港版也放行了。港版指标表的口径和排版跟 A 股版不同，解析不出来，
    /// 于是记成 wrong_file——全库 21 条 wrong_file 里一大批就是中行/工行/中信/浦发/太保
    /// 这些 **A+H 两地上市**的银行保险。
    /// </summary>
    private static readonly string[] TitleBlockers =
    [
        "摘要", "英文", "English", "H股", "港股",
        "问询", "回复", "专项报告", "行动方案", "落实情况", "更正", "补充",
    ];

    /// <summary>
    /// 标题有多像"报告正文"：**-1 = 排除**，0/1/2 = 候选优先级（越小越可信）。
    ///
    /// ⚠ 这里只是**粗筛加排序**，不是终判（2026-09-02 按用户意见改）。
    /// 真正的判据是"这份 PDF 里解析不解析得出监管指标"——见 FetchBestAsync：
    /// 它按这个档次逐个下载试，出得来指标就采用、出不来就删掉试下一个。
    ///
    /// 为什么不能靠标题定生死，两个方向都吃过亏：
    ///   · **太松**会下错——「关于落实2024年度报告问询函的回复公告」也含"年度报告"（实测 353 份里 9 份）；
    ///   · **太严**会漏掉，而且是**静默**的——平安银行 2022 年半年报的标题是
    ///     「2022年半年度报告**2**」（末尾多个 2），严格正则要求以"报告"结尾，直接漏过，
    ///     那一期就无声无息地没有数据。太严比太松更危险：下错了会进手工回填清单让人看见，
    ///     漏掉了则什么痕迹都不留。
    /// </summary>
    private static int TitleRank(string title, string kind)
    {
        foreach (var bad in TitleBlockers)
            if (title.Contains(bad, StringComparison.OrdinalIgnoreCase)) return -1;

        string core = kind == "中报" ? @"\d{4}\s*年\s*半年度报告" : @"\d{4}\s*年\s*年度报告";

        // 0 档：干干净净以"XXXX年年度报告"结尾（允许「（A股）」「（修订版）」这类版本后缀）
        if (Regex.IsMatch(title, core + @"\s*(（[^）]*）|\([^)]*\))?\s*$")) return 0;
        // 1 档：正文标题带了别的零碎（"…年度报告2"、末尾跟编号/日期之类）
        if (Regex.IsMatch(title, core)) return 1;
        // 2 档：连"年度报告"都不完整，只是像（"2024年半年报"）。兜底，正常轮不到
        if (kind == "中报" && Regex.IsMatch(title, @"\d{4}\s*年\s*半年报")) return 2;
        if (kind != "中报" && Regex.IsMatch(title, @"\d{4}\s*年\s*年报")) return 2;
        return -1;
    }

    /// <summary>
    /// 列出某只银行最近若干期的年报/中报。<paramref name="maxPerKind"/> 控制每类取几期。
    ///
    /// 默认 2 期是有讲究的：体检表只需要**当期 + 去年同期**（算同比和百分点差），2 期年报 +
    /// 2 期中报就够。取 3 期会让 PDF 数量和下载量直接多五成，而第三期基本用不上。
    /// </summary>
    public async Task<List<ReportRef>> ListReportsAsync(string code, int maxPerKind = 2, CancellationToken ct = default)
        => (await ListCandidatesAsync(code, maxPerKind, ct))
            .Select(g => g.Value[0]).OrderByDescending(r => r.ReportDate).ToList();

    /// <summary>
    /// 列出最近若干期的年报/中报，**每一期给出全部候选**（按 <see cref="TitleRank"/> 从可信到勉强排序）。
    ///
    /// 为什么要多候选（2026-09-02 改）：同一个报告期往往有好几条公告的标题都长得像正文，
    /// 光看标题分不出哪份才有指标表。中国银行 2025 年报就有「2025年年度报告」和
    /// 「H股公告-2025年年度报告」两条，平安银行还出现过「2022年半年度报告2」。
    /// 与其把宝押在正则上，不如**下载下来解析一下，用"出不出得来指标"当判据**——见 FetchBestAsync。
    ///
    /// <paramref name="maxPerKind"/> 数的是**报告期**不是公告条数：体检表只要"当期 + 去年同期"，
    /// 2 期年报 + 2 期中报够用，取 3 期会让下载量多五成而第三期基本用不上。
    /// </summary>
    public async Task<SortedDictionary<DateTime, List<ReportRef>>> ListCandidatesAsync(
        string code, int maxPerKind = 2, CancellationToken ct = default)
    {
        var byDate = new SortedDictionary<DateTime, List<ReportRef>>(
            Comparer<DateTime>.Create((a, b) => b.CompareTo(a)));   // 新的报告期排前面
        var rank = new Dictionary<(DateTime, string), int>();

        foreach (var (fmt, kind) in ListPages)
        {
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
                var date = kind == "中报" ? new DateTime(year, 6, 30) : new DateTime(year, 12, 31);

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

    /// <summary>
    /// 一个报告期的多个候选**逐个试**，直到解析出指标为止。
    ///
    /// 这是"标题只做粗筛、解析结果才是终判"这条原则的落点：
    ///   · 第 0 档候选正常一次就中，不会多下载什么；
    ///   · 只有它下错了（H股版/专项报告）或者解析不出（版式变了）才会试下一个；
    ///   · 全试完还不行才记 no_match，进手工回填清单。
    ///
    /// 返回**最后一次尝试**的状态；成功的话就是那次成功的。多试的那几份都会被删掉，
    /// 不留在本地占地方也不干扰"已缓存 PDF 重解析"那条自愈路径。
    /// </summary>
    public async Task<(BankReportFetchState State, List<BankRegulatoryMetric> Metrics)> FetchBestAsync(
        IReadOnlyList<ReportRef> candidates, FinancialInstitutionKind kind = FinancialInstitutionKind.Bank,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        BankReportFetchState? last = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = candidates[i];
            if (i > 0)
                progress?.Invoke($"    上一个候选没解析出指标，改试：{r.Title}");

            var (state, metrics) = await FetchOneAsync(r, kind, progress, ct);
            if (metrics.Count > 0) return (state, metrics);
            last = state;
        }
        return (last ?? new BankReportFetchState
        {
            Code = candidates.Count > 0 ? candidates[0].Code : "",
            ReportDate = candidates.Count > 0 ? candidates[0].ReportDate : default,
            Status = "no_pdf",
            Message = "这一期没有像正文的公告",
        }, []);
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
            metrics = _parser.Parse(pdfPath, r.Code, r.ReportDate, kind, progress, ct);
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
