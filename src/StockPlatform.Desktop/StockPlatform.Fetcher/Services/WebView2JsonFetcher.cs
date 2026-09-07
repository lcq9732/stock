using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Fetcher.Services;

/// <summary>
/// 用隐藏的 WebView2（Edge/Chromium 内核）去取东财 JSON —— <see cref="IBrowserJsonFetcher"/> 的实现。
///
/// 为什么非要浏览器内核不可，见接口上那张对照表：同一 IP、同一接口、相近时间，
/// 浏览器 90% 成功、27 个/分钟，程序的 HttpClient 第 7 个请求就被切。
/// 请求头、Cookie、连接复用、HTTP/2 都单独排除过了，差别在 TLS 指纹，而 .NET 改不了。
///
/// 几个关键设计：
///   · <b>直接导航到接口地址取数</b>——就是人在地址栏里敲那个 URL 的做法，而那个是确认能通的。
///     先前绕过远路：在 quote 页面里注入脚本、用 JSONP 打 push2，凭空多出跨域、回调参数、
///     脚本注入这些变量，最后全失败（报 err＝请求根本没被受理）。导航取数没有这些东西，
///     Cookie、TLS 指纹、请求头全是浏览器自己那一套，跟人工访问没有任何区别。
///   · <b>验证由程序自己扛</b>——东财这类拦截多半是 JS 挑战：浏览器加载页面、执行完 JS
///     就自动拿到通行 Cookie。所以拿回来的不像数据时，等几秒让 JS 跑完再发一次，
///     不用人去点。见 <see cref="FetchWithAutoVerifyAsync"/>。
///   · <b>整个生命周期钉在 UI 线程</b>——WebView2 的对象只能在创建它的线程上碰。
///     抓取跑在后台线程，所以每次调用都要 Dispatcher 切过去。
///   · <b>用不了就说清楚、让调用方回退</b>——没装运行时、初始化失败、页面打不开，
///     一律返回 false 而不是抛异常。宁可慢点用 HttpClient，也不能整项抓不了。
/// </summary>
public sealed class WebView2JsonFetcher : IBrowserJsonFetcher
{
    /// <summary>
    /// seed 页和探针用哪个板块（2026-09-05）。
    ///
    /// 具体是哪个板块**不重要**，重要的是这类页面（板块成分股页）自己打的就是 pushguest；
    /// 行情中心的默认页（沪深京 A 股个股列表）打的是 push2，那是完全另一个域名。
    /// 万一这个板块哪天下架，页面会退回默认列表——那时候换一个代码即可，
    /// 挑个不会消失的大板块就行。BK1265 是当初逐条比对验证过的那个。
    /// </summary>
    private const string SeedBoardCode = "BK1265";

    /// <summary>
    /// 先打开这个页面：拿到 eastmoney 的同源环境和服务器种的 Cookie。
    ///
    /// ⚠ **必须是板块成分股页，不能是行情中心默认页**（2026-09-05 改，之前就是错的）。
    /// 默认页是沪深京 A 股个股列表，它自己轮询刷新报价、打的全是 <c>push2</c>——
    /// 挂在那儿等于我们一边费劲躲开 push2，一边让 seed 页替我们不停地打它，还是同一个 IP。
    /// 而板块页（<c>#boards2-90.BKxxxx</c>）点翻页时打的正是
    /// <see cref="MemberHost"/>（pushguest），跟我们真正要发的请求是同一个域名、同一套上下文。
    /// </summary>
    private static string SeedPage =>
        $"https://quote.eastmoney.com/center/gridlist.html#boards2-90.{SeedBoardCode}";

    /// <summary>
    /// **我们真正要打的那个域名下**的一个真实请求，人工验证要在这个页面上过（2026-09-04）。
    ///
    /// 为什么不能只开 quote 首页：Cookie 是按域名走的。quote.eastmoney.com 种的 Cookie
    /// 不会跟着发到取数域名（除非它显式种在 .eastmoney.com 上）。
    /// 实测浏览器通道被拒时报的是 err（请求根本没被受理），不是 timeout（拿回了验证页），
    /// 符合"这个域名压根不认你"的表现。
    ///
    /// 直接开 API URL 而不是某个人看的页面：浏览器会把返回内容原样显示出来，
    /// 是 JSON 就说明通了，是验证页就当场能点——一个页面同时当验证入口和探针。
    ///
    /// ⚠ **跟着 <see cref="MemberHost"/> 走，不能写死**（2026-09-05）：域名现在是可配置的
    /// （<c>fetcher-settings.json</c> 的 <c>BoardMemberHost</c>），探针写死的话就会出现
    /// "配置退回 push2、探针却在探 pushguest"这种对不上——而且这种错不会报，
    /// 只表现为"预热说没事、真抓全失败"。
    /// </summary>
    private string ProbePage => BuildProbePage(MemberHost, SeedBoardCode);

    /// <summary>探针 URL 的拼法，单独抽出来是为了能测。用的是**成分股**形态（<c>fs=b:</c>），
    /// 跟正式取数一模一样——探一个跟实际不同形态的请求，探通了也说明不了什么。</summary>
    internal static string BuildProbePage(string host, string boardCode) =>
        $"https://{host}/api/qt/clist/get"
        + $"?pn=1&pz=20&po=0&np=1&fltt=2&invt=2&fid=f12&fs=b:{boardCode}&fields=f12,f14";

    /// <summary>
    /// 取数打哪个域名——决定探针探谁，也决定日志里写谁。默认
    /// <see cref="StockPlatform.Data.Remote.EastMoneyBoardFetcherBase.DefaultMemberHost"/>。
    ///
    /// 可写是因为它会随【重新读取配置】变（见 App.CreateBoardFetcher）：浏览器通道本身
    /// **不重建**（重建等于把攒下的 Cookie 和"熟面孔"身份丢掉），所以只能把新值塞进来。
    /// </summary>
    public string MemberHost { get; set; } =
        StockPlatform.Data.Remote.EastMoneyBoardFetcherBase.DefaultMemberHost;

    private readonly Dispatcher _ui;
    private readonly string _userDataFolder;
    private readonly Action<string>? _log;

    private Window? _host;
    private WebView2? _view;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<JsonpResult>> _pending = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initTried;

    /// <summary>
    /// 承载窗口被真正 Close 掉了吗（2026-09-05）。WPF 的 Window 一旦 Closed 就作废——
    /// 再 Show() 直接抛 InvalidOperationException，表现是【东财验证】从此再也打不开。
    /// 正常路径上 <see cref="HideInsteadOfClose"/> 会拦下关闭，这个标志只在出意外时置位，
    /// 供 <see cref="ShowForManualVerificationAsync"/> 认出来并整个重建通道。
    /// </summary>
    private volatile bool _hostClosed;

    /// <summary>
    /// 人工验证的"我弄好了"信号：验证窗口被关掉时置位（2026-09-04）。
    /// 抓取线程自动过不去时会打开窗口、然后等在这上面，人关掉窗口就接着跑——
    /// 不用回去重新点执行、也不用重排队。
    /// </summary>
    private TaskCompletionSource<bool>? _manualVerifyDone;

