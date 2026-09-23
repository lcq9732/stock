using System.Globalization;
using System.Net;
using System.Text;
using ExcelDataReader;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 深交所官网的**每日 ETF 份额**（2026-09-23）——官网「基金规模·ETF」那一页
/// （<c>www.szse.cn/market/fund/volume/etf/</c>）背后的报表 <c>CATALOGID=scsj_fund_jjgm</c>，用 xlsx 导出。
///
/// ════ 为什么是 xlsx、为什么按月 ════
/// 同一张报表的 JSON 接口每页固定 20 行（一天 700 多只就是 38 页），拉全历史要几万个请求；
/// xlsx 导出**不分页**，一次给整个区间。区间半年以内都能导出（2025 上半年 117 天、53,499 行），
/// **整年会返回「没有找到符合条件的数据！」**——所以一个月一个请求，全历史约 121 个。
///
/// ════ 覆盖与口径（2026-09-23 实测）════
/// · **2016-09-26 起**有数据，**含货币 ETF**（159001 等；上交所那个接口不列货币 ETF）。
/// · 导出列：日期、基金代码、基金简称、基金规模(份)。⚠ 单位是**份**——页面上写的是「万份」，
///   导出的却是份，这里换算成万份再交出去，跟上交所同口径。按表头判断单位，表头变了就报错不猜。
/// · 页面注明「T 日晚间更新的 T 日规模仅供参考，以 T+1 日早间更新的 T 日规模为准」——
///   最近几天的份额每轮都重抓覆盖，见 EtfShareFetchPlan。
///
/// ⚠ 这份 xlsx 是 inlineStr 单元格、而且 dimension 写的是 A1，openpyxl 读出来只有表头一格。
/// ExcelDataReader 不看 dimension，读得出来（单测里有从真实导出裁出来的样本）。
/// </summary>
public class SzseEtfShareProvider : IEtfShareProvider
{
    private readonly RateLimiter _rateLimiter;
    private readonly HttpClient _http;

    /// <summary>ExcelDataReader 建 reader 时要用 1252 这类代码页，.NET 默认不带——静态构造里注册，
    /// 这样单独调静态的 <see cref="Parse"/>（单测）也不会炸。跟 ExchangeMarginProvider 同一个做法。</summary>
    static SzseEtfShareProvider() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public string Market => "sz";

    /// <summary>2026-09-23 实测：2016-06 整月空、2016-09 从 26 日起有（48 只）。</summary>
    public DateOnly FirstDay => new(2016, 9, 26);

    public EtfShareBatch Batch => EtfShareBatch.Month;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    public SzseEtfShareProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
    {
        _rateLimiter = rateLimiter;
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.Timeout = TimeSpan.FromSeconds(120);   // 一个月两三万行，文件几百 KB
    }

    /// <summary>跟 <see cref="ExchangeMarginProvider"/> 一样走系统代理。</summary>
    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    public Task<List<EtfShareRow>> GetAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (to < from || to.DayNumber - from.DayNumber > 62)
            throw new ArgumentException($"深交所 ETF 份额按月取，收到 {from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}");
        return _rateLimiter.RunAsync(() => FetchAsync(from, to, ct), ct);
    }

    private async Task<List<EtfShareRow>> FetchAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var url = "https://www.szse.cn/api/report/ShowReport?SHOWTYPE=xlsx&CATALOGID=scsj_fund_jjgm&jjlb=ETF&TABKEY=tab1"
                + $"&txtStart={from:yyyy-MM-dd}&txtEnd={to:yyyy-MM-dd}&random={Random.Shared.NextDouble():0.0000}";
        byte[] bytes;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://www.szse.cn/market/fund/volume/etf/index.html");
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new RateLimitedException($"深交所返回 {(int)resp.StatusCode}，疑似触发限流");
            resp.EnsureSuccessStatusCode();
            bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (RateLimitedException) { throw; }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            throw new RateLimitedException($"无法连接深交所基金规模接口：{ex.InnerException?.Message ?? ex.Message}", ex);
        }
        return Parse(bytes, from, to);
    }

    /// <summary>
    /// 解析导出的 xlsx。拆出来是为了拿真实导出裁出来的样本单测。
    ///
    /// ⚠ 空和"响应不对"必须分开：只有一格「没有找到符合条件的数据！」＝那段确实没有，返回空列表；
    /// 不是 xlsx（反爬页）、表头变了、返回了区间外的日子，都抛——当成空会把那几天永久记成"没有数据"。
    /// </summary>
    public static List<EtfShareRow> Parse(byte[] xlsx, DateOnly from, DateOnly to)
    {
        if (xlsx.Length < 4 || xlsx[0] != (byte)'P' || xlsx[1] != (byte)'K')
            throw new RateLimitedException("深交所基金规模接口返回的不是 xlsx，疑似被拦截");

        using var ms = new MemoryStream(xlsx);
        using var reader = ExcelReaderFactory.CreateReader(ms);

        if (!reader.Read()) throw new InvalidOperationException("深交所基金规模 xlsx 是空的（连表头都没有）");
        var first = Cell(reader, 0);
        if (first.StartsWith("没有找到", StringComparison.Ordinal)) return [];

        // 表头：日期、基金代码、基金简称、基金规模(份)。单位按表头定，认不出来就报错。
        if (reader.FieldCount < 4 || first != "日期" || Cell(reader, 1) != "基金代码")
            throw new InvalidOperationException(
                $"深交所基金规模 xlsx 表头变了：{string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => Cell(reader, i)))}");
        var sizeHeader = Cell(reader, 3);
        double perWan = sizeHeader.Contains("万份") ? 1
                      : sizeHeader.Contains("份") ? 1e4
                      : throw new InvalidOperationException($"深交所基金规模 xlsx 的规模列单位认不出来：{sizeHeader}");

        var rows = new List<EtfShareRow>();
        while (reader.Read())
        {
            var dateText = Cell(reader, 0);
            var code = Cell(reader, 1);
            if (code.Length != 6 || !code.All(char.IsDigit)) continue;
            if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var day))
                throw new InvalidOperationException($"深交所基金规模 xlsx 日期列认不出来：{dateText}（{code}）");
            if (day < from || day > to)
                throw new InvalidOperationException(
                    $"深交所基金规模：请求 {from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}，返回里有 {day:yyyy-MM-dd} 的行（{code}）");
            if (!double.TryParse(Cell(reader, 3).Replace(",", ""), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var size) || size <= 0)
                continue;
            rows.Add(new EtfShareRow("sz", code, day, size / perWan));
        }
        return rows;
    }

    private static string Cell(IExcelDataReader r, int i) =>
        i < r.FieldCount ? (r.GetValue(i)?.ToString() ?? "").Trim() : "";
}
