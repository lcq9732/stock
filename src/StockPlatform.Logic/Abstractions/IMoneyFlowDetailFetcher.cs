using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 逐股分档资金流的一条**取数通道**（2026-09-14）。实现有两个，配置 <c>MoneyFlowBackfillTransport</c> 二选一：
///
/// | 通道 | 谁发的请求 | 状态 |
/// |---|---|---|
/// | <c>EastMoneyMoneyFlowProvider</c> | 程序里的 HttpClient | 本机网关按域名把 push2his 拦了，**发不出去** |
/// | <c>ChromeCdpMoneyFlowFetcher</c> | 真 Chrome/Edge 里的 JSONP | 实测能稳定取到，现在的默认 |
///
/// ⚠ 两条通道的 URL、fields、解析**完全一样**（见 <see cref="Services.MoneyFlowKlineParser"/>），
/// 差别只在请求由谁发出去——所以随便切，写进库的数据不会有差异。
///
/// 抽成接口而不是在一个类里加分支，是因为两条的失败形状差得远：HttpClient 那条能拿到
/// "连不上/超时"的底层原因，浏览器那条只知道"脚本没加载成功"；节奏参数、熔断阈值、
/// 收尾时该跟人说什么，也各是各的。<see cref="ExhaustedVerdict"/> 就是为此留的。
/// </summary>
public interface IMoneyFlowDetailFetcher
{
    /// <summary>进度／限流状态，由任务转发到程序日志（同时喂看门狗）。</summary>
    event Action<string>? OnStatus;

    /// <summary>这条通道叫什么，日志里要说清楚现在走的是哪条。</summary>
    string ChannelName { get; }

    /// <summary>熔断还要等到几点；没在暂停就是 null。暂停期里任务直接不开工。</summary>
    DateTime? PausedUntil { get; }

    /// <summary>
    /// 连续失败多少只就收尾。通道自报：HttpClient 那条 15 只，浏览器那条 25 只
    /// （跟已验证的 moneyflow-fetch.html 一致）。继续打只会让封禁更久。
    /// </summary>
    int GiveUpAfterConsecutiveFailures { get; }

    /// <summary>
    /// 连续失败收尾时跟人说什么。**必须由通道自己判**：
    /// "连不上"（要换网络，等多久都不会好）和"被限流"（等着就行）的处置完全相反，
    /// 2026-09-11 那次把网关拦截报成限流，就把人往"等一会儿就好了"的方向带了一整天。
    /// </summary>
    /// <param name="lastError">最后一次失败的原因，可能为 null。</param>
    string ExhaustedVerdict(string? lastError);

    /// <summary>抓一只股票的分档资金流（最近约 120 个交易日）。</summary>
    /// <remarks>
    /// 返回空列表＝接口说这只没数据（停牌/退市/secid 拼错），**不是失败**；
    /// 抓不到要抛异常。节奏（间隔、主动歇）由实现自己在这里面把控。
    /// </remarks>
    Task<List<NetInflowDetail>> FetchAsync(string code, CancellationToken ct = default);
}
