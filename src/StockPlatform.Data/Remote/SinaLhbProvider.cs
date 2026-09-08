using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 抓取某交易日的龙虎榜——新浪龙虎榜每日页
/// （q/go.php/vInvestConsult/kind/lhb/index.phtml?tradedate=yyyy-MM-dd，GBK HTML）。东财龙虎榜是
/// 结构化 JSON 但在用户环境被封，所以走新浪 HTML。页面按"上榜指标"分组：每组一个加粗标题行
/// (<c>font-weight:bold</c> 的 span) + 一个表头行 + 若干数据行；数据行含
/// <c>lookup_n.php?q=CODE</c>。解析时跟踪当前分组标题作为 <see cref="LhbRow.Reason"/>。
///
/// 数据行 8 列：序号、股票代码、股票名称、收盘价、对应值、成交量(万股)、成交额(万元)、查看详情。
/// 非交易日/无数据返回空列表。用 <see cref="RateLimiter"/> 限流（按日多次调用回补历史时）。
/// </summary>
public class SinaLhbProvider : ILhbProvider
{
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    /// <summary>2002-01-01——两所"公开信息制度"（龙虎榜）开始披露的年份。⚠ 跟融资融券的 2010 无关，
    /// 龙虎榜比两融早八年。取制度起点而非更晚的保守值，是因为抓不到只会返回空列表、不报错，代价仅是
    /// 多试几百天；而起点定晚了那几年就**永久抓不回来**。新浪那个页面究竟能翻到哪年没实测过，跑一遍
    /// 看日志里前几年是不是全空即可，若确认为空可以再往后调。</summary>
    public DateOnly EarliestAvailable => new(2002, 1, 1);

    static SinaLhbProvider()
    {
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public SinaLhbProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
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

    public Task<List<LhbRow>> GetDailyAsync(DateOnly date, CancellationToken ct = default) =>
        _rateLimiter.RunAsync(() => FetchAsync(date, ct), ct);

    private async Task<List<LhbRow>> FetchAsync(DateOnly date, CancellationToken ct)
    {
        var url = "http://vip.stock.finance.sina.com.cn/q/go.php/vInvestConsult/kind/lhb/index.phtml" +
                  $"?tradedate={date:yyyy-MM-dd}";
        byte[] bytes;
        try
        {
            bytes = await _http.GetByteArrayAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException($"无法连接新浪龙虎榜接口：{detail}（可能是代理/网络问题，也可能触发反爬限流）", ex);
        }
        if (bytes.Length == 0) throw new RateLimitedException("新浪龙虎榜接口返回空响应，疑似触发反爬限流");

        var html = Encoding.GetEncoding("GBK").GetString(bytes);
        var tradeDay = date.ToDateTime(TimeOnly.MinValue);
        var now = DateTime.Now;
        var rows = new List<LhbRow>();
        string reason = "";

        foreach (Match tr in Regex.Matches(html, "<tr.*?</tr>", RegexOptions.Singleline))
        {
            var block = tr.Value;

            // 分组标题行：加粗 span 里的上榜指标名
            var titleM = Regex.Match(block, "font-weight:bold[^>]*>(.*?)</span>", RegexOptions.Singleline);
            if (titleM.Success)
            {
                var t = StripTags(titleM.Groups[1].Value);
                if (t.Length > 0) reason = t;
                continue;
            }

            // 数据行：含个股链接 lookup_n.php?q=代码
            if (!block.Contains("lookup_n.php?q=", StringComparison.Ordinal)) continue;

            var tds = Regex.Matches(block, "<td[^>]*>(.*?)</td>", RegexOptions.Singleline)
                .Select(m => StripTags(m.Groups[1].Value)).ToList();
            if (tds.Count < 7) continue;

            var code = new string(tds[1].Where(char.IsDigit).ToArray());
            if (code.Length != 6) continue;

            rows.Add(new LhbRow
            {
                TradeDate = tradeDay,
                StockCode = code,
                StockName = tds[2],
                ClosePrice = ParseD(tds[3]),
                Deviation = ParseD(tds[4]),
                Volume = ParseD(tds[5]),
                Amount = ParseD(tds[6]),
                Reason = reason,
                FetchedAt = now,
            });
        }

        // ════ 骨架校验（2026-09-08）════
        // 0 行有两种含义，必须分开：
        //   · **这天真没有**（非交易日、或当天无人上榜）——页面还是那张正常的龙虎榜页，只是没有数据行。
        //     实测 2026-09-06（周日）和 2003-01-05 都返回 27,365 字节的完整框架页，含"龙虎榜""tradedate"。
        //   · **拿到的根本不是那张页**（反爬拦截页、错误页）——那时候把"0 行"当成"这天没有"记进
        //     DailyFetchNoData 就是**永久漏掉这一天**，那张表是"一次定案"的。
        // 所以 0 行时验一下骨架：验过了才敢说"确实没有"，验不过按失败抛（调用方只记 error、不定案）。
        // 有数据的日子不用验——195 条数据行本身就是最好的骨架证明（实测 2026-09-04 为 264KB）。
        if (rows.Count == 0 &&
            !(html.Contains("tradedate", StringComparison.OrdinalIgnoreCase) && html.Contains("龙虎榜", StringComparison.Ordinal)))
        {
            throw new RateLimitedException(
                $"新浪龙虎榜 {date:yyyy-MM-dd} 返回的页面不像龙虎榜页（{bytes.Length} 字节，缺少页面骨架），" +
                "疑似反爬拦截——按失败处理，不当成\"这天没有数据\"。");
        }
        return rows;
    }

    private static string StripTags(string s)
    {
        var text = WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]+>", " "));
        return Regex.Replace(text, "\\s+", " ").Trim();   // 收敛换行/多空格/&nbsp;
    }

    private static double ParseD(string s)
    {
        s = s.Replace(",", "").Replace("%", "").Trim();
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
