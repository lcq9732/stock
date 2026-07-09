using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Remote;

/// <summary>
/// Fetches circulating market cap (流通市值) one stock at a time via Tencent's public real-time
/// quote endpoint (<c>qt.gtimg.cn/q=</c>).
///
/// Response shape: <c>v_sh600519="1~贵州茅台~600519~...~14992.23~14992.23~6.44~...";</c> — a
/// tilde-separated field list. Field mapping verified against 3 diverse live stocks before
/// building this: index 44 (0-indexed) is 流通市值（亿元）, index 45 is 总市值（亿元）— confirmed
/// because for 300750（宁德时代，known to carry substantial non-circulating/locked-up shares）these
/// two values genuinely differ (15367.90 vs 16702.21，流通&lt;总，as expected), while for
/// 600519/000001 (nearly fully circulating) the two values are equal or nearly equal — exactly the
/// pattern a [流通市值, 总市值] pair should produce.
///
/// **Not wired in by default** (2026-07-08, superseded the same day it was built) —
/// <see cref="SinaListMarketCapFetcher"/> gets the same data essentially for free as a byproduct
/// of the stock-list scan it already does, instead of ~5000 individual per-stock calls, so that's
/// what App.xaml.cs actually constructs now. Kept as a second <see cref="IMarketCapFetcher"/>
/// implementation (alongside <see cref="EastMoneyMarketCapFetcher"/>) in case a per-stock fallback
/// is ever needed again.
/// </summary>
public class TencentMarketCapFetcher : IMarketCapFetcher
{
    private const int CirculatingMarketCapFieldIndex = 44; // 亿元
    private const double YuanPerYi = 1e8;

    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    static TencentMarketCapFetcher()
    {
        // 回包声明 charset=gbk（跟 SinaStockListProvider/SinaNetInflowFetcher 一样），GBK 不在
        // .NET Core 默认编码表里，需要先注册才能正确解码（哪怕我们只关心数字字段，股票名称字段
        // 里的GBK字节如果不先正确解码就直接按字节切分"~"，理论上可能把某个GBK字节误判成分隔符）。
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public TencentMarketCapFetcher(RateLimiter rateLimiter, HttpClient? httpClient = null)
    {
        _rateLimiter = rateLimiter;
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.Timeout = TimeSpan.FromSeconds(12);
    }

    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    /// <summary>Same per-stock independent-failure shape as EastMoneyMarketCapFetcher — one code's
    /// failure is skipped, not fatal to the batch.</summary>
    public async Task<List<MarketCapEntry>> GetMarketCapsAsync(IReadOnlyList<string> codes, IProgress<string>? progress, CancellationToken ct = default)
    {
        var result = new ConcurrentBag<MarketCapEntry>();
        var completed = 0;
        var tasks = codes.Select(async code =>
        {
            try
            {
                var entry = await _rateLimiter.RunAsync(() => FetchOneAsync(code, ct), ct);
                if (entry != null) result.Add(entry);
            }
            catch (OperationCanceledException)
            {
                throw; // 用户点了"停止"
            }
            catch (Exception)
            {
                // 单只股票在重试/熔断耗尽后仍失败，跳过这一只，不影响其余股票继续抓取
            }
            finally
            {
                var done = Interlocked.Increment(ref completed);
                if (done % 200 == 0 || done == codes.Count)
                    progress?.Report($"正在获取流通市值 ({done}/{codes.Count})");
            }
        });
        await Task.WhenAll(tasks);
        return result.ToList();
    }

    private async Task<MarketCapEntry?> FetchOneAsync(string code, CancellationToken ct)
    {
        var symbol = MarketClassifier.TencentSymbolPrefix(code) + code;
        var url = $"http://qt.gtimg.cn/q={symbol}";

        byte[] bytes;
        try
        {
            bytes = await _http.GetByteArrayAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException(
                $"无法获取 {code} 流通市值：{detail}（可能是代理/防火墙问题，也可能是触发了反爬限流）", ex);
        }

        if (bytes.Length == 0)
            throw new RateLimitedException($"获取 {code} 流通市值时腾讯返回空响应，疑似触发反爬限流");

        var text = Encoding.GetEncoding("GBK").GetString(bytes);

        // 格式："v_sh600519="1~贵州茅台~600519~...";"——代码查不到实时行情（停牌太久/已退市/
        // 代码不存在等）时，腾讯回的是 v_pv_none_match="1"; 这种占位符，不含"~"字段，按"暂时没有
        // 数据"跳过，不是网络问题，不需要重试。
        var start = text.IndexOf('"');
        var end = text.LastIndexOf('"');
        if (start < 0 || end <= start) return null;
        var fields = text.Substring(start + 1, end - start - 1).Split('~');
        if (fields.Length <= CirculatingMarketCapFieldIndex) return null;

        if (!double.TryParse(fields[CirculatingMarketCapFieldIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var yi))
            return null;
        var marketCap = yi * YuanPerYi;
        if (marketCap <= 0) return null;
        return new MarketCapEntry(code, marketCap);
    }
}
