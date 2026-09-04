using System.Net;
using System.Net.Http;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 东方财富的板块抓取。替代 <see cref="SinaBoardFetcher"/>——新浪那套概念分类严重老化：
/// 175 个概念板块里**没有**存储芯片、算力、液冷、AI芯片、CPO、先进封装、人形机器人，
/// 占位的却是"融资融券""社保重仓""成渝特区"这类根本不是产业链的东西。东财有 1000+ 个板块，
/// 上面这些主题一个不缺。
///
/// <b>成分股必须走 push2 的官方成分名单（<c>fs=b:BKxxxx</c>），不能用 datacenter 的
/// <c>RPT_F10_CORETHEME_BOARDTYPE</c> 替代。</b>这一条是 2026-09-03 实测出来的，代价很大所以写清楚：
/// F10 报表看起来很美（188 页拿全市场 9.4 万条归属关系，还绕开了限流最凶的 push2），但它是
/// "个股的核心题材归属"而不是"板块的成分名单"，**会系统性漏股**——
///   · 液冷服务器 BK1138：官方 170 只，F10 只有 166 只，漏掉美的集团、江苏神通、锦富技术、拓普集团；
///   · PCB BK0877：官方 194 只，F10 漏 2 只。
/// 两次都是 F10 ⊂ 官方名单、多出 0 只，是系统性缺失不是随机噪声。漏掉的还都是链上有实际业务的
/// 大票，板块营收中位数这类指标会直接算错，而且**永远不会报错**。
///
/// 代价是慢：1031 个板块 × 1~3 页 ≈ 2500 个请求，而 push2 限流极敏感（连续请求十几次就会被
/// 拒绝连接，恢复要按小时算，浏览器过一次人工验证可按 IP 放行一段时间）。所以这个抓取
/// **设计成跑不完也没关系**：编排层逐个板块落库并记进度，下一轮自动跳过已成功的（见
/// <c>BoardMemberFetchState</c> 表）。宁可跨几轮抓完，也不要用会漏股的数据凑数。
///
/// 分页有个坑：<c>fid</c> 必须用 <c>f12</c>（股票代码）排序，**不能用 <c>f3</c>（涨跌幅）**——
/// 涨跌幅盘中实时变动，翻页期间排序在动，会跨页重复和遗漏。实测用 f3 抓 141 只的板块，
/// 两页 141 行里去重后只剩 138 只。
/// </summary>
public class EastMoneyBoardFetcher : IBoardFetcher
{
    private const int PageSize = 100;      // push2 每页上限

    /// <summary>
    /// 板块列表最多翻这么多页。概念 + 行业各几百个，1200 的余量绰绰有余；真有一天超了，
    /// 下面那道对账会明确报出来（"翻到页数上限仍没取完"），不会静默截断成半截列表。
    /// </summary>
    private const int MaxListPages = 12;
    private readonly HttpClient _http;
    private readonly RateLimiter _limiter;

    public event Action<string>? OnStatus;

    private readonly string? _bindNetworkInterface;
    private readonly IBrowserJsonFetcher? _browser;

    /// <summary>
    /// 浏览器通道（如果配了）准备好没。**要在真正开抓之前调一次**——初始化要几秒
    /// （建 WebView2 + 打开东财页面拿 Cookie），不该混在第一个请求的计时里。
    /// 用不了会自己退回 HttpClient，返回 false 只是告诉调用方"这轮走的是慢路"。
    /// </summary>
    public async Task<bool> PrepareBrowserAsync(CancellationToken ct = default)
    {
        if (_browser == null) return false;
        try
        {
            var ok = await _browser.EnsureReadyAsync(ct);
            // 通道起来之后把观察窗打开：抓取期间人能看见东财那边在发生什么
            // （数据在刷？弹了图片验证码？页面打不开？），不用等日志报错再猜。
            if (ok) await _browser.ShowWorkWindowAsync(ct);
            return ok;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            OnStatus?.Invoke($"⚠ 浏览器通道初始化失败（{ex.Message}），退回普通 HTTP 抓取。");
            return false;
        }
    }

    /// <summary>这一轮实际走的是哪条路，给日志用。</summary>
    public string DescribeChannel() => _browser is { IsReady: true }
        ? "push2 走浏览器通道（Edge 内核）"
        : "push2 走普通 HTTP 抓取" + (_browser == null ? "" : "（浏览器通道没就绪）");