    /// <summary>人工验证最多等多久。人不在电脑前的话不能永远挂着，超时就当这一轮失败、下轮再来。</summary>
    private static readonly TimeSpan ManualVerifyTimeout = TimeSpan.FromMinutes(15);

    public bool IsReady { get; private set; }

    /// <param name="userDataFolder">
    /// WebView2 的用户数据目录（Cookie 存这儿）。放 publish/data 下面，跟别的运行时数据一起，
    /// 这样 Cookie 能跨次启动留着——省得每次开程序都被当成生面孔。
    /// </param>
    public WebView2JsonFetcher(string userDataFolder, Action<string>? log = null)
    {
        _ui = System.Windows.Application.Current?.Dispatcher
              ?? throw new InvalidOperationException("WebView2JsonFetcher 只能在 WPF 应用里用");
        _userDataFolder = userDataFolder;
        _log = log;
    }

    public async Task<bool> EnsureReadyAsync(CancellationToken ct = default)
    {
        if (IsReady) return true;
        await _initLock.WaitAsync(ct);
        try
        {
            if (IsReady) return true;
            if (_initTried) return false;      // 试过一次不行就别反复折腾，每次都要几秒
            _initTried = true;

            return await _ui.InvokeAsync(async () => await InitAsync(ct)).Task.Unwrap();
        }
        finally { _initLock.Release(); }
    }

