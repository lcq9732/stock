using System.Globalization;
using System.Net;
using System.Text;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 从新浪财经的报表下载接口抓取一只股票**全部历史**的关键财务科目（2026-07-31 实测可用）：
/// <c>money.finance.sina.com.cn/corp/go.php/vDOWN_{利润表|资产负债表|现金流量表}/displaytype/4/stockid/{code}/ctrl/all.phtml</c>
/// 一个请求返回该股上市以来所有报告期的整张报表（GBK 编码 TSV：首行"报表日期"+各期 yyyyMMdd，
/// 次行"单位 元"，之后每行=科目名+各期值）。银行/券商的科目名与一般企业不同（如"一、营业收入" vs
/// "营业总收入"、"归属于母公司的净利润" vs "归属于母公司所有者的净利润"），用备选名列表匹配。
/// 只抽取 <see cref="FinancialKeys"/> 里的科目，不存整张表。退市股同样有数据（乐视网退市后仍在老三板披露）。
/// ⚠️ 现金流量表的补充资料里也有"净利润"行（常年为0），净利润只从利润表取。
/// </summary>
public class SinaFinancialProvider : IFinancialProvider
{
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    static SinaFinancialProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // GBK
    }

    public SinaFinancialProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
    {
        _rateLimiter = rateLimiter;
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    /// <summary>每张报表要抽取的科目：规范键 → 备选行名列表（**顺序即优先级**，一般企业叫法在前、银行在后；
    /// 行名先去掉"一、二、…"的序号前缀再比对）。</summary>
    private static readonly (string Statement, (string Key, string[] Names)[] Items)[] Statements =
    [
        ("ProfitStatement",
        [
            (FinancialKeys.Revenue, ["营业总收入", "营业收入"]),
            (FinancialKeys.OperCost, ["营业成本"]),
            (FinancialKeys.NetProfit, ["净利润"]),
            (FinancialKeys.NetProfitParent, ["归属于母公司所有者的净利润", "归属于母公司的净利润", "归属于母公司股东的净利润"]),
        ]),
        ("BalanceSheet",
        [
            (FinancialKeys.TotalAssets, ["资产总计"]),
            (FinancialKeys.TotalLiabilities, ["负债合计"]),
            (FinancialKeys.EquityParent, ["归属于母公司股东权益合计", "归属于母公司股东的权益", "归属于母公司所有者权益合计", "所有者权益(或股东权益)合计"]),
        ]),
        ("CashFlow",
        [
            (FinancialKeys.Ocf, ["经营活动产生的现金流量净额"]),
        ]),
    ];

    public async Task<List<FinancialValue>> GetAllAsync(string code, CancellationToken ct = default)
    {
        var result = new List<FinancialValue>();
        foreach (var (statement, items) in Statements)
        {
            var url = $"https://money.finance.sina.com.cn/corp/go.php/vDOWN_{statement}" +
                      $"/displaytype/4/stockid/{code}/ctrl/all.phtml";
            var text = await _rateLimiter.RunAsync(() => FetchTextAsync(url, ct), ct);
            Parse(code, text, items, result);
        }
        return result;
    }

    private async Task<string> FetchTextAsync(string url, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://finance.sina.com.cn/");
            var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new RateLimitedException($"新浪财务接口返回 {(int)resp.StatusCode}，疑似触发反爬限流");
            resp.EnsureSuccessStatusCode();
            bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            throw new RateLimitedException($"无法连接新浪财务接口：{ex.InnerException?.Message ?? ex.Message}", ex);
        }
        if (bytes.Length == 0) throw new RateLimitedException("新浪财务接口返回空响应，疑似限流");
        return Encoding.GetEncoding("GBK").GetString(bytes);
    }

    private static void Parse(string code, string text, (string Key, string[] Names)[] items, List<FinancialValue> result)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2) return; // 空表（极少数标的没有该报表）

        var header = lines[0].Split('\t');
        var dates = new DateTime?[header.Length];
        for (int i = 1; i < header.Length; i++)
            dates[i] = DateTime.TryParseExact(header[i].Trim(), "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d) ? d : null;

        // 每个键记录当前命中的备选名优先级——低序号（更优先）的行名出现时覆盖之前的匹配
        var matched = new Dictionary<string, int>();
        var values = new Dictionary<string, Dictionary<DateTime, double>>();

        foreach (var line in lines.Skip(1))
        {
            var cols = line.Split('\t');
            var name = StripOrdinalPrefix(cols[0].Trim());
            if (name.Length == 0) continue;

            foreach (var (key, names) in items)
            {
                int rank = Array.IndexOf(names, name);
                if (rank < 0) continue;
                if (matched.TryGetValue(key, out var best) && best <= rank) continue; // 已有更优先的行
                matched[key] = rank;
                var byDate = new Dictionary<DateTime, double>();
                for (int i = 1; i < cols.Length && i < dates.Length; i++)
                {
                    if (dates[i] is not { } d) continue;
                    if (!IsQuarterEnd(d)) continue;
                    if (!double.TryParse(cols[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
                    byDate.TryAdd(d, v); // 同一报告期出现多列（调整前后）时保留最左（最新披露版本）
                }
                values[key] = byDate;
            }
        }

        foreach (var (key, byDate) in values)
            foreach (var (d, v) in byDate)
                result.Add(new FinancialValue { Code = code, ReportDate = d, Key = key, Value = v });
    }

    /// <summary>去掉"一、/二、/…"的序号前缀（"五、净利润"→"净利润"、"一、营业收入"→"营业收入"）。</summary>
    private static string StripOrdinalPrefix(string name)
    {
        if (name.Length >= 2 && name[1] == '、' && "一二三四五六七八九十".Contains(name[0]))
            return name[2..];
        return name;
    }

    private static bool IsQuarterEnd(DateTime d) =>
        (d.Month, d.Day) is (3, 31) or (6, 30) or (9, 30) or (12, 31);
}
