using System.Globalization;
using System.Net;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 三张财务报表的关键科目，**东财 F10**（2026-09-10 新增）。替代
/// <see cref="SinaFinancialProvider"/> 的中文行名匹配，见 doc/financial-source-eastmoney-design.md。
///
/// ════ 为什么换 ════
/// 按价值排序：① 摆脱**中文行名匹配**——原来拿"营业总收入"/"业务及管理费用"去 TSV 里找行，
/// 版式一改就静默丢科目（曾漏配银行行名，42 家银行的成本收入比整类算不出来）；东财是固定
/// 英文列名 + 显式 null。② 机构类型由 <c>ORG_TYPE</c> 直接给，不用靠"有没有已赚保费这一行"反推。
/// ③ 退市股照样有（300104 乐视网 71 期）。字段从 40 涨到 200+ 是**期权**，`FinancialKeys`
/// 现在只有 60 个 key，扩 key 之前兑现不了。
///
/// ════ 映射不是猜的，是数值证明的 ════
/// 拿库里 8 只样本票某期的 60 个科目值，去东财三张表的**所有数值列**里找相等的列
/// （1e-9 相对容差），值对上才算映射成立。600519/600036/600030/601318/000651/600019/000002/600941。
/// 逐格复核：000001 4022 格、000338 4380 格与库里**零差异**。
///
/// ════ ⚠ 必须按 ORG_TYPE 整套换表（2026-09-10 全量比对抓出来的）════
/// 银行/券商**不是"通用表 + 补两个科目"**，是三张表整套换：
///   · 银行的 <c>GBALANCE</c> / <c>GCASHFLOW</c> 是**空表**（不是列为 null，是整张表没这只票）。
///     第一版照通用表取，平安银行缺了 9566 格——assets/liab/equity_total/ocf/icf/fcf 全没了，
///     而且一个错都不报。
///   · 专表的列名也不同：<c>revenue</c> 银行/券商是 OPERATE_INCOME（通用是 TOTAL_OPERATE_INCOME）、
///     <c>total_cost</c> 是 OPERATE_EXPENSE、<c>admin_exp</c> 是 BUSINESS_MANAGE_EXPENSE、
///     券商的应收账款叫 RECEIVABLES。
///   · 净额 vs 毛额：招行 G 表 <c>INTEREST_INCOME</c> 172,733,000,000 −
///     <c>INTEREST_EXPENSE</c> 60,711,000,000＝B 表 <c>INTEREST_NI</c> 112,022,000,000（库里的值）。
///
/// 三套映射都用**跨票交集**定的：3 只银行（600036/000001/601398）、3 只券商（600030/000776/601688），
/// 只有三只票都指向同一列才采纳。单票匹配会被"两个科目恰好等值"骗到——平安银行没有少数股东权益，
/// <c>equity_total</c> 和 <c>equity_parent</c> 完全相等，只看它根本分不出哪列是哪个。
///
/// ════ 保险不走这里 ════
/// 东财**整组不填**保险公司的支出科目（赔付支出/退保金/保单红利/分保费用，三家保险 × 三张表全 null），
/// 而 <c>claim_expense</c> 有真实消费方（BrokerInsurerHealthCheckBuilder 的赔付率）。
/// 所以保险整只票走新浪，路由在 <see cref="FinancialSourceRouter"/>，本类只负责非保险的票。
/// </summary>
public class EastMoneyFinancialProvider : IFinancialProvider
{
    private const string Host = "https://datacenter.eastmoney.com/securities/api/data/v1/get";

    /// <summary>
    /// <c>ORG_TYPE</c> 的取值。东财用中文，**逐个实测过、不要凭语感写**（2026-09-10）：
    /// 茅台/宁德=通用，招行/工行/平安银行=银行，中信/广发等 10 只=<b>证券</b>，
    /// 平安/国寿/太保/新华/人保=保险。
    ///
    /// ⚠ 这里踩过一次：券商想当然写成了"券商"，结果**所有券商都被当成通用票**，
    /// 去读对券商为空的 G 表，整只票的科目全缺——而且不报错，只在比对里表现为
    /// "某只票缺 104 格"。字面值错一个字，一整类公司的数据就没了。
    /// </summary>
    public const string OrgTypeGeneral = "通用";
    public const string OrgTypeBank = "银行";
    public const string OrgTypeBroker = "证券";
    public const string OrgTypeInsurer = "保险";

    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus;

