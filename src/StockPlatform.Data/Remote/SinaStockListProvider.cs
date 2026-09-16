using System.Text;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Remote;

/// <summary>
/// Fetches the full list of A-share stock codes from Sina Finance's public quote-node endpoint —
/// a deliberately different domain (sina.com.cn) from <see cref="EastMoneyStockListProvider"/>
/// (eastmoney.com), so switching the Fetcher's data source dropdown to a non-EastMoney vendor
/// (see doc/data-platform-design.md section 3.4) also routes the stock-LIST step around a
/// network that blocks eastmoney.com, not just the bar-data step. Tencent's public API has no
/// equivalent full-list endpoint (only per-code lookups), so this is paired with the Tencent bar
/// source instead — see the composition root.
///
/// ════ 为什么翻完 hs_a 还要翻一遍 kcb ════
/// <c>hs_a</c>（沪深北A股）**不含科创板 CDR**。2026-09-16 实测：按 symbol 升序，第 27 页从
/// <c>sh688718</c> 一路排到 <c>sz000060</c>——沪市段结束后直接进深市，**中间没有 sh689009**。
/// 而同一接口的 <c>node=kcb</c>（科创板）有它（按 symbol 降序第一条就是 sh689009）。
///
/// 后果是静默的：689009 九号公司从来没进过 <c>StockMeta</c>，于是从来没被排进抓取清单——
/// 一根K线、一条财务、一条分红都没有，选股/诊断/所有基于K线的分析都看不到这只票，
/// 而它的两融数据到 2026-09-14 还在更新（见 <c>doc/missing-instruments-design.md</c>）。
///
/// 所以翻完 <c>hs_a</c> 再整个翻一遍 <c>kcb</c>、按 code 去重合并。**不特判 689009 这一只**
/// （将来再发 CDR 一样会漏），也**不靠"CDR 的 symbol 一定排在 688 后面"只翻降序第一页**
/// （省 5 个请求、多一条会悄悄失效的假设，不划算）。代价是科创板约 590 只 ⇒ 6 页、约 2 秒。
/// </summary>
public class SinaStockListProvider : IStockListProvider
{
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10) };

    // The server hard-caps every page at 100 rows no matter what "num" is requested (verified
    // empirically — num=200 and num=6000 both still return exactly 100). ~5500 A-shares means
    // ~55 pages; MaxPages is a generous ceiling so a change in the actual market size never
    // silently truncates the list.
    private const int PageSize = 100;
    private const int MaxPages = 200;

    /// <summary>要翻的节点，按顺序。<c>kcb</c> 是补 <c>hs_a</c> 漏掉的科创板 CDR，见类注释。</summary>
    private static readonly (string Node, string Label)[] Nodes =
    {
        ("hs_a", "沪深北A股"),
        ("kcb", "科创板（补 hs_a 漏掉的 CDR）"),
    };

    // 没有这个延时的话，真的会被限流——开发这个功能时用bash脚本紧挨着连续请求了约80页，很快就
    // 收到了HTML反爬拦截页而不是JSON（不是猜测，是实测踩到的坑）。这个类现在不只是"拉取全部"时
    // 偶尔调一次的股票列表步骤了，SinaListMarketCapFetcher 让它变成每次"拉取全部"/"拉取当天"
    // 都要完整跑一遍，被连续请求的频率比以前高，延时更有必要。
    private static readonly TimeSpan DelayBetweenPages = TimeSpan.FromMilliseconds(300);

    private readonly HttpClient _http;

    static SinaStockListProvider()
    {
        // GBK isn't included by default in .NET Core — this endpoint replies with
        // "charset=gbk" regardless of Accept-Charset.
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public SinaStockListProvider(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.Timeout = TimeSpan.FromSeconds(12);
    }

    public async Task<List<StockListEntry>> GetAllStocksAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new List<StockListEntry>();
        var seen = new HashSet<string>();
        foreach (var (node, label) in Nodes)
        {
            int added = 0;
            for (int page = 1; page <= MaxPages; page++)
            {
                var pageEntries = await FetchPageWithRetryAsync(node, page, ct);
                if (pageEntries.Count == 0) break; // 翻过末页了（真末页回的是空数组，见 FetchPageAsync）
                // 去重：kcb 的 688 段跟 hs_a 完全重叠，只有 689 那几只 CDR 是新的
                foreach (var e in pageEntries)
                    if (seen.Add(e.Code)) { result.Add(e); added++; }
                if (page % 10 == 0) progress?.Report($"正在获取全市场股票列表...（已取 {result.Count} 只）");
                await Task.Delay(DelayBetweenPages, ct); // 见 DelayBetweenPages 注释——连续不间断请求会被限流
            }
            if (node != "hs_a")
                progress?.Report($"　{label}：新增 {added} 只（合计 {result.Count} 只）");
        }
        return result;
    }

    private async Task<List<StockListEntry>> FetchPageWithRetryAsync(string node, int page, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await FetchPageAsync(node, page, ct);
            }
            catch (RateLimitedException) when (attempt < RetryDelays.Length && !ct.IsCancellationRequested)
            {
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }
    }

    private async Task<List<StockListEntry>> FetchPageAsync(string node, int page, CancellationToken ct)
    {
        // node=hs_a covers 沪深北 A股 all together (Shanghai/Shenzhen/Beijing exchanges);
        // node=kcb is 科创板,翻它是为了补 hs_a 漏掉的 CDR——见类注释。
        var url = "http://vip.stock.finance.sina.com.cn/quotes_service/api/json_v2.php/Market_Center.getHQNodeData" +
                  $"?page={page}&num={PageSize}&sort=symbol&asc=1&node={node}&symbol=&_s_r_a=init";

        byte[] bytes;
        try
        {
            bytes = await _http.GetByteArrayAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException($"无法连接新浪财经接口：{detail}（可能是网络问题，也可能是触发了反爬限流）", ex);
        }

        if (bytes.Length == 0)
            throw new RateLimitedException("新浪财经返回空响应，疑似触发反爬限流");

        var json = Encoding.GetEncoding("GBK").GetString(bytes).Trim();

        // ⚠ 翻过末页时接口回的是**空数组** `[]`（2026-09-16 实测：etf_hq_fund 有效页到 17，
        // page 19 回的就是 `[]`），而字面量 `null` 是**被限流**的表现。
        //
        // 以前这里把 `null` 也当成"空页"返回空列表，调用方 `if (pageEntries.Count == 0) break;`
        // 就当成翻过了末页——**限流那一刻之后的整段名单全部丢掉，一条错误都不报**。
        // 个股这边还有本地 StockMeta 兜底（只会丢新票），ETF 名单没有兜底，少一段就直接少抓
        // 一批K线。所以 `null` 走重试；重试耗尽就让整轮失败——失败是响亮的，静默截断不是。
        if (json.Length == 0 || json == "null")
            throw new RateLimitedException(
                "新浪财经返回 null（不是空数组 []）——这是触发反爬限流的表现，不是翻到了末页");

        using var doc = JsonDocument.Parse(json);
        var result = new List<StockListEntry>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var code = item.GetProperty("code").GetString() ?? "";
            var name = item.GetProperty("name").GetString() ?? "";
            if (code.Length != 6 || !code.All(char.IsDigit)) continue;

            // "nmc"（流通市值，单位万元）跟这批股票的行情数据一起免费带出来，不需要单独发请求——
            // 字段含义在 SinaListMarketCapFetcher 引入前用贵州茅台/000001/300750三只股本结构不同
            // 的股票跟 TencentMarketCapFetcher 的结果做过交叉校验，见 doc/data-platform-design.md
            // 3.5节。缺失/非数字/<=0 一律按"没有数据"处理，不写入假的0元市值。
            double? marketCap = ReadNumber(item, "nmc") is > 0 and var nmc ? nmc * 10_000 : null;

            // "trade"（最新价）：盘前/周末/节假日这个字段是 0（此时 nmc 是用 "settlement" 昨收算的，
            // 属于上一个交易日），开盘后才是当日实时价——见 StockListEntry.LastPrice 的注释，
            // SinaListMarketCapFetcher 靠它判断市场此刻是否在交易。<=0 归 null。
            double? lastPrice = ReadNumber(item, "trade") is > 0 and var t ? t : null;

            result.Add(new StockListEntry(code, name, marketCap, lastPrice));
        }
        return result;
    }

    /// <summary>这个接口的数值字段类型不统一——有的是 JSON 数字（<c>nmc</c>），有的是带尾随零的字符串
    /// （<c>trade</c> 回的是 "0.000" 这种）。统一按"能解析成数就用，否则 null"处理，免得字段类型
    /// 哪天变了就静默丢数据。</summary>
    private static double? ReadNumber(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.String => double.TryParse(el.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null,
            _ => null,
        };
    }
}
