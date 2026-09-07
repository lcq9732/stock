using System.Net.Http;
using StockPlatform.Data.Remote;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 分档资金流**全市场快照**那条通道的解析（2026-09-06）。
///
/// 为什么值得专门钉：这一路写的是全市场每只票当天那一行，错了不会报错，只会让库里多出
/// 一批看着很合理的假值——把停牌股的 <c>"-"</c> 解析成 0，界面上就是"主力净额 0"，
/// 跟"这天没数据"完全分不出来；把 f66（超大单）和 f72（大单）接反了更是查都没法查。
///
/// 下面的报文都是 2026-09-04 从 push2delay 抓回来的**真实响应**，只删了无关字段。
/// </summary>
public class MoneyFlowSnapshotTests
{
    /// <summary>抓取时刻：解析阶段原样写进每行，跟数据内容无关，随便给一个。</summary>
    private static readonly DateTime FetchedAt = new(2026, 9, 6, 12, 0, 0);

    // 000006 深振业A，2026-09-04 收盘后那一版。f124=1788507240 → 2026-09-04 15:34。
    private const string OneRow = """
        {"rc":0,"data":{"total":5909,"diff":[
          {"f2":7.01,"f3":0.43,"f12":"000006","f62":-878110.0,"f66":-2844000.0,"f69":-2.16,
           "f72":1965890.0,"f75":1.49,"f78":-5148156.0,"f81":-3.91,"f84":6026266.0,"f87":4.58,
           "f124":1788507240,"f184":-0.67}]}}
        """;

    // 000004/000005 停牌：所有数值字段都是字符串 "-"，时间戳是当天 08:00。
    private const string SuspendedRows = """
        {"rc":0,"data":{"total":5909,"diff":[
          {"f2":"-","f3":"-","f12":"000004","f62":"-","f66":"-","f69":"-","f72":"-","f75":"-",
           "f78":"-","f81":"-","f84":"-","f87":"-","f124":1788480000,"f184":"-"},
          {"f2":"-","f3":"-","f12":"000005","f62":"-","f66":"-","f69":"-","f72":"-","f75":"-",
           "f78":"-","f81":"-","f84":"-","f87":"-","f124":1788480000,"f184":"-"}]}}
        """;

    [Fact]
    public void 十三个字段要一一对上()
    {
        var page = EastMoneyMoneyFlowSnapshotProvider.ParsePage(OneRow, FetchedAt);

        Assert.NotNull(page);
        Assert.Equal(5909, page!.Total);
        var r = Assert.Single(page.Rows);

        Assert.Equal("000006", r.Code);
        Assert.Equal(new DateTime(2026, 9, 4), r.TradeDate);
        // 净额：f62 主力 = f66 超大单 + f72 大单，拿这条恒等式反查有没有接错线
        Assert.Equal(-878110.0, r.MainNet);
        Assert.Equal(-2844000.0, r.SuperNet);
        Assert.Equal(1965890.0, r.BigNet);
        Assert.Equal(r.SuperNet!.Value + r.BigNet!.Value, r.MainNet!.Value, 3);
        Assert.Equal(-5148156.0, r.MidNet);
        Assert.Equal(6026266.0, r.SmallNet);
        // 占比（%）
        Assert.Equal(-0.67, r.MainRatio);
        Assert.Equal(-2.16, r.SuperRatio);
        Assert.Equal(1.49, r.BigRatio);
        Assert.Equal(-3.91, r.MidRatio);
        Assert.Equal(4.58, r.SmallRatio);
        // 顺带给的行情
        Assert.Equal(7.01, r.ClosePrice);
        Assert.Equal(0.43, r.ChangeRate);
        Assert.Equal(FetchedAt, r.FetchedAt);
    }

