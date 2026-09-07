using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using StockPlatform.Data.Remote.Cdp;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 用**真实的 Chrome/Edge** 操作行情中心的板块页来取成分股（2026-09-06）——
/// <see cref="IBoardMemberPageScraper"/> 的实现，通过 CDP（Chrome DevTools Protocol）驱动。
///
/// ════ 为什么从 WebView2 换到真浏览器 ════
/// 2026-09-06 同一时刻、同一台机器、同一个 IP 的对照：
///
/// | 环境 | 板块页 BK1673 |
/// |---|---|
/// | WebView2（Edge 内核，嵌在程序里） | 表头「代码」找不到、`.qtpager` 不存在＝**列表没渲染** |
/// | 用户手动开的 Edge | 正常 |
/// | 全新未登录 profile 的真 Chrome（CDP 探针实测） | `codeHeader=true, pager=true`＝**正常** |
///
/// 也就是说 WebView2 里那张表就是渲染不出来，而真浏览器没问题。具体差在哪没再深究——
/// 换过来是几百行、且现有的选择器一个字都不用改，比继续查 WebView2 划算。
///
/// ════ 为什么不用 Playwright ════
/// 只需要四件事：导航、执行 JS、监听请求、取响应体，CDP 直连就够（见 <see cref="CdpConnection"/>），
/// **一个 NuGet 都不用加**。Playwright 要背几十 MB 的 Node 驱动，而且它默认带
/// <c>--enable-automation</c>，页面里 <c>navigator.webdriver</c> 会变成 true——
/// 那是反爬最容易认出来的标记。我们这样启动的浏览器不带这个标记。
///
/// ════ 原则：页面自己怎么行为就怎么行为 ════
/// 我们**一个 URL 都不拼**，只做三个动作：开板块页、点表头「代码」、点「下一页」，
/// 然后把页面自己发出去的请求的响应截下来。它顺带还打一次 push2 的资金流小表也好、
/// <c>pz</c> 被锁在 20 也好，都不干预——一改就不是"页面自己发的请求"了，
/// 而那正是唯一能稳定拿到 200 的东西（导航到接口 URL 会 503，页面点页码是 200）。
/// </summary>
public sealed class ChromeCdpBoardPageScraper : IBoardMemberPageScraper, IAsyncDisposable
{
    /// <summary>成分股接口的路径特征，用来从页面发的一堆请求里挑出我们要的。</summary>
    private const string MemberApiPath = "/api/qt/clist/get";

    /// <summary>
    /// 行情中心列表页，**不带 hash**——整个抓取过程只导航到它这一次，之后全靠切 hash。
    ///
    /// ⚠ 不带 hash 是关键（2026-09-06）：这个页面是单页应用，靠 <c>hashchange</c> 驱动。
    /// 原来每个板块都 <c>Page.navigate</c> 到带 hash 的地址，等于整个文档重新加载，
    /// 而首次加载时 hash **已经在地址里**、hashchange 根本不会触发——页面于是压根不去要数据，
    /// 表格永远空着（日志里就是"一个成分股请求都没发出去"）。
    /// 先落在不带 hash 的地址上，再切 hash，页面才会像人点菜单那样去取数。
    /// </summary>
    private const string BaseListPageUrl = "https://quote.eastmoney.com/center/gridlist.html";

    /// <summary>表格最多等多久渲染出来。等不到就换下一个板块，不在这儿耗着。</summary>
    private static readonly TimeSpan TableWait = TimeSpan.FromSeconds(20);

    /// <summary>一页数据最多等多久。</summary>
    private static readonly TimeSpan PageWait = TimeSpan.FromSeconds(25);

    /// <summary>翻页之间歇多久（实际是它的 0.5~1.5 倍）。故意比机器能达到的慢得多——见类注释里的时间账。</summary>
    private static readonly TimeSpan BetweenPages = TimeSpan.FromSeconds(4);