    /// <summary>
    /// 当前走哪块网卡、有没有配对。**调用方要在订阅 OnStatus 之后自己打进日志**——
    /// 这段话原来是在构造函数里发的，那时候还没人订阅，消息就丢了，
    /// 结果配了网卡也看不出有没有生效（2026-09-04 踩过）。
    /// </summary>
    public string DescribeBinding() => NetworkInterfaceBinder.Describe(_bindNetworkInterface);

    /// <summary>
    /// 限流熔断还要等到几点；没在暂停就是 null。
    /// 用来在暂停期里直接回绝新的抓取——实测暂停期内点【执行】会干等 8 分钟才报失败，
    /// 那 8 分钟既没数据也看不出在等什么。
    /// </summary>
    public DateTime? PausedUntil => _limiter.PausedUntil;

    /// <param name="bindNetworkInterface">
    /// 把 push2 的请求钉在这块网卡上出去（2026-09-04），填网卡名如 "Wi-Fi"；留空＝走默认路由。
    ///
    /// 为什么单给 push2 开这个口子：本机有线接的是公司网，网关按域名把 push2 拦了
    /// （TCP 和 TLS 都通、一发 HTTP 请求就被切断，0 字节），而 datacenter 那边一直正常。
    /// 换一条没限制的链路（另一个 Wi-Fi、或手机热点）就能通，见 <see cref="NetworkInterfaceBinder"/>。
    /// </param>
    /// <param name="browser">
    /// 浏览器通道（2026-09-04）。给了就优先走它，没给或没就绪就退回 HttpClient。
    ///
    /// 为什么要它：实测同一 IP、同一接口、相近时间，**浏览器 90% 成功、27 个/分钟，
    /// 而 HttpClient 第 7 个请求就被切、折算只有 4.8 个/分钟**。请求头、Cookie、连接复用、
    /// HTTP/2 都单独排除过，差别在 TLS 指纹——Chrome 的 ClientHello 跟 .NET 的 Schannel 不同，
    /// 而 .NET 改不了这个。详见 <see cref="IBrowserJsonFetcher"/>。
    /// </param>
    public EastMoneyBoardFetcher(RateLimiter limiter, HttpClient? httpClient = null,
                                 string? bindNetworkInterface = null,
                                 IBrowserJsonFetcher? browser = null)
    {
        _browser = browser;
        _limiter = limiter;
        _limiter.OnStatus += s => OnStatus?.Invoke(s);
        _http = httpClient ?? new HttpClient(
            NetworkInterfaceBinder.CreateHandler(bindNetworkInterface, s => OnStatus?.Invoke(s)));
        _bindNetworkInterface = bindNetworkInterface;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/");
        _http.Timeout = TimeSpan.FromSeconds(25);
    }

    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    /// <summary>
    /// 抓**某一页**板块列表（2026-09-04 加，给页级断点续传用）。
    ///
    /// 为什么要能单独抓一页：push2 限流下一轮往往抓到第 5 页就被拒，而原来是整类作废、
    /// 下轮从第 1 页重来——于是每轮白烧 5 页配额、再在同一个地方被拒，永远到不了第 6 页。
    /// 拆到页粒度之后，调用方抓一页存一页、记下"下次从第几页接着来"，配额不再浪费在
    /// 已经拿到手的那几页上。
    /// </summary>
    /// <returns>这一页的板块、接口自报的总数、这一页是不是最后一页。</returns>
    public async Task<(List<Board> Items, int Total, bool IsLastPage)> FetchBoardListPageAsync(
        BoardType type, int page, CancellationToken ct = default)
    {
        int t = type == BoardType.Concept ? 3 : 2;
        var label = type == BoardType.Concept ? "概念" : "行业";
        var now = DateTime.Now;

        var url = "https://push2.eastmoney.com/api/qt/clist/get" +
                  $"?pn={page}&pz={PageSize}&po=0&np=1&fltt=2&invt=2&fid=f12&fs=m:90+t:{t}" +
                  "&fields=f3,f6,f12,f14,f128,f140";
        var body = await _limiter.RunAsync(() => GetAsync(url, ct), ct);

        var items = new List<Board>();
        int total = 0;

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            // data 为 null 是 push2 限流的另一种表现（返回合法 JSON 但没内容）。
            // 第 1 页就这样＝这一轮什么都没拿到，得让调用方知道是限流而不是"没有板块了"。
            if (page == 1)
                throw new RateLimitedException(
                    $"东财{label}板块列表第 1 页没有内容——多半是被限流了（合法 JSON 但 data 为空）。");
            return (items, 0, true);
        }
        if (data.TryGetProperty("total", out var tot) && tot.TryGetInt32(out var tv)) total = tv;
        if (!data.TryGetProperty("diff", out var diff) || diff.ValueKind != JsonValueKind.Array)
            return (items, total, true);

