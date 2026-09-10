using System.Net;
using System.Text;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 财务报表换东财（2026-09-10）。盯三件"错了不报错"的事：
///   ① 映射取错列——东财字段命名会误导（G 表的利息对银行是毛额、对券商是净额），取错只是数值偏了，没人会发现；
///   ② 保险被放行到东财——它整组不填赔付支出/退保金/保单红利，抓回来的是"少了几个科目"的完整数据，看不出缺；
///   ③ null 被当成 0 写进库——"这一期没有这个科目"和"这一期该科目是 0"是两回事。
/// </summary>
public class EastMoneyFinancialTests
{
    /// <summary>JSON 片段里全是双引号，测试里用单引号写、这里统一换掉——比数 raw string 的引号可靠。</summary>
    private static string J(string s) => s.Replace('\'', '"');

    /// <summary>按 URL 里的 reportName 返回预设响应；顺带记下依次请求了哪些报表。</summary>
    private sealed class ReportHandler(Dictionary<string, string> byReport) : HttpMessageHandler
    {
        public readonly List<string> Asked = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            var name = url.Split("reportName=")[1].Split('&')[0];
            Asked.Add(name);
            var body = byReport.TryGetValue(name, out var b) ? b : J("{'result':null,'success':true,'code':0}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>拼一个单期的报表响应；<paramref name="fields"/> 用单引号写，如 <c>'TOTAL_ASSETS':1.5</c>。</summary>
    private static string Rows(string orgType, string fields)
        => J("{'result':{'data':[{'ORG_TYPE':'" + orgType + "','REPORT_DATE':'2026-06-30 00:00:00'," + fields
             + "}],'count':1},'success':true,'code':0}");

    private static EastMoneyFinancialProvider Make(ReportHandler h)
        => new(new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero), new HttpClient(h));

    // ─────────────────── 代码 → SECUCODE ───────────────────

    [Theory]
    [InlineData("600519", "600519.SH")]
    [InlineData("688001", "688001.SH")]
    [InlineData("000001", "000001.SZ")]
    [InlineData("300750", "300750.SZ")]
    [InlineData("920002", "920002.BJ")]   // 920 是北交所，走 MarketClassifier 而不是自写前缀规则
    public void 代码转东财SECUCODE(string code, string expect)
        => Assert.Equal(expect, EastMoneyFinancialProvider.ToSecuCode(code));

    [Theory]
    [InlineData("60051")]
    [InlineData("6005190")]
    [InlineData("60A519")]
    public void 非法代码要抛而不是静默返回错的(string bad)
        => Assert.Throws<ArgumentException>(() => EastMoneyFinancialProvider.ToSecuCode(bad));

    // ─────────────────── 映射与解析 ───────────────────

    [Fact]
    public async Task 通用票_三张表映射到规范科目()
    {
        var h = new ReportHandler(new()
        {
            ["RPT_F10_FINANCE_GINCOME"] = Rows("通用",
                "'TOTAL_OPERATE_INCOME':92278072083.21,'OPERATE_COST':9473762565.88," +
                "'PARENT_NETPROFIT':44516880421.86,'BASIC_EPS':35.57"),
            ["RPT_F10_FINANCE_GBALANCE"] = Rows("通用",
                "'TOTAL_ASSETS':309050784569.31,'UNASSIGN_RPOFIT':199683216816.20"),
            ["RPT_F10_FINANCE_GCASHFLOW"] = Rows("通用", "'NETCASH_OPERATE':70690750119.06"),
        });
        var rows = await Make(h).GetAllAsync("600519");

        double V(string k) => rows.Single(r => r.Key == k).Value;
        Assert.Equal(92278072083.21, V(FinancialKeys.Revenue));
        Assert.Equal(9473762565.88, V(FinancialKeys.OperCost));
        Assert.Equal(44516880421.86, V(FinancialKeys.NetProfitParent));
        Assert.Equal(35.57, V(FinancialKeys.EpsBasic));
        Assert.Equal(309050784569.31, V(FinancialKeys.TotalAssets));
        // 东财把"未分配利润"拼成了 UNASSIGN_RPOFIT，照抄那个拼写才取得到
        Assert.Equal(199683216816.20, V(FinancialKeys.UndistributedProfit));
        Assert.Equal(70690750119.06, V(FinancialKeys.Ocf));
        Assert.All(rows, r => Assert.Equal(new DateTime(2026, 6, 30), r.ReportDate));
        // 通用票不该去敲银行/券商专表
        Assert.DoesNotContain("RPT_F10_FINANCE_BINCOME", h.Asked);
        Assert.DoesNotContain("RPT_F10_FINANCE_SINCOME", h.Asked);
    }

    [Fact]
    public async Task 空值列不写库_不能当成0()
    {
        var h = new ReportHandler(new()
        {
            ["RPT_F10_FINANCE_GINCOME"] = Rows("通用", "'TOTAL_OPERATE_INCOME':100.0,'RESEARCH_EXPENSE':null"),
        });
        var rows = await Make(h).GetAllAsync("600519");

        Assert.Contains(rows, r => r.Key == FinancialKeys.Revenue);
        // "这一期没披露研发费用" ≠ "研发费用是 0"，后者会把研发强度算成 0% 而不是留空
        Assert.DoesNotContain(rows, r => r.Key == FinancialKeys.RdExpense);
    }

