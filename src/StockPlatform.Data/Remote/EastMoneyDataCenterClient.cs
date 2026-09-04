using System.Net;
using System.Net.Http;
using System.Text.Json;


namespace StockPlatform.Data.Remote;

/// <summary>
/// 东方财富 datacenter 报表接口（<c>datacenter-web.eastmoney.com/api/data/v1/get</c>）的通用客户端。
/// 业绩预告、业绩快报、龙虎榜营业部明细、大宗交易、机构调研、限售解禁、股东增减持、个股板块归属
/// 全都走这一个接口，差别只在 <c>reportName</c>，所以抽成一个客户端。
///
/// 关于东财在本项目里的可达性（2026-09-03 实测，纠正了此前"东财整体不可用"的判断）：
/// 三个域名是**分开限流**的，可达性也不同——
///   · <c>datacenter-web</c> 最宽松，本机可达，是这个类用的域名；
///   · <c>push2his</c>（个股历史资金流）次之，本机可达；
///   · <c>push2</c>（行情/板块成分）最严，密集请求十几次就会连续一小时以上拒绝连接。
/// 所以能走 datacenter 的一律走 datacenter：个股板块归属本来只能靠 push2 逐板块查成分股，
/// 后来发现 <c>RPT_F10_CORETHEME_BOARDTYPE</c> 是一张"个股→板块"的全市场映射表，188 页就能拿全，
/// 既快又绕开了最容易被封的域名。
///
/// 两个实测出来的硬约束：
///   1. <b>pageSize 上限 500</b>——传 1000 也只返回 500，分页得按 500 算。
///   2. <b>深分页会挂</b>——龙虎榜营业部明细全量 131 万行，按 500/页是 2620 页，翻到上千页时接口
///      会拒绝或极慢。所以大表必须按时间切片查（见 <see cref="EastMoneyQuerySlicer"/>），
///      切成月片后每片只有 40 页左右，翻页永远是浅的。
/// </summary>
public class EastMoneyDataCenterClient
{
    public const int PageSize = 500;

    /// <summary>
    /// 单次查询最多翻多少页。防的是"接口给的 totalPages 不可信"——正常路径下大表都按月/年
    /// 切过片，单片撑死几十页，这个上限永远碰不到；碰到了就说明分页出问题了，该停。
    /// </summary>
    public const int MaxPages = 2000;

    private const string BaseUrl = "https://datacenter-web.eastmoney.com/api/data/v1/get";

    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus;

