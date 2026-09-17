using System.Text.Json;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 巨潮资讯（cninfo）的全市场证券名单（2026-09-17 新增）——
/// <c>http://www.cninfo.com.cn/new/data/szse_stock.json</c>，一个静态 JSON、一次请求拿全。
///
/// ════ 为什么加第三个源 ════
/// 它是我们现有名单的**超集**：实测 2026-09-17，过滤后"库里有、巨潮没有的：**0 只**"。
/// 而它能补上另外两个源都给不出的东西——最要紧的是**科创板退市股**：
/// <c>688086 退市紫晶</c>、<c>688555 退市泽达</c>、<c>688287 退市观典</c>
/// 在上交所官网的 <c>stockType</c> 1/5/8/9/10/11/12/13/20/21 里**全都查不到**
/// （<c>stockType=5</c> 终止上市名单里 68 开头的是 0 只），而巨潮有。
///
/// ⚠ **文件名叫 szse，内容却是全市场**（600/603/688/000/002/300/301/920 都有），别被名字骗了。
///
/// ════ 两道过滤，缺一不可 ════
/// ① **剔 B 股**：<c>category</c> 字段区分 A股/B股/CDR，实测 6254 条里 B股 79 条。
///    用源自己给的字段判，不按号段猜。
/// ② ⚠ **剔新三板**：这份名单**含全国股转系统**的股票——实测多出来的 293 只非 B 股里，
///    278 只有行情，号段是 430/831~838/873 这些，全是新三板。
///    **不能让它们进来**：<see cref="StockPlatform.Logic.Services.MarketClassifier"/> 把
///    43/83/87 判成**北交所**（那是 920 代码迁移前的近似，见它的类注释），于是几百只新三板
///    会被按北交所去抓 K 线。北交所自 2025-10-09 已全部迁到 920，所以排除 43/83/87 是安全的。
///
/// 过滤用的是**白名单**（<see cref="AShareSegments"/>）而不是黑名单：漏掉一个新号段只是少几只票、
/// 下次加上就行；放进一个不该来的号段却会静默污染整个抓取清单。
///
/// ════ 已知副作用 ════
/// 过滤后仍会比另外两个源多出约 30 只，其中十来只是**抓不到 K 线的**：
/// <c>688688 蚂蚁集团</c>（从未上市）、<c>601206 海尔施</c>/<c>603302 鑫广绿环</c>/
/// <c>603361 浙江国祥</c>（暂缓上市）、<c>600849 上海医药</c>/<c>601313 江南嘉捷</c>（已换代码的老号）。
/// 它们会进失败名单——**这是有意的**：失败名单是可见的，比静默缺失好，量也就每天几十个请求。
/// </summary>
public class CninfoStockListProvider : IStockListProvider
{
    private const string Url = "http://www.cninfo.com.cn/new/data/szse_stock.json";

    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10) };

    /// <summary>
    /// A 股号段白名单。**故意不含 43/83/87**（那是新三板，见类注释），
    /// 也不含 200/900（B 股，另外还有 <c>category</c> 那道过滤兜着）。
    /// </summary>
    private static readonly string[] AShareSegments =
    {
        "000", "001", "002", "003",          // 深市主板
        "300", "301", "302",                 // 创业板
        "600", "601", "603", "605",          // 沪市主板
        "688", "689",                        // 科创板（含 CDR）
        "920",                               // 北交所（2025-10-09 起全部是这个号段）
    };

    private readonly HttpClient _http;

    public CninfoStockListProvider(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<List<StockListEntry>> GetAllStocksAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await FetchOnceAsync(progress, ct); }
            catch (RateLimitedException) when (attempt < RetryDelays.Length && !ct.IsCancellationRequested)
            {
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }
    }

    private async Task<List<StockListEntry>> FetchOnceAsync(IProgress<string>? progress, CancellationToken ct)
    {
        string body;
        try
        {
            body = await _http.GetStringAsync(Url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            throw new RateLimitedException(
                $"无法获取巨潮证券名单：{ex.InnerException?.Message ?? ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(body))
            throw new RateLimitedException("巨潮返回空响应");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("stockList", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new RateLimitedException("巨潮的返回里没有 stockList 数组，格式可能变了");

        var result = new List<StockListEntry>();
        int total = 0, bShares = 0, offBoard = 0;
        foreach (var item in list.EnumerateArray())
        {
            total++;
            var code = Str(item, "code");
            if (code.Length != 6 || !code.All(char.IsDigit)) continue;

            if (Str(item, "category") == "B股") { bShares++; continue; }        // ① 源自己给的分类
            if (!AShareSegments.Any(p => code.StartsWith(p, StringComparison.Ordinal)))
            {
                offBoard++;                                                     // ② 新三板等，见类注释
                continue;
            }

            // 巨潮不给市值/最新价——这两个字段由排在前面的新浪那条路提供
            // （合并顺序见 CompositeStockListProvider）
            result.Add(new StockListEntry(code, Str(item, "zwjc")));
        }

        progress?.Report($"巨潮全市场名单：{total} 条 → 收 {result.Count} 只"
                         + $"（剔 B股 {bShares} 只、非A股号段 {offBoard} 只）");
        return result;
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : "";
}
