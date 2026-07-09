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
/// Fetches per-stock daily 资金净流入 history from Sina Finance's public money-flow trend endpoint
/// — the DEFAULT net-inflow source (2026-07-08) because EastMoney (see
/// <see cref="EastMoneyNetInflowFetcher"/>) is largely unreachable in this user's actual network
/// environment, while Sina/Tencent are what they can actually use (Sina is already used elsewhere
/// in this codebase — <see cref="SinaStockListProvider"/> — paired with Tencent's K线 source).
///
/// Field note: this endpoint's `netamount` is the net inflow summed across ALL order-size
/// categories (超大单+大单+中单+小单), not EastMoney's f52 "主力净流入"（超大单+大单 only）— Sina's
/// history response only separately exposes `r0_net`（超大单）, not 大单, so the exact EastMoney
/// definition can't be reproduced here. `netamount` is used as it's the more literal reading of
/// "资金净流入"（total capital net inflow, the user's own wording）rather than "主力净流入".
///
/// Unlike EastMoney's endpoint, this one has no beg/end/limit parameters — every request returns
/// the stock's ENTIRE history (confirmed live: 3964 daily rows for 600519, back to 2010-03-01,
/// ~1MB+ per stock), so the requested [start, end] window is applied client-side after parsing,
/// not sent to the server. This is a real bandwidth/time cost across a full-market run (see
/// doc/data-platform-design.md) — accepted because a working-but-heavier source beats a
/// non-functional lighter one.
/// </summary>
public class SinaNetInflowFetcher : INetInflowFetcher
{
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    static SinaNetInflowFetcher()
    {
        // 这个接口回包声明 charset=gbk（跟 SinaStockListProvider 一样），GBK 不在 .NET Core 默认
        // 编码表里，不注册的话连 GetByteArrayAsync 之外的任何自动按声明字符集解码的路径都会抛异常。
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public SinaNetInflowFetcher(RateLimiter rateLimiter, HttpClient? httpClient = null)
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

    public async Task<List<NetInflow>> FetchAsync(string code, DateTime? start, DateTime? end, CancellationToken ct = default)
    {
        return await _rateLimiter.RunAsync(() => FetchInternalAsync(code, start, end, ct), ct);
    }

    private async Task<List<NetInflow>> FetchInternalAsync(string code, DateTime? start, DateTime? end, CancellationToken ct)
    {
        // Sina使用跟Tencent一样的sh/sz/bj前缀（daima参数），不是EastMoney那种"1./0."数字前缀，
        // 复用MarketClassifier.TencentSymbolPrefix——这个方法名虽然带Tencent，但前缀规则是新浪/
        // 腾讯共用的老牌约定，两家一致。
        var daima = MarketClassifier.TencentSymbolPrefix(code) + code;
        var url = "http://vip.stock.finance.sina.com.cn/quotes_service/api/json_v2.php/MoneyFlow.ssl_qsfx_zjlrqs" +
                  $"?daima={daima}";

        byte[] bytes;
        try
        {
            bytes = await _http.GetByteArrayAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException(
                $"无法连接新浪财经资金流向接口：{detail}（可能是代理/防火墙问题，也可能是触发了反爬限流）", ex);
        }

        if (bytes.Length == 0)
            return new List<NetInflow>(); // 该股票没有资金流向数据（比如刚上市/停牌），不算错误

        var body = Encoding.GetEncoding("GBK").GetString(bytes);
        if (string.IsNullOrWhiteSpace(body) || body == "null")
            return new List<NetInflow>(); // 该股票没有资金流向数据（比如刚上市/停牌），不算错误

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return new List<NetInflow>();

        var result = new List<NetInflow>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("opendate", out var dateEl) || !item.TryGetProperty("netamount", out var amountEl))
                continue;
            if (!DateTime.TryParseExact(dateEl.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;
            if (start.HasValue && date < start.Value.Date) continue;
            if (end.HasValue && date > end.Value.Date) continue;
            if (!double.TryParse(amountEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                continue;
            result.Add(new NetInflow { Code = code, PeriodStart = date, MainNetInflow = amount, FetchedAt = DateTime.Now });
        }
        return result;
    }
}