    public EastMoneyFinancialProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
    {
        _rateLimiter = rateLimiter;
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.Timeout = TimeSpan.FromSeconds(30);
        _rateLimiter.OnStatus += m => OnStatus?.Invoke(m);
    }

    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    // ─────────────────── 映射表（§2 of the design doc）───────────────────

    /// <summary>利润表（通用表 GINCOME）。银行/券商也先取这张，专有净额科目再由专表覆盖。</summary>
    private static readonly (string Key, string Col)[] IncomeMap =
    [
        (FinancialKeys.Revenue, "TOTAL_OPERATE_INCOME"),
        (FinancialKeys.TotalCost, "TOTAL_OPERATE_COST"),
        (FinancialKeys.OperCost, "OPERATE_COST"),
        (FinancialKeys.TaxSurcharge, "OPERATE_TAX_ADD"),
        (FinancialKeys.SellExpense, "SALE_EXPENSE"),
        (FinancialKeys.AdminExpense, "MANAGE_EXPENSE"),
        (FinancialKeys.FinanceExpense, "FINANCE_EXPENSE"),
        (FinancialKeys.RdExpense, "RESEARCH_EXPENSE"),
        (FinancialKeys.ImpairmentLoss, "ASSET_IMPAIRMENT_LOSS"),
        (FinancialKeys.FvChangeGain, "FAIRVALUE_CHANGE_INCOME"),
        (FinancialKeys.InvestIncome, "INVEST_INCOME"),
        (FinancialKeys.OperProfit, "OPERATE_PROFIT"),
        (FinancialKeys.TotalProfit, "TOTAL_PROFIT"),
        (FinancialKeys.IncomeTax, "INCOME_TAX"),
        (FinancialKeys.NetProfit, "NETPROFIT"),
        (FinancialKeys.NetProfitParent, "PARENT_NETPROFIT"),
        (FinancialKeys.MinorityPl, "MINORITY_INTEREST"),
        (FinancialKeys.EpsBasic, "BASIC_EPS"),
        (FinancialKeys.OtherOperExpense, "OTHER_BUSINESS_COST"),
    ];

    /// <summary>
    /// 资产负债表（GBALANCE）。
    /// ⚠ <c>UNASSIGN_RPOFIT</c> 是东财那边的拼写错误（RPOFIT），照抄，别"修正"成 PROFIT。
    /// 歧义列的取舍：<c>ar</c> 取 ACCOUNTS_RECE 不取 NOTE_ACCOUNTS_RECE（后者是票据+应收合计，
    /// 样本票据为 0 时才碰巧相等）；<c>ap</c> 同理；<c>assets</c> 取 TOTAL_ASSETS 不取 TOTAL_LIAB_EQUITY。
    /// </summary>
    private static readonly (string Key, string Col)[] BalanceMap =
    [
        (FinancialKeys.Cash, "MONETARYFUNDS"),
        (FinancialKeys.NoteReceivable, "NOTE_RECE"),
        (FinancialKeys.AccountsReceivable, "ACCOUNTS_RECE"),
        (FinancialKeys.Prepayment, "PREPAYMENT"),
        (FinancialKeys.Inventory, "INVENTORY"),
        (FinancialKeys.CurrentAssets, "TOTAL_CURRENT_ASSETS"),
        (FinancialKeys.TotalAssets, "TOTAL_ASSETS"),
        (FinancialKeys.ShortLoan, "SHORT_LOAN"),
        (FinancialKeys.NotePayable, "NOTE_PAYABLE"),
        (FinancialKeys.AccountsPayable, "ACCOUNTS_PAYABLE"),
        (FinancialKeys.AdvanceReceipts, "ADVANCE_RECEIVABLES"),
        (FinancialKeys.CurrentLiabilities, "TOTAL_CURRENT_LIAB"),
        (FinancialKeys.LongLoan, "LONG_LOAN"),
        (FinancialKeys.BondPayable, "BOND_PAYABLE"),
        (FinancialKeys.TotalLiabilities, "TOTAL_LIABILITIES"),
        (FinancialKeys.ShareCapital, "SHARE_CAPITAL"),
        (FinancialKeys.EquityParent, "TOTAL_PARENT_EQUITY"),
        (FinancialKeys.MinorityEquity, "MINORITY_EQUITY"),
        (FinancialKeys.EquityTotal, "TOTAL_EQUITY"),
        (FinancialKeys.UndistributedProfit, "UNASSIGN_RPOFIT"),
    ];