    [Fact]
    public void 停牌股整行不要绝不能变成零()
    {
        // "主力净额 0"是个看着完全合理的假值——它跟"这天没有数据"在库里长得一模一样，
        // 之后做因子时也不会有任何地方报错。所以停牌行必须整行丢掉。
        var page = EastMoneyMoneyFlowSnapshotProvider.ParsePage(SuspendedRows, FetchedAt);

        Assert.NotNull(page);
        Assert.Empty(page!.Rows);
        Assert.Equal(2, page.Suspended);
    }

    [Fact]
    public void 交易日取整批最新的时间戳()
    {
        // 停牌股的时间戳停在当天 08:00、正常股是收盘后的 15:3x：
        // 取最大才拿得到"这批属于哪个交易日、收盘了没有"。
        var mixed = """
            {"rc":0,"data":{"total":5909,"diff":[
              {"f2":"-","f3":"-","f12":"000004","f62":"-","f124":1788480000},
              {"f2":7.01,"f3":0.43,"f12":"000006","f62":-878110.0,"f66":-2844000.0,"f69":-2.16,
               "f72":1965890.0,"f75":1.49,"f78":-5148156.0,"f81":-3.91,"f84":6026266.0,"f87":4.58,
               "f124":1788507240,"f184":-0.67}]}}
            """;

        var page = EastMoneyMoneyFlowSnapshotProvider.ParsePage(mixed, FetchedAt);

        Assert.NotNull(page);
        Assert.Single(page!.Rows);
        Assert.Equal(1, page.Suspended);
        Assert.Equal(new DateTime(2026, 9, 4, 15, 34, 0), page.QuoteTime);
    }

    [Fact]
    public void 没收盘清算的一批要认得出来()
    {
        // 盘中（含午休，那会儿时间戳停在 11:30）拿到的是半天的资金流。
        // 写进库会污染当天那一行，而且事后完全看不出来——所以要在这里就判出来。
        var intraday = new MoneyFlowSnapshot(new DateTime(2026, 9, 4, 11, 30, 0), 5909, 0, []);
        var noon = new MoneyFlowSnapshot(new DateTime(2026, 9, 4, 14, 59, 0), 5909, 0, []);
        var settled = new MoneyFlowSnapshot(new DateTime(2026, 9, 4, 15, 34, 0), 5909, 0, []);

        Assert.True(intraday.IsIntraday);
        Assert.True(noon.IsIntraday);
        Assert.False(settled.IsIntraday);
        Assert.Equal(new DateTime(2026, 9, 4), settled.TradeDate);
    }

    [Fact]
    public void 解析不了要返回null而不是空页()
    {
        // 空页会让调用方以为"翻到头了"、提前收工，把半个市场当成全市场写进库——
        // 缺的那些票当天就静默地没有数据。所以这两种情况必须分得开。
        Assert.Null(EastMoneyMoneyFlowSnapshotProvider.ParsePage("", FetchedAt));
        Assert.Null(EastMoneyMoneyFlowSnapshotProvider.ParsePage("<html>502</html>", FetchedAt));
        Assert.Null(EastMoneyMoneyFlowSnapshotProvider.ParsePage("""{"rc":0,"data":{"total":1,"dif""",
                                                                 FetchedAt));
        Assert.Null(EastMoneyMoneyFlowSnapshotProvider.ParsePage("""{"rc":0}""", FetchedAt));
    }

    [Fact]
    public void 翻过头那一页是data为null()
    {
        // 这个才是"翻到头了"的正常表现，跟解析失败不是一回事
        var page = EastMoneyMoneyFlowSnapshotProvider.ParsePage("""{"rc":0,"data":null}""", FetchedAt);

        Assert.NotNull(page);
        Assert.Empty(page!.Rows);
        Assert.Equal(0, page.Suspended);
    }

    [Fact]
    public void f12是数字形态时要补回前导零()
    {
        // 东财同一个字段有时给字符串有时给数字，数字会把 000001 吃成 1
        var body = """
            {"rc":0,"data":{"total":1,"diff":[
              {"f12":1,"f2":11.5,"f3":0.1,"f62":123.0,"f124":1788507240}]}}
            """;

        var page = EastMoneyMoneyFlowSnapshotProvider.ParsePage(body, FetchedAt);

        Assert.NotNull(page);
        Assert.Equal("000001", Assert.Single(page!.Rows).Code);
    }

