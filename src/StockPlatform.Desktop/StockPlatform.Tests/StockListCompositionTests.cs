using System.Net;
using System.Text;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Abstractions;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 个股名单的合并（2026-09-17）——见 <see cref="CompositeStockListProvider"/> /
/// <see cref="SseStockListProvider"/>。全部用假 provider / 假 HttpClient，一个真请求都不发。
///
/// 为什么要合并：没有哪个源是全的，而且**漏的方式各不相同**——新浪 hs_a 漏科创板 CDR、
/// 漏改名"退市XX"仍在交易的票、漏当天上市的新股；上交所官方只有沪市。单独任何一个都会
/// **静默**丢票，下游没有任何地方会报错，只表现成"这只票在选股结果里从来没出现过"。
///
/// 钉死三件事：
///   ① 按代码去重，**先到的源赢**——顺序反了会把新浪带的市值/最新价洗掉
///   ② 单个源失败不致命（其余源照常），全失败才抛
///   ③ 上交所返回里那条代码为 "-" 的脏行要过滤掉
/// </summary>
public class StockListCompositionTests
{
    static StockListCompositionTests() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// ⭐ 去重时**保留第一个源的条目**。第一个源（新浪）的 MarketCap/LastPrice 有值
    /// （hs_a 免费带出 nmc/trade，SinaListMarketCapFetcher 靠它省掉全市场一轮市值请求），
    /// 上交所那条路是 null——顺序反了市值就没了。
    /// </summary>
    [Fact]
    public async Task 合并去重_先到的源赢_不洗掉市值()
    {
        var sina = new FakeList(
            new StockListEntry("600000", "浦发银行", 1.23e11, 10.5),
            new StockListEntry("000001", "平安银行", 2.34e11, 11.5));
        var sse = new FakeList(
            new StockListEntry("600000", "浦发银行"),          // 重复，且没有市值
            new StockListEntry("601091", "沈鼓集团"));          // 上交所独有：当天上市的新股

        var list = await new CompositeStockListProvider(("新浪", sina), ("上交所", sse)).GetAllStocksAsync();

        Assert.Equal(3, list.Count);
        var pufa = list.Single(x => x.Code == "600000");
        Assert.Equal(1.23e11, pufa.CirculatingMarketCap);                  // ⭐ 市值没被上交所那条 null 覆盖
        Assert.Equal(10.5, pufa.LastPrice);
        Assert.Contains(list, x => x.Code == "601091");         // 上交所补上了新股
    }

    /// <summary>兜底源挂了不该让整轮抓取失败——但失败必须报出来，不能静默少一批。</summary>
    [Fact]
    public async Task 单个源失败不致命_其余源照常()
    {
        var sina = new FakeList(new StockListEntry("600000", "浦发银行"));
        var boom = new ThrowingList(new HttpRequestException("上交所连不上"));
        var msgs = new List<string>();

        var composite = new CompositeStockListProvider(("新浪", sina), ("上交所", boom));
        composite.OnStatus += msgs.Add;
        var list = await composite.GetAllStocksAsync();

        Assert.Single(list);
        Assert.Contains(msgs, m => m.Contains("上交所") && m.Contains("失败"));
    }

