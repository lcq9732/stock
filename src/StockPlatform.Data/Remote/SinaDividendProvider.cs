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
/// （同页另有 id="sharebonus_2" 配股表，本类不抓——用户要的是分红。）逐股调用，用 <see cref="RateLimiter"/> 限流。
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
    {
        var url = $"https://vip.stock.finance.sina.com.cn/corp/go.php/vISSUE_ShareBonus/stockid/{code}.phtml";
        var html = await _rateLimiter.RunAsync(() => GetGbkAsync(url, ct), ct);
        return Parse(code, html);
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
