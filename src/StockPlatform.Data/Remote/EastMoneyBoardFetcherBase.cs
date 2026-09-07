using System.Text.Json;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 东财板块抓取的**业务逻辑**（2026-09-05 从 <see cref="EastMoneyBoardFetcher"/> 抽出来）——
/// URL 怎么拼、怎么翻页、拿到的名单怎么跟接口自报的 total 对账，全在这里，只有一份。
///
/// ════ 为什么要抽这一层 ════
/// 现在有两条取数通道并存，将来可能更多：
///   · <see cref="EastMoneyBoardFetcher"/>——WebView2 浏览器通道优先，HttpClient 回退；
///   · <see cref="EastMoneyBoardHttpFetcher"/>——纯 HttpClient。
/// 两者的**业务逻辑一模一样**，差别只在"怎么把一个 URL 变成一段 JSON"。要是各写一份，
/// 下面那几条用代价换来的规矩（f12 排序、total 对账、半截列表拒收）就会改一处漏一处——
/// 而它们漏掉的后果都是**静默的数据损坏**，不会报错。
///
/// 所以子类只需要实现 <see cref="GetAsync"/> 一个方法：给个 URL，还回一段 JSON。
///
/// ════ 两条必须守住的规矩（都是实测踩出来的）════
/// ① <b><c>fid</c> 必须用 <c>f12</c>（股票代码）排序，不能用 <c>f3</c>（涨跌幅）</b>——
///    涨跌幅盘中实时变动，翻页期间排序在动，会跨页重复和遗漏。实测用 f3 抓 141 只的板块，
///    两页 141 行去重后只剩 138 只。
/// ② <b>抓到的条数跟接口自报的 <c>total</c> 对不上就整批丢弃</b>。差一只都说明名单不完整
///    （分页丢了、或中途被限流截断）。宁可这一轮失败下轮重试，也不要把残缺名单写进库
///    当成完整的——上游是快照语义，"这轮没返回的＝已下架"会连成分股一起删。
///
/// ════ 成分股为什么必须走 clist 的官方名单 ════
/// 不能用 datacenter 的 <c>RPT_F10_CORETHEME_BOARDTYPE</c> 替代。2026-09-03 实测，代价很大
/// 所以写清楚：F10 报表看着很美（188 页拿全市场 9.4 万条归属关系，还绕开了限流最凶的 push2），
/// 但它是"个股的核心题材归属"而不是"板块的成分名单"，**会系统性漏股**——
///   · 液冷服务器 BK1138：官方 170 只，F10 只有 166 只，漏掉美的集团、江苏神通、锦富技术、拓普集团；
///   · PCB BK0877：官方 194 只，F10 漏 2 只。
/// 两次都是 F10 ⊂ 官方名单、多出 0 只，是系统性缺失不是随机噪声。漏掉的还都是链上有实际业务的
/// 大票，板块营收中位数这类指标会直接算错，而且**永远不会报错**。
///
/// ════ 板块列表这一步已经不走这儿了 ════
/// <see cref="FetchBoardListAsync"/> / <see cref="FetchBoardListPageAsync"/> 从 2026-09-05 起
/// 只是**回退路径**：主路改成了 <see cref="EastMoneySideMenuBoardListProvider"/>（行情中心
/// 左侧菜单那份静态 JSON，一个请求拿全量、不碰 push2、不弹验证）。这里留着是为了万一
/// 东财把那个文件挪走还能退回来抓。贵的是逐板块抓成分股——那一步现在打
/// <see cref="DefaultMemberHost"/>（pushguest），不再碰 push2。
/// **回退路径这两个方法还留在 push2 上**：pushguest 支不支持 <c>fs=m:90+t:2</c> 没验证过，
/// 而没验证的东西不该悄悄换上去；真要退回来用，那时再当场验。
/// </summary>
public abstract class EastMoneyBoardFetcherBase : IBoardFetcher
{
    /// <summary>每页上限。传更大的值会被服务端忽略。</summary>
    protected const int PageSize = 100;

