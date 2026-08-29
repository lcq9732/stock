namespace StockPlatform.Logic.Models;

/// <summary>
/// 银行监管指标的 metric_key（2026-08-29 新增）。
///
/// 这些数**不在三张报表里**，只在财报正文的"会计数据和财务指标摘要"章节（招行 2026 中报是
/// 第 6~9 页，全篇 309 页里就那两三页）。所有上市银行按监管要求披露同一套指标、表格结构基本
/// 一致，所以定位和解析是可行的，见 BankReportParser。
///
/// 补的正是十二条里靠三表算不出来的那几条：01 资产质量、02 拨备覆盖（存量）、03 核心一级资本、
/// 08 贷款集中度，以及 12 交叉验证要用的迁徙率。
/// </summary>
public static class BankRegulatoryKeys
{
    // ── 01 资产质量 ──
    /// <summary>不良贷款率（%）。</summary>
    public const string NplRatio = "npl_ratio";
    /// <summary>贷款拨备率（%）= 贷款损失准备 ÷ 贷款总额。</summary>
    public const string LoanProvisionRatio = "loan_provision_ratio";

    // ── 02 拨备 ──
    /// <summary>拨备覆盖率（%）= 贷款损失准备 ÷ 不良贷款余额。下限看 200%，上限 350%
    /// （超过就是把利润存进蓄水池，见 BankHealthCheckBuilder 的说明）。</summary>
    public const string ProvisionCoverage = "provision_coverage";
    /// <summary>信用成本（%，年化）= 信用减值损失 ÷ 平均贷款余额。</summary>
    public const string CreditCost = "credit_cost";

    // ── 03 资本充足 ──
    /// <summary>核心一级资本充足率（%）。⚠ 同时有高级法/权重法两个口径，见 basis 列。</summary>
    public const string CoreTier1Car = "core_tier1_car";
    /// <summary>一级资本充足率（%）。</summary>
    public const string Tier1Car = "tier1_car";
    /// <summary>资本充足率（%）。</summary>
    public const string TotalCar = "total_car";

    // ── 05 息差（官方口径，跟本地用总资产近似算的那个不是一回事） ──
    /// <summary>净利息收益率/净息差（%，年化）= 利息净收入 ÷ 总生息资产平均余额。</summary>
    public const string Nim = "nim";
    /// <summary>净利差（%）= 总生息资产平均收益率 − 总计息负债平均成本率。</summary>
    public const string NetInterestSpread = "net_interest_spread";

    // ── 11 成本 ──
    /// <summary>成本收入比（%）。PDF 里也有，正好跟从利润表倒推的值互校——两者对不上说明
    /// 科目映射或倒推口径有问题。</summary>
    public const string CostIncomeRatio = "cost_income_ratio";

    // ── 08 集中度（表格自带监管标准值） ──
    /// <summary>单一最大客户贷款和垫款比例（%），监管标准 ≤10。</summary>
    public const string Top1LoanRatio = "top1_loan_ratio";
    /// <summary>前十大客户贷款和垫款比例（%）。</summary>
    public const string Top10LoanRatio = "top10_loan_ratio";
    /// <summary>流动性比例（%），监管标准 ≥25。</summary>
    public const string LiquidityRatio = "liquidity_ratio";
    /// <summary>流动性覆盖率（%），监管标准 ≥100。</summary>
    public const string Lcr = "lcr";

    // ── 09/12 迁徙率：抓"正在恶化"的关键。招行不良率 0.94% 纹丝不动，
    //    关注类迁徙率却从 28.66% 飙到 39.94% ──
    /// <summary>正常类贷款迁徙率（%）。</summary>
    public const string NormalMigration = "normal_migration";
    /// <summary>关注类贷款迁徙率（%）。</summary>
    public const string SpecialMentionMigration = "sm_migration";
    /// <summary>次级类贷款迁徙率（%）。</summary>
    public const string SubstandardMigration = "substandard_migration";
    /// <summary>可疑类贷款迁徙率（%）。</summary>
    public const string DoubtfulMigration = "doubtful_migration";

    /// <summary>权重法口径（所有银行都有，横向比较用这个）。</summary>
    public const string BasisWeighted = "weighted";
    /// <summary>高级法口径（只有获批的少数银行有，数值明显高于权重法）。</summary>
    public const string BasisAdvanced = "advanced";
}

