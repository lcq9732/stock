namespace StockPlatform.Logic.Services;

/// <summary>
/// 「浏览器自己的网络错误页」的判据（2026-09-22）——用来把**连接被切**跟**要人过的验证**分开。
///
/// ════ 为什么要分这两件事 ════
/// 浏览器通道取数失败时，JSONP 的 <c>script onerror</c> 和导航取数拿到的**都是空**，
/// 光看"拿没拿到数据"这两种情形长得一模一样：
///
///   · 东财弹了滑块验证  → 只有人能过，程序该停下来把页面原样摆给人看；
///   · 东财把连接切了    → 没有任何验证可过，人来了也只能把窗口关掉。
///
/// 2026-09-04 为了不漏掉前者，把"请人"的条件放宽成了"连试都不行就请人"。
/// 代价在 2026-09-21 晚上显形：东财那阵子把出口切得很死，于是每一轮限流都被当成
/// 需要人工验证——弹窗、把整条通道用 <c>_awaitingHuman</c> 挡住、干等人来关，
/// **一轮白卡十几分钟**（实测 00:00:58 弹窗，人来关掉之后 00:15:17 才报出第一页失败）。
/// 而那个窗口里只有一句 <c>ERR_EMPTY_RESPONSE</c>。
///
/// ════ 判据：认浏览器的错误页，不认东财的页面 ════
/// 挑的是 Chromium 错误页**特有**的标识，正常行情页和验证浮层都不会出现：
/// <c>ERR_</c> 开头的错误码是首选（语言无关，中英文版 Edge 都一样），
/// 另配几句错误页文案兜底。
///
/// ⚠ 判错的代价不对称，跟验证页那边正好相反：
///   · 漏判（是错误页但没认出来）＝ 回到原来的行为，弹窗等人，人关掉窗口就继续——难看但不丢数据；
///   · 误判（其实是验证页却当成错误页）＝ **不请人了**，那道验证永远过不去。
///   所以宁可漏判：调用方必须先问"是不是验证页"，只有那边说不是，才轮到这条判据。
/// </summary>
public static class BrowserErrorPage
{
    /// <summary>
    /// Chromium 错误页的标识。
    ///
    /// <c>ERR_</c> 系列是主判据——它出现在错误页正文里（"ERR_EMPTY_RESPONSE"），
    /// 跟界面语言无关。后面几句文案是兜底：万一哪个版本的错误页没把错误码写进正文。
    ///
    /// ⚠ 英文错误页用的是**弯撇号**（U+2019，didn’t），不是 ASCII 的 '。两种都列上——
    /// 只写 ASCII 那版的话，实测抓到的那个页面一个都匹配不上。
    /// </summary>
    private static readonly string[] Markers =
    [
        // 错误码（语言无关，最可靠）
        "ERR_EMPTY_RESPONSE", "ERR_CONNECTION_RESET", "ERR_CONNECTION_CLOSED",
        "ERR_CONNECTION_REFUSED", "ERR_CONNECTION_TIMED_OUT", "ERR_CONNECTION_ABORTED",
        "ERR_NAME_NOT_RESOLVED", "ERR_INTERNET_DISCONNECTED", "ERR_NETWORK_CHANGED",
        "ERR_TIMED_OUT", "ERR_SSL_PROTOCOL_ERROR", "ERR_TOO_MANY_REDIRECTS",
        // 错误页文案（中英文 Edge 实测；英文那两句带弯撇号）
        "didn’t send any data", "didn't send any data",
        "This page isn’t working", "This page isn't working",
        "未发送任何数据", "此页面无法正常运作", "无法访问此页面", "拒绝了我们的连接请求",
    ];

    /// <summary>
    /// 这段页面文字是不是浏览器自己的网络错误页。
    ///
    /// <paramref name="pageText"/> 传页面标题 + 正文即可（调用方怎么取由它决定）。
    /// 空字符串一律返回 false——"什么都没取到"不等于"是错误页"，那种情况按老路走。
    /// </summary>
    public static bool IsNetworkError(string? pageText)
    {
        if (string.IsNullOrWhiteSpace(pageText)) return false;
        foreach (var m in Markers)
            if (pageText.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
