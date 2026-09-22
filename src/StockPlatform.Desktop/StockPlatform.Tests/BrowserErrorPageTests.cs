using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 「浏览器网络错误页」判据（<see cref="BrowserErrorPage"/>）。
///
/// 这条判据用来把**连接被切**和**要人过的验证**分开：两者在取数那一层长得一模一样（都是空），
/// 而处理方式正相反——前者请人也没用，后者不请人就永远过不去。
///
/// 2026-09-21 晚上因为分不开，每一轮限流都弹窗等人，一轮白卡十几分钟；
/// 那个窗口里只有一句 ERR_EMPTY_RESPONSE。下面第一条用例就是那次抓到的原文。
/// </summary>
public class BrowserErrorPageTests
{
    [Fact]
    public void 认得出实测抓到的那个错误页()
    {
        // 2026-09-22 00:14 从误弹的"人工验证"窗口里读出来的原文，一个字没改。
        // ⚠ didn’t 用的是弯撇号（U+2019）——只匹配 ASCII 的 ' 会一个都认不出来。
        const string real = "This page isn’t working right now | push2delay.eastmoney.com | " +
                            " didn’t send any data. | ERR_EMPTY_RESPONSE | Microsoft Edge";

        Assert.True(BrowserErrorPage.IsNetworkError(real));
    }

    [Theory]
    [InlineData("ERR_CONNECTION_RESET")]
    [InlineData("ERR_CONNECTION_TIMED_OUT")]
    [InlineData("ERR_NAME_NOT_RESOLVED")]
    [InlineData("此页面无法正常运作 push2delay.eastmoney.com 未发送任何数据")]
    [InlineData("err_empty_response")]                 // 大小写不敏感
    public void 各种断线形态都算错误页(string text)
        => Assert.True(BrowserErrorPage.IsNetworkError(text));

    [Fact]
    public void 验证浮层不能被当成错误页()
    {
        // 误判的代价是**不再请人**，那道验证就永远过不去——所以宁可漏判也别误判。
        // 这是东财实测的滑块浮层原话。
        const string challenge = "行情中心 沪深京A股 拖动下方滑块完成拼图 安全验证";

        Assert.False(BrowserErrorPage.IsNetworkError(challenge));
    }

    [Fact]
    public void 正常行情页不算()
    {
        const string page = "行情中心：国内快捷全面的股票、基金、期货、美股、港股行情系统_东方财富网 " +
                            "沪深京A股 涨幅榜 资金流向 验证码登录";

        Assert.False(BrowserErrorPage.IsNetworkError(page));
    }

    [Fact]
    public void 接口返回的JSON不算()
    {
        Assert.False(BrowserErrorPage.IsNetworkError("{\"rc\":0,\"data\":{\"total\":5917,\"diff\":[]}}"));
    }

    [Fact]
    public void 什么都没取到不算错误页()
    {
        // "空"不等于"错误页"——取不到页面内容时按老路走（该请人还是请人），
        // 不能因为拿不到就断定是限流、把真验证也挡掉。
        Assert.False(BrowserErrorPage.IsNetworkError(null));
        Assert.False(BrowserErrorPage.IsNetworkError(""));
        Assert.False(BrowserErrorPage.IsNetworkError("   "));
    }
}