/// <summary>
/// 券商监管指标（2026-08-29 新增）——《证券公司风险控制指标管理办法》要求的核心风控指标，
/// 所有券商在年报/中报的"净资本及风险控制指标"表里按统一格式披露（实测中信证券 2026 中报 p12）。
///
/// 这套指标的意义跟银行的资本充足率类似，但口径完全不同：银行看资本能吸收多少损失，券商看
/// **净资本能覆盖多少风险准备**、以及自营盘相对净资本有多大。每项都有明确的监管红线，
/// 而且实务上还有一档"预警标准"（红线的 1.2 倍），触及预警就要报送整改。
/// </summary>
public static class BrokerRegulatoryKeys
{
    /// <summary>风险覆盖率（%）= 净资本 ÷ 各项风险资本准备之和。监管 ≥100%，预警 120%。
    /// 券商最核心的一条，相当于银行的资本充足率。</summary>
    public const string RiskCoverage = "risk_coverage";
    /// <summary>资本杠杆率（%）= 核心净资本 ÷ 表内外资产总额。监管 ≥8%，预警 9.6%。</summary>
    public const string CapitalLeverage = "capital_leverage";
    /// <summary>流动性覆盖率（%）。监管 ≥100%，预警 120%。</summary>
    public const string LiquidityCoverage = "broker_lcr";
    /// <summary>净稳定资金率（%）。监管 ≥100%，预警 120%。</summary>
    public const string NetStableFunding = "net_stable_funding";
    /// <summary>净资本/净资产（%）。监管 ≥20%。偏低说明净资产里有大量不能计入净资本的资产。</summary>
    public const string NetCapitalToNetAssets = "netcap_to_netassets";
    /// <summary>净资本/负债（%）。监管 ≥8%。</summary>
    public const string NetCapitalToLiabilities = "netcap_to_liab";
    /// <summary>自营权益类证券及其衍生品/净资本（%）。监管 **≤100%**——方向跟上面几条相反，
    /// 越高越激进。</summary>
    public const string EquityPropToNetCapital = "equity_prop_to_netcap";
    /// <summary>自营非权益类证券及其衍生品/净资本（%）。监管 **≤500%**，同样是上限。</summary>
    public const string NonEquityPropToNetCapital = "nonequity_prop_to_netcap";
}

/// <summary>
/// 保险监管指标（2026-08-29 新增）——偿二代二期的偿付能力充足率，以及财险的综合成本率。
/// 实测中国平安 2025 年报 p15/p51。
///
/// ⚠ **集团口径 vs 子公司口径**：综合金融集团（平安/太保）的年报里，同一个指标会同时出现
/// 集团数和各子公司数（平安寿险/养老险/健康险横排一行）。解析取第一次命中，而集团口径在前，
/// 所以通常拿到的是集团数——但这依赖各家排版，用的时候留意 source_page。
///
/// 内含价值(EV)和新业务价值(NBV)暂不解析：它们是**金额**不是百分比，还带千位分隔符，
/// 跟现有这套按百分比设计的取数/校验逻辑不兼容，要另做一套。
/// </summary>
public static class InsurerRegulatoryKeys
{
    /// <summary>核心偿付能力充足率（%）= 核心资本 ÷ 最低资本。**监管最低要求 50%**。</summary>
    public const string CoreSolvency = "core_solvency";
    /// <summary>综合偿付能力充足率（%）= 实际资本 ÷ 最低资本。**监管最低要求 100%**。</summary>
    public const string ComprehensiveSolvency = "comprehensive_solvency";
    /// <summary>综合成本率（%，财险）。= 综合赔付率 + 综合费用率，**低于 100% 才是承保盈利**，
    /// 高于 100% 说明保费收入不够覆盖赔付和费用，只能靠投资收益补。</summary>
    public const string CombinedRatio = "combined_ratio";
    /// <summary>车险综合成本率（%）。财险里占比最大的险种，单独看更能反映承保能力。</summary>
    public const string AutoCombinedRatio = "auto_combined_ratio";
}

/// <summary>一条银行监管指标。</summary>
public class BankRegulatoryMetric
{
    public string Code { get; init; } = "";
    public DateTime ReportDate { get; init; }
    public string MetricKey { get; init; } = "";
    /// <summary>口径，见 <see cref="BankRegulatoryKeys.BasisWeighted"/>；不适用时为空串。</summary>
    public string Basis { get; init; } = "";
    public double Value { get; init; }
    /// <summary>报表自带的监管标准值（"≥25"/"≤10"）；没有就为空。
    /// 这是体检表"参考值"列最权威的来源——监管自己就是按"值 + 标准值"两列披露的。</summary>
    public string? StandardValue { get; init; }
    /// <summary>取自 PDF 第几页（1 起），便于人工回查核对。</summary>
    public int SourcePage { get; init; }
}

/// <summary>一份财报 PDF 的解析结果状态。</summary>
public class BankReportFetchState
{
    public string Code { get; init; } = "";
    public DateTime ReportDate { get; init; }
    /// <summary>ok | no_pdf | no_text | no_match | error</summary>
    public string Status { get; init; } = "";
    public int MetricCount { get; init; }
    public string? Message { get; init; }
    public string? PdfUrl { get; init; }
    public string? PdfPath { get; init; }
}
