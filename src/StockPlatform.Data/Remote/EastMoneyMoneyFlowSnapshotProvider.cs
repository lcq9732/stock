using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 分档资金流的**全市场当日快照**（2026-09-06，东财 <c>push2delay.eastmoney.com</c> 的
/// <c>clist/get</c>）——跟 <see cref="EastMoneyMoneyFlowProvider"/> 是**同一份数据的两种切法**，
/// 不是替换：那个是"一只票 120 天"，这个是"一天全市场"。
///
/// ════ 为什么突然能拿到"某天全市场"了 ════
/// 排行接口 <c>clist/get</c> 一直都有，只是它在 <c>push2.eastmoney.com</c> 上——那个域名在这台
/// 机器上被网关拦着（curl 直接 000，见 <c>DataSourceId.EmPush2</c> 的注释）。
/// 2026-09-06 挨个试镜像域名发现：<b><c>push2delay</c> 通，而且提供完整的 clist</b>
/// （<c>82.push2</c>/<c>7.push2</c> 不通，<c>push2his</c> 没有这个接口）。
///
/// 「delay」是延时行情：盘中滞后约 15 分钟。**对我们无所谓**——这份数据本来就只在收盘后取，
/// 取的是当日终值（实测 <c>f124</c> 时间戳 = 交易日 15:34，收盘清算完才更新的那一版）。
///
/// ════ 等价性（2026-09-06 逐条验过）════
/// 库里 2026-09-04 那批 4603 只 × 13 个字段跟本接口逐条比对：**零差异**
/// （净额容差 1 元、占比/价格容差 0.011，全部落在容差内）。所以它跟 push2his 的
/// <c>fflow/daykline</c> 是同一份数据，换了个入口而已。
///
/// ════ 代价：只有当天，没有历史 ════
/// 补历史仍旧得按股票走 push2his（120 天窗口）。所以两条通道并存、不互相取代：
/// 这条负责"每天一两分钟把当天全市场拿全"，那条负责"把从没抓过的票的 120 天补上"。
///
/// ════ 两个硬限制 ════
///   1. <b><c>pz</c> 上限 100</b>——填 500/1000/5000 都只给 100 条。全市场 5909 只 = 60 页。
///   2. <b>停牌股所有数值字段返回字符串 <c>"-"</c></b>（实测约 361 只），不是 0、也不是 null。
///      当成数字硬解会静默变成 0，那比缺数据更糟——"主力净额 0"是个看着很合理的假值。
/// </summary>
public class EastMoneyMoneyFlowSnapshotProvider
{
    /// <summary>能拿到 clist 的那个镜像域名。push2 本机不通，别改回去。</summary>
    public const string DefaultHost = "push2delay.eastmoney.com";

    /// <summary>服务端硬上限，填再大也只返回 100 条——不是我们保守，是它就给这么多。</summary>
    public const int PageSize = 100;

    /// <summary>沪深京全 A：深主板 t:6、创业板 t:80、沪主板 t:2、科创板 t:23、北交所 t:81+s:2048。</summary>
    private const string MarketFilter = "m:0+t:6,m:0+t:80,m:1+t:2,m:1+t:23,m:0+t:81+s:2048";

    /// <summary>
    /// f62/f184 主力净额+净占比、f66/f69 超大单、f72/f75 大单、f78/f81 中单、f84/f87 小单、
    /// f2 收盘价、f3 涨跌幅、f124 行情时间戳（拿它定交易日，见 <see cref="ParsePage"/>）。
    /// 正好铺满 <see cref="NetInflowDetail"/> 的每一列。
    /// </summary>
    private const string Fields = "f12,f2,f3,f62,f66,f69,f72,f75,f78,f81,f84,f87,f184,f124";

    /// <summary>
    /// 收盘清算完那一版才是当日终值。盘中（含午休，那会儿 f124 停在 11:30）拿到的是**半天的**
    /// 资金流，写进库会污染当天那一行、而且事后看不出来——所以整批拒收，见
    /// <see cref="MoneyFlowSnapshot.IsIntraday"/>。
    /// </summary>
    public static readonly TimeSpan SettlementTime = new(15, 0, 0);

    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;
    private readonly string _host;

    public event Action<string>? OnStatus;

    /// <summary>限流熔断还要等到几点；没在暂停就是 null。</summary>
    public DateTime? PausedUntil => _rateLimiter.PausedUntil;