    /// <summary>
    /// 现金流量表（GCASHFLOW）。
    /// <c>ocf</c> 取 NETCASH_OPERATE 不取 NETCASH_OPERATENOTE（后者是附注间接法的结果，
    /// 正常相等但口径不同）；<c>depreciation</c> 取 FA_IR_DEPR 不取 OILGAS_BIOLOGY_DEPR。
    /// </summary>
    private static readonly (string Key, string Col)[] CashFlowMap =
    [
        (FinancialKeys.SalesCash, "SALES_SERVICES"),
        (FinancialKeys.Ocf, "NETCASH_OPERATE"),
        (FinancialKeys.Icf, "NETCASH_INVEST"),
        (FinancialKeys.Fcf, "NETCASH_FINANCE"),
        (FinancialKeys.Capex, "CONSTRUCT_LONG_ASSET"),
        (FinancialKeys.CashEnd, "END_CCE"),
        (FinancialKeys.ImpairmentProvision, "ASSET_IMPAIRMENT"),
        (FinancialKeys.Depreciation, "FA_IR_DEPR"),
        (FinancialKeys.AmortIntangible, "IA_AMORTIZE"),
        (FinancialKeys.AmortLongPrepaid, "LPE_AMORTIZE"),
        (FinancialKeys.FvChangeLoss, "FAIRVALUE_CHANGE_LOSS"),
        (FinancialKeys.InventoryDecrease, "INVENTORY_REDUCE"),
        (FinancialKeys.ReceivableDecrease, "OPERATE_RECE_REDUCE"),
        (FinancialKeys.PayableIncrease, "OPERATE_PAYABLE_ADD"),
    ];

    // ═══════════════ 银行 / 券商：整套换表，不是"通用表 + 补两个科目" ═══════════════
    //
    // 2026-09-10 全量比对抓出来的：银行的 GBALANCE / GCASHFLOW **返回数据为空**（不是列为 null，
    // 是整张表没有这只票），只有 BBALANCE / BCASHFLOW 才有。第一版只对利润表分了表，
    // 结果平安银行缺了 9566 格——assets / liab / equity_total / ocf / icf / fcf 这些全没了。
    //
    // 而且专表的列名跟通用表**不一样**，不能只换前缀：
    //   · revenue    银行/券商是 OPERATE_INCOME，通用是 TOTAL_OPERATE_INCOME
    //   · total_cost 银行/券商是 OPERATE_EXPENSE，通用是 TOTAL_OPERATE_COST
    //   · admin_exp  银行/券商是 BUSINESS_MANAGE_EXPENSE（业务及管理费），通用是 MANAGE_EXPENSE
    //   · ar         券商是 RECEIVABLES，通用是 ACCOUNTS_RECE
    //
    // 下面每一条都经过**跨票交集**验证：3 只银行（600036/000001/601398）、3 只券商
    // （600030/000776/601688）各自数值匹配后取交集，只有三只票都指向同一列才写进来——
    // 单票匹配会被"两个科目恰好等值"骗到（平安银行没有少数股东权益，equity_total 和
    // equity_parent 完全相等，单看它就分不出哪列是哪个）。

    /// <summary>银行利润表（BINCOME）。</summary>
    private static readonly (string Key, string Col)[] BankIncomeFullMap =
    [
        (FinancialKeys.Revenue, "OPERATE_INCOME"),
        (FinancialKeys.TotalCost, "OPERATE_EXPENSE"),
        (FinancialKeys.TaxSurcharge, "OPERATE_TAX_ADD"),
        (FinancialKeys.AdminExpense, "BUSINESS_MANAGE_EXPENSE"),
        (FinancialKeys.FvChangeGain, "FAIRVALUE_CHANGE_INCOME"),
        (FinancialKeys.InvestIncome, "INVEST_INCOME"),
        (FinancialKeys.OperProfit, "OPERATE_PROFIT"),
        (FinancialKeys.TotalProfit, "TOTAL_PROFIT"),
        (FinancialKeys.IncomeTax, "INCOME_TAX"),
        (FinancialKeys.NetProfit, "NETPROFIT"),
        (FinancialKeys.NetProfitParent, "PARENT_NETPROFIT"),
        (FinancialKeys.MinorityPl, "MINORITY_INTEREST"),
        (FinancialKeys.EpsBasic, "BASIC_EPS"),
        (FinancialKeys.OtherOperExpense, "OTHER_BUSINESS_COST"),
        (FinancialKeys.InterestNet, "INTEREST_NI"),
        (FinancialKeys.FeeCommissionNet, "FEE_COMMISSION_NI"),
        (FinancialKeys.ImpairmentLoss, "ASSET_IMPAIRMENT_LOSS"),
    ];

