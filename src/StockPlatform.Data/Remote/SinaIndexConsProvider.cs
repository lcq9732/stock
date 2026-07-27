using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 抓取指数成分股名单——新浪"最新成份股目录"老接口
/// （corp/view/vII_NewestComponent.php?page=N&amp;indexid=CODE，GBK HTML）。选这个老接口而不是新版
/// getHQNodeDataSimple(node=zhishu_)：后者覆盖的指数很少（连 000300 都返回空），老接口几乎覆盖全部
/// 指数（宽基/行业/主题都能拿到），代价是要解析 HTML 且分页（每页 40 只）。东财在用户环境不可用，
/// 所以走新浪。逐指数调用，用 <see cref="RateLimiter"/> 限流（会跑几百个指数、每个又分几页）。
///
/// 解析锚点：成分表在 <c>&lt;table id="NewStockTable"&gt;</c> 里，每行代码列是
/// <c>&lt;div align="center"&gt;600118&lt;/div&gt;</c>（6 位纯数字），名称/纳入日期列不会误命中
/// <c>\d{6}</c>。分页循环到"空页或没有新代码"为止，不依赖解析页码链接（更抗改版）。
/// </summary>
public class SinaIndexConsProvider : IIndexConsProvider
{
    private const int MaxPages = 100;   // 最大指数(中证1000)约1000只/40≈25页，100页足够兜底
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    static SinaIndexConsProvider()
    {
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public SinaIndexConsProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
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

    public async Task<List<(string Code, DateTime? InDate)>> GetConsAsync(string indexCode, CancellationToken ct = default)
    {
        var all = new List<(string Code, DateTime? InDate)>();
        var seen = new HashSet<string>();
        for (int page = 1; page <= MaxPages; page++)
        {
            var rows = await _rateLimiter.RunAsync(() => FetchPageAsync(indexCode, page, ct), ct);
            int before = seen.Count;
            foreach (var r in rows)
                if (seen.Add(r.Code)) all.Add(r);
            // 空页，或这一页没带来任何新代码（超范围的 page 有些接口会回退到第一页）——都表示抓完了。
            if (rows.Count == 0 || seen.Count == before) break;
        }
        return all;
    }

    private async Task<List<(string Code, DateTime? InDate)>> FetchPageAsync(string indexCode, int page, CancellationToken ct)
    {
        var url = "http://vip.stock.finance.sina.com.cn/corp/view/vII_NewestComponent.php" +
                  $"?page={page}&indexid={indexCode}";
        byte[] bytes;
        try
        {
            bytes = await _http.GetByteArrayAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException($"无法连接新浪指数成分接口：{detail}（可能是代理/网络问题，也可能触发反爬限流）", ex);
        }
        if (bytes.Length == 0) throw new RateLimitedException("新浪指数成分接口返回空响应，疑似触发反爬限流");

        var html = Encoding.GetEncoding("GBK").GetString(bytes);
        int s = html.IndexOf("NewStockTable", StringComparison.Ordinal);
        if (s < 0) return new List<(string, DateTime?)>();   // 该指数无成分表（老指数/无数据）
        int e = html.IndexOf("</table>", s, StringComparison.Ordinal);
        var seg = e > s ? html[s..e] : html[s..];

        // 每个成分数据行：代码td(6位数字) + 名称td + 纳入日期td(yyyy-MM-dd)。
        var result = new List<(string, DateTime?)>();
        foreach (Match m in Regex.Matches(seg,
                     "<td><div align=\"center\">(\\d{6})</div></td>\\s*<td>.*?</td>\\s*<td><div align=\"center\">(\\d{4}-\\d{2}-\\d{2})</div></td>",
                     RegexOptions.Singleline))
        {
            DateTime? inDate = DateTime.TryParseExact(m.Groups[2].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : null;
            result.Add((m.Groups[1].Value, inDate));
        }
        return result;
    }
}
