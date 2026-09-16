using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 把一份定期报告下载下来、解析出监管指标（2026-08-29 新增，2026-09-15 拆薄）。
///
/// ════ 它现在只剩协调 ════
/// 「列公告 → 抠 PDF 直链 → 下载落盘」那一整套搬去了 <see cref="SinaReportIndex"/>——
/// 那是**通用的下载数据源**，不认识银行也不认识指标，【子公司名单】那条链也要用它。
/// 留在这里的是银行特有的那部分：
///   · 拿"解析不解析得出指标"当终判，逐个候选试（<see cref="FetchBestAsync"/>）
///   · 失败留痕成 <see cref="BankReportFetchState"/>
///
/// 拆之前这些混在一个类里，而且下载的成功判据就是"解析出了指标"——年报下载那条链根本没法
/// 复用：非金融年报一份都解析不出银行指标，会被当成下错文件删掉。
///
/// ════ 为什么 PDF 要留在本地 ════
/// 解析规则一定会改（各行版式有差异、以后想多抓两个指标），留着就能本地重放，不用重新下载
/// 42 家 × N 期；而且新浪那个文件服务器的 URL 里带内网 IP 和端口，老公告链接失效很常见。
/// 体积：实测 343 份金融报告约 2.2 GB。
/// </summary>
public class BankReportFetcher
{
    private readonly SinaReportIndex _index;
    private readonly string _cacheDir;

    /// <summary>解析器。默认用共享装配；测试可以注入只带某一条 source 的组合。</summary>
    private readonly BankReportParser _parser;

    public event Action<string>? OnStatus
    {
        add => _index.OnStatus += value;
        remove => _index.OnStatus -= value;
    }

    public BankReportFetcher(RateLimiter pageLimiter, RateLimiter fileLimiter, string cacheDir,
        HttpClient? httpClient = null, BankReportParser? parser = null)
        : this(new SinaReportIndex(pageLimiter, fileLimiter, httpClient), cacheDir, parser)
    {
    }

    /// <summary>共用一个已经建好的下载器时走这个——两条链共享限流器和连接。</summary>
    public BankReportFetcher(SinaReportIndex index, string cacheDir, BankReportParser? parser = null)
    {
        _index = index;
        _cacheDir = cacheDir;
        _parser = parser ?? BankReportParser.Default;
    }

    /// <summary>候选公告。类型定义在 <see cref="SinaReportIndex"/>，这里只是转个名方便老调用方。</summary>
    public SinaReportIndex.ReportRef ToRef(string code, DateTime date, string title, string url)
        => new(code, date, title, url);

    public Task<SortedDictionary<DateTime, List<SinaReportIndex.ReportRef>>> ListCandidatesAsync(
        string code, int maxPerKind = 2, CancellationToken ct = default)
        => _index.ListCandidatesAsync(code, maxPerKind, kinds: null, ct);

    public Task<List<SinaReportIndex.ReportRef>> ListReportsAsync(
        string code, int maxPerKind = 2, CancellationToken ct = default)
        => _index.ListReportsAsync(code, maxPerKind, kinds: null, ct);

    /// <summary>
    /// 一个报告期的多个候选**逐个试**，直到解析出指标为止。
    ///
    /// 这是"标题只做粗筛、解析结果才是终判"这条原则的落点：
    ///   · 第 0 档候选正常一次就中，不会多下载什么；
    ///   · 只有它下错了（H股版/专项报告）或者解析不出（版式变了）才会试下一个；
    ///   · 全试完还不行才记 no_match，进手工回填清单。
    ///
    /// 返回**最后一次尝试**的状态；成功的话就是那次成功的。
    /// </summary>
    public async Task<(BankReportFetchState State, List<BankRegulatoryMetric> Metrics)> FetchBestAsync(
        IReadOnlyList<SinaReportIndex.ReportRef> candidates,
        FinancialInstitutionKind kind = FinancialInstitutionKind.Bank,
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
    ///
    /// ⚠ 下载那一半已经搬到 <see cref="SinaReportIndex.DownloadPdfAsync"/>，它自己处理
    ///   "已经下过就不重下"。这里只负责把它的失败状态翻译成 <see cref="BankReportFetchState"/>。
    /// </summary>
    public async Task<(BankReportFetchState State, List<BankRegulatoryMetric> Metrics)> FetchOneAsync(
        SinaReportIndex.ReportRef r, FinancialInstitutionKind kind = FinancialInstitutionKind.Bank,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var metrics = new List<BankRegulatoryMetric>();
        string? pdfUrl = null, pdfPath = null;
        try
        {
            var dl = await _index.DownloadPdfAsync(r, _cacheDir, ct);
            pdfUrl = dl.PdfUrl;
            if (!dl.Ok)
            {
                // too_small / error 都归到 no_pdf，跟拆分前的口径一致
                var status = dl.Status == "no_pdf" || dl.Status == "too_small" ? "no_pdf" : "error";
                return (State(r, status, 0, dl.Message, pdfUrl, null), metrics);
            }
            pdfPath = dl.Path;

            // 传进度进去：数字被转曲的 PDF 会走 OCR，一页 4~5 秒，不报会像卡死。
            metrics = _parser.Parse(pdfPath!, r.Code, r.ReportDate, kind, progress, ct);
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

    private static BankReportFetchState State(SinaReportIndex.ReportRef r, string status, int count,
        string? msg, string? url, string? path) => new()
    {
        Code = r.Code, ReportDate = r.ReportDate, Status = status,
        MetricCount = count, Message = msg, PdfUrl = url, PdfPath = path,
    };
}