    /// <summary>
    /// 成分股接口的默认域名（2026-09-05 从 <c>push2</c> 换成这个）。
    ///
    /// 行情中心的板块页 <c>gridlist.html#boards2-90.BKxxxx</c> **点翻页时打的就是它**，
    /// push2 只在首屏被打一次（顶部那张资金流小表）。同一个路径、同一套参数，
    /// 但走另一组前端（61.129.129.196，IIS），不在公司网关的域名拦截名单里。
    ///
    /// 2026-09-05 实测（浏览器直接导航到接口地址，5 个请求）：
    ///   · 不带 <c>cb</c> 回调参数 → 返回**纯 JSON**，结构跟 push2 完全一样（total + diff）；
    ///   · <c>pz=100</c> 生效（网页端自己锁死 20，我们不受这个限制）；
    ///   · <c>fid=f12&amp;po=0</c> 支持，返回严格按代码升序——翻页规矩照旧成立；
    ///   · <c>ut</c> / <c>wbp2u</c> / <c>dect</c> / <c>timil</c> / Cookie / Referer 一个都不用带。
    /// 逐条等价性：BK1629 三页 282 只，跟库里前一天 push2 抓的 282 只**完全一致**
    /// （无缺失、无多余、无跨页重复）；BK0877 报 198、两页 100+98 衔接无重叠。
    ///
    /// ⚠ 没验证的是**连续几百个请求会不会被限流**——很可能跟 push2 共用后端风控。
    /// 所以节流一点没放松，反而按"人在网页上翻页"的节奏又慢了一档，见
    /// <see cref="PauseBetweenBoardsAsync"/>。
    /// </summary>
    public const string DefaultMemberHost = "pushguest.eastmoney.com";

    /// <summary>成分股接口走哪个域名。可由 <c>fetcher-settings.json</c> 的 <c>BoardMemberHost</c> 覆盖。</summary>
    protected string MemberHost { get; }

    /// <summary>换一个板块之前歇多久的**基准值**（实际是它的 0.5~1.5 倍随机）。</summary>
    private readonly TimeSpan _boardSwitchPause;

    /// <summary>已经抓过至少一个板块了吗——第一个板块前面不用歇。用 int 是为了 Interlocked。</summary>
    private int _anyBoardDone;

    /// <summary>
    /// 板块列表最多翻这么多页。概念 + 行业各几百个，1200 的余量绰绰有余；真有一天超了，
    /// 下面那道对账会明确报出来（"翻到页数上限仍没取完"），不会静默截断成半截列表。
    /// </summary>
    private const int MaxListPages = 12;

    protected readonly RateLimiter Limiter;

    public event Action<string>? OnStatus;

    /// <summary>子类和内部发状态用——事件本身是 private 的，派生类碰不到。</summary>
    protected void Report(string message) => OnStatus?.Invoke(message);

    /// <param name="memberHost">
    /// 成分股接口的域名；留空＝<see cref="DefaultMemberHost"/>。
    /// 留这个口子是为了出事能一行配置退回 <c>push2.eastmoney.com</c>，不用改代码重新发布。
    /// </param>
    /// <param name="boardSwitchPause">
    /// **换板块**时额外歇多久（默认 8 秒，实际按 0.5~1.5 倍随机）。测试传 <c>TimeSpan.Zero</c> 关掉。
    /// 为什么单独有这么一档，见 <see cref="PauseBetweenBoardsAsync"/>。
    /// </param>
    protected EastMoneyBoardFetcherBase(RateLimiter limiter, string? memberHost = null,
                                        TimeSpan? boardSwitchPause = null)
    {
        Limiter = limiter;
        MemberHost = string.IsNullOrWhiteSpace(memberHost) ? DefaultMemberHost : memberHost.Trim();
        _boardSwitchPause = boardSwitchPause ?? TimeSpan.FromSeconds(8);
        Limiter.OnStatus += s => OnStatus?.Invoke(s);
    }

