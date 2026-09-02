using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 抓取一只股票的分红送配历史——新浪财经"分红派息"页（GBK HTML，非东财）：
/// vISSUE_ShareBonus/stockid/{code}.phtml。页面里 <c>&lt;table id="sharebonus_1"&gt;</c> 是分红表，
/// 每个数据行 9 个 td：公告日期 | 送股(每10股) | 转增(每10股) | 派息税前(每10股,元) | 进度 |
/// 除权除息日 | 股权登记日 | 红股上市日 | 查看。日期缺失（方案未实施）源里给 "--"，这里存 null。
///
/// 同页 <c>&lt;table id="sharebonus_2"&gt;</c> 是**配股**表（2026-09-01 起也抓，见
/// <see cref="GetAllWithRightsAsync"/>）：公告日期 | 每10股配股数 | 配股价 | 基准股本 | 除权日 |
/// 股权登记日 | 缴款起始 | 缴款终止 | 配股上市日 | 募集资金 | 查看。配股是A股第四类除权事件，
/// 漏了它复权序列会在除权日凭空多一根阴线（见 <see cref="RightsIssueRow"/>）。两张表同在一页，
/// **一次请求拿两份，不增加任何抓取成本**。
/// 逐股调用，用 <see cref="RateLimiter"/> 限流。
/// </summary>
public class SinaDividendProvider : IDividendProvider
{
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    static SinaDividendProvider()
    {
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public SinaDividendProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
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

    public async Task<List<DividendRow>> GetAllAsync(string code, CancellationToken ct = default)
        => (await GetAllWithRightsAsync(code, ct)).Dividends;

    /// <summary>一次请求把分红和配股都拿回来——两张表本来就在同一页，分开抓等于白白多一倍请求。</summary>
    public async Task<DividendAndRights> GetAllWithRightsAsync(string code, CancellationToken ct = default)
    {
        var url = $"https://vip.stock.finance.sina.com.cn/corp/go.php/vISSUE_ShareBonus/stockid/{code}.phtml";
        var html = await _rateLimiter.RunAsync(() => GetGbkAsync(url, ct), ct);
        return new DividendAndRights(Parse(code, html), ParseRights(code, html));
    }

    /// <summary>
    /// 解析配股表 <c>sharebonus_2</c>。数据行 11 个 td，这里只取前 6 个用得上的。
    ///
    /// 两处防脏：
    ///   ① 公告日期早于 1990 年的丢掉——万科那条记的是 <c>1900-01-01</c>（除权日 1991-05-27，
    ///      A股开市初期的记录，源里公告日期就是个占位）。这种记录的除权日也远早于本地历史起点，
    ///      留着没用还会污染主键。
    ///   ② 配股数或配股价 ≤ 0 的丢掉——没有这两个数就算不出除权参考价。
    /// 注意**不做"未实施"过滤**：配股表没有分红表那样的"进度"列，方案通过但最终没实施的分不出来。
    /// 那种记录会有除权日却没有真实跳空，交给 AdjustFactorCalculator 的价格校验兜底（理论跳空
    /// 对不上实际就整条作废），跟破产重整假转增用的是同一道保险。
    /// </summary>
    private static List<RightsIssueRow> ParseRights(string code, string html)
    {
        var rows = new List<RightsIssueRow>();
        var now = DateTime.Now;

        var tableM = Regex.Match(html, "<table[^>]*id=\"sharebonus_2\".*?</table>", RegexOptions.Singleline);
        if (!tableM.Success) return rows;

        foreach (Match tr in Regex.Matches(tableM.Value, "<tr>(.*?)</tr>", RegexOptions.Singleline))
        {
            var tds = Regex.Matches(tr.Groups[1].Value, "<td[^>]*>(.*?)</td>", RegexOptions.Singleline)
                .Select(m => StripTags(m.Groups[1].Value)).ToList();
            if (tds.Count < 6) continue;              // 表头行匹配不到 td，自然被过滤

            var announce = ParseDate(tds[0]);
            if (announce is not { } a || a.Year < 1990) continue;

            double per10 = ParseD(tds[1]), price = ParseD(tds[2]);
            if (per10 <= 0 || price <= 0) continue;

            rows.Add(new RightsIssueRow
            {
                Code = code,
                AnnounceDate = a,
                SharesPer10 = per10,
                Price = price,
                ExDate = ParseDate(tds[4]),          // tds[3] 是基准股本，用不上
                RecordDate = ParseDate(tds[5]),
                FetchedAt = now,
            });
        }
        return rows;
    }

    private static List<DividendRow> Parse(string code, string html)
    {
        var rows = new List<DividendRow>();
        var now = DateTime.Now;

        var tableM = Regex.Match(html, "<table[^>]*id=\"sharebonus_1\".*?</table>", RegexOptions.Singleline);
        if (!tableM.Success) return rows;

        foreach (Match tr in Regex.Matches(tableM.Value, "<tr>(.*?)</tr>", RegexOptions.Singleline))
        {
            // 表头行用的是 <th>...</th>（且新浪那里 <th> 配 </td> 写得不规范），下面只认真正的 <td>，
            // 所以表头行匹配到 0 个 td、自然被过滤。数据行是 9 个 td。
            var tds = Regex.Matches(tr.Groups[1].Value, "<td[^>]*>(.*?)</td>", RegexOptions.Singleline)
                .Select(m => StripTags(m.Groups[1].Value)).ToList();
            if (tds.Count < 8) continue;

            var announce = ParseDate(tds[0]);
            if (announce == null) continue; // 公告日期是主键，缺了就跳

            rows.Add(new DividendRow
            {
                Code = code,
                AnnounceDate = announce.Value,
                BonusShares = ParseD(tds[1]),
                TransferShares = ParseD(tds[2]),
                DividendYuan = ParseD(tds[3]),
                Progress = tds[4].Length > 0 ? tds[4] : null,
                ExDate = ParseDate(tds[5]),
                RecordDate = ParseDate(tds[6]),
                FetchedAt = now,
            });
        }
        return rows;
    }

    private async Task<string> GetGbkAsync(string url, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = await _http.GetByteArrayAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException($"无法连接新浪分红派息接口：{detail}（可能是代理/网络问题，也可能触发反爬限流）", ex);
        }
        if (bytes.Length == 0) throw new RateLimitedException("新浪分红派息接口返回空响应，疑似触发反爬限流");
        return Encoding.GetEncoding("GBK").GetString(bytes);
    }

    private static DateTime? ParseDate(string s)
    {
        s = s.Trim();
        return DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
    }

    private static string StripTags(string s)
    {
        var text = WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]+>", " "));
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    private static double ParseD(string s)
    {
        s = s.Replace(",", "").Replace("%", "").Trim();
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
