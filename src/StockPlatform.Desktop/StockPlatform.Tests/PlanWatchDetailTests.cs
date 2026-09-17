using System.Net;
using System.Text.Json;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【回购公告进展】取正文那一步的**配对**和**回补**（2026-09-17）。
///
/// ════ 这组测试也是为一个真实的静默失败写的 ════
/// 09-17 那轮报「6 条没取到正文，已按标题留档」。查下来 6 条公告在东财**全都有**，
/// 是标题配不上：巨潮写全角括号「（郑州）」、东财写半角「(郑州)」；巨潮标题里多一个空格
/// 「艾迪精密␣关于…」。原来的 <c>TitlesLooselyMatch</c> 只去了「股票名：」前缀就做互相包含，
/// 于是走软失败分支——标题落库、金额/股数/比例全 null。
///
/// 这个坑的形状跟这套设计里其它几个一样：**不报错，只是数据少一截**。
/// 全库体检 1959 条里 38 条空壳，34 条是这两类差异，剩下 4 条是纯 B 股走错 ann_type。
/// 所以三条判据都得钉住：**配得上**、**B 股走对通道**、**旧空壳会被回补**。
/// </summary>
public class PlanWatchDetailTests
{
    // ─────────────────── ① 标题配对 ───────────────────

    /// <summary>★ 09-17 那 6 条的原始标题，左边巨潮、右边东财，一条都不许再漏。</summary>
    [Theory]
    // 全角括号 vs 半角括号
    [InlineData("中创智领：中创智领（郑州）工业技术集团股份有限公司关于回购A股股份的进展公告",
                "中创智领:中创智领(郑州)工业技术集团股份有限公司关于回购A股股份的进展公告")]
    [InlineData("豫园股份：上海豫园旅游商城（集团）股份有限公司关于以集中竞价交易方式回购公司A股股份比例达到1%暨回购进展公告",
                "豫园股份:上海豫园旅游商城(集团)股份有限公司关于以集中竞价交易方式回购公司A股股份比例达到1%暨回购进展公告")]
    // 标题里多一个空格
    [InlineData("艾迪精密：艾迪精密 关于股份回购实施结果暨股份变动的公告",
                "艾迪精密:艾迪精密关于股份回购实施结果暨股份变动的公告")]
    [InlineData("上海凤凰：上海凤凰关于以集中竞价交易方式首次回购B股股份暨回购 B 股股份的进展的公告",
                "上海凤凰:上海凤凰关于以集中竞价交易方式首次回购B股股份暨回购B股股份的进展的公告")]
    public void 同一篇公告_标点或空格写法不同也要配上(string cninfo, string eastmoney)
        => Assert.True(EastMoneyAnnouncementDetailFetcher.TitlesLooselyMatch(cninfo, eastmoney),
            $"配不上就会走软失败分支：只落标题、数值全 null。\n巨潮：{cninfo}\n东财：{eastmoney}");

    /// <summary>
    /// 归一化不能松到把**不同的公告**也配上——配错比配不上更糟：
    /// 抽出来的是另一篇的数值，而且看不出错。
    /// 「前十名股东持股情况」那条尤其要挡住，它跟回购报告书是同日发的。
    /// </summary>
    [Theory]
    [InlineData("艾迪精密：艾迪精密 关于以集中竞价交易方式回购股份的回购报告书",
                "艾迪精密:艾迪精密关于回购股份事项前十名股东和前十名无限售条件股东持股情况的公告")]
    [InlineData("中创智领：中创智领（郑州）工业技术集团股份有限公司关于回购A股股份的进展公告",
                "中创智领:中创智领(郑州)工业技术集团股份有限公司关于以集中竞价交易方式回购A股股份的回购报告书")]
    public void 不同的公告_不许误配(string cninfo, string eastmoney)
        => Assert.False(EastMoneyAnnouncementDetailFetcher.TitlesLooselyMatch(cninfo, eastmoney));

    // ─────────────────── ② B 股走对通道 ───────────────────

    /// <summary>
    /// ★ 纯 B 股（深 200／沪 900）必须请求 <c>ann_type=B</c>。
    /// 写死 A 的后果不是报错，是 <c>total_hits:0</c>——这类票的正文**永远**取不到，
    /// 库里 200512 闽灿坤B 的 4 条空壳就是这么来的。
    /// </summary>
    [Theory]
    [InlineData("200512", "B")]   // 深 B（闽灿坤B）
    [InlineData("900957", "B")]   // 沪 B
    [InlineData("600679", "A")]   // A+B 两地挂牌，代码是 A 股代码：B 股公告也在 A 列表里
    [InlineData("000001", "A")]
    [InlineData("920080", "A")]   // 北交所，别被当成 B
    public async Task 纯B股用B通道_其余用A(string code, string expected)
    {
        var handler = new RecordingHandler();
        var fetcher = new EastMoneyAnnouncementDetailFetcher(
            new RateLimiter(delayBetweenRequests: TimeSpan.Zero), new HttpClient(handler));

        var got = await fetcher.FetchDetailAsync(code, "关于回购公司股份的进展公告", RecordingHandler.NoticeDay);

        Assert.NotNull(got);
        Assert.Contains($"ann_type={expected}&", handler.Urls[0]);
    }

    // ─────────────────── ③ 回补旧空壳 ───────────────────

