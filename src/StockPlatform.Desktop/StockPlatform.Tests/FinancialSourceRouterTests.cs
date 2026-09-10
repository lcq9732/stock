using System.Net;
using System.Text;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 财务报表的按机构类型分流（2026-09-10）。
///
/// 这层存在的唯一理由是：东财**整组不填**保险公司的支出科目（赔付支出/退保金/保单红利/分保费用，
/// 三家保险 × 三处报表全 null），而 <c>claim_expense</c> 有真实消费方（赔付率指标，
/// 新浪覆盖 5 家 × 392 期 / 1997 年起，PDF 那条只有 3 家 × 10 期 / 2024 年起，替代不了）。
///
/// 所以这里盯的是：保险**绝不能**被放行到东财——放行了不会报错，只会安静地少几个科目。
/// </summary>
public class FinancialSourceRouterTests
{
    /// <summary>东财侧：记录被问了哪些报表，按需返回 ORG_TYPE。</summary>
    private sealed class EmHandler(string orgType) : HttpMessageHandler
    {
        public readonly List<string> Asked = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            Asked.Add(url.Split("reportName=")[1].Split('&')[0]);
            var body = ("{'result':{'data':[{'ORG_TYPE':'" + orgType +
                        "','REPORT_DATE':'2026-06-30 00:00:00','TOTAL_OPERATE_INCOME':100.0}],'count':1}," +
                        "'success':true,'code':0}").Replace('\'', '"');
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>新浪侧：返回 GBK 的 TSV 报表（首行报表日期、次行单位，之后每行一个科目）。</summary>
    private sealed class SinaHandler(bool withInsuranceRows) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls++;
            var sb = new StringBuilder();
            sb.Append("报表日期\t20260630\n单位\t元\n营业总收入\t575138000000\n");
            if (withInsuranceRows)
            {
                sb.Append("已赚保费\t279255000000\n");
                sb.Append("赔付支出\t221773000000\n");
            }
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var bytes = Encoding.GetEncoding("GBK").GetBytes(sb.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(bytes) });
        }
    }

    private static (FinancialSourceRouter Router, EmHandler Em, SinaHandler Sina, List<string> Log)
        Build(string emOrgType, bool sinaHasInsuranceRows = true)
    {
        var em = new EmHandler(emOrgType);
        var sina = new SinaHandler(sinaHasInsuranceRows);
        var router = new FinancialSourceRouter(
            new EastMoneyFinancialProvider(new RateLimiter(1, TimeSpan.Zero), new HttpClient(em)),
            new SinaFinancialProvider(new RateLimiter(1, TimeSpan.Zero), new HttpClient(sina)));
        var log = new List<string>();
        router.OnStatus += log.Add;
        return (router, em, sina, log);
    }

    [Fact]
    public async Task 名单里的保险_直接走新浪_一个东财请求都不发()
    {
        var (router, em, sina, log) = Build("保险");

        var rows = await router.GetAllAsync("601318");   // 中国平安

        Assert.Empty(em.Asked);                      // 名单命中就不该白问东财
        Assert.True(sina.Calls > 0);
        Assert.Contains(rows, r => r.Key == FinancialKeys.ClaimExpense);
        Assert.Contains(rows, r => r.Key == FinancialKeys.PremiumEarned);
        Assert.Contains(log, m => m.Contains("内置名单"));
    }

    [Fact]
    public async Task 名单外但东财说是保险_改道新浪并告知()
    {
        var (router, em, sina, log) = Build("保险");

        var rows = await router.GetAllAsync("600000");   // 假装它是家新上市的保险公司

        // 先问了东财（才知道是保险），然后掉头走新浪——名单过时只多花一个请求，不会拿到缺科目的数据
        Assert.Equal(["RPT_F10_FINANCE_GINCOME"], em.Asked);
        Assert.True(sina.Calls > 0);
        Assert.Contains(rows, r => r.Key == FinancialKeys.ClaimExpense);
        Assert.Contains(log, m => m.Contains("ORG_TYPE=保险"));
    }

    [Fact]
    public async Task 保险科目缺失要告警_不能静默通过()
    {
        // 模拟新浪页面版式变了、中文行名匹配失效
        var (router, _, _, log) = Build("保险", sinaHasInsuranceRows: false);

        await router.GetAllAsync("601318");

        Assert.Contains(log, m => m.Contains("⚠") && m.Contains("赔付支出"));
    }

    [Fact]
    public async Task 普通票_走东财_不碰新浪()
    {
        var (router, em, sina, _) = Build("通用");

        var rows = await router.GetAllAsync("600519");

        Assert.Equal(0, sina.Calls);
        Assert.Contains("RPT_F10_FINANCE_GINCOME", em.Asked);
        Assert.Contains(rows, r => r.Key == FinancialKeys.Revenue);
    }

    [Fact]
    public async Task 银行_走东财并取B表()
    {
        var (router, em, sina, _) = Build("银行");

        await router.GetAllAsync("600036");

        Assert.Equal(0, sina.Calls);
        Assert.Contains("RPT_F10_FINANCE_BINCOME", em.Asked);
    }
}
