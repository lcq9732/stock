using System.Net;
using System.Text;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 板块列表的**完整性对账**（2026-09-04）——不联网，用假响应喂 push2 的翻页逻辑。
///
/// 盯的是一条静默数据损坏：上游 <c>UpsertBoards</c> 的语义是"这一轮没返回的板块＝已下架"，
/// 会连同 BoardMember、BoardMemberFetchState 一起删掉。而成分股是逐板块抓的、约 2500 个请求、
/// 跨好几轮才攒得齐——所以一份"半截列表"能删掉几百个板块的成分股，重抓要好几天，全程不报错。
///
/// 半截是怎么来的：push2 限流最常见的表现是断连或空响应（那两种 GetAsync 会抛异常，安全），
/// 但它也会返回**合法 JSON 而 data 为 null**——那条路上翻页循环只能 break，拿着前几页就返回。
/// 这份测试就是钉住"那种半截列表必须抛异常、不能交给上游写库"。
/// </summary>
public class BoardListCompletenessTests
{
    /// <summary>按顺序吐预设响应的假 handler：第 N 个请求拿第 N 条。</summary>
    private sealed class ScriptedHandler(params string[] responses) : HttpMessageHandler
    {
        private int _n;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = _n < responses.Length ? responses[_n] : responses[^1];
            _n++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>一页板块的正常响应：total 报 <paramref name="total"/>，这一页给 <paramref name="count"/> 条。</summary>
    private static string Page(int total, int count, int startIndex)
    {
        // 手拼 JSON 而不是用 raw string 插值：这段里 } 和 {{ 挨得太密，raw string 的转义规则
        // 读起来比字符串拼接还费劲。
        var items = Enumerable.Range(startIndex, count).Select(i =>
            "{\"f3\":1.5,\"f6\":1000,\"f12\":\"BK" + i.ToString("D4") + "\",\"f14\":\"板块" + i
            + "\",\"f128\":\"龙头\",\"f140\":\"600000\"}");
        return "{\"rc\":0,\"data\":{\"total\":" + total
             + ",\"diff\":[" + string.Join(",", items) + "]}}";
    }

    /// <summary>限流的另一种表现：HTTP 200、JSON 合法，但 data 是 null。</summary>
    private const string NullData = """{"rc":0,"data":null}""";

    private static EastMoneyBoardFetcher Make(params string[] responses) =>
        new(new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero),
            new HttpClient(new ScriptedHandler(responses)));

    [Fact]
    public async Task 两页都拿全_正常返回()
    {
        var f = Make(Page(150, 100, 1), Page(150, 50, 101));

        var boards = await f.FetchBoardListAsync(BoardType.Concept);

        Assert.Equal(150, boards.Count);
    }

    [Fact]
    public async Task 第二页被限流成空data_整轮抛异常而不是返回半截()
    {
        // 接口报 150 个，第一页给了 100 个，第二页 data:null——
        // 修之前这里会返回 100 个，上游据此把另外 50 个板块连同成分股删掉
        var f = Make(Page(150, 100, 1), NullData);

        var ex = await Assert.ThrowsAsync<RateLimitedException>(
            () => f.FetchBoardListAsync(BoardType.Concept));

        Assert.Contains("不完整", ex.Message);
        Assert.Contains("150", ex.Message);
    }

    [Fact]
    public async Task 第一页就空_照旧抛异常()
    {
        var f = Make(NullData);
        await Assert.ThrowsAsync<RateLimitedException>(() => f.FetchBoardListAsync(BoardType.Industry));
    }

    [Fact]
    public async Task 翻页期间出现重复板块_去重后条数对不上也要抛()
    {
        // 两页各 100 条但有 50 条重复：去重后 150 ≠ total 200。
        // 少的那 50 个同样会被上游当成"已下架"删掉，所以也不能放过。
        var f = Make(Page(200, 100, 1), Page(200, 100, 51));

        await Assert.ThrowsAsync<RateLimitedException>(() => f.FetchBoardListAsync(BoardType.Concept));
    }

    [Fact]
    public async Task 接口没给total时_不拦()
    {
        // total 缺失就没法对账（老接口或字段改名）——这时不该把整轮拦下来，
        // 否则接口一改版板块就彻底抓不了。这条钉住"对账只在拿得到 total 时生效"。
        const string noTotal = """{"rc":0,"data":{"diff":[{"f12":"BK0001","f14":"板块1"}]}}""";
        var f = Make(noTotal);

        var boards = await f.FetchBoardListAsync(BoardType.Concept);

        Assert.Single(boards);
    }
}
