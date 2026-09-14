using StockPlatform.Data.Remote.Cdp;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 逐股分档资金流——**用真 Chrome/Edge 发请求**那条通道（2026-09-14）。
///
/// ════ 为什么要这条 ════
/// 打的 URL 跟 <see cref="EastMoneyMoneyFlowProvider"/> **一模一样**（同域名、同 fields，
/// 见 <see cref="MoneyFlowKlineParser.BuildUrl"/>），差别只有一个：请求由浏览器发出去，
/// 不是由程序里的 HttpClient。本机上这件事决定成败——HttpClient 那条被网关按域名拦死
/// （TCP/TLS 都通、一发请求就被切、收 0 字节），浏览器这条能稳定取到。
/// 用户 2026-09-13 拿 publish/moneyflow-transfer/moneyflow-fetch.html 实跑验证过一整份清单。
///
/// ════ 取数路径原样照搬那个 html ════
/// 用 <b>JSONP</b>（往页面里插 script 标签）而不是 fetch——JSONP 绕开 CORS，而且它就是
/// 已验证的那条路径。落脚页面是 quote.eastmoney.com（不是 about:blank：那是 opaque
/// origin，也给不出正常的 Referer）。
///
/// ════ 节奏＝那个 html 的默认值，一个都没改 ════
/// 间隔 2 秒（±30% 抖动）、每 10~15 只主动歇 2~5 分钟、单只失败快速重试 2s/10s、
/// 连续 25 只失败收尾。批量和歇多久**都是随机的**，这是故意的：东财的触发点是**累计请求数**
/// 不是速率（实测连发 16~35 个就被切），光降速没用；而"每 15 个整、歇整 2 分钟"这种精确
/// 周期本身就是机器行为最好认的特征。摊下来约 17~20 秒/只。
///
/// ⚠ 换网络解决不了限流——它是按**出口 IP** 算配额的。板块通道也走浏览器、共用同一个出口，
/// 所以那两项别跟这一项同时跑。
/// </summary>
public sealed class ChromeCdpMoneyFlowFetcher : IMoneyFlowDetailFetcher, IAsyncDisposable
{
    /// <summary>落脚页面。整个抓取过程只导航这一次，之后一直待在它上面插 script 标签。</summary>
    private const string HomeUrl = "https://quote.eastmoney.com/";

    /// <summary>单个请求等多久（跟 html 版的 REQ_TIMEOUT 一致）。
    /// ⚠ 必须小于 <c>CdpConnection</c> 的 30 秒命令超时，否则先炸的是 CDP 那一层。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(25);

