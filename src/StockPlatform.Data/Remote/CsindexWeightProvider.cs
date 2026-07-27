using System.Globalization;
using System.Net;
using System.Text;
using ExcelDataReader;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 抓取指数成分股权重——中证指数官网的 closeweight.xls
/// （oss-ch.csindex.com.cn/.../closeweight/{code}closeweight.xls，真 OLE2/BIFF Excel，用
/// <see cref="ExcelDataReader"/> 解析）。只有中证系指数有这个文件，非中证系会 404——那不算失败，
/// 返回空列表让调用方跳过；只有网络/限流/5xx 才抛 <see cref="RateLimitedException"/> 计入失败重试。
/// 源偏不稳（同一指数常首次失败、再试才成功），逐指数调用，用 <see cref="RateLimiter"/> 限流+熔断。
///
/// xls 列（见 akshare index_stock_cons_weight_csindex）：0 日期(yyyyMMdd)、4 成分券代码、9 权重(%)。
/// </summary>
public class CsindexWeightProvider : IIndexWeightProvider
{
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    static CsindexWeightProvider()
    {
        // ExcelDataReader 读老式 .xls(BIFF) 需要 CodePages 编码支持
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public CsindexWeightProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
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

    public Task<List<IndexWeightRow>> GetWeightsAsync(string indexCode, CancellationToken ct = default) =>
        _rateLimiter.RunAsync(() => FetchAsync(indexCode, ct), ct);

    private async Task<List<IndexWeightRow>> FetchAsync(string indexCode, CancellationToken ct)
    {
        var url = "https://oss-ch.csindex.com.cn/static/html/csindex/public/uploads/file/autofile/closeweight/" +
                  $"{indexCode}closeweight.xls";
        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException($"无法连接中证指数权重文件：{detail}（可能是网络/代理问题或限流）", ex);
        }

        // 非中证系指数没有这个文件 → 404，不算失败，返回空让调用方跳过。
        if (resp.StatusCode == HttpStatusCode.NotFound) return new List<IndexWeightRow>();
        if (!resp.IsSuccessStatusCode)
            throw new RateLimitedException($"中证指数权重文件返回 HTTP {(int)resp.StatusCode}，疑似限流");

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0) throw new RateLimitedException("中证指数权重文件为空，疑似限流");

        var rows = new List<IndexWeightRow>();
        var now = DateTime.Now;
        using var ms = new MemoryStream(bytes);
        using var reader = ExcelReaderFactory.CreateReader(ms);
        bool header = true;
        while (reader.Read())
        {
            if (header) { header = false; continue; }   // 首行是表头
            if (reader.FieldCount < 10) continue;

            var code = NormalizeCode(reader.GetValue(4));
            if (code.Length != 6) continue;

            double weight = 0;
            try { weight = Convert.ToDouble(reader.GetValue(9), CultureInfo.InvariantCulture); }
            catch { /* 权重列缺失/非数字，按0处理 */ }

            rows.Add(new IndexWeightRow
            {
                IndexCode = indexCode,
                StockCode = code,
                Weight = weight,
                AsOfDate = ParseAsOf(reader.GetValue(0)),
                FetchedAt = now,
            });
        }
        return rows;
    }

    private static string NormalizeCode(object? value)
    {
        if (value == null || value is DBNull) return "";
        var s = value is double d ? ((long)d).ToString(CultureInfo.InvariantCulture) : value.ToString()?.Trim() ?? "";
        s = s.PadLeft(6, '0');
        return s.Length == 6 && s.All(char.IsDigit) ? s : "";
    }

    private static DateTime ParseAsOf(object? value)
    {
        if (value is DateTime dt) return dt;
        var s = value is double d ? ((long)d).ToString(CultureInfo.InvariantCulture) : value?.ToString()?.Trim() ?? "";
        return s.Length >= 8 && DateTime.TryParseExact(s[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var r)
            ? r : default;
    }
}