    public EastMoneyMoneyFlowSnapshotProvider(RateLimiter rateLimiter, HttpClient? httpClient = null,
                                              string? host = null, string? bindNetworkInterface = null)
    {
        _rateLimiter = rateLimiter;
        _host = string.IsNullOrWhiteSpace(host) ? DefaultHost : host!.Trim();
        _http = httpClient ?? new HttpClient(
            NetworkInterfaceBinder.CreateHandler(bindNetworkInterface, s => OnStatus?.Invoke(s)));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
            + "Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/");
        _http.Timeout = TimeSpan.FromSeconds(25);
        _rateLimiter.OnStatus += s => OnStatus?.Invoke(s);
    }

    /// <summary>这一轮走的是哪个域名——日志里要报出来，出问题才知道是不是镜像换了。</summary>
    public string Host => _host;

    /// <summary>
    /// 把全市场当日的分档资金流拉全。约 60 页。
    ///
    /// 翻页翻到"服务端自报的 total 都拿到了"或"某一页空了"为止；页与页之间的节奏归
    /// <see cref="RateLimiter"/> 管。按代码排序（<c>fid=f12&amp;po=0</c>）而不是按涨跌幅——
    /// 跟板块成分股那边同一个道理：涨跌幅不是唯一键，并列项之间服务端不保证每次次序一样，
    /// 翻页就会跨页重复和遗漏。
    /// </summary>
    public async Task<MoneyFlowSnapshot> FetchAllAsync(IProgress<string>? progress = null,
                                                       CancellationToken ct = default)
    {
        var fetchedAt = DateTime.Now;
        var rows = new Dictionary<string, NetInflowDetail>(StringComparer.Ordinal);
        int total = 0, suspended = 0;
        DateTime? quoteTime = null;

        for (int pn = 1; pn <= 200; pn++)      // 200 页是保险丝：翻页判据万一失灵也不会无限打下去
        {
            ct.ThrowIfCancellationRequested();

            var url = $"https://{_host}/api/qt/clist/get?fid=f12&po=0&pz={PageSize}&pn={pn}"
                    + $"&np=1&fltt=2&invt=2&fs={MarketFilter}&fields={Fields}";
            string body;
            try
            {
                body = await _rateLimiter.RunAsync(() => _http.GetStringAsync(url, ct), ct);
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException)
                                       && !ct.IsCancellationRequested)
            {
                throw new RateLimitedException(
                    $"无法连接东财资金流排行接口（{_host} 第 {pn} 页）：{ex.InnerException?.Message ?? ex.Message}", ex);
            }

            var page = ParsePage(body, fetchedAt);
            // 解析不了 ≠ 这一页是空的。当成空的会让我们**提前收工**、把半个市场当成全市场——
            // 而调用方是按"这天的数据拿到了"来写库的，缺的那些票会静默地没有当天数据。
            if (page == null)
                throw new RateLimitedException($"东财资金流排行第 {pn} 页解析不了（空响应或被截断）");

            if (page.Total > 0) total = page.Total;
            suspended += page.Suspended;
            if (page.QuoteTime is { } qt && (quoteTime == null || qt > quoteTime)) quoteTime = qt;

            int before = rows.Count;
            foreach (var r in page.Rows) rows[r.Code] = r;

            // 停止判据是"这一页什么都没带来"，而不是"这一页是空的"——两种翻过头的形态都要认：
            // 空 diff，以及**又把上一页还回来**（分页参数没被理会时就是这样，实测东财这类接口
            // 出过）。只看 Rows.Count==0 的话后一种会一路打到保险丝那 200 页。
            if (rows.Count == before && page.Suspended == 0) break;
            if (total > 0 && rows.Count + suspended >= total) break;     // 拿全了
            if (pn % 20 == 0)
                progress?.Report($"分档资金流快照：已拉 {pn} 页、{rows.Count} 只"
                               + (total > 0 ? $"（全市场 {total} 只）" : "") + "。");
        }

        var snapshot = new MoneyFlowSnapshot(quoteTime, total, suspended, rows.Values.ToList());

        // 交易日以整批**最新的** f124 为准，而不是每行各自的时间戳：停牌股的时间戳是当天 08:00、
        // 正常股是收盘后的 15:3x，取最大才拿得到"这批属于哪个交易日"。
        // 顺带把日期对不上的行剔掉——长期停牌但还残留着旧值的票，不能把旧值贴上今天的日期。
        if (snapshot.TradeDate is { } day)
            snapshot.Rows.RemoveAll(r => r.TradeDate.Date != day);

        // ⚠ 整批的 FetchedAt 改成**行情时间戳**，不是"现在几点"（2026-09-06）。
        // 这不是洁癖，是补历史那条路的排队判据要靠它：逐股通道按"最久没抓的先抓"排队，
        // 而快照每天会把全市场每只票的 fetched_at 都刷一遍——要是刷成"现在"，
        // 5900 只票的时间戳就全一样了，排序退化成原始顺序，于是每轮都从 000001 开始、
        // 靠后的票永远轮不到（2026-09-04 已经踩过一次同样形状的坑）。
        // 写成行情时间（如 09-04 15:34）之后，逐股抓过的票时间戳必然更新，
        // MAX(fetched_at) 就还能区分"这只票被逐股补过历史"和"只有快照带过一行"。
        if (snapshot.QuoteTime is { } quote)
            foreach (var r in snapshot.Rows) r.FetchedAt = quote;

        return snapshot;
    }

    /// <summary>
    /// 解析一页。解析不了返回 <c>null</c>——**不能返回空页**，理由见调用处。
    ///
    /// <paramref name="fetchedAt"/> 原样写进每行的 <c>FetchedAt</c>：整批用同一个时刻，
    /// "这批是什么时候拿的"才对得上。
    /// </summary>
    public static MoneyFlowSnapshotPage? ParsePage(string? body, DateTime fetchedAt)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return null; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
            // data:null 是"这一页超出范围了"的正常表现（也可能是限流）——交给调用方按 total 对账
            if (data.ValueKind != JsonValueKind.Object)
                return new MoneyFlowSnapshotPage(0, null, 0, []);

            int total = data.TryGetProperty("total", out var t) && t.TryGetInt32(out var tv) ? tv : 0;
            var rows = new List<NetInflowDetail>();
            int suspended = 0;
            DateTime? quoteTime = null;

            if (data.TryGetProperty("diff", out var diff) && diff.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in diff.EnumerateArray())
                {
                    var code = Code(item);
                    if (code == null) continue;

                    var ts = Timestamp(item);
                    if (ts is { } stamp && (quoteTime == null || stamp > quoteTime)) quoteTime = stamp;

                    // 停牌：数值字段全是字符串 "-"。主力净额缺就整行不要——剩下那几个单独存没有
                    // 意义，而写成 0 会变成一个看着很合理的假值。
                    var main = D(item, "f62");
                    if (main == null || ts == null) { suspended++; continue; }

                    rows.Add(new NetInflowDetail
                    {
                        Code = code,
                        TradeDate = ts.Value.Date,
                        MainNet = main,
                        SuperNet = D(item, "f66"),
                        BigNet = D(item, "f72"),
                        MidNet = D(item, "f78"),
                        SmallNet = D(item, "f84"),
                        MainRatio = D(item, "f184"),
                        SuperRatio = D(item, "f69"),
                        BigRatio = D(item, "f75"),
                        MidRatio = D(item, "f81"),
                        SmallRatio = D(item, "f87"),
                        ClosePrice = D(item, "f2"),
                        ChangeRate = D(item, "f3"),
                        FetchedAt = fetchedAt,
                    });
                }
            }

            return new MoneyFlowSnapshotPage(total, quoteTime, suspended, rows);
        }
    }

    /// <summary>f12：字符串和数字两种形态都出现过，数字形态会把 000001 的前导零吃掉。</summary>
    private static string? Code(JsonElement item)
    {
        if (!item.TryGetProperty("f12", out var f12)) return null;
        var c = f12.ValueKind == JsonValueKind.String ? f12.GetString() : f12.ToString();
        if (string.IsNullOrWhiteSpace(c)) return null;
        if (f12.ValueKind == JsonValueKind.Number && c!.Length < 6) c = c.PadLeft(6, '0');
        return c!.Length == 6 && c.All(char.IsDigit) ? c : null;
    }

    /// <summary>
    /// f124 = 行情时间戳（Unix 秒）。实测正常股 = 交易日 15:34（收盘清算后那一版）、
    /// 停牌股 = 当天 08:00。转成本机时间——程序跑在东八区，交易日历也是按东八区算的。
    /// </summary>
    private static DateTime? Timestamp(JsonElement item)
    {
        if (!item.TryGetProperty("f124", out var f) || f.ValueKind != JsonValueKind.Number) return null;
        if (!f.TryGetInt64(out var secs) || secs <= 0) return null;
        return DateTimeOffset.FromUnixTimeSeconds(secs).ToLocalTime().DateTime;
    }

    /// <summary>取一个数值字段；停牌时是字符串 <c>"-"</c>，那种一律当"没有"。</summary>
    private static double? D(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : null,
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float,
                                                    CultureInfo.InvariantCulture, out var s) ? s : null,
            _ => null,
        };
    }
}