    /// <summary>单只的快速重试间隔（html 版的 RETRY_WAITS）。</summary>
    private static readonly TimeSpan[] RetryWaits = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];

    /// <summary>
    /// 被切之后歇多久再让调度重来。
    ///
    /// html 版没有这个——那是人手点的，收尾了人自己看着办。产品这边是**调度自动跑**的：
    /// 不熔断的话下一轮马上又来，25 只失败请求白白打出去，只会让封禁更久。
    /// </summary>
    private static readonly TimeSpan PauseAfterExhausted = TimeSpan.FromMinutes(30);

    private readonly ChromeCdpLauncher _launcher;
    private readonly Pace _pace;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly Random _rng = new();

    private bool _ready, _initTried, _first = true;
    private int _consecutiveFail, _requestsSinceRest, _codesSinceRest, _nextBatch;
    private DateTime? _pausedUntil;

    public event Action<string>? OnStatus;

    public string ChannelName => "真浏览器（CDP）里的 JSONP";

    /// <summary>跟已验证的 html 一致：连续 25 只失败才收尾。</summary>
    public int GiveUpAfterConsecutiveFailures => 25;

    public DateTime? PausedUntil => _pausedUntil is { } t && t > DateTime.Now ? t : null;

    /// <param name="userDataDir">浏览器 profile 目录。⚠ 必须是我们自己的目录，
    /// 指向日常那个 Chrome 的 profile 会因为目录被锁而直接启动失败。</param>
    /// <param name="port">CDP 调试端口。**别跟板块通道的 9333 撞**。</param>
    /// <param name="browserPath">浏览器 exe；留空＝自动找 Chrome，找不到再找 Edge。</param>
    /// <param name="pace">节奏参数；留空＝已验证的那套。只有测试该传别的值。</param>
    public ChromeCdpMoneyFlowFetcher(
        string userDataDir, int port = 9334, string? browserPath = null, Pace? pace = null)
    {
        _launcher = new ChromeCdpLauncher(userDataDir, port, browserPath);
        _launcher.OnStatus += s => OnStatus?.Invoke(s);
        _pace = pace ?? Pace.Verified;
    }

    /// <summary>
    /// 节奏参数。<see cref="Verified"/> 是 2026-09-13 实跑验证过的那套，生产一律用它。
    /// </summary>
    /// <param name="Delay">两只之间的基准间隔（实际是它的 0.7~1.3 倍）。</param>
    /// <param name="BatchMin">每抓这么多只主动歇一次（实际在 Min~Max 之间随机取）。</param>
    /// <param name="RestMin">主动歇多久（实际在 Min~Max 之间随机取）。</param>
    public readonly record struct Pace(
        TimeSpan Delay, int BatchMin, int BatchMax, TimeSpan RestMin, TimeSpan RestMax)
    {
        public static Pace Verified => new(
            TimeSpan.FromSeconds(2), 10, 15, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
    }

    // ─────────────── 取数 ───────────────

    public async Task<List<NetInflowDetail>> FetchAsync(string code, CancellationToken ct = default)
    {
        if (!await EnsureReadyAsync(ct))
            throw new RateLimitedException($"浏览器通道起不来，{code} 没抓（原因见上一条日志）");

        await PaceAsync(ct);

        string? lastWhy = null;
        for (int attempt = 0; attempt <= RetryWaits.Length; attempt++)
        {
            if (attempt > 0) await Task.Delay(RetryWaits[attempt - 1], ct);
            _requestsSinceRest++;

            var (ok, payload) = await JsonpAsync(
                MoneyFlowKlineParser.BuildUrl(EastMoneyMoneyFlowProvider.Host, code), ct);
            if (ok)
            {
                _consecutiveFail = 0;
                return MoneyFlowKlineParser.Parse(code, payload, DateTime.Now);
            }
            lastWhy = payload;
        }

        // 连续失败到阈值就熔断——继续打只会让封禁更久，而这一项没有时效压力。
        if (++_consecutiveFail >= GiveUpAfterConsecutiveFailures)
            _pausedUntil = DateTime.Now + PauseAfterExhausted;

        throw new RateLimitedException($"浏览器取 {code} 的分档资金流没成（连试三次）：{lastWhy}");
    }

    /// <summary>
    /// 在页面里做一次 JSONP，把回调拿到的对象 <c>JSON.stringify</c> 回来。
    ///
    /// 返回 <c>(true, JSON 文本)</c> 或 <c>(false, 失败原因)</c>。失败只有两种形状：
    /// 超时，和"脚本没加载成功"——后者就是连接被切／被拦，**浏览器不会告诉我们底层原因**，
    /// 这是这条通道天然看不到的东西，不是我们漏记了。
    /// </summary>
    private async Task<(bool Ok, string Payload)> JsonpAsync(string url, CancellationToken ct)
    {
        // 结果带前缀：CDP 只能回一个值给我们，而"拿到了"和"没拿到"都得能认出来。
        var js = $$"""
            (function () {
              return new Promise(function (resolve) {
                var name = '__mf_cb_' + Date.now() + '_' + Math.floor(Math.random() * 1e6);
                var done = false;
                var tag = document.createElement('script');
                function cleanup() {
                  try { delete window[name]; } catch (e) { window[name] = undefined; }
                  if (tag.parentNode) tag.parentNode.removeChild(tag);
                }
                var timer = setTimeout(function () {
                  if (done) return;
                  done = true; cleanup(); resolve('ERR|超时');
                }, {{(int)RequestTimeout.TotalMilliseconds}});
                window[name] = function (data) {
                  if (done) return;
                  done = true; clearTimeout(timer); cleanup();
                  try { resolve('OK|' + JSON.stringify(data)); }
                  catch (e) { resolve('ERR|回调给的东西转不成 JSON：' + e); }
                };
                tag.onerror = function () {
                  if (done) return;
                  done = true; clearTimeout(timer); cleanup();
                  resolve('ERR|连接被切断或被拦');
                };
                tag.src = '{{url}}' + '&cb=' + name;
                document.head.appendChild(tag);
              });
            })()
            """;

        string result;
        try
        {
            result = await _launcher.Cdp!.EvaluateAsync(js, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, $"CDP 出错：{ex.Message}"); }

        if (result.StartsWith("OK|", StringComparison.Ordinal)) return (true, result[3..]);
        if (result.StartsWith("ERR|", StringComparison.Ordinal)) return (false, result[4..]);
        // 空串＝页面里抛了异常（EvaluateAsync 把 exceptionDetails 吞成空串）
        return (false, string.IsNullOrEmpty(result) ? "页面里执行出错" : result);
    }

    // ─────────────── 节奏 ───────────────

    /// <summary>
    /// 这一只开抓之前该等多久。歇的时候**每 20 秒报一句**：任务侧的静默看门狗阈值是
    /// 10 分钟，而我们一歇就是 2~5 分钟——不报的话它看不出我们是在按计划歇还是卡死了。
    /// 等待随 <paramref name="ct"/> 中断，而中断点落在**两只之间**，不会打断进行中的那只。
    /// </summary>
    private async Task PaceAsync(CancellationToken ct)
    {
        if (_first)
        {
            _first = false;
            _nextBatch = _rng.Next(_pace.BatchMin, _pace.BatchMax + 1);
        }
        else if (_codesSinceRest >= _nextBatch)
        {
            var rest = _pace.RestMin + (_pace.RestMax - _pace.RestMin) * _rng.NextDouble();
            var until = DateTime.Now + rest;
            OnStatus?.Invoke($"这批抓了 {_codesSinceRest} 只（累计 {_requestsSinceRest} 个请求），"
                           + $"按节奏主动歇 {rest.TotalMinutes:F1} 分钟到 {until:HH:mm:ss}"
                           + "——东财是按累计请求数切的，主动歇是唯一能避开的办法。");
            while (DateTime.Now < until)
            {
                var left = until - DateTime.Now;
                await Task.Delay(left < TimeSpan.FromSeconds(20) ? left : TimeSpan.FromSeconds(20), ct);
                if (DateTime.Now < until)
                    OnStatus?.Invoke($"主动歇着，还有 {(until - DateTime.Now).TotalSeconds:F0} 秒。");
            }
            _codesSinceRest = 0;
            _requestsSinceRest = 0;
            _nextBatch = _rng.Next(_pace.BatchMin, _pace.BatchMax + 1);
        }
        else
        {
            await Task.Delay(_pace.Delay * (0.7 + 0.6 * _rng.NextDouble()), ct);
        }
        _codesSinceRest++;
    }

    // ─────────────── 启动 ───────────────

    private async Task<bool> EnsureReadyAsync(CancellationToken ct)
    {
        if (_ready) return true;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_ready) return true;
            if (_initTried) return false;      // 试过一次不行就别反复折腾，每次都要几秒
            _initTried = true;
            _ready = await _launcher.StartAsync(HomeUrl, ct);
            return _ready;
        }
        finally { _initLock.Release(); }
    }

    public string ExhaustedVerdict(string? lastError) =>
        "浏览器这条也被切了。这是**按出口 IP 算的配额**（累计 16~35 个请求就切），"
      + "换网卡、换 Wi-Fi 都只是换一个出口、不解除限制——歇一阵它自己会恢复"
      + (_pausedUntil is { } t ? $"，这一项已自动暂停到 {t:HH:mm}" : "")
      + "。" + (string.IsNullOrEmpty(lastError) ? "" : $"最后一次：{lastError}");

    public async ValueTask DisposeAsync() => await _launcher.DisposeAsync();
}
