using System.Net;
using System.Text;
using StockPlatform.Data.Remote;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 公告标题的粗筛与排序（2026-09-15 补）。
///
/// ════ 为什么现在才补 ════
/// 这段判据是踩了两个方向的坑才稳下来的（见 <c>BankReportFetcher.TitleRank</c> 的注释），
/// 却一直**没有任何测试**。马上要把它抽成独立的数据源类给【子公司名单】共用，
/// 搬之前必须先钉住——纯搬移搬坏了不会有任何东西报警。
///
/// 样本全部来自那段注释里记着的真实标题，不是编的。
///
/// ════ 判据的形状 ════
/// 返回 -1 = 排除，0/1/2 = 候选优先级（越小越可信）。**它只做粗筛加排序，不做终判**：
/// 真正的判据是"这份 PDF 解析不解析得出指标"，所以宁可多留几个候选逐个试，
/// 也不要在这里把对的那个排除掉——漏掉是静默的，下错了反而看得见。
/// </summary>
public class ReportTitleRankTests
{
    private readonly ITestOutputHelper _out;
    public ReportTitleRankTests(ITestOutputHelper output) => _out = output;

    private static int Rank(string title, string kind) => SinaReportIndex.TitleRank(title, kind);

    /// <summary>干干净净的正文标题 → 0 档（最可信）。</summary>
    [Theory]
    [InlineData("2025年年度报告", "年报")]
    [InlineData("2024年年度报告", "年报")]
    [InlineData("2025年年度报告（A股）", "年报")]
    [InlineData("2025年年度报告（修订版）", "年报")]
    [InlineData("2026年半年度报告", "中报")]
    public void 正文标题排0档(string title, string kind)
        => Assert.Equal(0, Rank(title, kind));

    /// <summary>
    /// ⚠ **平安银行那条**：标题是「2022年半年度报告<b>2</b>」，末尾多个 2。
    /// 严格要求以"报告"结尾的话会直接漏过，那一期就无声无息地没有数据。
    /// 太严比太松更危险——下错了会进手工回填清单让人看见，漏掉了什么痕迹都不留。
    /// </summary>
    [Theory]
    [InlineData("2022年半年度报告2", "中报")]
    [InlineData("2025年年度报告2", "年报")]
    public void 末尾带零碎的仍是候选_只是降到1档(string title, string kind)
    {
        int r = Rank(title, kind);
        _out.WriteLine($"「{title}」→ {r}");
        Assert.Equal(1, r);
    }

    /// <summary>
    /// ⚠ **353 份里下错 9 份的那一批**。这些标题都含"年度报告"四个字，
    /// 但文件里根本没有指标表，翻遍了也找不到数，白白进手工回填清单让人去翻。
    /// </summary>
    [Theory]
    [InlineData("关于落实2024年度报告问询函的回复公告", "年报")]
    [InlineData("2026半年度报告募集资金存放与实际使用情况的专项报告", "中报")]
    [InlineData("2025年年度报告摘要", "年报")]
    [InlineData("2025年年度报告（英文版）", "年报")]
    [InlineData("关于2024年年度报告的更正公告", "年报")]
    [InlineData("2025年年度报告补充公告", "年报")]
    public void 问询函摘要专项报告一律排除(string title, string kind)
    {
        int r = Rank(title, kind);
        _out.WriteLine($"「{title}」→ {r}");
        Assert.Equal(-1, r);
    }

    /// <summary>
    /// ⚠ **A+H 两地上市那一批**（中行/工行/中信/浦发/太保）。港版指标表的口径和排版跟 A 股版
    /// 不同，解析不出来，会记成 wrong_file——全库 21 条 wrong_file 里一大批就是它们。
    /// 老规则只看结尾，把港版也放行了。
    /// </summary>
    [Theory]
    [InlineData("H股公告-2025年年度报告", "年报")]
    [InlineData("港股公告：2025年年度报告", "年报")]
    public void 港版一律排除(string title, string kind)
        => Assert.Equal(-1, Rank(title, kind));

    /// <summary>年报和中报不能串台——用中报的 kind 去看年报标题，必须不认。</summary>
    [Fact]
    public void 年报中报不串台()
    {
        Assert.Equal(-1, Rank("2025年年度报告", "中报"));
        Assert.Equal(-1, Rank("2025年半年度报告", "年报"));
    }

    /// <summary>2 档兜底：连"年度报告"都不完整，只是像。正常轮不到，但不能排除掉。</summary>
    [Theory]
    [InlineData("2024年半年报", "中报")]
    [InlineData("2024年年报", "年报")]
    public void 简写形式落2档兜底(string title, string kind)
        => Assert.Equal(2, Rank(title, kind));

