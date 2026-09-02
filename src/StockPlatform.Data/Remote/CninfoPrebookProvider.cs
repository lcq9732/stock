using System.Net;
using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 定期报告**预约披露日**（2026-09-01 新增）。数据源：巨潮资讯网
/// <c>/new/information/getPrbookInfo</c>，深沪京全覆盖（页面入口 data/yypl）。
///
/// ════ 为什么用巨潮而不是别家 ════
/// · 东财也有（RPT_PUBLIC_BS_APPOIN，字段齐全），但用户环境里东财基本不通，不选。
/// · 上交所有 <c>commonSoaQuery.do?sqlId=SSE_SZSGG_DQBGYYQK_CAST_NEW</c>，好处是**有十几年历史**
///   （86123 条），坏处是**只有沪市**。以后要做"财报公布前后异动"之类的历史事件回测可以补上它。
/// · 巨潮覆盖深沪京、字段跟上交所一致（拿 600000 对照过，两边的变更轨迹完全相同），当前够用。
///
/// ════ 两个必须知道的接口脾气 ════
/// ① <b>分页参数是全小写的 <c>pagenum</c> / <c>pagesize</c></b>，不是驼峰。
///    写成 pageNum/pageSize 服务端**不报错、也不生效**，每次都默默返回第一页的 10 条——
///    2026-09-01 就是这么踩的：日志显示"抓了 5550 条"，其实是同样 10 只重复了 555 次，
///    入库按主键一去重只剩 10 条。参数名写对之后，<c>pagesize=6000</c> 一次 0.3 秒拿全市场。
/// ② <b>报告期不能瞎填</b>：可选值要先调 <see cref="GetAvailablePeriodsAsync"/>（对应页面上的下拉），
///    当前只给最近两期（如 2026 半年报、2026 一季）。填别的期一律返回 0 条——
///    这也意味着**拿不到更早的历史**，那是接口的限制不是 bug。
///
/// ════ 为什么每天都要抓 ════
/// 预约日**会改**，实测沪市 2000 条样本 12% 改过，而且**提前比延后还多**（55% vs 44%，
/// 最多提前 44 天）。提前那半边更危险——按原日期盯，财报已经出了还不知道。
/// 好在一次请求就能拿全市场，每天全量覆盖一遍的成本可以忽略，不需要什么增量策略。
/// </summary>
public class CninfoPrebookProvider
{
    private const string Base = "http://www.cninfo.com.cn/new/information/";
    private const string Referer = "http://www.cninfo.com.cn/new/commonUrl?url=data/yypl";

    /// <summary>深沪京全市场。取值来自页面的 plateTabList：szsh/sz/szmb/sse/... 用最全的那个。</summary>
    private const string AllMarkets = "szsh";

    private readonly RateLimiter _rateLimiter;
    private readonly HttpClient _http;

    public CninfoPrebookProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
    {
        _rateLimiter = rateLimiter;
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri(Referer);
        _http.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    private static HttpClientHandler CreateHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    };

    private async Task<JsonDocument> PostAsync(string path, Dictionary<string, string> form, CancellationToken ct)
        => await _rateLimiter.RunAsync(async () =>
        {
            using var resp = await _http.PostAsync(Base + path, new FormUrlEncodedContent(form), ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(body);
        }, ct);

    /// <summary>
    /// 接口当前认哪些报告期。返回按新到旧排序，元素是 (报告期, 显示名) 如 (2026-06-30, "2026半年报")。
    /// **必须先问过它再抓**——填它没给的报告期一律返回 0 条。
    /// </summary>
    public async Task<List<(DateTime Period, string Label)>> GetAvailablePeriodsAsync(CancellationToken ct = default)
    {
        using var doc = await PostAsync("getSelectData", new() { ["type"] = "prbook" }, ct);
        var list = new List<(DateTime, string)>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var v0 = el.TryGetProperty("value0", out var a) ? a.GetString() : null;
            var v1 = el.TryGetProperty("value1", out var b) ? b.GetString() : null;
            if (DateTime.TryParse(v0, out var d)) list.Add((d, v1 ?? v0!));
        }
        return list.OrderByDescending(x => x.Item1).ToList();
    }

    /// <summary>一页要多少：全市场才 5500 出头，一次全拿回来（实测 0.3 秒）。留了富余量。</summary>
    private const int PageSize = 8000;

    /// <summary>
    /// 抓某一期的预约披露表。<paramref name="stockCode"/> 给了就只查那一只，留空则全市场。
    ///
    /// 正常情况下**一次请求就够**（见 PageSize）。翻页的循环留着纯粹是兜底：万一哪天
    /// 上市公司数量涨过 PageSize、或者服务端偷偷把单页上限调小了，这里能自动续上，
    /// 不会像参数名写错那次一样悄悄只拿回第一页。
    /// </summary>
    public async Task<List<EarningsScheduleRow>> FetchAsync(
        DateTime period, string? stockCode = null, CancellationToken ct = default)
    {
        var rows = new List<EarningsScheduleRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int pageNum = 1, totalRows = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var doc = await PostAsync("getPrbookInfo", new()
            {
                ["sectionTime"] = period.ToString("yyyy-MM-dd"),
                ["firstTime"] = "",
                ["lastTime"] = "",
                ["market"] = AllMarkets,
                ["stockCode"] = stockCode ?? "",
                ["orderClos"] = "",
                ["isDesc"] = "",
                // ⚠ 必须小写！写成 pageNum/pageSize 不报错也不生效，永远只回第一页（见类注释）
                ["pagenum"] = pageNum.ToString(),
                ["pagesize"] = PageSize.ToString(),
            }, ct);

            var root = doc.RootElement;
            if (root.TryGetProperty("totalRows", out var tr) && tr.TryGetInt32(out var t)) totalRows = t;

            int got = 0;
            if (root.TryGetProperty("prbookinfos", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    got++;
                    if (Map(el, period) is not { } r) continue;
                    // 去重是防呆：分页真失效时（参数名错、服务端行为变），下面的推进条件会停住，
                    // 但万一停不住也不至于把同一批数据重复堆进内存。
                    if (seen.Add(r.Code)) rows.Add(r);
                }
            }

            // 这一页没东西了，或者已经拿够了 → 收工
            if (got == 0 || rows.Count >= totalRows || got < PageSize) break;
            pageNum++;
        }
        return rows;
    }

    // 字段名是巨潮内部编号，对照页面表头确认过：
    //   f002d=首次预约  f003d/f004d/f005d=一/二/三次变更  f006d=实际披露  f001d=报告期
    private static EarningsScheduleRow? Map(JsonElement el, DateTime period)
    {
        string? code = Str(el, "seccode");
        if (string.IsNullOrWhiteSpace(code)) return null;
        return new EarningsScheduleRow(
            code.Trim(),
            Date(el, "f001d_0102") ?? period,
            Date(el, "f002d_0102"),
            Date(el, "f003d_0102"),
            Date(el, "f004d_0102"),
            Date(el, "f005d_0102"),
            Date(el, "f006d_0102"));
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? Date(JsonElement el, string name)
    {
        var s = Str(el, name);
        return !string.IsNullOrWhiteSpace(s) && DateTime.TryParse(s, out var d) ? d : null;
    }
}
