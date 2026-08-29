using System.Globalization;
using System.Net;
using System.Text;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 从新浪财经的报表下载接口抓取一只股票**全部历史**的关键财务科目（2026-07-31 实测可用）：
/// <c>money.finance.sina.com.cn/corp/go.php/vDOWN_{利润表|资产负债表|现金流量表}/displaytype/4/stockid/{code}/ctrl/all.phtml</c>
/// 一个请求返回该股上市以来所有报告期的整张报表（GBK 编码 TSV：首行"报表日期"+各期 yyyyMMdd，
/// 次行"单位 元"，之后每行=科目名+各期值）。银行/券商的科目名与一般企业不同（如"一、营业收入" vs
/// "营业总收入"、"归属于母公司的净利润" vs "归属于母公司所有者的净利润"），用备选名列表匹配。
/// 只抽取 <see cref="FinancialKeys"/> 里的科目，不存整张表。退市股同样有数据（乐视网退市后仍在老三板披露）。
/// ⚠️ 现金流量表的补充资料里也有"净利润"行（常年为0），净利润只从利润表取。
/// </summary>
public class SinaFinancialProvider : IFinancialProvider
{
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    static SinaFinancialProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // GBK
    }

    public SinaFinancialProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
    {
        _rateLimiter = rateLimiter;
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    /// <summary>每张报表要抽取的科目：规范键 → 备选行名列表（**顺序即优先级**，一般企业叫法在前、银行在后；
    /// 行名先去掉"一、二、…"的序号前缀再比对）。</summary>
    private static readonly (string Statement, (string Key, string[] Names)[] Items)[] Statements =
    [
        // 2026-08-27 从 8 个科目扩到 40 个。行名是当天用 603501 实测下载三张表、逐行打印出来的，
        // 不是猜的。注意几处反直觉的地方：
        //   · 新准则的"合同负债"在 TSV 里仍叫"预收款项"（网页版才显示合同负债）
        //   · 所得税那行带"减："前缀，而 StripOrdinalPrefix 只剥"一、二、"，所以全名和两种冒号都列上
        //   · 现金流量表附注里的"公允价值变动损失"跟利润表"公允价值变动收益"符号相反，是两个 key
        ("ProfitStatement",
        [
            (FinancialKeys.Revenue, ["营业总收入", "营业收入"]),
            // 银行没有"营业总成本"，对应的是"二、营业支出"（前缀被 StripOrdinalPrefix 剥掉）。
            (FinancialKeys.TotalCost, ["营业总成本", "营业支出"]),
            (FinancialKeys.OperCost, ["营业成本"]),
            (FinancialKeys.TaxSurcharge, ["营业税金及附加"]),
            (FinancialKeys.SellExpense, ["销售费用"]),
            // 银行叫"业务及管理费用"——成本收入比的分子就是它，原来没配导致 42 家银行全算不出。
            (FinancialKeys.AdminExpense, ["管理费用", "业务及管理费用"]),
            (FinancialKeys.FinanceExpense, ["财务费用"]),
            (FinancialKeys.RdExpense, ["研发费用"]),
            (FinancialKeys.ImpairmentLoss, ["资产减值损失"]),
            (FinancialKeys.FvChangeGain, ["公允价值变动收益", "公允价值变动净收益"]),
            (FinancialKeys.InvestIncome, ["投资收益", "投资净收益"]),
            (FinancialKeys.OperProfit, ["营业利润"]),
            (FinancialKeys.TotalProfit, ["利润总额"]),
            // 银行的这一行没有"费用"二字（招行是"减:所得税"）。
            (FinancialKeys.IncomeTax, ["所得税费用", "减：所得税费用", "减:所得税费用", "减：所得税", "减:所得税"]),
            (FinancialKeys.NetProfit, ["净利润"]),
            (FinancialKeys.NetProfitParent, ["归属于母公司所有者的净利润", "归属于母公司的净利润", "归属于母公司股东的净利润"]),
            // ⚠ 银行**利润表**里少数股东损益那行写作"少数股东权益"，跟资产负债表的同名科目撞名。
            //   Statements 是按报表分组解析的，这个备选名只在 ProfitStatement 里生效，不会串到
            //   资产负债表的 MinorityEquity 上。
            (FinancialKeys.MinorityPl, ["少数股东损益", "少数股东权益"]),
            (FinancialKeys.EpsBasic, ["基本每股收益(元/股)", "基本每股收益"]),

            // ── 银行专属（2026-08-29）：见 FinancialKeys.InterestNet 那段注释 ──
            (FinancialKeys.InterestNet, ["利息净收入"]),
            (FinancialKeys.FeeCommissionNet, ["手续费及佣金净收入"]),
            // 银行叫"其他业务支出"、券商叫"其他业务成本"，都是倒推时要从营业支出里扣掉的那一项。
            (FinancialKeys.OtherOperExpense, ["其他业务支出", "其他业务成本"]),

            // ── 券商 / 保险专属（2026-08-29）：三类金融机构的身份特征科目，
            //    用来把"金融机构"细分成银行/券商/保险，见 FinancialKeys.PremiumEarned 那段注释 ──
            (FinancialKeys.PremiumEarned, ["已赚保费"]),
            (FinancialKeys.ClaimExpense, ["赔付支出"]),
            (FinancialKeys.BrokerageNet, ["代理买卖证券业务净收入"]),
            (FinancialKeys.UnderwritingNet, ["证券承销业务净收入"]),
            (FinancialKeys.AssetMgmtNet, ["受托客户资产管理业务净收入"]),
        ]),
        ("BalanceSheet",
        [
            (FinancialKeys.Cash, ["货币资金"]),
            (FinancialKeys.NoteReceivable, ["应收票据"]),
            (FinancialKeys.AccountsReceivable, ["应收账款"]),
            (FinancialKeys.Prepayment, ["预付款项"]),
            (FinancialKeys.Inventory, ["存货"]),
            (FinancialKeys.CurrentAssets, ["流动资产合计"]),
            (FinancialKeys.TotalAssets, ["资产总计"]),
            (FinancialKeys.ShortLoan, ["短期借款"]),
            (FinancialKeys.NotePayable, ["应付票据"]),
            (FinancialKeys.AccountsPayable, ["应付账款"]),
            (FinancialKeys.AdvanceReceipts, ["预收款项", "合同负债"]),
            (FinancialKeys.CurrentLiabilities, ["流动负债合计"]),
            (FinancialKeys.LongLoan, ["长期借款"]),
            (FinancialKeys.BondPayable, ["应付债券"]),
            (FinancialKeys.TotalLiabilities, ["负债合计"]),
            (FinancialKeys.ShareCapital, ["实收资本(或股本)", "实收资本", "股本"]),
            // 归母权益这一行各家叫法差异最大，实测到的写法：一般企业"归属于母公司股东权益合计"、
            // 招行"归属于母公司股东的权益"、中国平安"归属于母公司的股东权益合计"（多一个"的"）。
            // 少一个变体就会让那家公司的 ROE/PB 整个算不出来，所以宁可多列几个。
            (FinancialKeys.EquityParent, ["归属于母公司股东权益合计", "归属于母公司的股东权益合计",
                                          "归属于母公司股东的权益", "归属于母公司股东的权益合计",
                                          "归属于母公司所有者权益合计", "归属于母公司所有者的权益合计",
                                          "所有者权益(或股东权益)合计"]),
            (FinancialKeys.MinorityEquity, ["少数股东权益"]),
            (FinancialKeys.EquityTotal, ["所有者权益(或股东权益)合计", "所有者权益合计", "股东权益合计"]),
            (FinancialKeys.UndistributedProfit, ["未分配利润"]),
        ]),
        ("CashFlow",
        [
            // 正表
            (FinancialKeys.SalesCash, ["销售商品、提供劳务收到的现金"]),
            (FinancialKeys.Ocf, ["经营活动产生的现金流量净额"]),
            (FinancialKeys.Icf, ["投资活动产生的现金流量净额"]),
            (FinancialKeys.Fcf, ["筹资活动产生的现金流量净额"]),
            (FinancialKeys.Capex, ["购建固定资产、无形资产和其他长期资产所支付的现金"]),
            (FinancialKeys.CashEnd, ["期末现金及现金等价物余额"]),
            // 附注（间接法补充资料）——行名都唯一，不会跟正表/利润表撞。
            // ⚠ 附注里的"净利润""财务费用""少数股东权益"跟别处同名，一律不在这里取。
            (FinancialKeys.ImpairmentProvision, ["资产减值准备"]),
            (FinancialKeys.Depreciation, ["固定资产折旧、油气资产折耗、生产性物资折旧"]),
            (FinancialKeys.AmortIntangible, ["无形资产摊销"]),
            (FinancialKeys.AmortLongPrepaid, ["长期待摊费用摊销"]),
            (FinancialKeys.FvChangeLoss, ["公允价值变动损失"]),
            (FinancialKeys.InventoryDecrease, ["存货的减少"]),
            (FinancialKeys.ReceivableDecrease, ["经营性应收项目的减少"]),
            (FinancialKeys.PayableIncrease, ["经营性应付项目的增加"]),
        ]),
    ];

    public async Task<List<FinancialValue>> GetAllAsync(string code, CancellationToken ct = default)
    {
        var result = new List<FinancialValue>();
        foreach (var (statement, items) in Statements)
        {
            var url = $"https://money.finance.sina.com.cn/corp/go.php/vDOWN_{statement}" +
                      $"/displaytype/4/stockid/{code}/ctrl/all.phtml";
            var text = await _rateLimiter.RunAsync(() => FetchTextAsync(url, ct), ct);
            Parse(code, text, items, result);
        }
        return result;
    }

    private async Task<string> FetchTextAsync(string url, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://finance.sina.com.cn/");
            var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new RateLimitedException($"新浪财务接口返回 {(int)resp.StatusCode}，疑似触发反爬限流");
            resp.EnsureSuccessStatusCode();
            bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            throw new RateLimitedException($"无法连接新浪财务接口：{ex.InnerException?.Message ?? ex.Message}", ex);
        }
        if (bytes.Length == 0) throw new RateLimitedException("新浪财务接口返回空响应，疑似限流");
        return Encoding.GetEncoding("GBK").GetString(bytes);
    }

    private static void Parse(string code, string text, (string Key, string[] Names)[] items, List<FinancialValue> result)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2) return; // 空表（极少数标的没有该报表）

        var header = lines[0].Split('\t');
        var dates = new DateTime?[header.Length];
        for (int i = 1; i < header.Length; i++)
            dates[i] = DateTime.TryParseExact(header[i].Trim(), "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d) ? d : null;

        // 每个键记录当前命中的备选名优先级——低序号（更优先）的行名出现时覆盖之前的匹配
        var matched = new Dictionary<string, int>();
        var values = new Dictionary<string, Dictionary<DateTime, double>>();

        foreach (var line in lines.Skip(1))
        {
            var cols = line.Split('\t');
            var name = StripOrdinalPrefix(cols[0].Trim());
            if (name.Length == 0) continue;

            foreach (var (key, names) in items)
            {
                int rank = Array.IndexOf(names, name);
                if (rank < 0) continue;
                if (matched.TryGetValue(key, out var best) && best <= rank) continue; // 已有更优先的行
                matched[key] = rank;
                var byDate = new Dictionary<DateTime, double>();
                for (int i = 1; i < cols.Length && i < dates.Length; i++)
                {
                    if (dates[i] is not { } d) continue;
                    if (!IsQuarterEnd(d)) continue;
                    if (!double.TryParse(cols[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
                    byDate.TryAdd(d, v); // 同一报告期出现多列（调整前后）时保留最左（最新披露版本）
                }
                values[key] = byDate;
            }
        }

        foreach (var (key, byDate) in values)
            foreach (var (d, v) in byDate)
                result.Add(new FinancialValue { Code = code, ReportDate = d, Key = key, Value = v });
    }

    /// <summary>去掉"一、/二、/…"的序号前缀（"五、净利润"→"净利润"、"一、营业收入"→"营业收入"）。</summary>
    private static string StripOrdinalPrefix(string name)
    {
        if (name.Length >= 2 && name[1] == '、' && "一二三四五六七八九十".Contains(name[0]))
            return name[2..];
        return name;
    }

    private static bool IsQuarterEnd(DateTime d) =>
        (d.Month, d.Day) is (3, 31) or (6, 30) or (9, 30) or (12, 31);
}