    /// <summary>银行资产负债表（BBALANCE）。银行没有存货/预付/流动资产合计这些，不配就是不配。</summary>
    private static readonly (string Key, string Col)[] BankBalanceMap =
    [
        (FinancialKeys.TotalAssets, "TOTAL_ASSETS"),
        (FinancialKeys.TotalLiabilities, "TOTAL_LIABILITIES"),
        (FinancialKeys.EquityTotal, "TOTAL_EQUITY"),
        (FinancialKeys.EquityParent, "TOTAL_PARENT_EQUITY"),
        (FinancialKeys.MinorityEquity, "MINORITY_EQUITY"),
        (FinancialKeys.ShareCapital, "SHARE_CAPITAL"),
        (FinancialKeys.UndistributedProfit, "UNASSIGN_RPOFIT"),
        (FinancialKeys.BondPayable, "BOND_PAYABLE"),
    ];

    /// <summary>银行现金流量表（BCASHFLOW）。</summary>
    private static readonly (string Key, string Col)[] BankCashFlowMap =
    [
        (FinancialKeys.Ocf, "NETCASH_OPERATE"),
        (FinancialKeys.Icf, "NETCASH_INVEST"),
        (FinancialKeys.Fcf, "NETCASH_FINANCE"),
        (FinancialKeys.CashEnd, "END_CCE"),
        (FinancialKeys.PayableIncrease, "OPERATE_PAYABLE_ADD"),
        (FinancialKeys.AmortLongPrepaid, "LPE_AMORTIZE"),
        (FinancialKeys.Depreciation, "FA_IR_DEPR"),
        (FinancialKeys.AmortIntangible, "IA_AMORTIZE"),
        (FinancialKeys.ImpairmentProvision, "ASSET_IMPAIRMENT"),
    ];

    /// <summary>券商利润表（SINCOME）。</summary>
    private static readonly (string Key, string Col)[] BrokerIncomeFullMap =
    [
        (FinancialKeys.Revenue, "OPERATE_INCOME"),
        (FinancialKeys.TotalCost, "OPERATE_EXPENSE"),
        (FinancialKeys.TaxSurcharge, "OPERATE_TAX_ADD"),
        (FinancialKeys.AdminExpense, "BUSINESS_MANAGE_EXPENSE"),
        (FinancialKeys.InvestIncome, "INVEST_INCOME"),
        (FinancialKeys.OperProfit, "OPERATE_PROFIT"),
        (FinancialKeys.TotalProfit, "TOTAL_PROFIT"),
        (FinancialKeys.IncomeTax, "INCOME_TAX"),
        (FinancialKeys.NetProfit, "NETPROFIT"),
        (FinancialKeys.NetProfitParent, "PARENT_NETPROFIT"),
        (FinancialKeys.MinorityPl, "MINORITY_INTEREST"),
        (FinancialKeys.EpsBasic, "BASIC_EPS"),
        (FinancialKeys.OtherOperExpense, "OTHER_BUSINESS_COST"),
        (FinancialKeys.InterestNet, "INTEREST_NI"),
        (FinancialKeys.FeeCommissionNet, "FEE_COMMISSION_NI"),
        (FinancialKeys.BrokerageNet, "AGENT_SECURITY_NI"),
        (FinancialKeys.UnderwritingNet, "SECURITY_UNDERWRITE_NI"),
        (FinancialKeys.AssetMgmtNet, "ASSET_MANAGE_NI"),
        (FinancialKeys.ImpairmentLoss, "ASSET_IMPAIRMENT_LOSS"),
        (FinancialKeys.FvChangeGain, "FAIRVALUE_CHANGE_INCOME"),
    ];

    /// <summary>券商资产负债表（SBALANCE）。⚠ 应收账款在这张表叫 RECEIVABLES。</summary>
    private static readonly (string Key, string Col)[] BrokerBalanceMap =
    [
        (FinancialKeys.Cash, "MONETARYFUNDS"),
        (FinancialKeys.AccountsReceivable, "RECEIVABLES"),
        (FinancialKeys.AccountsPayable, "ACCOUNTS_PAYABLE"),
        (FinancialKeys.TotalAssets, "TOTAL_ASSETS"),
        (FinancialKeys.TotalLiabilities, "TOTAL_LIABILITIES"),
        (FinancialKeys.EquityTotal, "TOTAL_EQUITY"),
        (FinancialKeys.EquityParent, "TOTAL_PARENT_EQUITY"),
        (FinancialKeys.MinorityEquity, "MINORITY_EQUITY"),
        (FinancialKeys.ShareCapital, "SHARE_CAPITAL"),
        (FinancialKeys.UndistributedProfit, "UNASSIGN_RPOFIT"),
        (FinancialKeys.ShortLoan, "SHORT_LOAN"),
        (FinancialKeys.LongLoan, "LONG_LOAN"),
        (FinancialKeys.BondPayable, "BOND_PAYABLE"),
    ];

