using StockPlatform.Data.Remote;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 板块抓取的解析与对账 —— **全模拟，一个真实请求都不发**（2026-09-04）。
///
/// 为什么坚持不打真实接口：push2 的配额极紧（连发 5 个就可能被拒，撞掉之后几十分钟缓不过来），
/// 拿它验代码逻辑是把真正要用来取数的额度烧在测试上。而解析、翻页、对账这些恰恰跟网络无关——
/// 喂一段真实的返回报文就能测透。
///
/// 下面用的 JSON 片段来自 2026-09-04 实测抓到的真实响应（total=504 的概念板块列表）。
/// </summary>
public class BoardFetcherParsingTests
{
    private readonly ITestOutputHelper _out;
    public BoardFetcherParsingTests(ITestOutputHelper o) => _out = o;

    /// <summary>假的浏览器通道：按调用顺序吐预设好的报文，不碰网络。</summary>
    private sealed class FakeBrowser : IBrowserJsonFetcher
    {
        private readonly Queue<Func<string>> _replies;
        public List<string> Urls { get; } = [];

        public FakeBrowser(params Func<string>[] replies) => _replies = new(replies);

        public bool IsReady => true;
        public Task<bool> EnsureReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task ShowWorkWindowAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HideWorkWindowAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<string> GetJsonAsync(string url, CancellationToken ct = default)
        {
            Urls.Add(url);
            if (_replies.Count == 0) throw new InvalidOperationException("假数据源没货了");
            return Task.FromResult(_replies.Dequeue()());
        }
    }

    /// <summary>造一页板块列表的返回，格式跟东财真实响应一致。</summary>
    private static string ListJson(int count, int total, string prefix = "BK")
    {
        var diff = string.Join(",", Enumerable.Range(1, count).Select(i =>
            "{\"f3\":1.19,\"f6\":7154123242.0,\"f12\":\"" + prefix + i.ToString("0000")
            + "\",\"f14\":\"板块" + i + "\",\"f128\":\"领涨股\",\"f140\":\"600000\"}"));
        return "{\"rc\":0,\"rt\":6,\"svr\":180606400,\"lt\":1,\"full\":1,\"data\":{\"total\":"
             + total + ",\"diff\":[" + diff + "]}}";
    }

    /// <summary>造一页成分股的返回。<paramref name="offset"/> 让第 2 页的代码接着第 1 页排。</summary>
    private static string MemberJson(int count, int total, int offset = 0)
    {
        var diff = string.Join(",", Enumerable.Range(1, count).Select(i =>
            "{\"f12\":\"" + (600000 + offset + i).ToString("000000") + "\",\"f14\":\"股票" + (offset + i) + "\"}"));
        return "{\"rc\":0,\"data\":{\"total\":" + total + ",\"diff\":[" + diff + "]}}";
    }