    /// <summary>
    /// 取一个 URL 的 JSON——**子类之间唯一的差别就在这儿**。
    /// 被限流/被拒时要抛 <see cref="RateLimitedException"/>，上游据此判断是"限流"而不是"没数据"。
    /// </summary>
    protected abstract Task<string> GetAsync(string url, CancellationToken ct);

    /// <summary>这一轮实际走的是哪条路，给日志用。</summary>
    public abstract string DescribeChannel();

    /// <summary>
    /// 开抓之前的准备（建浏览器、拿 Cookie 之类）。**要在真正开抓之前调一次**，
    /// 别把那几秒混进第一个请求的计时里。纯 HttpClient 的实现不需要准备，返回 false。
    /// </summary>
    public virtual Task<bool> PrepareAsync(CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>当前走哪块网卡、有没有配对。调用方要在订阅 OnStatus 之后自己打进日志。</summary>
    public virtual string DescribeBinding() => NetworkInterfaceBinder.Describe(null);

    /// <summary>
    /// 走 push2 的通道一律 7 天：一轮三小时起、还随时被限流，宁可让数据陈一周也不重抓。
    /// <see cref="EastMoneyTerminalBoardFetcher"/> 覆盖成 0——它读本地文件，没有节流的理由。
    /// </summary>
    public virtual TimeSpan MemberFreshFor => TimeSpan.FromDays(7);

    /// <summary>
    /// 限流熔断还要等到几点；没在暂停就是 null。
    /// 用来在暂停期里直接回绝新的抓取——实测暂停期内点【执行】会干等 8 分钟才报失败，
    /// 那 8 分钟既没数据也看不出在等什么。
    /// </summary>
    public DateTime? PausedUntil => Limiter.PausedUntil;

    // ─────────────── 板块列表（回退路径，主路是菜单 JSON）───────────────

    private static string ListUrl(BoardType type, int page)
    {
        // 东财的板块类型编号：t:1=地域 t:2=行业 t:3=概念。
        // ⚠ 别写成 `Concept ? 3 : 2` —— 加了地域之后那样会把地域当成行业去抓。
        int t = type switch
        {
            BoardType.Concept => 3,
            BoardType.Industry => 2,
            BoardType.Region => 1,
            _ => throw new NotSupportedException($"push2 没有 {type} 这一类板块的 fs 参数。"),
        };
        return "https://push2.eastmoney.com/api/qt/clist/get"
             + $"?pn={page}&pz={PageSize}&po=0&np=1&fltt=2&invt=2&fid=f12&fs=m:90+t:{t}"
             + "&fields=f3,f6,f12,f14,f128,f140";
    }

    private static string Label(BoardType type) => type.Label();

    /// <summary>
    /// 抓**某一页**板块列表（2026-09-04 加，给页级断点续传用）。
    ///
    /// 为什么要能单独抓一页：push2 限流下一轮往往抓到第 5 页就被拒，而原来是整类作废、
    /// 下轮从第 1 页重来——于是每轮白烧 5 页配额、再在同一个地方被拒，永远到不了第 6 页。
    /// 拆到页粒度之后，调用方抓一页存一页、记下"下次从第几页接着来"。
    /// </summary>
    /// <returns>这一页的板块、接口自报的总数、这一页是不是最后一页。</returns>
    public async Task<(List<Board> Items, int Total, bool IsLastPage)> FetchBoardListPageAsync(
        BoardType type, int page, CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var body = await Limiter.RunAsync(() => GetAsync(ListUrl(type, page), ct), ct);

        var items = new List<Board>();
        int total = 0;

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            // data 为 null 是限流的另一种表现（返回合法 JSON 但没内容）。
            // 第 1 页就这样＝这一轮什么都没拿到，得让调用方知道是限流而不是"没有板块了"。
            if (page == 1)
                throw new RateLimitedException(
                    $"东财{Label(type)}板块列表第 1 页没有内容——多半是被限流了（合法 JSON 但 data 为空）。");
            return (items, 0, true);
        }
        if (data.TryGetProperty("total", out var tot) && tot.TryGetInt32(out var tv)) total = tv;
        if (!data.TryGetProperty("diff", out var diff) || diff.ValueKind != JsonValueKind.Array)
            return (items, total, true);

        foreach (var item in diff.EnumerateArray())
        {
            var board = ToBoard(item, type, now);
            if (board != null) items.Add(board);
        }

        // 这一页不满就是最后一页了
        return (items, total, items.Count < PageSize);
    }