    [Fact]
    public void 缺时间戳的行不要()
    {
        // 没有 f124 就定不了这行属于哪个交易日。贴上"今天"是最坏的选择——
        // 非交易日跑的时候，那会把上一交易日的数据写成今天的。
        var body = """
            {"rc":0,"data":{"total":1,"diff":[{"f12":"600000","f2":11.5,"f3":0.1,"f62":123.0}]}}
            """;

        var page = EastMoneyMoneyFlowSnapshotProvider.ParsePage(body, FetchedAt);

        Assert.NotNull(page);
        Assert.Empty(page!.Rows);
        Assert.Equal(1, page.Suspended);
    }
}

/// <summary>
/// 快照通道的**翻页与收尾**（2026-09-06）。解析对了不代表这一路是对的：
/// 翻页少翻一页、被同一页糊弄住、把 fetched_at 写成"现在"，都不会报错，
/// 后果分别是当天少几百只、卡在 200 页保险丝、以及补历史那条路的排队彻底失灵。
/// </summary>
public class MoneyFlowSnapshotPagingTests
{
    /// <summary>按页号给固定报文的假服务端；顺带记下一共被打了几次。</summary>
    private sealed class PagedHandler(Func<int, string> body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var pn = int.Parse(System.Text.RegularExpressions.Regex
                .Match(request.RequestUri!.Query, @"pn=(\d+)").Groups[1].Value);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body(pn)),
            });
        }
    }

    private static EastMoneyMoneyFlowSnapshotProvider NewProvider(PagedHandler handler) =>
        new(new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero),
            new HttpClient(handler));

    private const long Settled = 1788507240;   // 2026-09-04 15:34
    private const long PreOpen = 1788480000;   // 2026-09-04 08:00（停牌股就停在这儿）

    private static string Row(string code, long stamp = Settled) =>
        $$"""{"f12":"{{code}}","f2":10.0,"f3":1.0,"f62":100.0,"f66":60.0,"f69":0.6,"f72":40.0,"f75":0.4,"f78":-30.0,"f81":-0.3,"f84":-70.0,"f87":-0.7,"f124":{{stamp}},"f184":1.0}""";

    // 拼字符串而不是用原始字符串插值：报文末尾正好是连续的 }}，在 $$"""…""" 里那是插值边界
    private static string Page(int total, params string[] rows) =>
        "{\"rc\":0,\"data\":{\"total\":" + total + ",\"diff\":[" + string.Join(",", rows) + "]}}";

    [Fact]
    public async Task 拿够total就收手()
    {
        // 3 只里 1 只停牌：拿到 2 行 + 1 只停牌 = 3，够了就不该再翻第二页
        var suspended = """{"f12":"000004","f2":"-","f62":"-","f124":1788480000}""";
        var handler = new PagedHandler(_ => Page(3, Row("000001"), Row("000002"), suspended));

        var snap = await NewProvider(handler).FetchAllAsync();

        Assert.Equal(1, handler.Calls);
        Assert.Equal(2, snap.Rows.Count);
        Assert.Equal(1, snap.Suspended);
        Assert.Equal(3, snap.Total);
    }

    [Fact]
    public async Task 翻到空页也收手()
    {
        // total 自报得比实际多（服务端口径跟 fs 过滤对不上时会这样）：翻到空页就得停，
        // 不能一路打到保险丝
        var handler = new PagedHandler(pn => pn == 1 ? Page(9999, Row("000001")) : Page(9999));

        var snap = await NewProvider(handler).FetchAllAsync();

        Assert.Equal(2, handler.Calls);
        Assert.Single(snap.Rows);
    }

    [Fact]
    public async Task 每页都还回同一批也要收手()
    {
        // 分页参数没被理会时的形态：页页都是第一页。只看"这一页空不空"的话会一直翻下去。
        var handler = new PagedHandler(_ => Page(9999, Row("000001"), Row("000002")));

        var snap = await NewProvider(handler).FetchAllAsync();

        Assert.Equal(2, handler.Calls);      // 第二页发现什么都没新增，停
        Assert.Equal(2, snap.Rows.Count);
    }

    [Fact]
    public async Task 整批的抓取时刻写成行情时间()
    {
        // ⚠ 这条是补历史那条路的排队命根子：写成"现在"的话，全市场 5900 只的 fetched_at
        // 每天都被刷成同一个值，"最久没抓的先抓"就退化成原始顺序，靠后的票永远轮不到。
        var handler = new PagedHandler(pn => pn == 1 ? Page(2, Row("000001"), Row("000002")) : Page(2));

        var snap = await NewProvider(handler).FetchAllAsync();

        Assert.All(snap.Rows, r => Assert.Equal(new DateTime(2026, 9, 4, 15, 34, 0), r.FetchedAt));
        Assert.Equal(new DateTime(2026, 9, 4), snap.TradeDate);
        Assert.False(snap.IsIntraday);
    }

    [Fact]
    public async Task 日期跟整批对不上的行要剔掉()
    {
        // 长期停牌但还残留着旧值的票：它的时间戳停在别的交易日，不能贴上今天的日期入库
        var stale = Row("000003", new DateTimeOffset(new DateTime(2026, 8, 20, 15, 34, 0))
            .ToUnixTimeSeconds());
        var handler = new PagedHandler(pn => pn == 1 ? Page(2, Row("000001"), stale) : Page(2));

        var snap = await NewProvider(handler).FetchAllAsync();

        Assert.Equal(["000001"], snap.Rows.Select(r => r.Code));
    }

    [Fact]
    public async Task 中间某页解析不了要报错而不是当成结束()
    {
        // 被限流截断的那一页长这样。当成"翻到头了"的话，这一天就只剩前半个市场，
        // 而且没有任何地方会提示。
        var handler = new PagedHandler(pn => pn == 1 ? Page(500, Row("000001")) : "<html>502</html>");

        await Assert.ThrowsAsync<RateLimitedException>(() => NewProvider(handler).FetchAllAsync());
    }
}