    /// <summary>全部源都失败才算"名单取不到"，把第一个异常抛出去。</summary>
    [Fact]
    public async Task 全部源失败才抛()
    {
        var composite = new CompositeStockListProvider(
            ("甲", new ThrowingList(new HttpRequestException("甲挂了"))),
            ("乙", new ThrowingList(new HttpRequestException("乙也挂了"))));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => composite.GetAllStocksAsync());
        Assert.Equal("甲挂了", ex.Message);
    }

    /// <summary>
    /// 上交所 stockType=10 的返回里有一条**代码是 "-"** 的脏行（公司简称也是 "-"），
    /// 按"只收 6 位纯数字"过滤掉。
    /// </summary>
    [Fact]
    public async Task 上交所名单过滤脏行并读出代码与简称()
    {
        const string body = """
            cb({"pageHelp":{"total":3,"data":[
              {"SECURITY_CODE_A":"600000","SECURITY_ABBR_A":"浦发银行","COMPANY_ABBR":"浦发银行"},
              {"SECURITY_CODE_A":"-","SECURITY_ABBR_A":"-","COMPANY_ABBR":"-"},
              {"SECURITY_CODE_A":"689009","SECURITY_ABBR_A":"九号公司","COMPANY_ABBR":"九号公司"}
            ]}})
            """;
        var handler = new StubHandler(_ => body);

        var list = await new SseStockListProvider(new HttpClient(handler)).GetAllStocksAsync();

        Assert.Equal(new[] { "600000", "689009" }, list.Select(x => x.Code));   // "-" 那条没进来
        Assert.Equal("九号公司", list.Single(x => x.Code == "689009").Name);
        Assert.Contains(handler.Requests, u => u.Contains("stockType=10"));     // 用的是全量那个 stockType
    }

    /// <summary>
    /// ⭐ 巨潮那份名单**含新三板和 B 股**，必须过滤掉——实测 6254 条里 B股 79 条，
    /// 另有几百只 430/831~838/873 的新三板。放进来的后果特别隐蔽：
    /// <c>MarketClassifier</c> 把 43/83/87 判成**北交所**（920 迁移前的老近似），
    /// 于是几百只新三板会被按北交所去抓 K 线。
    /// </summary>
    [Fact]
    public async Task 巨潮名单剔掉B股和新三板()
    {
        const string body = """
            {"stockList":[
              {"code":"600000","zwjc":"浦发银行","category":"A股"},
              {"code":"688086","zwjc":"退市紫晶","category":"A股"},
              {"code":"920025","zwjc":"凯达重工","category":"A股"},
              {"code":"200011","zwjc":"深物业B","category":"B股"},
              {"code":"430047","zwjc":"新三板甲","category":"A股"},
              {"code":"831010","zwjc":"新三板乙","category":"A股"},
              {"code":"873223","zwjc":"新三板丙","category":"A股"},
              {"code":"689009","zwjc":"九号公司","category":"CDR"}
            ]}
            """;
        var handler = new StubHandler(_ => body);

        var list = await new CninfoStockListProvider(new HttpClient(handler)).GetAllStocksAsync();

        // 留下：沪市主板、科创板退市股、北交所 920、CDR
        Assert.Equal(new[] { "600000", "688086", "920025", "689009" }, list.Select(x => x.Code));
        Assert.DoesNotContain(list, x => x.Code == "200011");                   // B股（按 category 判）
        Assert.DoesNotContain(list, x => x.Code.StartsWith("43") || x.Code.StartsWith("83")
                                      || x.Code.StartsWith("87"));              // ⭐ 新三板
    }

    /// <summary>巨潮的返回里没有 stockList ⇒ 当格式变了处理，不是"名单为空"。</summary>
    [Fact]
    public async Task 巨潮返回缺stockList当成格式变了()
    {
        var handler = new StubHandler(_ => """{"other":[]}""");

        var provider = new CninfoStockListProvider(new HttpClient(handler));

        await Assert.ThrowsAsync<RateLimitedException>(() => provider.GetAllStocksAsync());
    }

    /// <summary>上交所返回的不是 jsonp（被拦截页）⇒ 当限流处理，不是"名单为空"。</summary>
    [Fact]
    public async Task 上交所返回非jsonp当成被拦截()
    {
        var handler = new StubHandler(_ => "<html>403 Forbidden</html>");

        var provider = new SseStockListProvider(new HttpClient(handler));

        await Assert.ThrowsAsync<RateLimitedException>(() => provider.GetAllStocksAsync());
    }

    // ─────────────────── 测试替身 ───────────────────

    private sealed class FakeList(params StockListEntry[] rows) : IStockListProvider
    {
        public Task<List<StockListEntry>> GetAllStocksAsync(
            IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromResult(rows.ToList());
    }

    private sealed class ThrowingList(Exception ex) : IStockListProvider
    {
        public Task<List<StockListEntry>> GetAllStocksAsync(
            IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromException<List<StockListEntry>>(ex);
    }

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
                Content = new StringContent(reply(url), Encoding.UTF8),
            });
        }
    }
}