    /// <summary>完全不相干的公告，排除。</summary>
    [Theory]
    [InlineData("关于召开2025年第一次临时股东大会的通知", "年报")]
    [InlineData("2025年第三季度报告", "年报")]
    [InlineData("董事会决议公告", "年报")]
    public void 不相干的公告排除(string title, string kind)
        => Assert.Equal(-1, Rank(title, kind));

    // ─────────────────────────────────────────────────────────────────
    // ListCandidatesAsync：拿假 HTML 喂进去，测"列公告 → 排序 → 按期截断"整条链
    // ─────────────────────────────────────────────────────────────────

    /// <summary>照新浪列表页的真实结构拼一条 href。</summary>
    private static string Row(int stockid, int id, string title)
        => $"<a href='/corp/view/vCB_AllBulletinDetail.php?stockid={stockid}&id={id}' target=_blank>{title}</a>";

    /// <summary>按 URL 里的 page_type 返回不同的假页面。GBK 编码，跟真站一致。</summary>
    private sealed class FakeHandler(Func<string, string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            var bytes = Encoding.GetEncoding("GBK").GetBytes(body(url));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            });
        }
    }

    private static SinaReportIndex Index(Func<string, string> body)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return new SinaReportIndex(
            new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero),
            new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero),
            new HttpClient(new FakeHandler(body)));
    }

    /// <summary>
    /// ⚠ **最重要的一条**：同一个报告期有多个候选时，可信的排在前面。
    /// 中国银行 2025 年报就同时有「2025年年度报告」和「H股公告-2025年年度报告」两条；
    /// 港版那条要被排除，正文那条留下。
    /// </summary>
    [Fact]
    public async Task 同期多候选时按可信度排序()
    {
        var html = string.Join("\n",
            Row(601988, 100, "H股公告-2025年年度报告"),     // 排除
            Row(601988, 101, "2025年年度报告2"),            // 1 档
            Row(601988, 102, "2025年年度报告"),             // 0 档
            Row(601988, 103, "关于落实2024年度报告问询函的回复公告")); // 排除

        var byDate = await Index(url => url.Contains("ndbg") ? html : "").ListCandidatesAsync("601988");

        var list = byDate[new DateTime(2025, 12, 31)];
        foreach (var r in list) _out.WriteLine($"{r.ReportDate:yyyy-MM-dd} {r.Title}");

        Assert.Equal(2, list.Count);                        // 两条被排除了
        Assert.Equal("2025年年度报告", list[0].Title);      // 0 档在最前
        Assert.Equal("2025年年度报告2", list[1].Title);
    }

    /// <summary>maxPerKind 数的是**报告期**不是公告条数——同一期的其它候选仍要收。</summary>
    [Fact]
    public async Task 期数按报告期截断_不是按公告条数()
    {
        var html = string.Join("\n",
            Row(601988, 1, "2025年年度报告"),
            Row(601988, 2, "2025年年度报告2"),      // 同一期的第二个候选，不占额度
            Row(601988, 3, "2024年年度报告"),
            Row(601988, 4, "2023年年度报告"));      // 第 3 期，超了

        var byDate = await Index(url => url.Contains("ndbg") ? html : "")
            .ListCandidatesAsync("601988", maxPerKind: 2);

        Assert.Equal(2, byDate.Count);                                  // 只留 2 期
        Assert.Equal(new DateTime(2025, 12, 31), byDate.Keys.First());  // 新的在前
        Assert.Equal(2, byDate[new DateTime(2025, 12, 31)].Count);      // 同期两个候选都在
        Assert.DoesNotContain(new DateTime(2023, 12, 31), byDate.Keys);
    }

    /// <summary>一类列表页取不到，不能影响另一类——现在靠 catch{continue} 保证。</summary>
    [Fact]
    public async Task 中报页挂了不影响年报()
    {
        var f = Index(url => url.Contains("zqbg")
            ? throw new HttpRequestException("中报页 500")
            : Row(601988, 1, "2025年年度报告"));

        var byDate = await f.ListCandidatesAsync("601988");
        Assert.Single(byDate);
        Assert.Equal(new DateTime(2025, 12, 31), byDate.Keys.Single());
    }

    /// <summary>同一条公告在页面上列了两遍（真站上出现过），不能产生两个候选。</summary>
    [Fact]
    public async Task 重复公告只算一个候选()
    {
        var html = Row(601988, 1, "2025年年度报告") + "\n" + Row(601988, 1, "2025年年度报告");
        var byDate = await Index(url => url.Contains("ndbg") ? html : "").ListCandidatesAsync("601988");
        Assert.Single(byDate[new DateTime(2025, 12, 31)]);
    }
}
