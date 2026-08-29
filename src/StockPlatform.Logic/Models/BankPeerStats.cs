namespace StockPlatform.Logic.Models;

/// <summary>
/// 银行体检表的"参考值"来源（2026-08-29 新增）——全行业分位/均值。
///
/// ════ 为什么参考值必须跟着行业走 ════
/// 网上流传的银行选股清单普遍钉死固定阈值（"ROE&gt;10%"），而 A 股银行业净息差已经从 2019 年的
/// 约 2.2% 一路降到 2026Q1 的 1.37%，全行业 ROE 系统性下移——实测 42 家里只有 14 家 ROE 过 10%，
/// **工建农中交邮六大行全部不达标**。一个把系统重要性最高的六家全排除掉的标准，排掉的不是"雷"
/// 而是"时代"。所以相对性指标（ROE/ROA/净息差/PB）一律用当期行业分位，不写死。
///
/// 结构性指标不在这里：成本收入比 30/35/45、信用减值同比为负=预警，这些不随周期漂移，直接写在
/// <see cref="Services.BankHealthCheckBuilder"/> 里。
///
/// ════ Live vs Builtin ════
/// <see cref="IsLive"/>=true 是从本地库当期实算的（见 <see cref="Services.BankPeerStatsBuilder"/>）；
/// 库里银行数据不足时退回 <see cref="Builtin"/>——一份带日期的实测快照，界面会标明"内置基准"
/// 让用户知道它的时效，而不是假装是当期值。
/// </summary>
public class BankPeerStats
{
    /// <summary>这份基准对应的报告期/统计时点。</summary>
    public DateTime AsOf { get; init; }

    /// <summary>true=从本地库当期实算；false=内置快照。</summary>
    public bool IsLive { get; init; }

    /// <summary>参与统计的银行家数。</summary>
    public int SampleSize { get; init; }

    // ── 相对性指标的分位（单位均为 %） ──
    public double RoeMedian { get; init; }
    public double RoeP75 { get; init; }
    public double RoaMedian { get; init; }
    public double RoaP75 { get; init; }

    /// <summary>净息差行业均值。⚠ 内置值是官方口径（分母=生息资产）；本地实算用的是
    /// "利息净收入÷平均总资产"的近似（分母偏大、结果偏低约 0.15pct），两者不可混用，
    /// 所以 <see cref="IsLive"/> 时这一项也用实算样本自己的中位数，保证同口径可比。</summary>
    public double NimAvg { get; init; }

    /// <summary>成本收入比中位数（%）。</summary>
    public double CostIncomeMedian { get; init; }

    // ── 以下几项本地库暂时算不出（在财报 PDF 附注里），只作为参考值展示，见十二条的 01/02/03 ──
    public double NplRatioAvg { get; init; }
    public double ProvisionCoverageAvg { get; init; }
    public double CoreTier1Avg { get; init; }

    /// <summary>
    /// 内置基准快照：2026 年一季度 42 家 A 股上市银行实测值。
    /// 不良率/拨备覆盖率/净息差为全行业加权平均（上市银行一季报汇总）；ROE/ROA 为 2025 年报口径
    /// （归母净利÷平均归母净资产、净利润÷平均总资产）算出的 42 家分位；核心一级为行业平均 10.55%
    /// （超过 13% 的只有建行、沪农商行、招行、江阴、工行、贵阳 6 家）。
    /// </summary>
    public static BankPeerStats Builtin { get; } = new()
    {
        AsOf = new DateTime(2026, 3, 31),
        IsLive = false,
        SampleSize = 42,
        RoeMedian = 9.07,
        RoeP75 = 10.73,
        RoaMedian = 0.726,
        RoaP75 = 0.85,
        NimAvg = 1.37,
        CostIncomeMedian = 31.0,
        NplRatioAvg = 1.22,
        ProvisionCoverageAvg = 233.36,
        CoreTier1Avg = 10.55,
    };
}