    /// <summary>券商现金流量表（SCASHFLOW）。</summary>
    private static readonly (string Key, string Col)[] BrokerCashFlowMap =
    [
        (FinancialKeys.Ocf, "NETCASH_OPERATE"),
        (FinancialKeys.Icf, "NETCASH_INVEST"),
        (FinancialKeys.Fcf, "NETCASH_FINANCE"),
        (FinancialKeys.CashEnd, "END_CCE"),
        (FinancialKeys.PayableIncrease, "OPERATE_PAYABLE_ADD"),
        (FinancialKeys.ReceivableDecrease, "OPERATE_RECE_REDUCE"),
        (FinancialKeys.AmortLongPrepaid, "LPE_AMORTIZE"),
        (FinancialKeys.AmortIntangible, "IA_AMORTIZE"),
        (FinancialKeys.Depreciation, "FA_IR_DEPR"),
        (FinancialKeys.FvChangeLoss, "FAIRVALUE_CHANGE_LOSS"),
        (FinancialKeys.ImpairmentProvision, "ASSET_IMPAIRMENT"),
    ];

    /// <summary>一类机构对应的三张报表 + 三套映射。</summary>
    private sealed record ReportSet(
        string Income, string Balance, string CashFlow,
        (string Key, string Col)[] IncomeMap,
        (string Key, string Col)[] BalanceMap,
        (string Key, string Col)[] CashFlowMap);

    private static readonly ReportSet GeneralSet = new(
        "RPT_F10_FINANCE_GINCOME", "RPT_F10_FINANCE_GBALANCE", "RPT_F10_FINANCE_GCASHFLOW",
        IncomeMap, BalanceMap, CashFlowMap);

    private static readonly ReportSet BankSet = new(
        "RPT_F10_FINANCE_BINCOME", "RPT_F10_FINANCE_BBALANCE", "RPT_F10_FINANCE_BCASHFLOW",
        BankIncomeFullMap, BankBalanceMap, BankCashFlowMap);

    private static readonly ReportSet BrokerSet = new(
        "RPT_F10_FINANCE_SINCOME", "RPT_F10_FINANCE_SBALANCE", "RPT_F10_FINANCE_SCASHFLOW",
        BrokerIncomeFullMap, BrokerBalanceMap, BrokerCashFlowMap);

    private static ReportSet SetFor(string orgType) => orgType switch
    {
        OrgTypeBank => BankSet,
        OrgTypeBroker => BrokerSet,
        _ => GeneralSet,
    };

    // ─────────────────── 取数 ───────────────────

    public async Task<List<FinancialValue>> GetAllAsync(string code, CancellationToken ct = default)
        => (await FetchWithOrgTypeAsync(code, ct)).Rows;

    /// <summary>
    /// 跟 <see cref="GetAllAsync"/> 一样取数，但**同时把 <c>ORG_TYPE</c> 返回出去**——
    /// <see cref="FinancialSourceRouter"/> 靠它发现"这其实是家保险公司"，改走新浪。
    /// 取不到数据时 OrgType 为空串。
    /// </summary>
    public async Task<(string OrgType, List<FinancialValue> Rows)> FetchWithOrgTypeAsync(
        string code, CancellationToken ct = default)
    {
        var secucode = ToSecuCode(code);
        var result = new List<FinancialValue>();

        // ① 通用利润表先取一次：它对四类机构都有数据，<c>ORG_TYPE</c> 就在每一行上，
        //    而"该读哪三张表"完全由它决定。银行/券商为此多花一个请求（4 个 vs 3 个），
        //    只涉及一百来只票，换的是"绝不会拿错表"。
        var probe = await QueryAsync(GeneralSet.Income, secucode, ct);
        var orgType = probe.Count > 0 ? Str(probe[0], "ORG_TYPE") : "";

        // 保险直接交回给路由器，别浪费后面几个请求——它整只票要改走新浪。
        if (orgType == OrgTypeInsurer) return (orgType, result);

        var set = SetFor(orgType);

        // ② 利润表：通用票探测那次就是它本身，不重复请求；银行/券商要换成专表重取。
        var income = set.Income == GeneralSet.Income ? probe : await QueryAsync(set.Income, secucode, ct);
        Collect(code, income, set.IncomeMap, result);

        // ③ 资产负债表 + 现金流量表。⚠ 必须跟着 ORG_TYPE 走：银行的 GBALANCE/GCASHFLOW
        //    是**空表**（不是列为 null），照通用表取会静默丢掉 assets/liab/ocf 这些核心科目。
        Collect(code, await QueryAsync(set.Balance, secucode, ct), set.BalanceMap, result);
        Collect(code, await QueryAsync(set.CashFlow, secucode, ct), set.CashFlowMap, result);

        return (orgType, result);
    }