        foreach (var item in diff.EnumerateArray())
        {
            var code = Str(item, "f12");
            if (string.IsNullOrEmpty(code)) continue;
            items.Add(new Board
            {
                BoardCode = code,
                Type = type,
                Name = Str(item, "f14"),
                MemberCount = 0,                 // 由成分股抓取时按实际名单写
                ChangePct = Num(item, "f3"),
                Amount = Num(item, "f6"),
                LeaderCode = Str(item, "f140"),
                LeaderName = Str(item, "f128"),
                AsOf = now,
            });
        }

        // 这一页不满就是最后一页了
        return (items, total, items.Count < PageSize);
    }

    /// <summary>
    /// 板块列表（含涨跌幅/成交额/领涨股）。t:3=概念 t:2=行业，每页 100，约 5 页。
    /// 这一步便宜（10 个请求），贵的是后面逐个板块抓成分股。
    /// </summary>
    public async Task<List<Board>> FetchBoardListAsync(BoardType type, CancellationToken ct = default)
    {
        int t = type == BoardType.Concept ? 3 : 2;
        var label = type == BoardType.Concept ? "概念" : "行业";
        var result = new List<Board>();
        var now = DateTime.Now;
        int total = 0;

        for (int page = 1; page <= MaxListPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var url = "https://push2.eastmoney.com/api/qt/clist/get" +
                      $"?pn={page}&pz={PageSize}&po=0&np=1&fltt=2&invt=2&fid=f12&fs=m:90+t:{t}" +
                      "&fields=f3,f6,f12,f14,f128,f140";
            var body = await _limiter.RunAsync(() => GetAsync(url, ct), ct);

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object) break;
            if (!data.TryGetProperty("diff", out var diff) || diff.ValueKind != JsonValueKind.Array) break;
            if (data.TryGetProperty("total", out var tot) && tot.TryGetInt32(out var tv)) total = tv;

            int n = 0;
            foreach (var item in diff.EnumerateArray())
            {
                var code = Str(item, "f12");
                if (string.IsNullOrEmpty(code)) continue;
                result.Add(new Board
                {
                    BoardCode = code,
                    Type = type,
                    Name = Str(item, "f14"),
                    MemberCount = 0,                 // 由成分股抓取时按实际名单写
                    ChangePct = Num(item, "f3"),
                    Amount = Num(item, "f6"),
                    LeaderCode = Str(item, "f140"),
                    LeaderName = Str(item, "f128"),
                    AsOf = now,
                });
                n++;
            }
            if (n < PageSize || (total > 0 && result.Count >= total)) break;
        }

        if (result.Count == 0)
            throw new RateLimitedException($"东财{label}板块列表返回 0 个——多半是被限流了，本轮不更新板块。");

        // 去重：翻页期间理论上不会变（按代码排序），但真出现重复也不能让它进库
        var deduped = result.GroupBy(b => b.BoardCode).Select(g => g.First()).ToList();

        // ── 收尾对账：条数跟接口自报的 total 对不上，就当**半截列表**处理，整轮放弃、不写库 ──
        //
        // 为什么这道检查必须有（2026-09-04）：上游 UpsertBoards 的语义是"这一轮没返回的板块
        // ＝已下架"，会把它们连同 BoardMember、BoardMemberFetchState 一起删掉。而成分股是
        // 逐板块抓的、约 2500 个请求、跨好几轮才攒得齐——一次半截的列表就能删掉几百个板块的
        // 成分股，重抓要好几天，而且**全程不报错**。
        //
        // 半截是怎么来的：push2 限流最常见的表现是断连或空响应，那两种 GetAsync 已经抛异常了；
        // 但它也会返回**合法 JSON 而 data 为 null**，那条路上面的循环只能 break，然后拿着前几页
        // 就走到这里。上游原本只挡了"一个都没有"，前几页有货的半截列表照样会写进库。
        // 盘中跑更容易撞上——那会儿 push2 限流更紧。
        //
        // 判据跟成分股那边一致：**差一个都算不完整**。宁可本轮不更新（库里留着上次的完整快照，
        // 日志有明确原因），也不要把残缺列表当成"市场现状"写进去。
        if (total > 0 && deduped.Count != total)
        {
            throw new RateLimitedException(result.Count >= MaxListPages * PageSize
                ? $"东财{label}板块列表翻到页数上限（{MaxListPages} 页 × {PageSize} 条）仍没取完："
                  + $"接口报 {total} 个、只取到 {deduped.Count} 个。这不是限流，是 MaxListPages 该调大了。"
                : $"东财{label}板块列表不完整：接口报 {total} 个，实际只取到 {deduped.Count} 个"
                  + "（多半是翻页中途被限流——push2 除了断连，也会返回合法 JSON 但 data 为空）。"
                  + "本轮不更新板块，库里保留上次的完整快照，下轮重试。");
        }

        OnStatus?.Invoke($"东财{label}板块 {deduped.Count} 个（接口报 {total} 个）");
        return deduped;
    }

    /// <summary>
    /// 某个板块的官方成分股名单。按代码排序翻页——**不能按涨跌幅排序**，那会跨页重复/遗漏。
    /// 返回的名单会跟接口报的 total 对账，对不上就抛异常（宁可这个板块本轮失败、下轮重试，
    /// 也不要把一份残缺名单写进库当成完整的）。
    /// </summary>
    public async Task<List<string>> FetchMembersAsync(string boardCode, CancellationToken ct = default)
    {
        var codes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int total = 0;

        for (int page = 1; page <= 20; page++)
        {
            ct.ThrowIfCancellationRequested();
            var url = "https://push2.eastmoney.com/api/qt/clist/get" +
                      $"?pn={page}&pz={PageSize}&po=0&np=1&fltt=2&invt=2&fid=f12&fs=b:{boardCode}" +
                      "&fields=f12,f14";
            var body = await _limiter.RunAsync(() => GetAsync(url, ct), ct);

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object) break;      // 空板块，正常收尾
            if (!data.TryGetProperty("diff", out var diff) || diff.ValueKind != JsonValueKind.Array) break;
            if (data.TryGetProperty("total", out var tot) && tot.TryGetInt32(out var tv)) total = tv;

            int n = 0;
            foreach (var item in diff.EnumerateArray())
            {
                var c = Str(item, "f12");
                if (c.Length > 0 && seen.Add(c)) codes.Add(c);
                n++;
            }
            if (n < PageSize || (total > 0 && codes.Count >= total)) break;
        }

        // 跟接口自报的 total 对账。差一只都说明这份名单不完整——分页丢了、或者中途被限流截断。
        if (total > 0 && codes.Count != total)
            throw new RateLimitedException(
                $"板块 {boardCode} 成分股不完整：接口报 {total} 只，实际取到 {codes.Count} 只。本轮不写入，下轮重试。");

        return codes;
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        // 浏览器通道优先：同一 IP 同一接口，它 90% 成功而 HttpClient 第 7 个就被切（见构造函数注释）。
        // 没配、没就绪、或者它自己失败了，都落回下面的 HttpClient——慢，但至少能抓。
        if (_browser is { IsReady: true })
        {
            try
            {
                var viaBrowser = await _browser.GetJsonAsync(url, ct);
                if (!string.IsNullOrWhiteSpace(viaBrowser)) return viaBrowser;
                throw new RateLimitedException("东财 push2 经浏览器通道返回空（典型的限流表现）。");
            }
            catch (RateLimitedException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                throw new RateLimitedException(
                    $"东财 push2 经浏览器通道取数失败：{ex.Message}", ex);
            }
        }

        try
        {
            var body = await _http.GetStringAsync(url, ct);
            if (string.IsNullOrWhiteSpace(body))
                throw new RateLimitedException("东财 push2 返回空响应（典型的限流表现）。");
            return body;
        }
        catch (RateLimitedException) { throw; }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException)
                                   && !ct.IsCancellationRequested)
        {
            // push2 限流时不是返回 HTTP 错误码，而是直接断开连接
            throw new RateLimitedException(
                $"东财 push2 连接被断开：{ex.InnerException?.Message ?? ex.Message}"
                + "（push2 限流很敏感，可在浏览器访问一次 quote.eastmoney.com 过人工验证后重试）", ex);
        }
    }

    private static string Str(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            _ => "",
        };
    }

    private static double Num(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : 0,
            JsonValueKind.String => double.TryParse(v.GetString(), out var d2) ? d2 : 0,
            _ => 0,
        };
    }
}
