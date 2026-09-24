using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;
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
    private readonly IBrowserJsonFetcher? _browser;

    public event Action<string>? OnStatus;

    /// <summary>限流熔断还要等到几点；没在暂停就是 null。</summary>
    public DateTime? PausedUntil => _rateLimiter.PausedUntil;

    /// <param name="browser">
    /// 浏览器通道（2026-09-21 接上）。**东财给浏览器和给程序的待遇差 5 倍**，见
    /// <see cref="IBrowserJsonFetcher"/> 上那张实测表：同一 IP 同一接口，真浏览器 90% 成功、
    /// HttpClient 第 7 个请求就被切。差别在 TLS 指纹，.NET 改不了。
    ///
    /// 板块通道 2026-09-04 就换过去了，这一项当时没跟上——直到 09-21 晚上 HttpClient 这条
    /// 彻底取不到数（19:30 打到第 16 页被切、20:22 第 1 页就被切），而同一时刻
    /// 在 quote.eastmoney.com 页面里用 JSONP 照样连抓 7 页。更硬的证据是一条**全新的 4G 出口**
    /// （配额满的）跑 file:// 那个网页版一次都没成——所以不是出口 IP 的配额问题，
    /// 是请求形态：只有真从浏览器内核发出去的才认。
    ///
    /// 传 null 就还是老样子走 HttpClient（测试和非桌面场景）。
    /// </param>
    public EastMoneyMoneyFlowSnapshotProvider(RateLimiter rateLimiter, HttpClient? httpClient = null,
                                              string? host = null, string? bindNetworkInterface = null,
                                              IBrowserJsonFetcher? browser = null)
    {
        _rateLimiter = rateLimiter;
        _browser = browser;
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

    /// <summary>这一轮走的是哪条通道——浏览器还是 HttpClient。两者成败差一个数量级，
    /// 日志里不写清楚的话，"今天怎么又抓不到"根本无从查起。</summary>
    public string DescribeChannel() => _browser is { IsReady: true }
        ? $"浏览器通道（Edge 内核）→ {_host}"
        : $"HTTP 直连 {_host}" + (_browser == null ? "" : "（浏览器通道没就绪）");

    /// <summary>
    /// 浏览器通道准备好没。**要在真正开抓之前调一次**——初始化要几秒（建 WebView2 +
    /// 打开东财页面拿 Cookie 和同源环境），不该混在第一页的计时里。
    /// 用不了会自己退回 HttpClient，返回 false 只是告诉调用方"这轮走的是那条几乎抓不到的路"。
    /// </summary>
    public async Task<bool> PrepareAsync(CancellationToken ct = default)
    {
        if (_browser == null) return false;
        try
        {
            var ok = await _browser.EnsureReadyAsync(ct);
            // 通道起来就把观察窗打开：抓取期间人能看见东财那边在发生什么
            // （数据在刷？弹了图片验证码？页面打不开？），不用等日志报错再猜。
            if (ok) await _browser.ShowWorkWindowAsync(ct);
            return ok;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            OnStatus?.Invoke($"⚠ 浏览器通道初始化失败（{ex.Message}），退回 HttpClient。");
            return false;
        }
    }

    /// <summary>
    /// 取一页的原始 JSON。浏览器通道优先，没配/没就绪/它自己失败了才落回 HttpClient。
    ///
    /// ⚠ 落回不是"降级到慢一点"，是**降级到基本抓不到**（2026-09-21 实测：HttpClient 这条
    /// 在东财这边已经一个请求都过不去）。所以调用方看见 <see cref="DescribeChannel"/>
    /// 说"HTTP 直连"时，基本可以预期这一轮会失败——这句话就是拿来提前示警的。
    /// </summary>
    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        if (_browser is { IsReady: true })
        {
            var viaBrowser = await _browser.GetJsonAsync(url, ct);
            if (string.IsNullOrWhiteSpace(viaBrowser))
                throw new RateLimitedException($"东财 {_host} 经浏览器通道返回空（典型的限流表现）。");
            return viaBrowser;
        }
        return await _http.GetStringAsync(url, ct);
    }

    /// <summary>
    /// 把全市场当日的分档资金流拉全。约 60 页。
    ///
    /// 翻页翻到"服务端自报的 total 都拿到了"或"某一页空了"为止；页与页之间的节奏归
    /// <see cref="RateLimiter"/> 管。按代码排序（<c>fid=f12&amp;po=0</c>）而不是按涨跌幅——
    /// 跟板块成分股那边同一个道理：涨跌幅不是唯一键，并列项之间服务端不保证每次次序一样，
    /// 翻页就会跨页重复和遗漏。
    /// </summary>
    /// <summary>
    /// 一轮里连着多少页抓不到就收手（2026-09-21）。
    ///
    /// 东财被切之后是**整条出口被切**，不是这一页碰巧不行——接着往下打只会白扔请求、
    /// 还可能把封禁拖得更长。两页足够分辨："偶尔一页超时"和"被切了"在第二页就分得开。
    /// </summary>
    private const int GiveUpAfterConsecutiveCuts = 2;

    /// <summary>
    /// 把全市场当日的分档资金流拉全。约 60 页。
    ///
    /// ════ 抓到哪算哪，不再整轮丢弃（2026-09-21 改）════
    /// 原来任一页失败就整轮抛异常、一行都不落库，理由是"别把半个市场当成全市场"。
    /// 可东财的配额实测**一轮只放过约 16 页**（09-21 那晚 HttpClient 和浏览器通道都断在第 16~17 页），
    /// 于是那个设计的实际效果是：每轮抓 16 页、每轮全扔掉，**永远攒不满**。
    ///
    /// 现在改成：拿到的页照常返回，抓不到的页号记进 <see cref="MoneyFlowSnapshot.MissingPages"/>，
    /// 由调用方落库并记住进度，下一轮只补缺的页。
    /// "别把半个市场当成全市场"这件事没有放弃，只是换了个更靠得住的地方判——
    /// 由 <c>SqliteMoneyFlowDayAudit</c> 查库判"这天齐没齐"，那个判据不依赖某一轮跑成什么样。
    /// </summary>
    /// <param name="pagesAlreadyHave">
    /// 「这个交易日已经抓到手的页号」——跨轮续抓靠它跳过重复的页。
    ///
    /// ⚠ 是个**回调**而不是一个现成的集合，因为参数是交易日，而**交易日只有数据自己说了算**
    /// （f124 行情时间戳）。曾经想用本地交易日历先问一次：那样一旦日历滞后（还没更新到今天），
    /// 读到的就是昨天的进度，于是今天那些根本没抓过的页被当成"抓过了"跳掉——
    /// 静默丢一整片数据，事后完全看不出来。
    ///
    /// 所以第 1 页**一定会抓**，拿它的时间戳定交易日，再回调问这一天已经有哪些页。
    /// 代价是每轮重抓一页（配额约 16 页/轮里的 1 页），换的是"跳过的页确实是这一天抓过的"。
    /// </param>
    public async Task<MoneyFlowSnapshot> FetchAllAsync(
        IProgress<string>? progress = null,
        Func<DateTime, IReadOnlyCollection<int>>? pagesAlreadyHave = null,
        CancellationToken ct = default)
    {
        var run = new MoneyFlowSnapshotRun();
        await foreach (var _ in FetchPagesAsync(run, progress, pagesAlreadyHave, ct)) { }
        return run.ToSnapshot();
    }

    /// <summary>
    /// 跟 <see cref="FetchAllAsync"/> 同一套翻页，只是**每拿到一页就交出去一页**（2026-09-24）。
    ///
    /// 为什么要逐页交：跨轮续抓（09-21）之后，一轮里已经拿到的页是有效数据，可整轮攒在内存里
    /// 等最后一次性落库的话，中途一按停止就全扔了——09-24 16:05 那轮拿到约 30 页、
    /// 停止后一行都没进库，下一轮又从第 1 页抓起。逐页交出去，调用方每页落一次库、记一次进度，
    /// 停止时只丢正在抓的那一页。
    ///
    /// 翻页途中的累计状态（总数、停牌数、缺页、跳过页）写在 <paramref name="run"/> 里，
    /// 枚举正常走完后用 <see cref="MoneyFlowSnapshotRun.ToSnapshot"/> 拿整轮结果。
    ///
    /// 交出去的行已经按"目前为止最新的行情时间"剔掉了日期对不上的、并把 FetchedAt 写成行情时间
    /// （理由见 <see cref="MoneyFlowSnapshotRun.ToSnapshot"/>）。第 1 页一定最先抓，
    /// 它的时间戳就是收盘清算那一版，所以逐页判跟整批判结果一样。
    /// </summary>
    public async IAsyncEnumerable<MoneyFlowSnapshotPageRows> FetchPagesAsync(
        MoneyFlowSnapshotRun run,
        IProgress<string>? progress = null,
        Func<DateTime, IReadOnlyCollection<int>>? pagesAlreadyHave = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var fetchedAt = DateTime.Now;
        int consecutiveCuts = 0;

        // 总页数要等第一页回来才知道（服务端自报 total）。在那之前用保险丝当上界：
        // 翻页判据万一失灵也不会无限打下去。
        int lastPage = 200;

        for (int pn = 1; pn <= lastPage; pn++)
        {
            ct.ThrowIfCancellationRequested();
            if (run.Skip.Contains(pn)) continue;             // 别处/上一轮已经拿到了

            var url = $"https://{_host}/api/qt/clist/get?fid=f12&po=0&pz={PageSize}&pn={pn}"
                    + $"&np=1&fltt=2&invt=2&fs={MarketFilter}&fields={Fields}";

            MoneyFlowSnapshotPage? page = null;
            string? why = null;
            try
            {
                var body = await _rateLimiter.RunAsync(() => GetAsync(url, ct), ct);
                // 解析不了 ≠ 这一页是空的。当成空的会让我们提前收工、把半个市场当成全市场。
                page = ParsePage(body, fetchedAt);
                if (page == null) why = "空响应或被截断";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // 浏览器通道抛的不是 HttpRequestException（那是 WebView2 那边的异常），
                // 所以这里不按异常类型挑——挑了就会漏出去变成一个没有上下文的原始错误。
                why = ex.InnerException?.Message ?? ex.Message;
            }

            if (page == null)
            {
                run.Missing.Add(pn);
                consecutiveCuts++;
                progress?.Report($"　第 {pn} 页没拿到（{why}）。");
                if (consecutiveCuts >= GiveUpAfterConsecutiveCuts)
                {
                    // 剩下的页这一轮都不试了——被切之后接着打只是白扔请求。
                    //
                    // ⚠ total 还不知道时（第一页就被切）**不要把剩下的页号编出来**：那时 lastPage
                    //   还是保险丝的 200，会报出"缺 200 页"这种吓人又没意义的数字（实际只有 60 页）。
                    //   这一轮本来就一页都没拿到，缺多少页下一轮问服务端就知道了。
                    if (run.Total > 0)
                        for (int rest = pn + 1; rest <= lastPage; rest++)
                            if (!run.Skip.Contains(rest)) run.Missing.Add(rest);
                    progress?.Report($"连着 {consecutiveCuts} 页没拿到，这一轮到此为止"
                                   + $"（已拿 {run.PagesGot} 页 / {run.Rows.Count} 只，缺 {run.Missing.Count} 页）。");
                    break;
                }
                continue;
            }

            consecutiveCuts = 0;

            // 交易日定下来了（第一页回来那一刻）→ 这才问得了"这一天已经抓过哪些页"。
            // 只问一次：后面每页都问纯属浪费，而且中途换答案会让缺页清单前后不一致。
            if (run.QuoteTime == null && page.QuoteTime is { } first && pagesAlreadyHave != null)
            {
                run.Skip = pagesAlreadyHave(first.Date);
                if (run.Skip.Count > 0)
                    progress?.Report($"　{first:MM-dd} 上几轮已抓到 {run.Skip.Count} 页，这一轮跳过它们。");
            }

            if (page.Total > 0)
            {
                run.Total = page.Total;
                // 知道总数之后把上界收到真实页数——保险丝那 200 页只是兜底，
                // 拿它当"缺页清单"的上界会凭空多出 140 个根本不存在的页。
                lastPage = (int)Math.Ceiling(run.Total / (double)PageSize);
            }
            run.Suspended += page.Suspended;
            if (page.QuoteTime is { } qt && (run.QuoteTime == null || qt > run.QuoteTime)) run.QuoteTime = qt;

            int before = run.Rows.Count;
            foreach (var r in page.Rows) run.Rows[r.Code] = r;

            // 翻过头了：空 diff，或者**又把上一页还回来**（分页参数没被理会时就是这样，
            // 实测东财这类接口出过）。只看 Rows.Count==0 的话后一种会一路打到保险丝。
            if (run.Rows.Count == before && page.Suspended == 0) break;

            run.RowsByPage[pn] = page.Rows.Count;
            if (pn % 20 == 0)
                progress?.Report($"分档资金流快照：已拉 {run.PagesGot} 页、{run.Rows.Count} 只"
                               + (run.Total > 0 ? $"（全市场 {run.Total} 只）" : "") + "。");

            var kept = page.Rows;
            if (run.QuoteTime is { } quote)
            {
                kept = page.Rows.Where(r => r.TradeDate.Date == quote.Date).ToList();
                foreach (var r in kept) r.FetchedAt = quote;
            }
            yield return new MoneyFlowSnapshotPageRows(pn, page.Rows.Count, kept);
        }
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

/// <summary>逐页交出的一页：页号、这一页原始拿到几行（进度表记它）、剔过日期之后的行。</summary>
public sealed record MoneyFlowSnapshotPageRows(int PageNo, int RowsGot, List<NetInflowDetail> Rows);

/// <summary>
/// <see cref="EastMoneyMoneyFlowSnapshotProvider.FetchPagesAsync"/> 翻页途中的累计状态。
/// 逐页枚举带不出返回值，整轮的总数/缺页/跳过页只能放在这里。
/// </summary>
public sealed class MoneyFlowSnapshotRun
{
    public DateTime? QuoteTime { get; internal set; }
    public int Total { get; internal set; }
    public int Suspended { get; internal set; }
    internal Dictionary<string, NetInflowDetail> Rows { get; } = new(StringComparer.Ordinal);
    internal Dictionary<int, int> RowsByPage { get; } = [];
    internal List<int> Missing { get; } = [];
    internal IReadOnlyCollection<int> Skip { get; set; } = [];

    /// <summary>这一轮到目前为止拿到的页数。</summary>
    public int PagesGot => RowsByPage.Count;

    /// <summary>这批数据属于哪个交易日；一页都没拿到时是 null。</summary>
    public DateTime? TradeDate => QuoteTime?.Date;

    /// <summary>行情时间还没到收盘清算——同 <see cref="MoneyFlowSnapshot.IsIntraday"/>。</summary>
    public bool IsIntraday =>
        QuoteTime is { } q && q.TimeOfDay < EastMoneyMoneyFlowSnapshotProvider.SettlementTime;

    /// <summary>整轮结果。</summary>
    public MoneyFlowSnapshot ToSnapshot()
    {
        var snapshot = new MoneyFlowSnapshot(QuoteTime, Total, Suspended, Rows.Values.ToList())
        {
            RowsByPage = new Dictionary<int, int>(RowsByPage),
            MissingPages = Missing.ToList(),
            SkippedPages = Skip.Count,
        };

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
}

/// <summary>一整轮全市场快照。</summary>
/// <param name="QuoteTime">整批最新的行情时间戳——交易日和"收盘了没有"都从它推。</param>
/// <param name="Total">服务端自报的全市场只数。</param>
/// <param name="Suspended">停牌等没有数据的只数。</param>
/// <param name="Rows">全部有数据的行。</param>
public sealed record MoneyFlowSnapshot(DateTime? QuoteTime, int Total, int Suspended,
                                       List<NetInflowDetail> Rows)
{
    /// <summary>这一轮每一页拿到几行（页号→行数）。落库进度记的就是它的键。</summary>
    public IReadOnlyDictionary<int, int> RowsByPage { get; init; } = new Dictionary<int, int>();

    /// <summary>
    /// 这一轮没拿到的页号（2026-09-21）。
    ///
    /// 它不是"出错了"的意思，是**下一轮要补哪些页**——东财一轮只放过约 16 页，
    /// 缺页是常态。空了才说明这天抓全了。
    /// </summary>
    public IReadOnlyList<int> MissingPages { get; init; } = [];

    /// <summary>这一轮因为"上轮已经抓到"而跳过的页数，日志里要报——
    /// 否则"只拿了 16 页"跟"补完最后 16 页"看起来一模一样。</summary>
    public int SkippedPages { get; init; }

    /// <summary>这天抓全了没有（这一轮没有缺页）。</summary>
    public bool Complete => MissingPages.Count == 0;

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
