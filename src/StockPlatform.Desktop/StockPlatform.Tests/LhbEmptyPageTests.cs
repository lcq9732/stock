using System.Net;
using System.Text;
using StockPlatform.Data.Remote;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 新浪龙虎榜"0 行"的骨架校验（2026-09-08）。
///
/// 为什么要它：0 行有两种含义，**分不开就会永久漏数据**——
///   · 这天真没有（非交易日/无人上榜）→ 记进 DailyFetchNoData，往后不再重试；
///   · 拿到的根本不是那张页（反爬拦截）→ 要是也当成"这天没有"记下来，那天就**再也不会被抓**了
///     （那张表是一次定案的）。
/// 实测（2026-09-08 沙箱）：2026-09-06 周日和 2003-01-05 都返回 27,365 字节的正常框架页，
/// 含"龙虎榜""tradedate"；有数据的 2026-09-04 是 264KB、195 条数据行。
/// </summary>
public class LhbEmptyPageTests
{
    private sealed class StubHandler(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var bytes = Encoding.GetEncoding("GBK").GetBytes(html);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private static SinaLhbProvider Provider(string html)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return new SinaLhbProvider(new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero),
                                   new HttpClient(new StubHandler(html)));
    }

    /// <summary>正常的"这天没有数据"页：有骨架、没有数据行 → 0 行，不抛。调用方据此定案。</summary>
    [Fact]
    public async Task 正常空页返回零行()
    {
        var html = "<html><body><div>沪深股市每日龙虎榜</div>" +
                   "<form><input name='tradedate' value='2026-09-06'></form>" +
                   "<table><tr><td>没有找到相应的数据</td></tr></table></body></html>";
        var rows = await Provider(html).GetDailyAsync(new DateOnly(2026, 9, 6));
        Assert.Empty(rows);
    }

    /// <summary>反爬/错误页：没有骨架 → 必须抛，绝不能被当成"这天没有数据"。</summary>
    [Fact]
    public async Task 空壳页要抛异常()
    {
        var html = "<html><body>访问过于频繁，请稍后再试</body></html>";
        await Assert.ThrowsAsync<RateLimitedException>(
            () => Provider(html).GetDailyAsync(new DateOnly(2026, 9, 6)));
    }

    /// <summary>有数据行的页面不走骨架校验——数据行本身就是最好的骨架证明。</summary>
    [Fact]
    public async Task 有数据的页面正常解析()
    {
        var html = "<html><body>" +
                   "<tr><td><span style='font-weight:bold'>日涨幅偏离值达到7%的证券</span></td></tr>" +
                   "<tr><td>1</td><td><a href='lookup_n.php?q=600519'>600519</a></td><td>贵州茅台</td>" +
                   "<td>1500.00</td><td>8.5</td><td>1234</td><td>56789</td><td>详情</td></tr>" +
                   "</body></html>";
        var rows = await Provider(html).GetDailyAsync(new DateOnly(2026, 9, 4));
        Assert.Single(rows);
        Assert.Equal("600519", rows[0].StockCode);
        Assert.Equal("日涨幅偏离值达到7%的证券", rows[0].Reason);
    }
}