    /// <summary>
    /// 一段最多连着翻多少页（2026-09-06）。
    ///
    /// 网页端**连续翻页到第 30 页就到头了**（600 只），再往后要登录——实测 811 只的板块
    /// 每次都停在 600。但把页面重开一次、重新点代码排序、用「转到」直接跳到第 31 页，
    /// 就能接着往下取：那 30 页限制的是"一次会话里连着翻"，不是这个板块只能看 600 只。
    /// 所以大板块按 30 页一段：每满一段就重开、跳到下一段起点。
    /// </summary>
    private const int PagesPerSegment = 30;

    /// <summary>一个板块最多翻多少页，防失控。200 页 × 20＝4000 只，比任何板块都宽裕。</summary>
    private const int MaxPages = 200;

    /// <summary>
    /// 等人过图片验证最多等多久。**必须有上限**：无人值守跑一整夜时人不在电脑前，
    /// 没有上限就是挂到天亮、一个板块都不再抓。超时就放弃这个板块交给下轮重试——
    /// 而且验证是全站的，换下一个多半也过不去，会很快连续失败到熔断阈值、本轮干净收尾。
    /// </summary>
    private static readonly TimeSpan ChallengeWait = TimeSpan.FromMinutes(30);

    private readonly string _userDataDir;
    private readonly string? _browserPathOverride;
    private readonly int _port;

    /// <inheritdoc/>
    public event Action<string>? OnStatus;

    /// <summary>发状态：转成事件，由 <c>EastMoneyBoardPageFetcher</c> 接到程序日志上。</summary>
    private void Log(string message) => OnStatus?.Invoke(message);

    private Process? _browser;
    private CdpConnection? _cdp;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initTried;

    // 当前板块期间截到的页数据，以及页面发的那些请求各回了什么状态码
    private readonly System.Collections.Concurrent.ConcurrentQueue<MemberPage> _pages = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _requestUrls = new();
    private readonly List<int> _statuses = [];
    private volatile string _boardCode = "";

    // 验证码频率统计：光知道"又弹了"没用，要判断能不能无人值守跑一夜，得知道
    // 多久弹一次、中间能抓多少个板块、人过完之后能撑多久。
    private int _challengeCount;
    private DateTime? _lastChallengeAt;
    private int _boardsSinceChallenge;

    public bool IsReady { get; private set; }

    /// <param name="userDataDir">
    /// 浏览器的 profile 目录（Cookie、登录态存这儿）。
    /// ⚠ **必须是我们自己的目录，不能指向你日常那个 Chrome 的 profile**——
    /// 日常浏览器开着时那个目录被锁，会直接启动失败。代价是登录要在这个 profile 里单独登一次。
    /// </param>
    /// <param name="browserPath">浏览器 exe 路径；留空＝自动找 Chrome，找不到再找 Edge。</param>
    /// <param name="port">CDP 调试端口。</param>
    public ChromeCdpBoardPageScraper(string userDataDir, string? browserPath = null, int port = 9333)
    {
        _userDataDir = userDataDir;
        _browserPathOverride = browserPath;
        _port = port;
    }

    private sealed record MemberPage(int Total, List<string> Codes, string Url);

    // ─────────────── 启动与连接 ───────────────

    public async Task<bool> EnsureReadyAsync(CancellationToken ct = default)
    {
        if (IsReady) return true;
        await _initLock.WaitAsync(ct);
        try
        {
            if (IsReady) return true;
            if (_initTried) return false;      // 试过一次不行就别反复折腾，每次都要几秒
            _initTried = true;
            return await StartAsync(ct);
        }
        finally { _initLock.Release(); }
    }