    private async Task<bool> InitAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: _userDataFolder);

            // 承载窗口：WebView2 要有 HWND 才能初始化，但完全不需要给人看。
            // 0 尺寸 + 挪到屏幕外 + 不进任务栏，等于一个后台浏览器。
            // ⚠ 创建时就用**正常边框样式**（2026-09-04 修）：平时靠"挪到屏幕外 + 0 尺寸 +
            //    不进任务栏"藏起来，验证时只改位置和大小。
            //    原来是无边框创建、验证时再改 WindowStyle——WPF 对已显示窗口切 WindowStyle
            //    支持很不可靠，改了不生效也不报错，表现就是"日志说打开了、屏幕上没东西"，
            //    用户以为没点上、连点了六次。
            _host = new Window
            {
                Width = 1,
                Height = 1,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.SingleBorderWindow,
                ShowActivated = false,
                Title = "东财数据通道（后台）",
            };
            // 认主窗口当爹（2026-09-04）：光设 ShowInTaskbar=false 不够——没有 Owner 的窗口
            // 在 Windows 眼里仍是个独立顶层窗口，会跑到任务栏的窗口分组里露脸（实测露过）。
            // 挂上 Owner 之后它就是主窗口的附属窗口，任务栏和 Alt+Tab 都不会单独列它。
            var main = System.Windows.Application.Current?.MainWindow;
            if (main != null && !ReferenceEquals(main, _host)) _host.Owner = main;

            // 窗口图标换掉（2026-09-04）：WPF 窗口默认继承 exe 的图标，于是任务栏上
            // 主程序和这个东财窗口挂着两个一模一样的蓝色下载箭头，人分不清点哪个。
            // 换成系统的信息图标，跟托盘那个保持一致。
            _host.Icon = ChannelWindowIcon();

            // 最小化就缩到托盘（2026-09-04 用户要求）：这个窗口只是让人看东财那边的动静，
            // 不该在任务栏上一直占个位置。想看的时候点托盘图标叫回来。
            _host.StateChanged += (_, _) =>
            {
                if (_host is { WindowState: WindowState.Minimized }) MinimizeToTray();
            };

            // 窗口真被关掉了就记一笔（2026-09-05）。正常路径上 HideInsteadOfClose 会拦住关闭，
            // 走到这儿说明出了意外（拦截器被解绑、或者 Teardown）。WPF 的 Window 一旦 Closed
            // 就作废，再 Show() 直接抛——所以要留个标志，让 ShowForManualVerificationAsync
            // 认出这个死掉的通道并整个重建，而不是从此再也打不开验证窗口。
            _host.Closed += (_, _) => { _hostClosed = true; IsReady = false; };

            _view = new WebView2();
            _host.Content = _view;
            _host.Show();

            await _view.EnsureCoreWebView2Async(env);
            var core = _view.CoreWebView2;

            // 用不上的功能全关掉：这是个数据通道，不是给人用的浏览器。
            // 但**开发者工具留着**——出问题时能让人打开验证窗口按 F12 自己看。
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;

            // UA 显式设成标准 Chrome：WebView2 默认的 UA 带 Edg/ 标识，而我们实测通过的那一轮
            // 用的是 Chrome。这一项本身多半不是成败关键（请求头单独测过没差别），
            // 但既然要模仿浏览器，就别在这种地方留下不必要的差异。
            core.Settings.UserAgent =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                + "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

            // ⚠ 给所有 push2 请求补上 Referer/Origin（2026-09-04）。
            //
            // 导航取数＝在地址栏里敲那个 URL，**Referer 是空的**。而东财页面自己调这个接口时
            // 一定带着 Referer: quote.eastmoney.com——接口本来就是给那些页面用的。
            // 这一条能解释一个一直没想通的矛盾：在 quote 页面里用 JSONP 发（有 Referer）
            // 实测 18/20 成功，直接导航到同一个地址（没 Referer）却被拒。
            //
            // 顺带把 Accept-Language 也补上：真实浏览器发页面外的请求时这几个头都在。
            core.AddWebResourceRequestedFilter("*://push2*.eastmoney.com/*",
                CoreWebView2WebResourceContext.All);
            core.WebMessageReceived += OnWebMessage;

            core.WebResourceRequested += (_, e) =>
            {
                try
                {
                    var h = e.Request.Headers;
                    h.SetHeader("Referer", "https://quote.eastmoney.com/");
                    h.SetHeader("Origin", "https://quote.eastmoney.com");
                    h.SetHeader("Accept", "*/*");
                    h.SetHeader("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
                }
                catch { /* 改不了头也别把请求搞挂 */ }
            };

            // 初始化时试取一次，确认这条通道真的能拿到数据。
            // 走的就是正式取数那条路（停在 quote 页面里发 JSONP），所以这一次成功＝后面都能用。
            try
            {
                // allowManualVerify: false —— 这一发**绝不能**去请人过验证：那条路会绕回
                // ShowForManualVerificationAsync → EnsureReadyAsync，而 _initLock 正被
                // 外层的 EnsureReadyAsync 拿着，必定死锁（2026-09-05 实测，点【东财验证】
                // 后整个按钮就废了）。探针只是"看一眼能不能取数"，失败也不影响通道建立。
                var probe = await FetchWithAutoVerifyAsync(ProbePage, ct, allowManualVerify: false);
                _log?.Invoke($"浏览器通道已就绪（WebView2），{MemberHost} 可取数（试拉回 {probe.Length} 字节）。");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 取不到不代表通道废了——可能只是这一刻被限流。通道留着，抓取时按正常节奏重试。
                _log?.Invoke($"⚠ 浏览器通道起来了，但试取 {MemberHost} 没成功：{ex.Message}。"
                           + "先按这条通道跑，一直失败的话点【东财验证】看看东财返回的是什么。");
            }

            IsReady = true;
            _log?.Invoke($"浏览器通道已就绪（WebView2）——{MemberHost} 的请求改从 Edge 内核发出去。"
                       + "实测同一 IP 同一接口：浏览器 90% 成功、27 个/分钟，"
                       + "普通 HttpClient 第 7 个请求就被切。");
            return true;
        }
        catch (Exception ex)
        {
            // 没装运行时、被组策略拦、创建失败……都归到这里，一律退回 HttpClient
            _log?.Invoke($"⚠ 浏览器通道用不了（{ex.GetType().Name}：{ex.Message}），"
                       + "退回普通 HTTP 抓取——会慢些，但不影响能不能抓到。");
            Teardown();
            return false;
        }
    }

    /// <summary>
    /// 同一时刻只允许一个导航——浏览器地址栏就一个，第二个请求会把第一个顶掉。
    /// 反正限流器本来就是单并发。
    /// </summary>
    private readonly SemaphoreSlim _navLock = new(1, 1);

    /// <summary>
    /// 取数方式：**停在东财自己的页面里，用 JSONP 发请求**（2026-09-04 实证定下来的）。
    ///
    /// 关键在 Referer。东财这些接口本来就是给它自家页面用的，页面调它时一定带着
    /// <c>Referer: quote.eastmoney.com</c>；而把浏览器导航到 API 地址＝在地址栏里敲 URL，
    /// **Referer 是空的**，会被拒。
    ///
    /// 实测（2026-09-04，同一时刻同一 IP）：
    ///   · 停在 quote 页面里用 JSONP 发 → 连发两次都拿到 total=504 的完整数据
    ///   · 直接导航到同一个 API 地址   → 被拒（ErrorHttpInvalidServerResponse）
    /// 那会儿页面上明明有数据在正常刷新，说明配额好好的，纯粹是请求方式的问题。
    ///
    /// 导航取数留作回退：JSONP 靠页面环境（DOM、脚本能不能插），万一哪天页面改版把它挡了，
    /// 还有第二条路可走。两条路都失败才算这次取不到。
    /// </summary>
    public async Task<string> GetJsonAsync(string url, CancellationToken ct = default)
    {
        if (!IsReady) throw new InvalidOperationException("浏览器通道没就绪");

        if (_awaitingHuman)
            throw new HttpRequestException("正在等人过东财的验证，这期间不发请求（免得把验证页冲掉）。");

        await _navLock.WaitAsync(ct);
        try
        {
            return await _ui.InvokeAsync(async () => await FetchWithAutoVerifyAsync(url, ct)).Task.Unwrap();
        }
        finally { _navLock.Release(); }
    }

    /// <summary>
    /// 在**当前页面的上下文**里用 JSONP 取一次数。必须在 UI 线程调。
    ///
    /// 走 JSONP 而不是 fetch：跨子域（quote → push2）的 fetch 会被 CORS 挡掉，
    /// 实测直接 Failed to fetch；而 &lt;script&gt; 标签不受同源策略限制，
    /// 这也正是东财页面自己取数的方式。
    ///
    /// 脚本是**自包含**的，不依赖任何预先注入的全局函数——早先那版把桥函数注入到页面上、
    /// 之后再调用，页面一跳函数就没了，而调用时抛的 ReferenceError 又被
    /// <c>ExecuteScriptAsync</c> 的返回值丢弃，表现成"干等到超时"，查了很久才定位到。
    /// </summary>
    private async Task<(bool Ok, string Body, string Reason)> JsonpFetchAsync(
        string url, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            // ⚠ 一定要看 ExecuteScriptAsync 的返回值：脚本里但凡有个错，丢弃返回值就等于把它咽了，
            //    然后只能干等到超时——排查时会往限流上想，其实是桥断了。
            // ⚠ ExecuteScriptAsync 自己**没有超时**（2026-09-04 卡死过）：页面正忙、导航没完、
            //    渲染进程卡住时它就是不返回，整条抓取线程跟着一起挂——实测抓完 4 个板块之后
            //    停了十分钟，日志一行没出，连重试都没触发，就是卡在这儿。
            var scriptResult = await _view!.CoreWebView2
                .ExecuteScriptAsync(BuildJsonpScript(id, url))
                .WaitAsync(TimeSpan.FromSeconds(20), ct);
            if (scriptResult is null or "null" || !scriptResult.Contains("sent"))
                return (false, "", $"注入脚本没跑起来（页面返回 {scriptResult ?? "null"}）");

            var res = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(25), ct);
            if (!string.IsNullOrEmpty(res.Body)) return (true, res.Body!, "ok");

            return (false, "", res.Reason switch
            {
                "err" => "东财拒绝了这个请求（script onerror）",
                "timeout" => "请求发出去了但东财没回调（20 秒）",
                "nohost" => "页面 DOM 还没就绪",
                _ => $"没拿到数据（{res.Reason}）",
            });
        }
        catch (TimeoutException) { return (false, "", "页面没回话（脚本执行或回调超时）"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, "", ex.Message); }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>
    /// 往页面里发一段**自包含**的 JSONP 脚本：请求、拿到（或失败）之后把结果 postMessage 回来。
    ///
    /// <c>reason</c> 那几个标记是给排错用的——<c>err</c>（脚本加载失败＝请求被拒）
    /// 和 <c>timeout</c>（脚本加载了但回调没触发＝参数或页面环境有问题）
    /// 是完全不同的两种毛病，混成一句"没返回数据"就没法查。
    /// </summary>
    private static string BuildJsonpScript(string id, string url)
    {
        var jsUrl = JsonSerializer.Serialize(url);
        var jsId = JsonSerializer.Serialize(id);
        return $$"""
            (function () {
              var id = {{jsId}}, url = {{jsUrl}};
              function post(body, reason) {
                try {
                  window.chrome.webview.postMessage(
                    JSON.stringify({ id: id, body: body, reason: reason }));
                } catch (e) {}
              }
              try {
                var host = document.body || document.head || document.documentElement;
                if (!host) { post(null, 'nohost'); return 'nohost'; }
                var cb = 'jQuery' + Math.random().toString().slice(2) + '_' + Date.now();
                var s = document.createElement('script');
                var done = false;
                function finish(body, reason) {
                  if (done) return; done = true;
                  try { delete window[cb]; } catch (e) {}
                  if (s.parentNode) s.parentNode.removeChild(s);
                  post(body, reason);
                }
                window[cb] = function (d) { finish(JSON.stringify(d), 'ok'); };
                s.onerror = function () { finish(null, 'err'); };
                setTimeout(function () { finish(null, 'timeout'); }, 20000);
                s.src = url + (url.indexOf('?') < 0 ? '?' : '&') + 'cb=' + cb;
                host.appendChild(s);
                return 'sent';
              } catch (e) {
                post(null, 'ex:' + (e && e.message ? e.message : e));
                return 'ex';
              }
            })()
            """;
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var id = doc.RootElement.GetProperty("id").GetString();
            if (id == null || !_pending.TryRemove(id, out var tcs)) return;
            var body = doc.RootElement.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString() : null;
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
            tcs.TrySetResult(new JsonpResult(body, reason ?? "?"));
        }
        catch
        {
            // 东财页面自己也会 postMessage，不认识的忽略掉就行
        }
    }

    /// <summary>页面回传的一次结果。</summary>
    private readonly record struct JsonpResult(string? Body, string Reason);

    /// <summary>
    /// 取一次数，**验证由程序自己扛**（2026-09-04）。必须在 UI 线程调。
    ///
    /// 东财这类拦截多半是 JS 挑战：第一次访问返回的不是数据，而是一段脚本；
    /// 浏览器把它执行完就自动拿到通行 Cookie，再发一次就正常了——整个过程不需要人点什么。
    /// 所以这里的做法是：拿回来的不像数据，就多等几秒让那段脚本跑完，然后重发。
    ///
    /// 等待时间逐次拉长（2 秒 → 4 秒），一共试 3 次。再不行就抛出去，让上层按限流处理、
    /// 下一轮再来——真是被限流的话再怎么重试也没用，硬撑只会撞得更狠。
    /// </summary>
    /// <param name="allowManualVerify">
    /// 自动过不去时能不能**请人来过验证**。默认能；<b>初始化阶段的探针必须传 false</b>。
    ///
    /// ⚠ 2026-09-05 死锁实录：请人工那条路会调 <see cref="ShowForManualVerificationAsync"/>，
    /// 而它开头要 <see cref="EnsureReadyAsync"/>。初始化时 <c>IsReady</c> 还是 false、
    /// <c>_initLock</c> 又正被 EnsureReadyAsync 自己拿着，于是：
    ///   EnsureReadyAsync → InitAsync → 探针 → 请人工 → ShowForManualVerification
    ///   → EnsureReadyAsync → 抢同一把 _initLock（SemaphoreSlim 不可重入）→ **永久卡死**。
    /// 表现是点【东财验证】后什么都不发生：窗口不出来、日志停在"正在打开…"再无下文，
    /// 连那个防抖标志都因为 finally 走不到而永远为 true，之后每次点都只回一句"正在打开中"。
    /// 探针失败本来就不影响通道建立（见 InitAsync 那段注释），所以它压根不需要请人。
    /// </param>
    private async Task<string> FetchWithAutoVerifyAsync(
        string url, CancellationToken ct, bool allowManualVerify = true)
    {
        // 重试 2 次而不是 3 次（2026-09-04）：撞上验证时每多打一发就多冲一次验证页，
        // 而且请求本身还在耗配额。宁可早点把人叫来。
        const int MaxTries = 2;
        string last = "";
        string lastError = "";
        // 页面上这会儿是不是正显示着验证——决定请人之前要不要再动页面（见下面 break 处）
        bool challengeShowing = false;

        for (int attempt = 1; attempt <= MaxTries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // ⚠ 导航失败（IsSuccess=false，比如被限流切断）也要落进这个重试循环，
            //    不能让它直接抛出去（2026-09-04 实测踩到）：那样等于绕过了重试和人工验证，
            //    第 6 页一被拒整轮就完了，而它明明再等几秒就能好。
            // 主路径：停在东财页面里用 JSONP 发（带 Referer，实证有效）
            var (ok, body, why) = await JsonpFetchAsync(url, ct);
            if (ok && (body.Contains("\"data\"") || body.Contains("\"rc\""))) return body;
            last = ok ? body : "";
            lastError = ok ? "东财返回的不是数据" : why;

            // 回退：导航取数。JSONP 靠页面环境（DOM、能不能插脚本），万一页面改版把它挡了，
            // 还有第二条路。注意它没有 Referer，成功率低，只当兜底。
            //
            // ⚠ **只在第一次失败时试导航**（2026-09-04）：导航会把当前页面换掉，
            //    而东财的验证页往往就在这时候冒出来——反复导航等于反复把它冲走，
            //    人永远看不到、也就永远过不了验证，请求却一直在打，越打限得越死。
            if (!ok && attempt == 1)
            {
                // 导航**之前**先看一眼：验证也可能是在 JSONP 这一步就直接弹在当前页上的，
                // 那样下面这次导航就是把它冲掉的那一下。
                challengeShowing = await LooksLikeChallengeAsync(ct);
                if (challengeShowing)
                {
                    _log?.Invoke("浏览器通道：当前页面上已经摆着人工验证了——不动它，直接请人。");
                    break;
                }

                try
                {
                    var viaNav = await NavigateAndReadAsync(url, ct);
                    if (viaNav.Contains("\"data\"") || viaNav.Contains("\"rc\""))
                    {
                        _log?.Invoke("浏览器通道：JSONP 没成，改用导航取数拿到了数据。");
                        await BackToSeedPageAsync(ct);   // 回到页面上，下一个请求还走 JSONP
                        return viaNav;
                    }
                    if (!string.IsNullOrWhiteSpace(viaNav)) last = viaNav;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { lastError = $"{why}；导航取数也不行（{ex.Message}）"; }

                // ⚠ 验证图片就是这一步导航之后冒出来的（2026-09-05 用户反馈"出现验证图片，
                //    这时就不要 Refresh 页面，要不然人就验证不了"）。原来这里无条件
                //    BackToSeedPage——等于刚把验证叫出来、下一行就把它冲走，人只看见闪一下。
                //    所以：先看看页面现在是不是验证页，是就**什么都别动**，直接去请人。
                challengeShowing = await LooksLikeChallengeAsync(ct);
                if (challengeShowing)
                {
                    _log?.Invoke("浏览器通道：页面上出现了人工验证（图片/滑块）——"
                               + "保住这个页面不动了，接下来请人来过一下。");
                    break;      // 不回 seed、不再重试：再发请求只会把这一页冲掉
                }

                await BackToSeedPageAsync(ct);
            }

            if (attempt < MaxTries)
            {
                // 被限流切断时，桶要十几秒才补得上；验证脚本也要几秒才跑完。
                // 5 秒、15 秒是按实测的恢复节奏定的（连发 5 个被切、隔十几秒又能通）。
                var wait = TimeSpan.FromSeconds(attempt == 1 ? 5 : 15);
                _log?.Invoke($"浏览器通道：第 {attempt} 次没拿到数据（{lastError}），"
                           + $"等 {wait.TotalSeconds:0} 秒再试。");
                await Task.Delay(wait, ct);
            }
        }

        // 自动过不去了——请人来一趟，然后**等在这儿**，别让这一轮就这么失败掉（2026-09-04 用户要求）。
        // 失败退出的代价不只是这一个请求：上层会按限流处理、进熔断、整项跳过，
        // 人回来还得重新点执行、重新排队。等着的话，人一关窗口就接着往下跑。
        // ⚠ 请人工的条件放宽了（2026-09-04）：原来只有"页面回了东西但不是数据"才请人，
        //    可东财这道拦截是**图片验证码**——JSONP 被拒时 script onerror 拿不到任何内容，
        //    导航回退又常在连接层就挂了，两边都是空，于是永远走不到请人那一步。
        //    现在只要连试都不行就请人；配额问题请人也解决不了，所以靠 ShouldAskHuman 限流，
        //    别一失败就弹窗。
        // 最后再确认一次现场：第二次 JSONP 失败之后页面也可能已经变成验证页了，
        // 这一眼决定下面还要不要再导航（要是已经在验证页上，多导航一次就白等了）。
        if (!challengeShowing) challengeShowing = await LooksLikeChallengeAsync(ct);

        // ⚠ allowManualVerify 必须在最前面短路：初始化探针走到这儿再去请人的话，
        //    会绕回 ShowForManualVerificationAsync → EnsureReadyAsync，死锁在 _initLock 上
        //    （详见上面参数注释里的死锁实录）。
        if (allowManualVerify
            && ShouldAskHuman(challengeShowing)
            && await WaitForManualVerifyAsync(url, last, challengeShowing, ct))
        {
            var afterHuman = await NavigateAndReadAsync(url, ct);
            if (afterHuman.Contains("\"data\"") || afterHuman.Contains("\"rc\""))
            {
                _log?.Invoke("人工验证之后取数恢复正常，继续抓取。");
                await BackToSeedPageAsync(ct);   // 回页面上，下一个请求还走 JSONP
                return afterHuman;
            }
            _log?.Invoke("⚠ 人工验证之后还是拿不到数据——可能不是验证的问题，这一轮先放过。");
        }

        throw new HttpRequestException(
            string.IsNullOrWhiteSpace(last)
                ? $"浏览器通道：连试 {MaxTries} 次都没拿到数据（{lastError}）"
                : $"浏览器通道：连试 {MaxTries} 次都没拿到数据，东财返回的是：{Truncate(last, 150)}");
    }

    /// <summary>
    /// 抓取期间**把浏览器窗口开着**（2026-09-04 用户要求），让人能实时看见东财那边在发生什么：
    /// 数据在刷、弹了图片验证码、还是整个页面打不开——一眼就知道，不用等日志报错再猜。
    ///
    /// 摆在屏幕右下角、不抢焦点（<c>ShowActivated=false</c> + 不 Activate），
    /// 所以你该干嘛干嘛，它只是在角落里跑。嫌碍事就直接关掉——
    /// 关闭只是把它收起来（见 <see cref="HideInsteadOfClose"/>），抓取照常进行。
    /// </summary>
    public async Task ShowWorkWindowAsync(CancellationToken ct = default)
    {
        if (!IsReady) return;
        await _ui.InvokeAsync(() =>
        {
            if (_host == null) return;

            var w = SystemParameters.WorkArea;
            _host.Width = 760;
            _host.Height = 520;
            _host.Left = Math.Max(0, w.Right - 780);
            _host.Top = Math.Max(0, w.Bottom - 560);
            _host.ShowInTaskbar = false;      // 不占任务栏，它不是给人操作的主窗口
            _host.Title = "东财取数中（这个窗口只是让你看见发生了什么，可以直接关掉）";

            _host.Closing -= HideInsteadOfClose;
            _host.Closing += HideInsteadOfClose;
            _host.Show();
            // ⚠ 不 Activate、不 Topmost：它是个观察窗，不该抢你正在用的窗口的焦点
        }).Task;
    }

    /// <summary>
    /// 这个窗口用的图标：一个墨绿底、白色"财"字的圆章，自己画的。
    ///
    /// 为什么不用系统现成的：<c>SystemIcons.Information</c> 在任务栏上看着像个错误/警告提示，
    /// 而这个窗口是在正常干活，挂个感叹号会让人以为出事了（2026-09-04 用户指出）。
    /// 主程序是蓝色下载箭头，这个是绿色圆章，任务栏和托盘上一眼分得开。
    /// </summary>
    private static System.Drawing.Icon? BuildChannelIcon()
    {
        if (_channelIcon != null) return _channelIcon;
        try
        {
            using var bmp = new System.Drawing.Bitmap(32, 32);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x1F, 0x7A, 0x4D));
                g.FillEllipse(bg, 1, 1, 30, 30);
                using var font = new System.Drawing.Font("Microsoft YaHei", 15,
                    System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
                using var fmt = new System.Drawing.StringFormat
                {
                    Alignment = System.Drawing.StringAlignment.Center,
                    LineAlignment = System.Drawing.StringAlignment.Center,
                };
                g.DrawString("财", font, System.Drawing.Brushes.White,
                    new System.Drawing.RectangleF(0, 0, 32, 32), fmt);
            }
            // GetHicon 拿到的句柄要克隆一份再用——原句柄随 Bitmap 释放就失效了
            var h = bmp.GetHicon();
            try { _channelIcon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(h).Clone(); }
            finally { DestroyIcon(h); }
            return _channelIcon;
        }
        catch { return null; }
    }

    private static System.Drawing.Icon? _channelIcon;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>同一个图标给 WPF 窗口用（Window.Icon 要的是 ImageSource）。</summary>
    private static System.Windows.Media.ImageSource? ChannelWindowIcon()
    {
        try
        {
            var ico = BuildChannelIcon();
            if (ico == null) return null;
            return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                ico.Handle,
                System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        }
        catch { return null; }
    }

    /// <summary>
    /// 这个窗口自己的托盘图标（2026-09-04）。**故意跟主程序用不同的图标**——
    /// 托盘上同时挂两个一模一样的图标，人分不清哪个是主程序、哪个是东财窗口。
    /// </summary>
    private System.Windows.Forms.NotifyIcon? _tray;

    /// <summary>最小化 → 藏起来 + 在托盘上留个入口。</summary>
    private void MinimizeToTray()
    {
        if (_host == null) return;
        _host.Hide();
        _host.ShowInTaskbar = false;

        if (_tray == null)
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("显示东财窗口", null, (_, _) => RestoreFromTray());
            menu.Items.Add("收起（继续后台取数）", null, (_, _) => RemoveTray());

            _tray = new System.Windows.Forms.NotifyIcon
            {
                // 跟窗口用同一个绿色"财"字圆章，跟主程序那个蓝色下载箭头分得开
                Icon = BuildChannelIcon() ?? System.Drawing.SystemIcons.Application,
                Text = "东财取数窗口（点开可看实时状态/过验证）",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _tray.MouseClick += (_, a) =>
            {
                if (a.Button == System.Windows.Forms.MouseButtons.Left) RestoreFromTray();
            };
        }
        _tray.Visible = true;
    }

    private void RestoreFromTray()
    {
        if (_host == null) return;
        _host.ShowInTaskbar = true;
        _host.Show();
        _host.WindowState = WindowState.Normal;
        _host.Activate();
        RemoveTray();
    }

    private void RemoveTray()
    {
        try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
        _tray = null;
    }

    /// <summary>抓完把观察窗收起来（挪到屏幕外），通道继续可用。</summary>
    public async Task HideWorkWindowAsync()
    {
        if (_host == null) return;
        await _ui.InvokeAsync(() =>
        {
            if (_host == null) return;
            _host.Closing -= HideInsteadOfClose;
            _host.Hide();
            _host.ShowInTaskbar = false;
            _host.Width = 1;
            _host.Height = 1;
            _host.Left = -32000;
            _host.Top = -32000;
            _host.Show();
        }).Task;
    }

    /// <summary>上次请人工验证是什么时候——用来避免一直弹窗。</summary>
    private DateTime _lastAskedHuman = DateTime.MinValue;

    /// <summary>
    /// 隔多久才允许再请一次人。抓一轮几百上千个请求，要是每个失败都弹窗，
    /// 人根本没法用电脑；而验证过一次能管一阵子，短时间内反复弹也没意义。
    /// </summary>
    private static readonly TimeSpan AskHumanCooldown = TimeSpan.FromMinutes(20);

    /// <summary>页面上确实摆着验证时的冷却（2026-09-05）。这是**看见的事实**、不是猜，
    /// 压着不请人只会让后面每个请求都在验证页上白撞；留 2 分钟只为防连环弹窗。</summary>
    private static readonly TimeSpan AskHumanCooldownWhenChallenged = TimeSpan.FromMinutes(2);

    /// <summary>这会儿该不该请人来过验证。</summary>
    /// <param name="challengeShowing">页面上已经看见验证了（不是靠失败次数猜的）。</param>
    private bool ShouldAskHuman(bool challengeShowing = false)
    {
        var cooldown = challengeShowing ? AskHumanCooldownWhenChallenged : AskHumanCooldown;
        if (DateTime.Now - _lastAskedHuman < cooldown) return false;
        _lastAskedHuman = DateTime.Now;
        return true;
    }

    /// <summary>
    /// 把验证窗口摆到人面前，然后等他弄完（2026-09-04）。返回 false 表示没等到（超时/取消）。
    ///
    /// "等在那里"是刻意的：自动重试都过不去时，如果直接失败退出，上层会按限流处理——
    /// 进熔断、整项跳过、今天可能不再来，人回来还得重新点执行。而这里挂着的话，
    /// 人关掉窗口的那一刻抓取就从断点继续，一个请求都不浪费。
    /// </summary>
    /// <param name="url">这次没取到的那个接口地址——页面上还没有验证时，导航到它把验证叫出来。</param>
    /// <param name="challengeShowing">页面上已经摆着验证了：那就**一步都别动**，直接给人看。</param>
    private async Task<bool> WaitForManualVerifyAsync(
        string url, string lastBody, bool challengeShowing, CancellationToken ct)
    {
        // 等人期间整条通道停手：这段时间任何自动请求都只会把验证页冲掉、白耗配额。
        // （调用方持着 _navLock，所以别的板块的请求本来就在排队等，这个标记是双保险，
        //   也让日志能说清楚"现在是在等人，不是卡死"。）
        _awaitingHuman = true;
        try
        {
        _manualVerifyDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _log?.Invoke("━━━━━━━━━━ 需要人工验证 ━━━━━━━━━━");
        _log?.Invoke(string.IsNullOrWhiteSpace(lastBody)
            ? "自动重试了 3 次都被拒，多半是东财弹了**图片验证码**——那个只能人来点，程序过不了。"
            : $"自动重试了 3 次仍拿不到数据，东财返回的是：{Truncate(lastBody, 100)}");
        _log?.Invoke("已弹出验证窗口，请在里面把滑块拼图拖到缺口位置。");

        // 页面上还没有验证的话（JSONP 被拒时当前页往往还是那个正常的行情页），
        // **在把窗口摆到人面前之前**导航一次，让东财把验证吐出来；已经在验证页上就不动。
        // 这是这条路径上最后一次导航——之后到人关窗口为止，谁都不许再碰这个页面
        // （_awaitingHuman 挡着，见 GetJsonAsync / BackToSeedPageAsync）。
        if (!challengeShowing)
        {
            _log?.Invoke("页面上还没看到验证，先导航到那个接口地址把它叫出来（之后不再刷新）…");
            try { await NavigateAndReadAsync(url, ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* 拿不到内容正是预期：验证页/被拒都算，人看得见就行 */ }
        }

        // 往托盘弹一条气泡：抓取常常是夜里或你在忙别的时候跑的，
        // 程序挂在这儿等你，你却不知道——日志写得再清楚，没人看也是白写。
        TrayNotifier.Notify(
            "东财需要人工验证",
            "板块抓取暂停了，等你过一下滑块验证。\n"
            + "把拼图拖到缺口，然后关掉那个窗口，抓取会自己接着跑。\n"
            + $"（最多等 {ManualVerifyTimeout.TotalMinutes:0} 分钟，超时就这轮跳过、下轮再来）");
        _log?.Invoke($"弄好之后**直接关掉那个窗口**，抓取会自己接着跑，不用回来点执行。"
                   + $"最多等 {ManualVerifyTimeout.TotalMinutes:0} 分钟，超时就当这轮失败、下轮再来。");
        _log?.Invoke("━━━━━━━━━━━━━━━━━━━━━━━━━━");

        try
        {
            await ShowForManualVerificationAsync(ct);
            await _manualVerifyDone.Task.WaitAsync(ManualVerifyTimeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            _log?.Invoke($"等了 {ManualVerifyTimeout.TotalMinutes:0} 分钟没人来处理，这一轮先放过，下轮再试。");
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.Invoke($"打开验证窗口时出错（{ex.Message}），这一轮先放过。");
            return false;
        }
        finally { _manualVerifyDone = null; }
        }
        finally { _awaitingHuman = false; }
    }

    /// <summary>正在等人过验证吗——这期间不该有任何自动请求发出去。</summary>
    private volatile bool _awaitingHuman;

    /// <summary>
    /// 判"当前页面是不是要人来过的验证页"的特征词（2026-09-05）。
    ///
    /// 挑的是**验证页才会有、正常行情页不会有**的词：行情页上确实会出现"验证码"
    /// （登录框里就有"验证码登录"），所以那个词单独不算数，必须配合"安全验证/滑块/
    /// 拖动/拼图"这类只有挑战页才写的说法，或者地址里带 captcha。
    /// 宁可漏判也别误判：漏判最多回到原来的行为（回 seed 重试），
    /// 误判却会让抓取白等人 15 分钟。
    /// </summary>
    /// <summary>
    /// 验证页/验证浮层的特征词。
    ///
    /// ⚠ 这些是**全文**扫描的（2026-09-06 从"前 600 字"改的），所以每个词都得足够具体，
    /// 不能用"验证"这种单字——页脚有"验证码登录"、帮助链接里也有"安全验证"字样，
    /// 全文扫到就会把正常页面误判成验证页，然后永远等人。
    ///
    /// 实测东财用的是**滑块拼图浮层**，浮层上的原话是
    /// 「拖动下方滑块完成拼图」和「拖动左边滑块完成上方拼图」。
    /// </summary>
    private static readonly string[] ChallengeMarkers =
    [
        // 东财实测用的（2026-09-06 截图确认）
        "完成拼图", "拖动下方滑块", "拖动左边滑块", "拖动滑块",
        // 通用验证组件的痕迹
        "captcha", "geetest", "nc_wrapper", "verifycode",
        "人机验证", "点击按钮开始验证",
    ];

    /// <summary>
    /// 当前页面看着像不像正摆着一道要人过的验证。必须在 UI 线程调。
    ///
    /// 判出来是 true 的唯一后果是"**别再碰这个页面**、把它原样给人看"——所以判错的代价
    /// 不对称：漏判＝维持原状，误判＝抓取停下来等人。据此把特征词收得比较紧
    /// （见 <see cref="ChallengeMarkers"/>），拿不准一律当不是。
    /// </summary>
    private async Task<bool> LooksLikeChallengeAsync(CancellationToken ct)
    {
        try
        {
            var core = _view?.CoreWebView2;
            if (core == null) return false;

            // ⚠ 必须扫**全文**（2026-09-06 改）：原来只取正文前 600 字，而东财的验证是
            //    **浮层**，页面本身照常渲染——前 600 字全是顶部那排导航
            //    （网站首页/加收藏/移动客户端/东方财富/…/大盘星图/自选股/特色行情…），
            //    浮层上"拖动下方滑块完成拼图"那句排在很后面，600 字根本够不着。
            //    结果每次都判成"不是验证页"，然后走到"找不到表头"报一个误导人的失败。
            //
            //    配套的是特征词改成了具体短语（见 ChallengeMarkers），所以扫全文不会误判。
            //    截 2 万字是防超长页面把脚本卡住，验证浮层的文字远在这之前。
            const string js = """
                (function () {
                  var t = document.title || '';
                  var b = document.body ? (document.body.innerText || '') : '';
                  // 页面内容本身就是接口返回的 JSON / JSONP（导航取数那条路）＝拿到数据了，
                  // 由调用方据此直接判"不是验证页"。用**形态**判断，不能用"正文里有没有 data 字样"：
                  // 板块页的内联脚本里到处都是 data，扫全文必然撞上（2026-09-06）。
                  var isJson = /^\s*(\{|jQuery\w*\s*\()/.test(b);
                  return (isJson ? 'ISJSON ' : 'ISPAGE ') + location.href + ' | ' + t + ' | ' + b.slice(0, 20000);
                })()
                """;
            var raw = await core.ExecuteScriptAsync(js).WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (string.IsNullOrEmpty(raw) || raw == "null") return false;

            string text;
            try { text = JsonSerializer.Deserialize<string>(raw) ?? ""; }
            catch { text = raw; }

            // 页面内容就是接口返回的 JSON＝已经拿到数据了，不可能是验证页
            if (text.StartsWith("ISJSON", StringComparison.Ordinal)) return false;

            return ChallengeMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // 页面卡着、脚本跑不了——判不出来就当不是（维持原来的行为）
            return false;
        }
    }

    /// <summary>
    /// 回到东财页面上——JSONP 要在它的上下文里发才带得上 Referer。
    /// 导航取数会把浏览器带走，用完必须回来。
    /// </summary>
    private async Task BackToSeedPageAsync(CancellationToken ct)
    {
        // ⚠ 正在等人过验证时**绝对不能导航**（2026-09-05）：那一下会把人正在看的验证图片
        //    冲掉，人就再也验证不了了。等人期间本来也没有请求要发，回不回 seed 无所谓。
        if (_awaitingHuman) return;

        try
        {
            var core = _view!.CoreWebView2;
            if (core.Source.StartsWith("https://quote.eastmoney.com", StringComparison.OrdinalIgnoreCase))
                return;

            var done = new TaskCompletionSource<bool>();
            void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
            {
                core.NavigationCompleted -= OnNav;
                done.TrySetResult(e.IsSuccess);
            }
            core.NavigationCompleted += OnNav;
            core.Navigate(SeedPage);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 回不去也别把这次取数搞挂，下次 JSONP 失败会再试导航 */ }
    }

    /// <summary>导航到 url，等它加载完，把页面上的文本读回来。必须在 UI 线程调。</summary>
    private async Task<string> NavigateAndReadAsync(string url, CancellationToken ct)
    {
        var core = _view!.CoreWebView2;

        var done = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>();
        void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= OnNav;
            done.TrySetResult(e);
        }
        core.NavigationCompleted += OnNav;
        core.Navigate(url);

        CoreWebView2NavigationCompletedEventArgs res;
        try
        {
            res = await done.Task.WaitAsync(TimeSpan.FromSeconds(25), ct);
        }
        catch (TimeoutException)
        {
            core.NavigationCompleted -= OnNav;
            throw new HttpRequestException("浏览器通道：25 秒没加载完（东财没响应）");
        }

        if (!res.IsSuccess)
            throw new HttpRequestException(
                $"浏览器通道：东财拒绝了这个请求（{res.WebErrorStatus}）");

        // 接口返回的是纯文本，浏览器会把它包在 <pre> 里显示；
        // 万一哪天带上了 JSON 查看器，退回读 body 的文本也拿得到。
        const string readJs = """
            (function () {
              var pre = document.querySelector('pre');
              if (pre && pre.innerText) return pre.innerText;
              if (document.body && document.body.innerText) return document.body.innerText;
              return document.documentElement ? document.documentElement.innerText : '';
            })()
            """;
        // 同上：这里也必须有超时，不然页面一卡整条抓取线程就跟着挂
        var raw = await core.ExecuteScriptAsync(readJs).WaitAsync(TimeSpan.FromSeconds(20), ct);

        // ExecuteScriptAsync 给回来的是 JSON 编码过的字符串，要脱一层引号和转义
        if (string.IsNullOrEmpty(raw) || raw == "null") return "";
        try { return JsonSerializer.Deserialize<string>(raw) ?? ""; }
        catch { return raw; }
    }

    private static string Truncate(string s, int n)
        => s.Length <= n ? s : s[..n] + "…";

    /// <summary>
    /// 把这个平时藏着的浏览器窗口**显示出来**，让人手工过一次东财的验证（2026-09-04）。
    ///
    /// 为什么需要它：WebView2 用的是程序自己的用户数据目录，跟你日常的 Chrome/Edge 是两套。
    /// 那套 profile 一开始是空的——在东财眼里就是个生面孔，可能被要求先过人工验证。
    /// 而验证只能人来点，程序代替不了。过完之后 Cookie 存在 data/local/webview2 里，
    /// 后面的抓取就带着它走了，也能跨次启动留着。
    ///
    /// 顺带也是个排错窗口：抓取一直失败时打开它，能直接看见东财到底返回了什么
    /// （验证页？空白？正常数据？），比对着日志猜快得多。
    /// </summary>
    public async Task ShowForManualVerificationAsync(CancellationToken ct = default)
    {
        // ⚠ 先把"只试一次"那道闸门放开（2026-09-05 修，用户报"点了验证按钮没反应"）。
        //
        // EnsureReadyAsync 里有 `if (_initTried) return false;`——初始化失败过一次，
        // 这个进程内就再也不试了。那条规矩对**自动抓取**是对的（每次重试都要几秒，
        // 失败的原因通常也不会自己好），但对**人主动点按钮**是错的：
        // 抓取时初始化失败过一次之后，人再怎么点验证按钮都只会走到下面那行抛异常，
        // 表现就是"点了没反应"——而人点这个按钮，往往正是因为抓取失败想来看看怎么回事。
        //
        // 人点按钮＝明确的重试意图，值得再花那几秒。自动抓取那条路不受影响，仍然只试一次。
        _initTried = false;

        // 上次那个窗口被真关掉了（正常路径拦得住，走到这儿说明出过意外）——整个通道重建。
        // 不重建的话 _host 是个已 Closed 的 Window，下面 Show() 必抛，验证窗口从此打不开。
        if (_hostClosed)
        {
            _log?.Invoke("上次的验证窗口已被关闭，正在重建浏览器通道……");
            await _ui.InvokeAsync(() =>
            {
                try { if (_view?.CoreWebView2 != null) _view.CoreWebView2.WebMessageReceived -= OnWebMessage; } catch { }
                try { _view?.Dispose(); } catch { }
                _view = null;
                _host = null;
            }).Task;
            _hostClosed = false;
            IsReady = false;
        }

        if (!await EnsureReadyAsync(ct))
            throw new InvalidOperationException("浏览器通道起不来，没法打开验证窗口——原因看日志。");

        await _ui.InvokeAsync(() =>
        {
            if (_host == null) return;

            // 只改位置和大小，**不动 WindowStyle**——理由见创建那里的注释
            _host.Width = 1100;
            _host.Height = 800;
            _host.Left = 120;
            _host.Top = 80;
            _host.ShowInTaskbar = true;
            _host.Title = "东财人工验证 —— 过完验证直接关掉这个窗口即可（Cookie 会自动留下）";

            // 重复点【东财验证】不该叠加事件处理：先摘再挂
            _host.Closing -= HideInsteadOfClose;
            _host.Closing += HideInsteadOfClose;

            _host.Show();
            // 强制冒到最前再取消置顶：主窗口是全屏的，不这么做窗口会开在它后面，
            // 人看不见就会以为按钮没反应（实测被连点了六次）
            _host.Topmost = true;
            _host.Activate();
            _host.Topmost = false;
            _host.Focus();

            // ⚠ **这里刻意不导航**（2026-09-04 用户发现）：东财的验证页往往一闪而过，
            //    而"闪掉"正是我们自己造成的——验证页刚出现，代码又 Navigate 一次把它冲走，
            //    页面就变成 ERR_EMPTY_RESPONSE，人根本来不及点。
            //    现在保住现场：窗口里是什么就让人看什么。要是停在错误页上，
            //    点页面里那个 Refresh 就能把验证重新叫出来。
            _log?.Invoke($"验证窗口已显示（{_host.Width:0}×{_host.Height:0}），保持着刚才那一刻的页面。"
                       + "看到滑块/拼图就把它拖到缺口；停在错误页上就点页面里的 Refresh 让验证重新出来；"
                       + "看到一串 JSON 说明已经通了，直接关掉窗口即可。"
                       + "看不到窗口就按 Alt+Tab 找「东财人工验证」。");
        }).Task;
    }

    /// <summary>
    /// 人点验证窗口的关闭按钮时：**拦下来只藏起来，绝不真关**。
    ///
    /// ⚠ 2026-09-05 修了两处会让【东财验证】按钮永久失效的写法（用户报"关闭时报错，
    ///    然后再点就打不开了"）：
    ///
    /// ① <b>原来在 Closing 事件处理器里同步调 Hide()/Show()</b>。WPF 不允许在窗口正在关闭的
    ///    事件里这么摆布它，会抛异常——而异常一抛，后面的 <c>TrySetResult</c> 就不执行了：
    ///    等在 <see cref="WaitForManualVerifyAsync"/> 里的抓取线程收不到"人弄好了"的信号，
    ///    只能一直等到 15 分钟超时。现在改成排到事件处理完之后再做（BeginInvoke）。
    ///
    /// ② <b>原来它把自己解绑了</b>（<c>_host.Closing -= HideInsteadOfClose</c>）。于是人第二次
    ///    点关闭时没人拦，窗口**真的被 Close 掉**——而 WPF 的 Window 一旦 Closed 就作废了，
    ///    再 <c>Show()</c> 直接抛 InvalidOperationException，验证窗口从此再也打不开。
    ///    现在不解绑：这个窗口在整个进程生命周期里只藏不关，只有 <see cref="Teardown"/>
    ///    才会解绑并真正关掉它。
    /// </summary>
    private void HideInsteadOfClose(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_host == null) return;

        // 拦下这次关闭。窗口对象必须留着——被真 Close 掉就再也 Show 不起来了。
        // **不解绑自己**，否则下一次关闭就没人拦了。
        e.Cancel = true;

        // 告诉正等着的抓取线程"人弄好了，接着跑"。放在最前面：它最要紧，
        // 后面收窗口的动作出什么岔子都不能耽误这一步。
        _manualVerifyDone?.TrySetResult(true);

        // 挪回屏幕外缩回 1×1，恢复成后台通道的样子。**同样不动 WindowStyle**——
        // 切它在 WPF 里不可靠，而且这里也没必要，反正窗口在屏幕外面。
        // 排到 Closing 处理完之后再动手，别在事件里摆布正在关闭的窗口。
        var host = _host;
        host.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                host.Hide();
                host.ShowInTaskbar = false;
                host.Width = 1;
                host.Height = 1;
                host.Left = -32000;
                host.Top = -32000;
                host.Show();
                _log?.Invoke("东财验证窗口已收起，浏览器通道继续可用（Cookie 已留在 data/local/webview2）。");
            }
            catch (Exception ex)
            {
                // 收不回去顶多是屏幕上留个小窗口，抓取照常——绝不能让它把通道带塌
                _log?.Invoke($"收起验证窗口时出了点岔子（{ex.Message}），不影响抓取。");
            }
        }), DispatcherPriority.Background);
    }

    private void Teardown()
    {
        RemoveTray();   // 不收的话托盘上会留个点了没反应的幽灵图标
        try { if (_view?.CoreWebView2 != null) _view.CoreWebView2.WebMessageReceived -= OnWebMessage; } catch { }
        try { _view?.Dispose(); } catch { }
        // ⚠ 先解绑 HideInsteadOfClose 再 Close：不解的话这次 Close 会被它 e.Cancel 掉，
        //    窗口关不掉、进程就跟着赖住（2026-09-04 踩过，表现是发布时报"文件被占用"）。
        try { if (_host != null) _host.Closing -= HideInsteadOfClose; } catch { }
        try { _host?.Close(); } catch { }
        _view = null;
        _host = null;
        IsReady = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ui.CheckAccess()) Teardown();
        else await _ui.InvokeAsync(Teardown).Task;
        _initLock.Dispose();
    }
}
