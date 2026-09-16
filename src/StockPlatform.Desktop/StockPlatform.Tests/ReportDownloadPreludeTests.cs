using System.Net;
using System.Text;
using StockPlatform.Data.Remote;
using StockPlatform.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 【子公司名单】的下载前置（2026-09-15）。
///
/// ════ 这一组守的是"零请求自愈" ════
/// <c>SubsidiaryParser.ParserVersion</c> 改版会触发**全量重跑**。只要 PDF 都在本地，
/// 那一轮必须**一个请求都不发**——否则每调一次解析规则就要重列 50 家的公告。
///
/// 保证它的是两件事，缺一不可：
///   ① 目标报告期**从日历算**，不问服务器（<see cref="SubsidiaryExtractTask.LatestAnnualPeriod"/>）
///   ② 查文件**在列公告之前**，不是只在下载之前
/// 顺序反了的话，重跑时每家仍会发一个列表页请求，而且不会有任何东西报警。
/// </summary>
public class ReportDownloadPreludeTests
{
    private readonly ITestOutputHelper _out;
    public ReportDownloadPreludeTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// 年报法定披露截止是 4 月 30 日，所以 5 月起"上一个完整年度"的年报才算该有了。
    /// 5 月之前问的话，最新一期只能是**前年**的。
    /// </summary>
    [Theory]
    [InlineData("2026-09-15", "2025-12-31")]   // 今天
    [InlineData("2026-05-01", "2025-12-31")]   // 刚过截止日
    [InlineData("2026-04-30", "2024-12-31")]   // 截止日当天，2025 年报还不一定出
    [InlineData("2026-01-10", "2024-12-31")]   // 年初
    [InlineData("2026-12-31", "2025-12-31")]   // 年末，2026 年报要等明年
    public void 最新年报期从日历算(string today, string expect)
    {
        var got = SubsidiaryExtractTask.LatestAnnualPeriod(DateTime.Parse(today));
        _out.WriteLine($"{today} → {got:yyyy-MM-dd}");
        Assert.Equal(DateTime.Parse(expect), got);
    }

    /// <summary>落点必须是 <c>{dir}/{code}/{yyyy-MM-dd}.pdf</c>——两条链共用同一套命名。</summary>
    [Fact]
    public void 落点命名两条链一致()
    {
        var p = SinaReportIndex.PathOf(@"C:\x\reports", "601939", new DateTime(2025, 12, 31));
        Assert.Equal(Path.Combine(@"C:\x\reports", "601939", "2025-12-31.pdf"), p);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.GetEncoding("GBK").GetBytes("")),
            });
        }
    }

    private static SinaReportIndex Index(HttpMessageHandler h)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return new SinaReportIndex(
            new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero),
            new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero),
            new HttpClient(h));
    }

    /// <summary>
    /// ⚠ **已经下过就一个请求都不发**。这是"重解析免费"的基础：
    /// PDF 留在本地的全部意义就在这里。
    /// </summary>
    [Fact]
    public async Task 文件已在则零请求()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dl_{Guid.NewGuid():N}");
        var date = new DateTime(2025, 12, 31);
        var path = SinaReportIndex.PathOf(dir, "601939", date);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[SinaReportIndex.MinPdfBytes + 1]);

        try
        {
            var h = new CountingHandler();
            var r = await Index(h).DownloadPdfAsync(
                new SinaReportIndex.ReportRef("601939", date, "2025年年度报告", "http://x/detail"), dir);

            _out.WriteLine($"status={r.Status} calls={h.Calls}");
            Assert.Equal("already", r.Status);
            Assert.True(r.Ok);
            Assert.Equal(0, h.Calls);          // ← 一个请求都没发
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// 本地文件太小（新浪偶尔返回几百字节的错误页）当没下成，要重下。
    /// 不判大小的话，一个坏文件会永远赖在那儿，而重解析每轮都拿它白跑一遍。
    /// </summary>
    [Fact]
    public async Task 残缺文件当没下成()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dl_{Guid.NewGuid():N}");
        var date = new DateTime(2025, 12, 31);
        var path = SinaReportIndex.PathOf(dir, "601939", date);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[500]);       // 太小

        try
        {
            var h = new CountingHandler();
            var r = await Index(h).DownloadPdfAsync(
                new SinaReportIndex.ReportRef("601939", date, "2025年年度报告", "http://x/detail"), dir);

            Assert.NotEqual("already", r.Status);   // 没被当成已下好
            Assert.True(h.Calls > 0);               // 去取了详情页
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// 详情页里没有 PDF 链接 → 留痕成 no_pdf，**不抛异常**。
    /// 失败必须留痕，否则界面上"无数据"分不清是没抓、抓失败、还是这项本来就取不到。
    /// </summary>
    [Fact]
    public async Task 详情页没有PDF链接时留痕不抛()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dl_{Guid.NewGuid():N}");
        try
        {
            var r = await Index(new CountingHandler()).DownloadPdfAsync(
                new SinaReportIndex.ReportRef("601939", new DateTime(2025, 12, 31),
                                              "2025年年度报告", "http://x/detail"), dir);
            Assert.Equal("no_pdf", r.Status);
            Assert.False(r.Ok);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// 只列年报那一路时，中报的列表页请求要省掉——50 家就是省 50 个请求。
    /// </summary>
    [Fact]
    public async Task 只要年报时不请求中报页()
    {
        var urls = new List<string>();
        var h = new UrlRecorder(urls);
        await Index(h).ListCandidatesAsync("601939", maxPerKind: 1,
                                           kinds: [SinaReportIndex.KindAnnual]);

        foreach (var u in urls) _out.WriteLine(u);
        Assert.Single(urls);
        Assert.Contains("ndbg", urls[0]);          // 年报页
        Assert.DoesNotContain(urls, u => u.Contains("zqbg"));   // 没碰中报页
    }

    private sealed class UrlRecorder(List<string> urls) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            urls.Add(req.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.GetEncoding("GBK").GetBytes("")),
            });
        }
    }
}
