using System.Globalization;
using System.Net;
using System.Text;
using ExcelDataReader;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 深交所官网「停复牌提示」（报表 <c>CATALOGID=1798</c>，2026-09-30，见 doc/suspension-design.md），用 xlsx 导出。
///
/// ════ 为什么是 xlsx、为什么按月 ════
/// 同一张报表的 JSON 接口每页**固定 10 条**（传 PAGESIZE 不理），全量 7 万多条要翻七千页；
/// xlsx 导出不分页，一个月一个请求。**不给区间整表导出会截断在 65,536 行**（老 xls 的行数上限），
/// 所以必须分段——按月最多几百条，离上限很远；万一哪个月顶到了上限就抛，不拿半截当全量。
/// 2026-09 的导出 152 行，跟 JSON 接口同区间的 recordcount 一致。
///
/// ════ 口径（2026-09-30 沙箱实测）════
/// · 列：证券代码、证券简称、停牌时间、复牌时间、停牌期限、停牌原因。股票、ETF、可转债在一张表里。
/// · 时刻写法：「2026-09-30 开市」或「2026-09-30 09:34:03」；复牌时刻是**复牌那天**（那天有交易）。
/// · 长期停牌分两条：「停牌」（有停牌时间、没有复牌时间）+ 之后的「取消停牌」（只有复牌时间），
///   按日期区间查时各自落在自己那个月里，配对交给 <see cref="StockPlatform.Logic.Services.SuspensionRule"/>。
/// · 2008 年起有数据（2004-01 空、2008-01 有 438 条）。
/// </summary>
public sealed class SzseSuspensionProvider : ISuspensionProvider
{
    /// <summary>老 xls 的行数上限——整表导出就截在这里（含表头）。</summary>
    private const int XlsRowLimit = 65536;

    private readonly RateLimiter _rateLimiter;
    private readonly HttpClient _http;

    /// <summary>ExcelDataReader 要 1252 这类代码页，.NET 默认不带（同 <see cref="SzseEtfShareProvider"/>）。</summary>
    static SzseSuspensionProvider() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public string Source => SuspensionSource.Szse;
    public string Label => "深交所";

    /// <summary>跟上交所一样从 2004 年起问（理由见 <see cref="SseSuspensionProviderBase.FirstMonth"/>）。</summary>
    public DateOnly FirstMonth => new(2004, 1, 1);

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    public SzseSuspensionProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
    {
        _rateLimiter = rateLimiter;
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    public Task<List<SuspensionRow>> GetMonthAsync(DateOnly month, CancellationToken ct = default)
    {
        var from = new DateOnly(month.Year, month.Month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        return _rateLimiter.RunAsync(() => FetchAsync(from, to, ct), ct);
    }

    private async Task<List<SuspensionRow>> FetchAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var url = "https://www.szse.cn/api/report/ShowReport?SHOWTYPE=xlsx&CATALOGID=1798&TABKEY=tab1"
                + $"&txtKsrq={from:yyyy-MM-dd}&txtZzrq={to:yyyy-MM-dd}&random={Random.Shared.NextDouble():0.0000}";
        byte[] bytes;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://www.szse.cn/");
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new RateLimitedException($"深交所返回 {(int)resp.StatusCode}，疑似触发限流");
            resp.EnsureSuccessStatusCode();
            bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (RateLimitedException) { throw; }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            throw new RateLimitedException($"无法连接深交所停复牌接口：{ex.InnerException?.Message ?? ex.Message}", ex);
        }
        return Parse(bytes);
    }

    /// <summary>
    /// 解析导出的 xlsx（拆出来给单测拿真实导出验）。
    ///
    /// ⚠ 空和"响应不对"必须分开：只有「没有找到符合条件的数据」才是空；不是 xlsx（反爬页）、
    /// 表头变了、时刻写法认不出来、行数顶到 xls 上限（被截断），都抛。
    /// 每一行都留（股票、ETF、可转债、B 股……），六列全存；
    /// 只跳过连代码都没有的全空行（JSON 接口末页实测夹着这种）。
    /// </summary>
    public static List<SuspensionRow> Parse(byte[] xlsx)
    {
        if (xlsx.Length < 4 || xlsx[0] != (byte)'P' || xlsx[1] != (byte)'K')
            throw new RateLimitedException("深交所停复牌接口返回的不是 xlsx，疑似被拦截");

        using var ms = new MemoryStream(xlsx);
        using var reader = ExcelReaderFactory.CreateReader(ms);

        if (!reader.Read()) throw new InvalidOperationException("深交所停复牌 xlsx 是空的（连表头都没有）");
        var first = Cell(reader, 0);
        if (first.StartsWith("没有找到", StringComparison.Ordinal)) return [];

        string[] expect = ["证券代码", "证券简称", "停牌时间", "复牌时间", "停牌期限", "停牌原因"];
        var header = Enumerable.Range(0, reader.FieldCount).Select(i => Cell(reader, i)).ToArray();
        if (header.Length < expect.Length || !expect.SequenceEqual(header.Take(expect.Length)))
            throw new InvalidOperationException($"深交所停复牌 xlsx 表头变了：{string.Join(" | ", header)}");

        var rows = new List<SuspensionRow>();
        int lines = 1;
        while (reader.Read())
        {
            lines++;
            var code = Cell(reader, 0);
            if (code.Length == 0) continue;   // 全空的行（JSON 接口末页实测有），没有键存不了
            var (sd, st) = When(Cell(reader, 2));
            var (ed, et) = When(Cell(reader, 3));
            // 六列一列不落：代码、简称、停牌时刻（拆成日期+时刻）、复牌时刻（同）、期限、原因
            rows.Add(new SuspensionRow(
                SuspensionSource.Szse, "sz", code, Cell(reader, 1),
                sd, st, ed, et,
                Kind: Cell(reader, 4), StopTime: "", Reason: Cell(reader, 5)));
        }
        if (lines >= XlsRowLimit)
            throw new InvalidOperationException($"深交所停复牌 xlsx 有 {lines} 行、顶到了 xls 上限，结果被截断了——区间要切小");
        return rows;
    }

    /// <summary>「2026-09-30 开市」/「2026-09-30 09:34:03」/ 空。</summary>
    private static (DateOnly?, string) When(string text)
    {
        if (text.Length == 0) return (null, "");
        if (text.Length < 10
            || !DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            throw new InvalidOperationException($"深交所停复牌时刻认不出来：{text}");
        var rest = text[10..].Trim();
        // 只写日期不写时刻的（2014 年前后有 24 条，如「2014-06-09」）当开市——那个年代的停复牌都以开市为界
        if (rest is "开市" or "") return (day, SuspensionRow.AtOpen);
        if (TimeOnly.TryParseExact(rest, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || TimeOnly.TryParseExact(rest, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return (day, rest);
        throw new InvalidOperationException($"深交所停复牌时刻认不出来：{text}");
    }

    private static string Cell(IExcelDataReader r, int i) =>
        i < r.FieldCount ? (r.GetValue(i)?.ToString() ?? "").Trim() : "";
}
