using System.Net;
using System.Text;
using StockPlatform.Data.Remote;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 新浪列表接口的两个坑（2026-09-16）——见 <see cref="SinaStockListProvider"/> /
/// <see cref="SinaEtfListProvider"/>。全部用假 HttpClient，一个真请求都不发。
///
/// ① **`hs_a` 不含科创板 CDR**。实测：按 symbol 升序第 27 页从 sh688718 一路排到 sz000060，
///    沪市段结束后直接进深市，中间没有 sh689009。后果是 689009 九号公司从来没进过 StockMeta，
///    于是一根K线、一条财务、一条分红都没有，而它的两融数据到 2026-09-14 还在更新。
///    修法是翻完 hs_a 再整个翻一遍 kcb、按 code 去重合并。
///
/// ② **限流时接口回字面量 `null`，不是空数组**。以前这里把 null 也当成"空页"返回空列表，
///    调用方 `if (pageEntries.Count == 0) break;` 就当成翻过了末页——限流那一刻之后的整段名单
///    全部丢掉，**一条错误都不报**。真末页回的是 `[]`（实测 etf_hq_fund 有效页到 17、page 19
///    回 `[]`），两者必须分开。
///
/// 见 doc/missing-instruments-design.md。
/// </summary>
public class SinaListProviderTests
{
    static SinaListProviderTests() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // 接口回 GBK

    private static string Row(string symbol, string code, string name) =>
        $$"""{"symbol":"{{symbol}}","code":"{{code}}","name":"{{name}}","nmc":123456,"trade":"10.000"}""";

    private static string Arr(params string[] rows) => "[" + string.Join(",", rows) + "]";

    /// <summary>
    /// ⭐ 翻完 hs_a 还要翻 kcb，才能把 689009 拿到手；重叠的 688 段按 code 去重，不重复。
    /// </summary>
    [Fact]
    public async Task 个股名单翻完hs_a还翻kcb_把科创板CDR补进来()
    {
        var handler = new StubHandler(url =>
        {
            bool p1 = url.Contains("page=1&");
            if (url.Contains("node=hs_a"))
                return p1 ? Arr(Row("sh600000", "600000", "浦发银行"),
                                Row("sh688001", "688001", "华兴源创")) : "[]";
            if (url.Contains("node=kcb"))
                // kcb 的 688 段跟 hs_a 完全重叠，只有 689009 是新的
                return p1 ? Arr(Row("sh688001", "688001", "华兴源创"),
                                Row("sh689009", "689009", "九号公司")) : "[]";
            return "[]";
        });

        var list = await new SinaStockListProvider(new HttpClient(handler)).GetAllStocksAsync();

        Assert.Equal(3, list.Count);                                   // 688001 没被算两次
        Assert.Contains(list, x => x.Code == "689009");                // ⭐ 就是它
        Assert.Single(list.Where(x => x.Code == "688001"));
        Assert.Contains(handler.Requests, u => u.Contains("node=kcb"));
    }

    /// <summary>空数组才是末页——正常结束，不抛。</summary>
    [Fact]
    public async Task 空数组是末页_正常结束()
    {
        var handler = new StubHandler(url =>
            url.Contains("page=1&") ? Arr(Row("sh600000", "600000", "浦发银行")) : "[]");

        var list = await new SinaStockListProvider(new HttpClient(handler)).GetAllStocksAsync();

        Assert.Single(list);
    }

    /// <summary>
    /// ⭐ `null` 是**限流**，不是末页：要走重试，重试耗尽就整轮失败——
    /// 失败是响亮的，静默截断不是。
    ///
    /// ⚠ 这个测试会跑十几秒：provider 的重试延时是 2s + 10s（真实限流就得等这么久才有意义）。
    /// </summary>
    [Fact]
    public async Task 返回null当成限流_不当末页()
    {
        var handler = new StubHandler(url =>
            url.Contains("page=1&") ? Arr(Row("sh600000", "600000", "浦发银行")) : "null");

        var provider = new SinaStockListProvider(new HttpClient(handler));

        await Assert.ThrowsAsync<RateLimitedException>(() => provider.GetAllStocksAsync());
        // 第 2 页被试了 3 次（首次 + 两次重试），而不是被当成末页 break 掉
        Assert.Equal(3, handler.Requests.Count(u => u.Contains("page=2&")));
    }

    /// <summary>ETF 名单同一个坑，同一个修法。</summary>
    [Fact]
    public async Task ETF名单返回null也当成限流()
    {
        var handler = new StubHandler(url =>
            url.Contains("page=1&") ? Arr(Row("sh510300", "510300", "沪深300ETF")) : "null");

        var provider = new SinaEtfListProvider(new HttpClient(handler));

        await Assert.ThrowsAsync<RateLimitedException>(() => provider.GetAllStocksAsync());
    }

    /// <summary>ETF 的 Code 必须是**带前缀的 8 位符号**——裸码会让 ETF 漏进个股选股全集
    /// （GetAllCodes 只认 6 位纯数字）。</summary>
    [Fact]
    public async Task ETF名单返回带前缀的八位符号()
    {
        var handler = new StubHandler(url =>
            url.Contains("page=1&") ? Arr(Row("sh510300", "510300", "沪深300ETF"),
                                          Row("sz159915", "159915", "创业板ETF")) : "[]");

        var list = await new SinaEtfListProvider(new HttpClient(handler)).GetAllStocksAsync();

        Assert.Equal(new[] { "sh510300", "sz159915" }, list.Select(x => x.Code));
    }

    /// <summary>按 URL 回假数据的 HttpClient，并记下每个请求，好断言"重试了几次/翻了哪些节点"。</summary>
    private sealed class StubHandler(Func<string, string> reply) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests) Requests.Add(url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                // 接口回 GBK，provider 也按 GBK 解——假数据得走同一条路
                Content = new ByteArrayContent(Encoding.GetEncoding("GBK").GetBytes(reply(url))),
            });
        }
    }
}
