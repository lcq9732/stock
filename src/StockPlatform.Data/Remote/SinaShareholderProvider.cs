using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 抓取一只股票的股东数据——新浪财经"股本股东"页（GBK HTML，非东财）：
/// - 主要股东页 vCI_StockHolder/stockid/{code}.phtml → 每报告期的**股东户数**(股东总数)+**十大股东**明细
/// - 流通股东页 vCI_CirculateStockHolder/stockid/{code}.phtml → 每报告期的**十大流通股东**明细
///
/// 两个页面都按 <c>&lt;a name="yyyy-MM-dd"&gt;</c> 分成多期块；每期块里明细数据行是
/// <c>&lt;td&gt;&lt;div align="center"&gt;序号&lt;/div&gt;&lt;/td&gt;</c> 打头、后跟"股东名称/持股数量/占比/股本性质"
/// 四个 td。主要股东页额外有"股东总数""平均持股数"两行汇总。逐股调用，用 <see cref="RateLimiter"/> 限流。
/// </summary>
public class SinaShareholderProvider : IShareholderProvider
{
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    static SinaShareholderProvider()
    {
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public SinaShareholderProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
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

    public async Task<ShareholderData> GetAsync(string code, CancellationToken ct = default)
    {
        var data = new ShareholderData();
        var now = DateTime.Now;

        // ⚠ 注意：这里一只票要发**两个**请求，而每个请求各自去抢限速器的信号量。调用方是
        // Task.WhenAll(全部股票)，所以实际执行顺序是"所有票的第1个请求 → 所有票的第2个请求"，
        // 前半程谁都不完成、一条都写不进库。现在没暴露成问题，只因为股东用的是 3并发/1秒、
        // 轮一圈快；**一旦这个接口也需要降速（像财务那样降到单并发+4秒），就会立刻变成
        // "跑几百个请求、零写入、零进度"**——财务 2026-08-27 就是这么踩的坑，见
        // FetchOrchestrator.RunFetchFinancialsAsync 里那段注释。
        // 届时的修法有两个：① 像财务那样改成顺序 foreach（牺牲并发）；
        // ② 把两个请求合进**一次** RunAsync 调用，让它们共占一个信号量名额（保留并发，更优）。
        var shUrl = $"https://vip.stock.finance.sina.com.cn/corp/go.php/vCI_StockHolder/stockid/{code}.phtml";
        var shHtml = await _rateLimiter.RunAsync(() => GetGbkAsync(shUrl, "股东", ct), ct);
        ParseMainHolder(code, shHtml, now, data);

        var cirUrl = $"https://vip.stock.finance.sina.com.cn/corp/go.php/vCI_CirculateStockHolder/stockid/{code}.phtml";
        var cirHtml = await _rateLimiter.RunAsync(() => GetGbkAsync(cirUrl, "流通股东", ct), ct);
        ParseHolderTable(code, cirHtml, TopShareholderRow.KindFloat, now, data.TopHolders);

        return data;
    }

    /// <summary>主要股东页：解析户数(股东总数)+平均持股+十大股东明细。</summary>
    private void ParseMainHolder(string code, string html, DateTime now, ShareholderData data)
    {
        foreach (var (date, block) in SplitByReportDate(html))
        {
            var numM = Regex.Match(block, "股东总数</strong></div></td>\\s*<td[^>]*>\\s*(\\d+)");
            var avgM = Regex.Match(block, "平均持股数</strong></div></td>\\s*<td[^>]*>\\s*([\\d,]+)");
            if (numM.Success)
            {
                data.Counts.Add(new ShareholderCountRow
                {
                    Code = code,
                    ReportDate = date,
                    HolderNum = long.TryParse(numM.Groups[1].Value, out var n) ? n : 0,
                    AvgShares = avgM.Success ? ParseD(avgM.Groups[1].Value) : 0,
                    FetchedAt = now,
                });
            }
            AddRows(code, date, block, TopShareholderRow.KindTotal, now, data.TopHolders);
        }
    }

    /// <summary>流通股东页：只有明细（无户数汇总）。</summary>
    private void ParseHolderTable(string code, string html, string kind, DateTime now, List<TopShareholderRow> sink)
    {
        foreach (var (date, block) in SplitByReportDate(html))
            AddRows(code, date, block, kind, now, sink);
    }

    private void AddRows(string code, DateTime date, string block, string kind, DateTime now, List<TopShareholderRow> sink)
    {
        // 明细行：<td><div align="center">序号</div></td> + 后跟 名称/持股数/占比/性质 四个 td
        foreach (Match m in Regex.Matches(block, "<td><div align=\"center\">(\\d{1,2})</div></td>(.*?)</tr>", RegexOptions.Singleline))
        {
            int rank = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (rank < 1 || rank > 10) continue;
            var tds = Regex.Matches(m.Groups[2].Value, "<td[^>]*>(.*?)</td>", RegexOptions.Singleline)
                .Select(x => StripTags(x.Groups[1].Value)).ToList();
            if (tds.Count < 4) continue;
            var name = tds[0];
            if (name.Length == 0) continue;
            sink.Add(new TopShareholderRow
            {
                Code = code,
                ReportDate = date,
                Kind = kind,
                Rank = rank,
                HolderName = name,
                Shares = ParseD(tds[1]),
                Ratio = ParseD(tds[2]),
                ShareType = tds[3],
                ChangeDirection = ParseChangeDirection(tds[1]),
                FetchedAt = now,
            });
        }
    }

    /// <summary>按 &lt;a name="yyyy-MM-dd"&gt; 把页面切成每个报告期一块。</summary>
    private static IEnumerable<(DateTime Date, string Block)> SplitByReportDate(string html)
    {
        var ms = Regex.Matches(html, "<a name=\"(\\d{4}-\\d{2}-\\d{2})\"");
        for (int i = 0; i < ms.Count; i++)
        {
            if (!DateTime.TryParseExact(ms[i].Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                continue;
            int start = ms[i].Index;
            int end = i + 1 < ms.Count ? ms[i + 1].Index : html.Length;
            yield return (d, html[start..end]);
        }
    }

    private async Task<string> GetGbkAsync(string url, string what, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = await _http.GetByteArrayAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException($"无法连接新浪{what}接口：{detail}（可能是代理/网络问题，也可能触发反爬限流）", ex);
        }
        if (bytes.Length == 0) throw new RateLimitedException($"新浪{what}接口返回空响应，疑似触发反爬限流");
        return Encoding.GetEncoding("GBK").GetString(bytes);
    }

    private static string StripTags(string s)
    {
        var text = WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]+>", " "));
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    /// <summary>
    /// 从单元格文本里取数字。**必须先剥掉数字以外的所有字符**——新浪在持股数/占比后面会挂一个
    /// 环比涨跌箭头（<c>40610695↓</c>），旧版只 Replace 了逗号和百分号，箭头留在串里导致
    /// <c>double.TryParse</c> 失败、静默返回 0。后果是"排进前十却显示 0 股"这种自相矛盾的数据，
    /// 库里有 14953 行（0.5%）中招，集中在香港中央结算、高盛、各类ETF 这些调仓频繁的机构上
    /// （2026-08-13 修复）。这里改成白名单过滤：只保留数字、小数点、正负号，其余一律丢弃，
    /// 这样将来数据源再加别的装饰符号也不会重蹈覆辙。
    /// </summary>
    private static double ParseD(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        Span<char> buf = stackalloc char[s.Length];
        int n = 0;
        foreach (var ch in s)
            if (char.IsAsciiDigit(ch) || ch == '.' || ch == '-' || ch == '+') buf[n++] = ch;
        if (n == 0) return 0;
        return double.TryParse(buf[..n], NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>从持股数单元格里读出环比增减方向。新浪用箭头表示：↑(红)=增持、↓(绿)=减持，
    /// 没有箭头 = 持股不变或本期新进榜（两者数据源不区分，一律记 null）。</summary>
    private static string? ParseChangeDirection(string cellText) =>
        cellText.Contains('↑') ? TopShareholderRow.ChangeUp :
        cellText.Contains('↓') ? TopShareholderRow.ChangeDown : null;
}