    private async Task<bool> StartAsync(CancellationToken ct)
    {
        try
        {
            var exe = _browserPathOverride ?? FindBrowser();
            if (exe == null)
            {
                Log("⚠ 没找到 Chrome 或 Edge，页面通道用不了。");
                return false;
            }

            Directory.CreateDirectory(_userDataDir);

            // 参数说明：
            //   · 不加 --headless：无头浏览器渲染管线不一样，而且更容易被认出来，我们要的就是"跟人开的一样"；
            //   · 不加 --enable-automation / --disable-blink-features：前者会把 navigator.webdriver 置为 true，
            //     后者是给"被 Playwright 之类标记过"的场景擦屁股的，我们本来就没有那个标记，加了反而多余；
            //   · --no-first-run / --no-default-browser-check：新 profile 第一次启动别弹欢迎页和"设为默认浏览器"。
            var args = string.Join(' ',
                $"--remote-debugging-port={_port}",
                $"--user-data-dir=\"{_userDataDir}\"",
                "--no-first-run",
                "--no-default-browser-check",
                "about:blank");

            _browser = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
            if (_browser == null) { Log("⚠ 浏览器启动失败。"); return false; }

            var wsUrl = await WaitForPageTargetAsync(ct);
            if (wsUrl == null)
            {
                Log($"⚠ 浏览器起来了但调试端口 {_port} 连不上，页面通道用不了。");
                return false;
            }

            _cdp = new CdpConnection();
            await _cdp.ConnectAsync(wsUrl, ct);
            await _cdp.SendAsync("Page.enable", ct: ct);
            await _cdp.SendAsync("Network.enable", ct: ct);

            _cdp.On("Network.responseReceived", OnResponseReceived);
            _cdp.On("Network.loadingFinished", OnLoadingFinished);

            // 先把行情中心开出来（这一次是真导航），之后每个板块都只切 hash，不再重载文档
            await NavigateAsync(_cdp, BaseListPageUrl, ct);

            IsReady = true;
            Log($"页面通道已就绪：{Path.GetFileName(exe)}（调试端口 {_port}，profile 在 {_userDataDir}）。");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"⚠ 页面通道启动失败（{ex.GetType().Name}：{ex.Message}）。");
            return false;
        }
    }

    /// <summary>常见安装位置里找 Chrome，找不到再找 Edge。</summary>
    private static string? FindBrowser()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>轮询调试端口，等浏览器起来并给出一个页面 target。</summary>
    private async Task<string?> WaitForPageTargetAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var targets = await http.GetFromJsonAsync<List<CdpTarget>>(
                    $"http://127.0.0.1:{_port}/json/list", ct);
                var page = targets?.FirstOrDefault(t => t.type == "page" && !string.IsNullOrEmpty(t.webSocketDebuggerUrl));
                if (page != null) return page.webSocketDebuggerUrl;
            }
            catch { /* 还没起来，接着等 */ }
            await Task.Delay(500, ct);
        }
        return null;
    }

    private sealed class CdpTarget
    {
        public string? type { get; set; }
        public string? url { get; set; }
        public string? webSocketDebuggerUrl { get; set; }
    }

    // ─────────────── 截页面自己发的请求 ───────────────

    /// <summary>
    /// 响应头到了：先记下 requestId→URL 和状态码。
    /// **这时候还不能取响应体**——body 可能还没下载完，要等 loadingFinished。
    /// </summary>
    private void OnResponseReceived(JsonElement p)
    {
        try
        {
            if (!p.TryGetProperty("response", out var resp)) return;
            var url = resp.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            if (!IsMemberRequest(url)) return;

            if (p.TryGetProperty("requestId", out var rid) && rid.GetString() is { } id)
                _requestUrls[id] = url;

            if (resp.TryGetProperty("status", out var st) && st.TryGetInt32(out var code))
                lock (_statuses) _statuses.Add(code);
        }
        catch { /* 单条事件出错不能影响抓取 */ }
    }

    /// <summary>
    /// 响应体下载完了：这时候才能 <c>Network.getResponseBody</c>。
    /// ⚠ 必须扔到 Task.Run 里发命令——事件回调跑在 CDP 的读循环线程上，
    /// 在里面等另一条 CDP 命令的回执就是自己等自己，会把整条连接堵死。
    /// </summary>
    private void OnLoadingFinished(JsonElement p)
    {
        try
        {
            if (!p.TryGetProperty("requestId", out var rid)) return;
            var id = rid.GetString();
            if (id == null || !_requestUrls.TryRemove(id, out var url)) return;

            var cdp = _cdp;
            if (cdp == null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var res = await cdp.SendAsync("Network.getResponseBody", new { requestId = id });
                    if (!res.TryGetProperty("body", out var b)) return;
                    var body = b.GetString();
                    var parsed = EastMoneyClistPage.Parse(body);
                    if (parsed != null)
                        _pages.Enqueue(new MemberPage(parsed.Value.Total, parsed.Value.Codes, url));
                }
                catch { /* 单页取不到：total 对账会拦住，不在这儿把整轮搞挂 */ }
            });
        }
        catch { }
    }

    /// <summary>
    /// 是不是当前板块的成分股请求。页面还会打顶部那张资金流小表（fields 里带 f62、pz=5），
    /// 那个是 push2 的、只有 5 条，跟我们无关。
    /// </summary>
    private bool IsMemberRequest(string url)
    {
        if (!url.Contains(MemberApiPath, StringComparison.OrdinalIgnoreCase)) return false;
        var code = _boardCode;
        if (code.Length > 0 &&
            !url.Contains($"fs=b%3A{code}", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains($"fs=b:{code}", StringComparison.OrdinalIgnoreCase)) return false;
        return !url.Contains("f62", StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────── 抓一个板块 ───────────────

    public async Task<(IReadOnlyList<string> Codes, int Total)> ScrapeMembersAsync(
        string boardCode, CancellationToken ct = default)
    {
        if (!await EnsureReadyAsync(ct))
            throw new InvalidOperationException("页面通道没就绪（浏览器没起来），抓不了成分股。");

        var cdp = _cdp!;
        Interlocked.Increment(ref _boardsSinceChallenge);
        _boardCode = boardCode;
        _pages.Clear();
        _requestUrls.Clear();
        lock (_statuses) _statuses.Clear();

        // ── ① 切到这个板块（点菜单，不重载文档）──
        await SwitchToBoardAsync(cdp, boardCode, ct);

        // ── ② 等表格渲染出来，再点表头「代码」把排序换成 f12 ──
        // 判据是表头在不在：它既是"表格好了没"的标志，又正好是要点的那个东西，
        // 它在＝点必中，它不在＝点多少次都白点。
        if (!await WaitForCodeHeaderAsync(cdp, boardCode, ct))
            throw new InvalidOperationException(
                $"板块 {boardCode} 的表格没渲染出来（等了 {TableWait.TotalSeconds:0} 秒 × 3 轮），跳过换下一个。"
                + $"{DescribeStatuses()}页面当时是这样：{await cdp.EvaluateAsync(BoardPageScripts.DescribePage, ct)}");

        _pages.Clear();
        if (await cdp.EvaluateAsync(BoardPageScripts.ClickCodeHeader, ct) != "ok")
            throw new InvalidOperationException($"板块 {boardCode} 表头「代码」明明在、却点不动——页面结构可能改了。");

        var first = await WaitForPageAsync(p => p.Url.Contains("fid=f12"), ct)
            ?? throw new InvalidOperationException(
                $"板块 {boardCode} 点完表头没等到按代码排序的数据。{DescribeStatuses()}");

        int total = first.Total;
        var codes = new List<string>(first.Codes);
        if (total <= 0 && codes.Count == 0) return (codes, 0);      // 空板块，正常收尾

        // ── ③ 一页一页点「下一页」，每满 30 页重开一次页面、跳到下一段 ──
        int page = 1;                                   // 手上已经有第 1 页了
        while ((total <= 0 || codes.Count < total) && page < MaxPages)
        {
            ct.ThrowIfCancellationRequested();
            await DelayJitteredAsync(BetweenPages, ct);

            MemberPage? next;
            if (page % PagesPerSegment == 0)
            {
                // 这一段翻满了。连着翻下去会撞上登录墙（只能到第 30 页），
                // 所以重开页面、重新点代码排序、用「转到」跳到下一段的第一页。
                next = await JumpToNextSegmentAsync(cdp, boardCode, page + 1, ct);
                if (next == null)
                {
                    Log($"板块 {boardCode} 翻到第 {page} 页后跳不过去了（{DescribeStatuses()}），"
                      + $"已取 {codes.Count} 只、接口报 {total} 只。");
                    break;
                }
            }
            else
            {
                next = await ClickAndWaitAsync(cdp, BoardPageScripts.ClickNextPage, _ => true, ct);
                if (next == null) break;                // 没有下一页了，或重试几次仍没等到
            }

            if (next.Total > 0) total = next.Total;

            int before = codes.Count;
            codes.AddRange(next.Codes);
            page++;
            if (codes.Count == before) break;           // 一页都没新增＝页面没真翻动，别空转
        }

        return (codes, total);
    }

    /// <summary>
    /// 表格渲染不出来时：**先去别的列表转一圈，再回到这个板块**（2026-09-06 用户实测有效）。
    ///
    /// 为什么不是原地重开：板块页是靠 URL 里的 hash（<c>#boards2-90.BKxxxx</c>）驱动的单页应用。
    /// 地址没变的话，页面认为"还是这个板块"，该初始化的东西不再初始化，那张表就一直空着——
    /// 日志里的表现就是"期间页面一个成分股请求都没发出去"。先导到另一个列表页
    /// （等于人点了下左边菜单）再导回来，中间那一下会把状态清干净。
    ///
    /// ⚠ 只在**没弹验证码**的情况下用。刚过完验证时人往往正自己在页面上点，
    /// 程序这时候导航会把人做的事冲掉。
    /// </summary>
    private async Task DetourAndReturnAsync(CdpConnection cdp, string boardCode, CancellationToken ct)
    {
        Log($"板块 {boardCode} 的表格没出来（页面没去要数据），先去别的列表转一圈再回来。");

        // 沪深京 A 股——行情中心里最普通的一个列表，等价于人点了下左边菜单
        await cdp.EvaluateAsync(BoardPageScripts.SwitchToOtherList, ct);
        await Task.Delay(2000, ct);
        await SwitchToBoardAsync(cdp, boardCode, ct);
    }

    /// <summary>
    /// 跳到下一段的第一页：重开板块页 → 等表格 → 重新点代码排序 → 用「转到」跳页。
    ///
    /// 三步一步都不能省：
    ///   · 重开页面——不重开的话连续翻页的限制还在，第 31 页取不到；
    ///   · 重新点代码排序——刷新后页面回到默认的 <c>fid=f3</c>（涨跌幅），
    ///     不点回来就等于换了排序键接着翻，会跨页重复和遗漏；
    ///   · 跳页——直接落到这一段的第一页，中间不重复取。
    /// </summary>
    private async Task<MemberPage?> JumpToNextSegmentAsync(
        CdpConnection cdp, string boardCode, int targetPage, CancellationToken ct)
    {
        Log($"板块 {boardCode} 已翻满一段（{targetPage - 1} 页），重开页面跳到第 {targetPage} 页接着取。");

        await DetourAndReturnAsync(cdp, boardCode, ct);
        if (!await WaitForCodeHeaderAsync(cdp, boardCode, ct)) return null;

        _pages.Clear();
        if (await cdp.EvaluateAsync(BoardPageScripts.ClickCodeHeader, ct) != "ok") return null;
        if (await WaitForPageAsync(p => p.Url.Contains("fid=f12"), ct) == null) return null;

        await DelayJitteredAsync(BetweenPages, ct);
        _pages.Clear();
        if (await cdp.EvaluateAsync(BoardPageScripts.GotoPage(targetPage), ct) != "ok") return null;

        // 只认这一段起始页那一条：跳页时页面可能还会补发别的请求，收错了会漏数据
        return await WaitForPageAsync(p => p.Url.Contains($"pn={targetPage}&"), ct);
    }

    /// <summary>
    /// 切到某个板块——**这是"打开一个板块"的唯一入口**，全程不重载文档。
    ///
    /// 文档要是被导走了（页面崩了、跳到别的站），先把行情中心重新开出来再切，
    /// 否则后面所有脚本都跑在一张不认识的页面上。
    /// </summary>
    private async Task SwitchToBoardAsync(CdpConnection cdp, string boardCode, CancellationToken ct)
    {
        var href = await cdp.EvaluateAsync("location.href", ct);
        if (!href.Contains("gridlist.html", StringComparison.OrdinalIgnoreCase))
            await NavigateAsync(cdp, BaseListPageUrl, ct);

        _pages.Clear();
        await cdp.EvaluateAsync(BoardPageScripts.SwitchToBoard(boardCode), ct);
        await Task.Delay(800, ct);      // 给 hashchange 一点时间去发请求
    }

    /// <summary>导航并等页面加载完。</summary>
    private async Task NavigateAsync(CdpConnection cdp, string url, CancellationToken ct)
    {
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoad(JsonElement _) => loaded.TrySetResult(true);
        cdp.On("Page.loadEventFired", OnLoad);

        await cdp.SendAsync("Page.navigate", new { url }, ct);
        try { await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); }
        catch (TimeoutException) { /* 加载慢也接着往下走，下面等表头那步会兜住 */ }

        await Task.Delay(800, ct);
    }

    /// <summary>
    /// 等表头「代码」出现＝等表格渲染好。等的过程里盯着验证浮层：一出现就停下等人过，
    /// 过完**重新开一次板块页**（验证期间接口被拦、表格加载不出来，不重开永远等不到），
    /// 然后重新计时。最多重开 2 次。
    /// </summary>
    private async Task<bool> WaitForCodeHeaderAsync(CdpConnection cdp, string boardCode, CancellationToken ct)
    {
        for (int reopen = 0; reopen <= 2; reopen++)
        {
            bool sawChallenge = false;
            var deadline = DateTime.UtcNow + TableWait;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (await cdp.EvaluateAsync(BoardPageScripts.HasCodeHeader, ct) == "yes") return true;

                if (await LooksLikeChallengeAsync(cdp, ct))
                {
                    sawChallenge = true;
                    await WaitOutChallengeAsync(cdp, ct);
                    break;                       // 过完验证跳出去重来，别在空页面上继续等
                }

                await Task.Delay(500, ct);
            }

            if (reopen >= 2) break;

            // 不管刚才是不是弹过验证，都用同一招：去别的列表转一圈再切回来。
            // 验证过完页面往往停在没数据的状态，跟"路由没触发"是一样的处置
            // （2026-09-06 用户要求：验证码人只管过，绕路交给程序，不用再留时间等人手工点）。
            await DetourAndReturnAsync(cdp, boardCode, ct);
        }
        return false;
    }

    /// <summary>点一下、等这一页回来；没回来就再点一次（503 是间歇的，重发多半就好）。</summary>
    private async Task<MemberPage?> ClickAndWaitAsync(
        CdpConnection cdp, string clickJs, Func<MemberPage, bool> accept, CancellationToken ct)
    {
        TimeSpan[] backoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(12)];
        for (int attempt = 0; attempt <= backoff.Length; attempt++)
        {
            if (attempt > 0)
            {
                Log($"这一页没回来（多半是 503 抖动），{backoff[attempt - 1].TotalSeconds:0} 秒后再点一次。");
                await Task.Delay(backoff[attempt - 1], ct);
            }

            _pages.Clear();
            if (await cdp.EvaluateAsync(clickJs, ct) != "ok") return null;   // 找不到可点的＝到底了

            var page = await WaitForPageAsync(accept, ct);
            if (page != null) return page;
        }
        return null;
    }

    /// <summary>等页面把我们要的那一页送回来；等的过程里盯着验证浮层。</summary>
    private async Task<MemberPage?> WaitForPageAsync(Func<MemberPage, bool> accept, CancellationToken ct)
    {
        var cdp = _cdp!;
        var deadline = DateTime.UtcNow + PageWait;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            while (_pages.TryDequeue(out var page))
                if (accept(page)) return page;

            if (await LooksLikeChallengeAsync(cdp, ct))
            {
                await WaitOutChallengeAsync(cdp, ct);
                deadline = DateTime.UtcNow + PageWait;      // 人过完验证，重新给足等待时间
                continue;
            }

            await Task.Delay(300, ct);
        }
        return null;
    }

    // ─────────────── 图片验证 ───────────────

    private static async Task<bool> LooksLikeChallengeAsync(CdpConnection cdp, CancellationToken ct)
    {
        try { return (await cdp.EvaluateAsync(BoardPageScripts.HasChallenge, ct)).StartsWith("yes"); }
        catch (OperationCanceledException) { throw; }
        catch { return false; }      // 判不出来就当不是，别让抓取卡在这儿
    }

    /// <summary>
    /// 验证出现了：停下等人过，靠**验证图从页面上消失**判断过没过（用户给的判据，最直接）。
    /// ⚠ 程序只识别它在不在，**不去解那个验证码**——解码得人来。
    /// </summary>
    private async Task WaitOutChallengeAsync(CdpConnection cdp, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _challengeCount);
        var boards = Interlocked.Exchange(ref _boardsSinceChallenge, 0);
        var sinceLast = _lastChallengeAt is { } prev
            ? $"，距上次 {(DateTime.Now - prev).TotalMinutes:F1} 分钟、中间抓了 {boards} 个板块"
            : $"，本轮开跑后抓了 {boards} 个板块";
        _lastChallengeAt = DateTime.Now;

        Log($"━━━━━━━━━━ 东财弹了图片验证（本轮第 {n} 次{sinceLast}）━━━━━━━━━━");
        Log("在浏览器窗口里点完验证即可，验证图一消失就自动接着抓，不用重新点【执行】。");
        try { await cdp.SendAsync("Page.bringToFront", ct: ct); } catch { }

        var since = DateTime.UtcNow;
        var deadline = since + ChallengeWait;
        var nextReport = TimeSpan.FromMinutes(1);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(2000, ct);

            if (!await LooksLikeChallengeAsync(cdp, ct))
            {
                _lastChallengeAt = DateTime.Now;      // 从"过完"开始计时，下次才算得出撑了多久
                // 过完验证不用留时间给人手工绕路了——绕路那一步（去别的列表转一圈再回来）
                // 现在程序自己会做，人只管过验证码（2026-09-06 用户要求）。
                Log($"验证已通过（验证图消失，等了 {(DateTime.UtcNow - since).TotalMinutes:F1} 分钟），继续抓。");
                return;
            }

            var waited = DateTime.UtcNow - since;
            if (waited >= nextReport)
            {
                Log($"仍在等人过图片验证（已等 {waited.TotalMinutes:F0} 分钟，最多等 {ChallengeWait.TotalMinutes:0} 分钟）——不是卡死。");
                nextReport = waited + TimeSpan.FromMinutes(5);
            }
        }
        Log($"等了 {ChallengeWait.TotalMinutes:0} 分钟没人来过验证，这个板块先放弃，交给下轮重试。");
    }

    // ─────────────── 杂项 ───────────────

    /// <summary>"一个请求都没发"和"发了全是 503"是两回事，光看页面分不出来。</summary>
    private string DescribeStatuses()
    {
        int[] codes;
        lock (_statuses) codes = [.. _statuses];
        return codes.Length == 0
            ? "期间页面一个成分股请求都没发出去（不是被拒，是压根没发）。"
            : $"期间页面发了 {codes.Length} 个成分股请求，状态码：{string.Join("、", codes)}。";
    }

    private static async Task DelayJitteredAsync(TimeSpan baseDelay, CancellationToken ct)
    {
        var factor = 0.5 + Random.Shared.NextDouble();
        await Task.Delay(baseDelay * factor, ct);
    }

    /// <summary>浏览器窗口本来就开着（不是无头），把它提到前面来就行。</summary>
    public async Task ShowWorkWindowAsync(CancellationToken ct = default)
    {
        if (_cdp is { } cdp)
        {
            try { await cdp.SendAsync("Page.bringToFront", ct: ct); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cdp != null) await _cdp.DisposeAsync();
        try
        {
            if (_browser is { HasExited: false })
            {
                _browser.Kill(entireProcessTree: true);
                _browser.WaitForExit(3000);
            }
        }
        catch { }
        _browser?.Dispose();
        _initLock.Dispose();
    }
}
