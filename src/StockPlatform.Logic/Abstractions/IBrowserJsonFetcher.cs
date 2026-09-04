namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 用**真实浏览器内核**去取 JSON（2026-09-04）。
///
/// 为什么需要它——东财 push2 给浏览器和给程序的待遇差 5 倍，实测（同一 IP、同一接口、相近时间）：
///
/// | 客户端                  | TLS 栈              | 成功率        | 实际吞吐    |
/// |------------------------|--------------------|--------------|-----------|
/// | 真浏览器（JSONP）        | BoringSSL(Chrome)  | 18/20 (90%)  | 27 个/分钟 |
/// | curl                   | Schannel(Windows)  | 6/8          | —         |
/// | python 脚本             | OpenSSL            | ~35%         | —         |
/// | 程序 HttpClient         | Schannel           | 第 7 个就断   | 4.8 个/分钟|
///
/// 排查过程里请求头、Cookie 值、连接复用（keep-alive vs 每次新建）、HTTP/2 都单独测过，
/// 全都不是原因；剩下的只能是 <b>TLS 指纹</b>（Chrome 的 ClientHello 跟 .NET 的 Schannel 不同）
/// 加上东财服务器自己种的真实 Cookie。.NET 改不了 TLS 指纹——Schannel 不给定制 ClientHello，
/// 所以想拿到浏览器那个待遇，只能让请求真的从浏览器内核发出去。
///
/// 实现在 Desktop 层（<c>WebView2JsonFetcher</c>）——WebView2 是 Windows 桌面依赖，
/// Data/Logic 这两层不该知道它的存在，所以这里只留接口。
/// </summary>
public interface IBrowserJsonFetcher : IAsyncDisposable
{
    /// <summary>
    /// 浏览器内核准备好了没（初始化 + 导航到东财页面拿 Cookie 和同源环境）。
    /// 返回 false 表示这台机器上用不了（没装 WebView2 运行时之类），调用方应退回 HttpClient。
    /// </summary>
    Task<bool> EnsureReadyAsync(CancellationToken ct = default);

    /// <summary>
    /// 用 JSONP 取一个东财接口，返回原始 JSON 文本。
    /// 走 JSONP 而不是 fetch：东财这些接口本来就是给页面上的 &lt;script&gt; 用的，
    /// fetch 会撞跨域（实测 <c>credentials:'omit'</c> 直接 Failed to fetch）。
    /// </summary>
    Task<string> GetJsonAsync(string url, CancellationToken ct = default);

    /// <summary>上一次 <see cref="EnsureReadyAsync"/> 的结论；没准备好时调用方走回退。</summary>
    bool IsReady { get; }

    /// <summary>
    /// 抓取期间把浏览器窗口开着，让人实时看见东财那边在发生什么——
    /// 数据在刷、弹了图片验证码、还是页面打不开。摆在角落、不抢焦点，嫌碍事可以直接关。
    /// </summary>
    Task ShowWorkWindowAsync(CancellationToken ct = default);

    /// <summary>抓完把观察窗收起来。</summary>
    Task HideWorkWindowAsync();
}