    [Fact]
    public async Task 银行_利息净收入从B表取而不是G表的毛额()
    {
        // 招行 2026-06-30 的真实数字：G 表给的是收/支两条（毛），B 表才是净额
        var h = new ReportHandler(new()
        {
            ["RPT_F10_FINANCE_GINCOME"] = Rows("银行",
                "'INTEREST_INCOME':172733000000,'INTEREST_EXPENSE':60711000000,'FEE_COMMISSION_INCOME':44442000000"),
            ["RPT_F10_FINANCE_BINCOME"] = Rows("银行",
                "'INTEREST_NI':112022000000,'FEE_COMMISSION_NI':39855000000"),
        });
        var rows = await Make(h).GetAllAsync("600036");

        Assert.Equal(112022000000, rows.Single(r => r.Key == FinancialKeys.InterestNet).Value);
        Assert.Equal(39855000000, rows.Single(r => r.Key == FinancialKeys.FeeCommissionNet).Value);
        Assert.Contains("RPT_F10_FINANCE_BINCOME", h.Asked);
    }

    [Fact]
    public async Task 券商_整套走S表_通用表只用来判类型()
    {
        var h = new ReportHandler(new()
        {
            // G 表这一列对中信也恰好等值，但正确的取法是 S 表——单看一只票会被"两列同值"骗到，
            // 所以映射是拿 3 只券商做跨票交集定下来的
            ["RPT_F10_FINANCE_GINCOME"] = Rows("证券", "'FEE_COMMISSION_INCOME':21427610803.03"),
            ["RPT_F10_FINANCE_SINCOME"] = Rows("证券",
                "'FEE_COMMISSION_NI':21427610803.03,'AGENT_SECURITY_NI':9855822967.23," +
                "'SECURITY_UNDERWRITE_NI':3022520064.39,'ASSET_MANAGE_NI':7182239267.80,'OPERATE_INCOME':100.0"),
            ["RPT_F10_FINANCE_SBALANCE"] = Rows("证券", "'TOTAL_ASSETS':200.0,'RECEIVABLES':3.0"),
            ["RPT_F10_FINANCE_SCASHFLOW"] = Rows("证券", "'NETCASH_OPERATE':50.0"),
        });
        var rows = await Make(h).GetAllAsync("600030");

        Assert.Equal(21427610803.03, rows.Single(r => r.Key == FinancialKeys.FeeCommissionNet).Value);
        Assert.Equal(9855822967.23, rows.Single(r => r.Key == FinancialKeys.BrokerageNet).Value);
        Assert.Equal(3022520064.39, rows.Single(r => r.Key == FinancialKeys.UnderwritingNet).Value);
        Assert.Equal(7182239267.80, rows.Single(r => r.Key == FinancialKeys.AssetMgmtNet).Value);
        // 券商的营业收入是 OPERATE_INCOME，不是通用表的 TOTAL_OPERATE_INCOME
        Assert.Equal(100.0, rows.Single(r => r.Key == FinancialKeys.Revenue).Value);
        // 应收账款在券商表叫 RECEIVABLES
        Assert.Equal(3.0, rows.Single(r => r.Key == FinancialKeys.AccountsReceivable).Value);
        // 资产负债表/现金流量表也必须走 S 表——走通用表会拿到空表、静默丢科目
        Assert.Contains("RPT_F10_FINANCE_SBALANCE", h.Asked);
        Assert.Contains("RPT_F10_FINANCE_SCASHFLOW", h.Asked);
        Assert.DoesNotContain("RPT_F10_FINANCE_GBALANCE", h.Asked);
        Assert.DoesNotContain("RPT_F10_FINANCE_GCASHFLOW", h.Asked);
    }

    [Fact]
    public async Task 银行_资产负债表和现金流量表也要走B表()
    {
        // 2026-09-10 全量比对抓出来的 bug：银行的 GBALANCE/GCASHFLOW 是**空表**，
        // 第一版照通用表取，平安银行直接缺了 9566 格（assets/liab/ocf 这些全没）
        var h = new ReportHandler(new()
        {
            ["RPT_F10_FINANCE_GINCOME"] = Rows("银行", "'TOTAL_OPERATE_INCOME':1.0"),
            ["RPT_F10_FINANCE_BINCOME"] = Rows("银行", "'OPERATE_INCOME':112.0,'INTEREST_NI':50.0"),
            ["RPT_F10_FINANCE_BBALANCE"] = Rows("银行", "'TOTAL_ASSETS':6028785000000,'TOTAL_EQUITY':548214000000"),
            ["RPT_F10_FINANCE_BCASHFLOW"] = Rows("银行", "'NETCASH_OPERATE':777.0"),
        });
        var rows = await Make(h).GetAllAsync("600036");

        Assert.Equal(6028785000000, rows.Single(r => r.Key == FinancialKeys.TotalAssets).Value);
        Assert.Equal(548214000000, rows.Single(r => r.Key == FinancialKeys.EquityTotal).Value);
        Assert.Equal(777.0, rows.Single(r => r.Key == FinancialKeys.Ocf).Value);
        // 银行的营业收入取 B 表的 OPERATE_INCOME，不是通用表那个 1.0
        Assert.Equal(112.0, rows.Single(r => r.Key == FinancialKeys.Revenue).Value);
        Assert.DoesNotContain("RPT_F10_FINANCE_GBALANCE", h.Asked);
        Assert.DoesNotContain("RPT_F10_FINANCE_GCASHFLOW", h.Asked);
    }

    [Fact]
    public async Task 保险_识别出来就立刻收手_不白跑后面两个请求()
    {
        var h = new ReportHandler(new()
        {
            ["RPT_F10_FINANCE_GINCOME"] = Rows("保险", "'TOTAL_OPERATE_INCOME':575138000000"),
        });
        var (orgType, rows) = await Make(h).FetchWithOrgTypeAsync("601318");

        Assert.Equal(EastMoneyFinancialProvider.OrgTypeInsurer, orgType);
        Assert.Empty(rows);
        // 只问了利润表就掉头，没去要资产负债表/现金流量表
        Assert.Equal(["RPT_F10_FINANCE_GINCOME"], h.Asked);
    }
}