    /// <summary>
    /// ★ 软失败留下的空壳行（<c>art_code</c> 为空）增量窗口再也不会路过——
    /// 水位线早越过那天了。所以每轮跑完新公告之后要把它们挑出来再取一次正文。
    /// 判据：回补的那几条确实被取了正文，交回来的记录带上了数值，而且主键没变
    /// （stage 只从标题定，变了就会在库里多出一行空壳）。
    /// </summary>
    [Fact]
    public async Task 每轮跑完_旧空壳会被重新取一次正文()
    {
        var stale = new[]
        {
            Shell("600655", "上海豫园旅游商城（集团）股份有限公司关于以集中竞价交易方式回购公司A股股份比例达到1%暨回购进展公告"),
            Shell("603638", "艾迪精密 关于股份回购实施结果暨股份变动的公告"),
        };
        var (task, repo, detail) = Build(stale);

        var result = await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, result.State);
        Assert.Equal(2, detail.Calls);                       // 两条都去取了正文
        Assert.Equal(2, repo.Saved.Count);                   // 两条都补回来了
        Assert.All(repo.Saved, r => Assert.NotEqual("", r.ArtCode));
        Assert.All(repo.Saved, r => Assert.NotNull(r.CumShares));
        // 主键三件套原样：补数值是就地 upsert，不是新增一行
        Assert.Equal(["600655", "603638"], repo.Saved.Select(r => r.Code).OrderBy(x => x));
        Assert.All(repo.Saved, r => Assert.Equal(stale.Single(s => s.Code == r.Code).Stage, r.Stage));
    }

    /// <summary>没有空壳时不该白跑一趟，也不该在日志里多一句「回补 0 条」。</summary>
    [Fact]
    public async Task 没有空壳时_不跑回补()
    {
        var (task, _, detail) = Build([]);

        var texts = new List<string>();
        task.OnProgress += p => texts.Add(p.Text);
        await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(0, detail.Calls);
        Assert.DoesNotContain(texts, t => t.Contains("回补"));
    }

    /// <summary>
    /// 回补取不到正文（比如那条公告确实已经翻出东财最近 100 条之外）不算失败：
    /// 不写库、不吵、整轮照样是完成——下一轮还会再试一次。
    /// </summary>
    [Fact]
    public async Task 回补仍取不到_不写库也不算失败()
    {
        var (task, repo, detail) = Build([Shell("600655", "关于回购公司股份的进展公告")]);
        detail.ReturnNull = true;

        var result = await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(1, detail.Calls);
        Assert.Empty(repo.Saved);
        Assert.NotEqual(TaskState.Failed, result.State);
    }

    // ─────────────────── 脚手架 ───────────────────

    private static PlanAnnouncement Shell(string code, string title) => new()
    {
        Code = code,
        Name = code,
        Kind = PlanKind.Buyback,
        AnnounceDate = DateTime.Today.AddDays(-30),
        Stage = PlanAnnouncementExtractor.ClassifyStage(title),
        Title = title,
        ArtCode = "",
    };

    private static (PlanWatchTask Task, FakeRepo Repo, FakeDetail Detail) Build(
        IReadOnlyList<PlanAnnouncement> stale)
    {
        var repo = new FakeRepo { Watermark = DateTime.Today, Missing = [.. stale] };
        var detail = new FakeDetail();
        return (new PlanWatchTask(repo, new EmptySearch(), detail), repo, detail);
    }

    private sealed class FakeRepo : IPlanAnnouncementRepository
    {
        public DateTime? Watermark;
        public List<PlanAnnouncement> Missing = [];
        public readonly List<PlanAnnouncement> Saved = [];

        public void EnsureSchema() { }
        public int Upsert(IEnumerable<PlanAnnouncement> items)
        {
            var n = 0;
            foreach (var x in items) { Saved.Add(x); n++; }
            return n;
        }
        public List<PlanAnnouncement> GetByCode(string code, string kind) => [];
        public List<PlanAnnouncement> GetMissingDetail(string kind, DateTime since, int limit)
            => Missing.Where(m => m.AnnounceDate >= since).Take(limit).ToList();
        public Dictionary<string, PlanAnnouncement> GetOpenPlans(string kind) => [];
        public DateTime? GetLatestAnnounceDate(string kind) => Watermark;
        public (int Rows, int Stocks, int OpenPlans) GetCounts(string kind) => (Saved.Count, 1, 0);
    }

    /// <summary>这组测试只看回补那一段，新公告一律搜不到。</summary>
    private sealed class EmptySearch : IAnnouncementSearchProvider
    {
        public Task<List<AnnouncementSearchHit>> SearchAsync(
            string keyword, DateOnly start, DateOnly end,
            IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new List<AnnouncementSearchHit>());
    }

    private sealed class FakeDetail : IAnnouncementDetailFetcher
    {
        public int Calls;
        public bool ReturnNull;

        public Task<(string ArtCode, string Content)?> FetchDetailAsync(
            string code, string title, DateOnly approxDate, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(ReturnNull
                ? null
                : ((string, string)?)($"art{code}", "截至 2026 年 8 月 31 日，公司累计回购股份 100 股，成交金额 1000 元。"));
        }
    }

    /// <summary>把东财那两个接口的最小回包造出来，顺便记下请求过的 URL。</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public static readonly DateOnly NoticeDay = new(2026, 9, 2);
        public readonly List<string> Urls = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            var json = url.Contains("/api/security/ann")
                ? JsonSerializer.Serialize(new
                {
                    data = new
                    {
                        list = new[]
                        {
                            new
                            {
                                title = "关于回购公司股份的进展公告",
                                notice_date = NoticeDay.ToString("yyyy-MM-dd") + " 00:00:00",
                                art_code = "AN001",
                            },
                        },
                    },
                })
                : """{"data":{"notice_content":"累计回购股份 100 股。"}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
