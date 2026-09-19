using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 代码前缀判据（2026-09-19）。这个类是"六位代码 → 板块/交易所/是不是A股"的唯一权威处，
/// 各 provider 自己拼前缀规则害过一次（920 被当成沪市，342 只票静默抓不到）。
///
/// 这一组重点钉的是 <see cref="MarketClassifier.IsAShareCode"/> 跟
/// <see cref="MarketClassifier.Classify"/> **方向相反**这件事——两个方法对 43/83/87 的答案
/// 故意不一致，看着像 bug，其实是各自的用途决定的。不写下来，下一个人很容易"顺手统一"掉。
/// </summary>
public class MarketClassifierTests
{
    [Theory]
    [InlineData("000001")]   // 深市主板
    [InlineData("002731")]
    [InlineData("300280")]   // 创业板
    [InlineData("600519")]   // 沪市主板
    [InlineData("605001")]
    [InlineData("688086")]   // 科创板
    [InlineData("689009")]   // 科创板 CDR
    [InlineData("920305")]   // 北交所
    public void A股号段放行(string code) => Assert.True(MarketClassifier.IsAShareCode(code));

    [Theory]
    [InlineData("200002")]   // 深市B股
    [InlineData("900951")]   // 沪市B股
    [InlineData("832317")]   // 老三板 —— Classify 会把它判成北交所，见下一个用例
    [InlineData("833874")]
    [InlineData("873169")]
    [InlineData("430047")]
    [InlineData("12345")]    // 位数不对
    [InlineData("60051X")]   // 非纯数字
    public void 非A股号段拦下(string code) => Assert.False(MarketClassifier.IsAShareCode(code));

    /// <summary>
    /// ⭐ 两个方法对 83x 的答案**故意不一样**，别"统一"：
    /// · <c>Classify</c> 要回答"该用哪个前缀去抓这只票"——尽量认，83x 按北交所近似（920 迁移前的老号段）；
    /// · <c>IsAShareCode</c> 要回答"要不要把它收进我们的名单"——只放行确定的，83x 是新三板，几百只，
    ///   放进来会静默污染整个抓取清单。
    /// </summary>
    [Fact]
    public void 老三板号段_认得出板块但不算A股()
    {
        Assert.Equal(MarketBoard.Beijing, MarketClassifier.Classify("832317"));
        Assert.False(MarketClassifier.IsAShareCode("832317"));
    }

    [Fact]
    public void 北交所归到自己的交易所_不是沪市也不是深市()
    {
        // 【补全退市名单】原来写死 "Shanghai ? sse : szse"，920 会被记成深市。
        Assert.Equal(Exchange.Beijing, MarketClassifier.ExchangeOf("920305"));
        Assert.Equal(Exchange.Shanghai, MarketClassifier.ExchangeOf("600519"));
        Assert.Equal(Exchange.Shenzhen, MarketClassifier.ExchangeOf("000001"));
    }
}