    /// <summary>
    /// 板块列表（含涨跌幅/成交额/领涨股）。t:3=概念 t:2=行业，每页 100，约 5 页。
    /// </summary>
    public async Task<List<Board>> FetchBoardListAsync(BoardType type, CancellationToken ct = default)
    {
        var label = Label(type);
        var result = new List<Board>();
        var now = DateTime.Now;
        int total = 0;

        for (int page = 1; page <= MaxListPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var body = await Limiter.RunAsync(() => GetAsync(ListUrl(type, page), ct), ct);

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object) break;
            if (!data.TryGetProperty("diff", out var diff) || diff.ValueKind != JsonValueKind.Array) break;
            if (data.TryGetProperty("total", out var tot) && tot.TryGetInt32(out var tv)) total = tv;

            int n = 0;
            foreach (var item in diff.EnumerateArray())
            {
                var board = ToBoard(item, type, now);
                if (board == null) continue;
                result.Add(board);
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
        // 半截是怎么来的：限流最常见的表现是断连或空响应，那两种 GetAsync 已经抛异常了；
        // 但它也会返回**合法 JSON 而 data 为 null**，那条路上面的循环只能 break，然后拿着前几页
        // 就走到这里。判据是：**差一个都算不完整**。
        if (total > 0 && deduped.Count != total)
        {
            throw new RateLimitedException(result.Count >= MaxListPages * PageSize
                ? $"东财{label}板块列表翻到页数上限（{MaxListPages} 页 × {PageSize} 条）仍没取完："
                  + $"接口报 {total} 个、只取到 {deduped.Count} 个。这不是限流，是 MaxListPages 该调大了。"
                : $"东财{label}板块列表不完整：接口报 {total} 个，实际只取到 {deduped.Count} 个"
                  + "（多半是翻页中途被限流——除了断连，也会返回合法 JSON 但 data 为空）。"
                  + "本轮不更新板块，库里保留上次的完整快照，下轮重试。");
        }

        Report($"东财{label}板块 {deduped.Count} 个（接口报 {total} 个）");
        return deduped;
    }

    private static Board? ToBoard(JsonElement item, BoardType type, DateTime now)
    {
        var code = Str(item, "f12");
        if (string.IsNullOrEmpty(code)) return null;
        return new Board
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
        };
    }

    // ─────────────── 成分股（整轮里最贵的一步：一个板块一到几个请求）───────────────

    /// <summary>
    /// 某个板块的官方成分股名单。按代码排序翻页——**不能按涨跌幅排序**，那会跨页重复/遗漏。
    /// 返回的名单会跟接口报的 total 对账，对不上就抛异常（宁可这个板块本轮失败、下轮重试，
    /// 也不要把一份残缺名单写进库当成完整的）。
    /// </summary>
    public virtual async Task<List<string>> FetchMembersAsync(string boardCode, CancellationToken ct = default)
    {
        var codes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int total = 0;

        await PauseBetweenBoardsAsync(ct);

        for (int page = 1; page <= 20; page++)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"https://{MemberHost}/api/qt/clist/get" +
                      $"?pn={page}&pz={PageSize}&po=0&np=1&fltt=2&invt=2&fid=f12&fs=b:{boardCode}" +
                      "&fields=f12,f14";
            var body = await Limiter.RunAsync(() => GetAsync(url, ct), ct);

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

        return ReconcileMembers(boardCode, codes, total);
    }

