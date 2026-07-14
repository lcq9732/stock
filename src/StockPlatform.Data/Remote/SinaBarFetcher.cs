using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Remote;

/// <summary>
/// Fetches daily bars from Sina Finance's public classic K线 endpoint
/// (money.finance.sina.com.cn/.../CN_MarketData.getKLineData) — added 2026-07-08 alongside
/// EastMoney/Tencent as a THIRD selectable K线 source, so a network that can already reach Sina
/// (this user already relies on it for 资金净流入/股票列表) can get K线 from the same vendor
/// instead of needing yet another one, per the user's "一个源就够就不要拼凑" preference.
///
/// Confirmed live across all 3 exchange boards (Shanghai/Shenzhen/Beijing) before building this,
/// and confirmed NOT capped at a small page size — a datalen=8000 request for 600519 returned its
/// full lifetime history (5956 rows back to its 2001-08-27 listing) in ONE call, no paging needed
/// (unlike TencentBarFetcher's 640-row hard cap requiring page-stitching). Since this endpoint has
/// no beg/end range parameters (only "give me the most recent N rows"), the requested [start,end]
/// window is estimated into a datalen count and then applied client-side after parsing.
///
/// Known gaps: the endpoint doesn't return 成交额/换手率, so Amount/Turnover are left at 0.
/// （这条限制的分量在2026-07-13变了：TencentBarFetcher 已改用 newfqkline 补上这两个字段，
/// 回测/大盘热度指标开始依赖 Bar.Amount——所以新浪现在只适合当腾讯的回退兜底，它补进来的行
/// 会缺成交额，事后可以用 Fetcher 的"回填成交额/换手率"按钮从腾讯补齐，见
/// FetchOrchestrator.RunBackfillAmountTurnoverAsync。）
/// PctChange is computed locally from consecutive closes, same technique as TencentBarFetcher.
///
/// **Unverified as of 2026-07-08**: whether this endpoint returns front-adjusted (前复权) prices
/// like EastMoney (fqt=1) / Tencent (qfq) do, or raw/unadjusted prices. This codebase previously
/// verified EastMoney and Tencent produce identical front-adjusted numbers across a real 600519
/// dividend event (2026-06-25) — the same cross-check against Sina could NOT be completed here
/// because both EastMoney and Tencent were themselves unreachable from the dev sandbox at
/// verification time (curl to EastMoney returned an empty reply; Tencent's endpoint 302-redirected)
/// — which is itself consistent with this user's report that EastMoney is unreliable, but it means
/// this specific question is still open. If Sina's data turns out to be unadjusted, technical
/// indicators (MA/MACD) computed across a dividend date would show a discontinuity artifact not
/// present in EastMoney/Tencent-sourced series. A fresh "拉取全部" using ONLY this source end to
/// end is at least internally self-consistent either way (no mixing of adjusted and raw prices
/// for the same stock) — flagged to the user as something to visually spot-check before relying on
/// this source for a stock with an upcoming or recent dividend.
/// </summary>
public class SinaBarFetcher : IBarDataFetcher
{
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    static SinaBarFetcher()
    {
        // 这个接口回包声明 charset=gbk（跟 SinaStockListProvider/SinaNetInflowFetcher 一样），GBK
        // 不在 .NET Core 默认编码表里，不注册的话 GetStringAsync 会直接抛异常。
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public SinaBarFetcher(RateLimiter rateLimiter, HttpClient? httpClient = null)
    {
        _rateLimiter = rateLimiter;
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri("http://finance.sina.com.cn");
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    public async Task<(string Name, List<Bar> Bars)> FetchAsync(string code, string granularity, DateTime? start, DateTime? end, CancellationToken ct = default)
    {
        if (granularity != Granularity.Day)
            throw new ArgumentException($"SinaBarFetcher 目前只直接支持日线（周/月请用本地聚合，分钟线暂未实现）：'{granularity}'");

        return await _rateLimiter.RunAsync(() => FetchInternalAsync(code, start, end, ct), ct);
    }

    // 没有beg/end范围参数，只能"要最近N条"，按[start,end]估算一个足够大的条数一次性要回来，本地
    // 按日期过滤——实测datalen没有明显硬上限（8000条时如实返回了茅台从2001年上市至今的全部5956条
    // 历史，没有被截断），宁可多要一些也不要漏掉该补的历史。
    private const int MinDatalen = 60;
    private const int MaxDatalen = 8000;

    private async Task<(string, List<Bar>)> FetchInternalAsync(string code, DateTime? start, DateTime? end, CancellationToken ct)
    {
        var startDate = (start ?? DateTime.Today.AddYears(-3)).Date;
        var endDate = (end ?? DateTime.Today).Date;
        var spanDays = Math.Max(0, (endDate - startDate).TotalDays);
        var datalen = Math.Clamp((int)Math.Ceiling(spanDays * 0.75) + 20, MinDatalen, MaxDatalen);

        // 新浪跟腾讯共用sh/sz/bj前缀；大盘指数（"sh000001"这种已带前缀的完整符号，见
        // MarketIndexCatalog）原样使用——这个接口对指数同样有效（作为Tencent回退链的兜底），
        // 但跟个股一样不返回成交额/换手率，指数行情的量能字段还是以腾讯为准。
        var symbol = MarketIndexCatalog.IsPrefixedSymbol(code) ? code.Trim() : MarketClassifier.TencentSymbolPrefix(code) + code;
        var url = "http://money.finance.sina.com.cn/quotes_service/api/json_v2.php/CN_MarketData.getKLineData" +
                  $"?symbol={symbol}&scale=240&ma=no&datalen={datalen}";

        byte[] bytes;
        try
        {
            bytes = await _http.GetByteArrayAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException(
                $"无法连接新浪财经接口：{detail}（可能是网络问题，也可能是触发了反爬限流）", ex);
        }

        if (bytes.Length == 0)
            throw new InvalidOperationException($"未获取到 {code} 的数据，请检查代码是否正确");

        var body = Encoding.GetEncoding("GBK").GetString(bytes);
        if (string.IsNullOrWhiteSpace(body) || body == "null")
            throw new InvalidOperationException($"未获取到 {code} 的数据，请检查代码是否正确");

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"未获取到 {code} 的数据，请检查代码是否正确");

        var bars = new List<Bar>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var date = DateTime.ParseExact(row.GetProperty("day").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (date < startDate || date > endDate) continue;
            bars.Add(new Bar
            {
                Code = code,
                Granularity = Granularity.Day,
                PeriodStart = date,
                Open = double.Parse(row.GetProperty("open").GetString()!, CultureInfo.InvariantCulture),
                Close = double.Parse(row.GetProperty("close").GetString()!, CultureInfo.InvariantCulture),
                High = double.Parse(row.GetProperty("high").GetString()!, CultureInfo.InvariantCulture),
                Low = double.Parse(row.GetProperty("low").GetString()!, CultureInfo.InvariantCulture),
                Volume = double.Parse(row.GetProperty("volume").GetString()!, CultureInfo.InvariantCulture),
                // 新浪这个接口不直接给成交额/换手率，只能留0——这样的行事后可以用"回填成交额/
                // 换手率"从腾讯补齐（回填只挑amount=0的行，见类注释）。
                Amount = 0,
                Turnover = 0,
                FetchedAt = DateTime.Now,
            });
        }
        bars = bars.OrderBy(b => b.PeriodStart).ToList();

        double? prevClose = null;
        foreach (var b in bars)
        {
            b.PctChange = prevClose is > 0 ? (b.Close - prevClose.Value) / prevClose.Value * 100 : 0;
            prevClose = b.Close;
        }

        // 这个接口不直接返回股票名称——不要紧，FetchOrchestrator.ProcessOneStockAsync拿到返回值
        // 后本来就丢弃了Name（股票名称另外从StockListProvider.GetAllStocksAsync走StockMeta表）。
        return (code, bars);
    }
}