/// <summary>一页 clist 响应的解析结果。</summary>
/// <param name="Total">服务端自报的匹配总数，用来对账翻页翻完没有。</param>
/// <param name="QuoteTime">这一页里最新的 f124。</param>
/// <param name="Suspended">这一页里没有资金流数据的（停牌等）。</param>
/// <param name="Rows">这一页解析出来的行。</param>
public sealed record MoneyFlowSnapshotPage(int Total, DateTime? QuoteTime, int Suspended,
                                             List<NetInflowDetail> Rows);

/// <summary>一整轮全市场快照。</summary>
/// <param name="QuoteTime">整批最新的行情时间戳——交易日和"收盘了没有"都从它推。</param>
/// <param name="Total">服务端自报的全市场只数。</param>
/// <param name="Suspended">停牌等没有数据的只数。</param>
/// <param name="Rows">全部有数据的行。</param>
public sealed record MoneyFlowSnapshot(DateTime? QuoteTime, int Total, int Suspended,
                                       List<NetInflowDetail> Rows)
{
    /// <summary>这批数据属于哪个交易日；一行都没拿到时是 null。</summary>
    public DateTime? TradeDate => QuoteTime?.Date;

    /// <summary>
    /// 还没收盘清算——这批是**半天的**数据，不能入库。
    /// 判据是行情时间戳的时刻早于 15:00：盘中它是当下的时间、午休停在 11:30，收盘后才跳到 15:3x。
    /// 非交易日跑的话它是**上一个交易日**的 15:3x，照样是终值、照样入库。
    /// </summary>
    public bool IsIntraday =>
        QuoteTime is { } q && q.TimeOfDay < EastMoneyMoneyFlowSnapshotProvider.SettlementTime;
}