    /// <summary>
    /// 去重 + 跟接口自报的 <paramref name="total"/> 对账。**差一只都算不完整**，直接抛。
    ///
    /// 为什么单独抽出来（2026-09-05）：现在有两条形态完全不同的取数路——自己拼 URL 翻页的
    /// <see cref="FetchMembersAsync"/>，和操作页面翻页的 <c>EastMoneyBoardPageFetcher</c>。
    /// 它们怎么拿数据毫不相干，但**这道对账必须一模一样**：上游 UpsertBoards 是快照语义，
    /// "这轮没返回的＝已下架"会把板块连同 BoardMember 一起删，一份半截名单就能悄悄删掉
    /// 几百只成分股、而且全程不报错。共用一份，就不会出现"改了一处漏了另一处"。
    /// </summary>
    protected static List<string> ReconcileMembers(
        string boardCode, IReadOnlyList<string> codes, int total)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<string>();
        foreach (var c in codes)
            if (!string.IsNullOrWhiteSpace(c) && seen.Add(c)) deduped.Add(c);

        if (total > 0 && deduped.Count != total)
            throw new RateLimitedException(
                $"板块 {boardCode} 成分股不完整：接口报 {total} 只，实际取到 {deduped.Count} 只。本轮不写入，下轮重试。");

        return deduped;
    }

    /// <summary>
    /// **换板块之前**多歇一会儿（2026-09-05 加）——把请求节奏做成"人在网页上翻页"的样子。
    ///
    /// 为什么光有 <see cref="RateLimiter"/> 的固定间隔不够：真人翻页的节奏是**不均匀**的。
    /// 同一个板块里连点几次下一页很快（几秒一次），但换一个板块要回菜单、重新点开、
    /// 等首屏——中间那一下明显更长。而我们原来是从头到尾一个匀速间隔，
    /// 上千个请求排成一条完全等距的队列，恰恰是机器行为里最好认的特征。
    ///
    /// 所以分两档：页与页之间由 RateLimiter 管（带 ±30% 抖动），板块与板块之间再加这一档。
    /// 代价是一轮多花半小时上下，换来的是节奏上没有明显的机器特征——
    /// 而 <see cref="DefaultMemberHost"/> 那个新域名会不会限流还没验证过，这时候宁可慢。
    ///
    /// ⚠ **不模拟首屏那次 push2 请求**：真人打开板块页时，浏览器会先打一次 push2 的资金流
    /// 小表再打成分股列表。照抄的话等于白白往 push2 上加一倍请求——而躲开 push2 正是
    /// 换域名的目的。两个域名各算各的账，不会因为"少了那一次"露馅。
    /// 同理 <c>pz=100</c> 也没改回网页端的 20：那会让请求数直接乘以 5，
    /// 为了"更像人"把请求数翻五倍是笔亏本买卖。
    /// </summary>
    protected async Task PauseBetweenBoardsAsync(CancellationToken ct)
    {
        // 第一个板块前面不用歇：这时候还没发过请求，歇了只是让人干等
        if (Interlocked.Exchange(ref _anyBoardDone, 1) == 0) return;
        if (_boardSwitchPause <= TimeSpan.Zero) return;

        var factor = 0.5 + Random.Shared.NextDouble();      // 0.5~1.5 倍，别每次都是同一个数
        await Task.Delay(_boardSwitchPause * factor, ct);
    }

    // ─────────────── JSON 取值：同一个字段有时是数字有时是字符串 ───────────────

    protected static string Str(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            _ => "",
        };
    }

    protected static double Num(JsonElement el, string prop)
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