    public EastMoneyDataCenterClient(RateLimiter rateLimiter, HttpClient? httpClient = null)
    {
        _rateLimiter = rateLimiter;
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://data.eastmoney.com/");
        _http.Timeout = TimeSpan.FromSeconds(30);
        _rateLimiter.OnStatus += s => OnStatus?.Invoke(s);
    }

    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    /// <summary>
    /// 翻页拉取一个报表的全部行。逐页 yield 而不是攒成一个大 List——龙虎榜这类表单次全量上百万行，
    /// 一次性装进内存既慢又容易把 32 位进程撑爆，调用方边取边写库才是对的。
    /// </summary>
    /// <param name="reportName">如 RPT_PUBLIC_OP_NEWPREDICT</param>
    /// <param name="filter">东财的 filter 语法，多个条件是并列括号：<c>(TRADE_DATE&gt;='2026-01-01')(TRADE_DATE&lt;='2026-01-31')</c></param>
    /// <param name="sortColumns">
    /// 排序列。<b>必须给，而且必须能唯一定序</b>——键不唯一时同键行在页与页之间的先后不保证，
    /// 翻页就会既重复又丢行。实测个股题材表按 SECURITY_CODE 单列排（一只股十几行），
    /// 前 3 页 1500 行里就有 15 行重复（＝同时丢了 15 行）；补上 BOARD_CODE 作次级键后为 0。
    /// 所以凡是"一个主体多行"的报表，都要带一个能拆开同主体的次级列。
    /// </param>
    /// <param name="onTotalCount">
    /// 接口自报的总行数（第一页时回调一次）。给调用方**对账**用：抓完拿实际行数跟它比，
    /// 对不上说明中途悄悄少了——这是这个项目里反复救命的那道防线（板块成分股靠它抓出过漏股）。
    /// </param>
    public async IAsyncEnumerable<JsonElement> QueryAsync(
        string reportName,
        string? filter = null,
        string? sortColumns = null,
        bool descending = true,
        Action<int>? onTotalCount = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        int page = 1;
        int totalPages = 1;
        string? prevFirstRow = null;

        while (page <= totalPages)
        {
            ct.ThrowIfCancellationRequested();

            // 硬页数上限（2026-09-03）：totalPages 是**接口给的**，不能无条件信任。
            // 实测同一个报表在 pageSize=2 时报 pages=15723——万一哪天接口忽略了我们传的
            // pageSize=500、按它自己的粒度算页数，这个循环就会去翻上万页。
            // 正常路径下所有大表都按月/年切过片，单片撑死几十页，到不了这个数。
            if (page > MaxPages)
            {
                OnStatus?.Invoke($"⚠ {reportName} 翻页超过 {MaxPages} 页仍未结束（接口报 {totalPages} 页），" +
                                 "判定分页异常，停止本片抓取——已 yield 出去的数据有效。");
                yield break;
            }

            var url = BuildUrl(reportName, page, filter, sortColumns, descending);
            var body = await _rateLimiter.RunAsync(() => GetAsync(url, ct), ct);

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new RateLimitedException(
                    $"东财报表 {reportName} 第 {page} 页返回的不是 JSON（多半是被限流了）：{Truncate(body, 120)}", ex);
            }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("result", out var result) ||
                    result.ValueKind != JsonValueKind.Object)
                {
                    // code=9501 报表不存在，或者该 filter 下没有任何数据——两种都不是错误，正常收尾。
                    yield break;
                }
                if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    yield break;

                if (page == 1 && result.TryGetProperty("pages", out var p) && p.TryGetInt32(out var tp))
                    totalPages = tp;
                if (page == 1 && onTotalCount != null &&
                    result.TryGetProperty("count", out var c) && c.TryGetInt32(out var tc))
                    onTotalCount(tc);

                // 分页失效检测（2026-09-03）：如果接口忽略了 pageNumber，每页会返回同一批数据，
                // 循环就会一路"抓"到 totalPages、全是重复行。主键去重虽然兜得住不写脏数据，
                // 但请求全白发、还平白撞限流。拿每页第一行比一下，一样就说明翻页没生效。
                var firstRow = data.GetArrayLength() > 0 ? data[0].GetRawText() : null;
                if (firstRow != null && firstRow == prevFirstRow)
                {
                    OnStatus?.Invoke($"⚠ {reportName} 第 {page} 页跟上一页内容相同——翻页没生效" +
                                     "（接口可能忽略了 pageNumber），停止本片抓取。");
                    yield break;
                }
                prevFirstRow = firstRow;