    /// <summary>
    /// 取一张报表的**全部报告期**。<c>pageSize=500</c> 一次拿完（茅台 103 期、pages=1，
    /// 全市场最长的也远不到 500 期），所以不翻页；真有超过 500 期的会在这里被截断，
    /// 因此顺带对账一次 <c>count</c>，对不上就告警——宁可吵一声也不要静默丢历史。
    /// </summary>
    private async Task<List<JsonElement>> QueryAsync(string reportName, string secucode, CancellationToken ct)
    {
        var url = $"{Host}?reportName={reportName}&columns=ALL&pageSize=500&pageNumber=1" +
                  $"&filter=(SECUCODE=%22{secucode}%22)&sortColumns=REPORT_DATE&sortTypes=-1";
        var txt = await _rateLimiter.RunAsync(() => GetStringAsync(url, ct), ct);

        using var doc = JsonDocument.Parse(txt);
        var root = doc.RootElement;
        if (!root.TryGetProperty("result", out var res) || res.ValueKind != JsonValueKind.Object)
            return [];   // 没有这类报表（比如非银行取 BINCOME）时 result 是 null，不是错误
        if (!res.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var rows = data.EnumerateArray().Select(e => e.Clone()).ToList();
        if (res.TryGetProperty("count", out var c) && c.TryGetInt32(out var total) && total > rows.Count)
            OnStatus?.Invoke($"⚠ {secucode} 的 {reportName} 有 {total} 期、只取回 {rows.Count} 期（单页上限 500）");
        return rows;
    }

    /// <summary>把一张报表的行按映射表摊平成 (报告期, 科目, 值)。null 的列直接跳过，不写 0。</summary>
    private static void Collect(string code, List<JsonElement> rows,
                                (string Key, string Col)[] map, List<FinancialValue> result)
    {
        foreach (var row in rows)
        {
            if (!TryReportDate(row, out var date)) continue;
            foreach (var (key, col) in map)
            {
                var v = Num(row, col);
                if (v.HasValue) result.Add(new FinancialValue { Code = code, ReportDate = date, Key = key, Value = v.Value });
            }
        }
    }

    /// <summary>"2026-06-30 00:00:00" → 日期。</summary>
    private static bool TryReportDate(JsonElement row, out DateTime date)
    {
        date = default;
        var s = Str(row, "REPORT_DATE");
        return s.Length >= 10 &&
               DateTime.TryParse(s[..10], CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static double? Num(JsonElement row, string col)
        => row.TryGetProperty(col, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? d : null;

    private static string Str(JsonElement row, string key)
        => row.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>6 位代码 → 东财的 <c>SECUCODE</c>（600519.SH）。市场判定统一走 MarketClassifier。</summary>
    public static string ToSecuCode(string code)
    {
        code = code.Trim();
        if (code.Length != 6 || !code.All(char.IsDigit))
            throw new ArgumentException($"'{code}' 不是合法的6位A股代码");
        return MarketClassifier.Classify(code) switch
        {
            MarketBoard.Unknown => throw new ArgumentException($"无法识别代码 '{code}' 所属市场"),
            MarketBoard.Beijing => code + ".BJ",
            _ => code + (MarketClassifier.EastMoneySecIdPrefix(code).StartsWith('1') ? ".SH" : ".SZ"),
        };
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://data.eastmoney.com/");
            var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new RateLimitedException($"东财财报接口返回 {(int)resp.StatusCode}，疑似限流");
            resp.EnsureSuccessStatusCode();
            var s = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(s)) throw new RateLimitedException("东财财报接口返回空响应");
            return s;
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            throw new RateLimitedException($"无法连接东财财报接口：{ex.InnerException?.Message ?? ex.Message}", ex);
        }
    }
}