/// <summary>
/// 分档资金流逐股通道的 secid 前缀（2026-09-06）。
///
/// 为什么要钉死：拼错的后果是**东财回 <c>rc:100 / data:null</c>，不抛异常、不算失败**，
/// 程序只会安静地一行都抓不到。实际发生过：920 开头的北交所票被当成沪市发成 <c>1.920000</c>，
/// 全库 342 只一行历史都没有，而界面上只表现为"还有 342 只从没抓过"这个数字一直不动。
/// </summary>
public class MoneyFlowSecIdTests
{
    [Theory]
    // 北交所：92 是 2024-2025 代码迁移之后的主力段，43/83/87 是老段。全都归 0.（东财把北交所
    // 跟深市放同一个 market id，见 MarketClassifier.EastMoneySecIdPrefix）
    [InlineData("920000", "0.920000")]   // 安徽凤凰——实测 1.920000 返回 data:null
    [InlineData("920002", "0.920002")]
    [InlineData("430047", "0.430047")]
    [InlineData("830799", "0.830799")]
    [InlineData("871981", "0.871981")]
    // 沪市：主板 6、科创 688、CDR 689、B股 900
    [InlineData("600000", "1.600000")]
    [InlineData("688001", "1.688001")]
    [InlineData("689009", "1.689009")]
    [InlineData("900901", "1.900901")]
    // 深市：主板 0、创业板 30x、B股 200
    [InlineData("000001", "0.000001")]
    [InlineData("300750", "0.300750")]
    [InlineData("301000", "0.301000")]
    [InlineData("200011", "0.200011")]
    public void secid前缀要按板块判而不是看首位数字(string code, string expected)
        => Assert.Equal(expected, EastMoneyMoneyFlowProvider.SecId(code));
}