                int count = 0;
                foreach (var item in data.EnumerateArray())
                {
                    // Clone：JsonDocument 一 Dispose，未 Clone 的 JsonElement 就成了悬空引用，
                    // 而这里是 yield，调用方拿到手时本轮的 doc 早已释放。
                    yield return item.Clone();
                    count++;
                }
                if (count == 0) yield break;
            }

            page++;
        }
    }

    /// <summary>只取第一页，用于探活/取总数这类场景。</summary>
    public async Task<(int TotalPages, List<JsonElement> Rows)> QueryFirstPageAsync(
        string reportName, string? filter = null, string? sortColumns = null,
        bool descending = true, CancellationToken ct = default)
    {
        var url = BuildUrl(reportName, 1, filter, sortColumns, descending);
        var body = await _rateLimiter.RunAsync(() => GetAsync(url, ct), ct);
        var rows = new List<JsonElement>();
        int pages = 0;
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("result", out var result) &&
            result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("pages", out var p) && p.TryGetInt32(out var tp)) pages = tp;
            if (result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var item in data.EnumerateArray()) rows.Add(item.Clone());
        }
        return (pages, rows);
    }

    private static string BuildUrl(string reportName, int page, string? filter,
                                   string? sortColumns, bool descending)
    {
        // filter 里的括号、单引号、比较符是语法的一部分，转义了接口就不认了，所以这里手工拼而不是
        // 用 UrlEncode 全部转义。reportName/sortColumns 都是代码里写死的常量，没有注入面。
        var url = $"{BaseUrl}?reportName={reportName}&columns=ALL" +
                  $"&pageNumber={page}&pageSize={PageSize}";
        if (!string.IsNullOrEmpty(sortColumns))
        {
            // sortColumns 支持逗号分隔的多个字段，sortTypes 要一一对应。
            //
            // **必须给一个能定唯一顺序的排序**，否则深分页会跨页重复/遗漏：按日期单字段排时，
            // 同一天的几十上百行之间没有确定顺序，翻页时服务端返回的次序会变。实测限售解禁
            // 按 FREE_DATE 单排、一年 4 页 1784 行，去重后只剩 1774 行（丢 0.6%）；
            // 加上 SECURITY_CODE 作次键后 1784 行一行不少。
            //
            // 次键一律升序——方向不重要，要的只是"确定"。
            var cols = sortColumns.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var types = new string[cols.Length];
            types[0] = descending ? "-1" : "1";
            for (int i = 1; i < cols.Length; i++) types[i] = "1";
            url += $"&sortColumns={string.Join(",", cols)}&sortTypes={string.Join(",", types)}";
        }
        if (!string.IsNullOrEmpty(filter))
            url += $"&filter={filter}";
        return url;
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            return await _http.GetStringAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException)
                                   && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException(
                $"无法连接东财 datacenter 接口：{detail}（可能是代理/防火墙，也可能是触发了限流）", ex);
        }
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");
}

/// <summary>
/// 把一个大时间段切成小片，供 <see cref="EastMoneyDataCenterClient"/> 分片查询用。
///
/// 存在的唯一理由是东财的深分页会挂：龙虎榜营业部明细全量 131 万行、2620 页，翻到上千页接口就
/// 开始拒绝。按月切片之后每片 40 页左右，永远是浅分页。副作用是断点续传的粒度天然变成了"片"，
/// 某一片失败只需要重跑那一片。
/// </summary>
public static class EastMoneyQuerySlicer
{
    public record Slice(string Name, DateTime Start, DateTime End);

    public static List<Slice> ByMonth(DateTime start, DateTime end)
    {
        var list = new List<Slice>();
        var cur = new DateTime(start.Year, start.Month, 1);
        while (cur <= end)
        {
            var next = cur.AddMonths(1);
            var lo = cur < start ? start : cur;
            var hi = next.AddDays(-1);
            if (hi > end) hi = end;
            if (lo <= hi) list.Add(new Slice($"{cur:yyyy-MM}", lo, hi));
            cur = next;
        }
        return list;
    }

    public static List<Slice> ByYear(DateTime start, DateTime end)
    {
        var list = new List<Slice>();
        for (int y = start.Year; y <= end.Year; y++)
        {
            var lo = new DateTime(y, 1, 1);
            var hi = new DateTime(y, 12, 31);
            if (lo < start) lo = start;
            if (hi > end) hi = end;
            if (lo <= hi) list.Add(new Slice(y.ToString(), lo, hi));
        }
        return list;
    }

    /// <summary>拼东财的日期区间 filter。</summary>
    public static string DateFilter(string field, DateTime start, DateTime end) =>
        $"({field}>='{start:yyyy-MM-dd}')({field}<='{end:yyyy-MM-dd}')";
}