    private static EastMoneyBoardFetcher New(FakeBrowser browser)
        => new(new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero, retryDelays: []),
               browser: browser);

    // ─────────────── 板块列表：按页抓 ───────────────

    [Fact]
    public async Task 满页时报告还没抓完_并带回接口自报的总数()
    {
        var f = New(new FakeBrowser(() => ListJson(100, 504)));
        var (items, total, isLast) = await f.FetchBoardListPageAsync(BoardType.Concept, 1);

        Assert.Equal(100, items.Count);
        Assert.Equal(504, total);
        Assert.False(isLast);                    // 满 100 条 → 后面还有
        Assert.Equal("BK0001", items[0].BoardCode);
        Assert.Equal(BoardType.Concept, items[0].Type);
    }

    [Fact]
    public async Task 不满一页就是最后一页()
    {
        // 504 个板块的第 6 页只有 4 个——这一页正是之前反复抓不到的那一页
        var f = New(new FakeBrowser(() => ListJson(4, 504)));
        var (items, total, isLast) = await f.FetchBoardListPageAsync(BoardType.Concept, 6);

        Assert.Equal(4, items.Count);
        Assert.True(isLast);
    }

    [Fact]
    public async Task 请求的页码和板块类型要拼进URL()
    {
        var browser = new FakeBrowser(() => ListJson(100, 504));
        await New(browser).FetchBoardListPageAsync(BoardType.Industry, 3);

        var url = browser.Urls[0];
        _out.WriteLine(url);
        Assert.Contains("pn=3", url);
        Assert.Contains("t:2", url);             // 行业是 t:2，概念是 t:3
        Assert.Contains("fid=f12", url);         // ⚠ 必须按代码排序，见下一个测试
    }

    [Fact]
    public async Task 排序必须按代码而不是涨跌幅()
    {
        // fid=f3（涨跌幅）盘中一直在变，翻页期间排序在动，会跨页重复和遗漏——
        // 实测用 f3 抓 141 只的板块，两页 141 行去重后只剩 138 只。
        var browser = new FakeBrowser(() => ListJson(100, 504));
        await New(browser).FetchBoardListPageAsync(BoardType.Concept, 1);

        Assert.Contains("fid=f12", browser.Urls[0]);
        Assert.DoesNotContain("fid=f3", browser.Urls[0]);
    }

    [Fact]
    public async Task 第一页返回空内容时判定为限流()
    {
        // push2 限流除了断连，也会返回**合法 JSON 但 data 为 null**。
        // 第 1 页就这样＝这一轮什么都没拿到，得让调用方知道是限流而不是"没有板块了"，
        // 否则会被当成"抓完了"，然后拿一份空名单去提交、把正表清空。
        var f = New(new FakeBrowser(() => """{"rc":0,"data":null}"""));
        await Assert.ThrowsAsync<RateLimitedException>(
            () => f.FetchBoardListPageAsync(BoardType.Concept, 1));
    }

    [Fact]
    public async Task 后续页返回空内容时当作抓完了()
    {
        // 翻到没有更多数据时接口也会给 data:null，这跟第 1 页的含义不一样
        var f = New(new FakeBrowser(() => """{"rc":0,"data":null}"""));
        var (items, _, isLast) = await f.FetchBoardListPageAsync(BoardType.Concept, 7);

        Assert.Empty(items);
        Assert.True(isLast);
    }

    // ─────────────── 成分股：漏一只都不行 ───────────────

    [Fact]
    public async Task 成分股条数跟接口自报的total对不上要拒绝()
    {
        // 这是数据质量的最后一道闸：实测 F10 报表抓液冷服务器会漏 4 只（含美的集团、拓普集团
        // 这种链上有实际业务的大票），而漏了不报错。宁可这一轮不写，也不能把残缺名单当成分股。
        var f = New(new FakeBrowser(() => MemberJson(96, 100)));
        var ex = await Assert.ThrowsAsync<RateLimitedException>(
            () => f.FetchMembersAsync("BK1137"));

        _out.WriteLine(ex.Message);
        Assert.Contains("100", ex.Message);
        Assert.Contains("96", ex.Message);
    }

    [Fact]
    public async Task 成分股条数对得上就正常返回()
    {
        var f = New(new FakeBrowser(() => MemberJson(37, 37)));
        var members = await f.FetchMembersAsync("BK1137");

        Assert.Equal(37, members.Count);
        Assert.Equal("600001", members[0]);
    }

    [Fact]
    public async Task 成分股满页时会继续翻页直到取够()
    {
        // 成分多的板块要翻第 2 页；两页加起来必须等于 total
        var f = New(new FakeBrowser(() => MemberJson(100, 137), () => MemberJson(37, 137, offset: 100)));
        var members = await f.FetchMembersAsync("BK1137");

        Assert.Equal(137, members.Count);
        Assert.Equal(137, members.Distinct().Count());   // 翻页不能重复
    }

    [Fact]
    public async Task 空板块正常返回空名单不报错()
    {
        // 新建的板块可能一只成分股都没有，这不是错误
        var f = New(new FakeBrowser(() => """{"rc":0,"data":{"total":0,"diff":[]}}"""));
        Assert.Empty(await f.FetchMembersAsync("BK9999"));
    }
}
