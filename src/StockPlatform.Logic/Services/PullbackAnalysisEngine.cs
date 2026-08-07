using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// "回调法" — 把用户自述的三条买股原则做成可执行的筛选（2026-08-03 建立，回测过程见下）。
///
/// 原则映射：
///   ① 只买盈利好的股票 → 净利润为正 + 经营现金流为正（**故意只用这两条轻量条件**）
///   ② 不追高           → 收盘价低于MA20，且距一年高点已回撤≥10%
///   ③ 到心理价位就卖   → 不是选股条件，引擎只把目标价/止损价算出来放进 Basis 供执行
/// 另加两条可操作性约束：日均成交额（进得去出得来）、一手金额上限（见 <see cref="MaxLotAmount"/>）。
///
/// **回测结论（2022-06~2026-02，44个时点、186226 个样本，+10%止盈/-10%止损/最长持有120日）**：
///   - 基准（成交额≥1亿）             平均 0.59%，胜率 52.9%
///   - 仅"收盘&lt;MA20"                平均 0.93%，胜率 54.6%  ← 单条最有效
///   - 加"净利&gt;0 + 现金流&gt;0"     平均 0.96%，胜率 54.7%  ← 本引擎采用
///   - 再加 ROE≥10%/净利同比增长      平均 0.28%，胜率 51.4%  ← **反而更差，所以没有采用**
///   重基本面筛选在这份样本里是负贡献；去掉止损单看尾部，它把最差单笔从 -71.6% 改善到 -58.4%，
///   但5%分位（-31.0%→-31.7%）和"跌超30%的比例"（都是17.6%）几乎没变，平均收益却掉了2.8个点，
///   不值得。所以这里只保留"不亏损、赚真钱"这两条底线，不做质量优选。
///
/// **<see cref="StockScreenResult.SortScore"/> = 低于MA20的幅度**（跌得越深分越高）。分档回测：
///   0~-3% 平均0.29%/胜率51.3% · -3~-8% 0.73%/53.6% · -8~-15% 2.98%/65.0% · &lt;-15% 5.40%/77.0%。
///   单调性很漂亮，但**分年看并不稳**：2025年深跌档 +7.57%，2022年是 -1.54%（反向），且4024个
///   深跌样本里有1144个来自2025-01-13和2025-04-16两个V型底。本质是"买跌有效当且仅当后面有反弹"，
///   所以深跌只做排序分和提示，**不设硬阈值**（同 <see cref="MarketEnvironmentCalculator"/> 的处理）。
/// </summary>
public class PullbackAnalysisEngine
{
    /// <summary>一年按 243 个交易日算——"距一年高点回撤"和数据量下限都用它。</summary>
    private const int TradingDaysPerYear = 243;
    private const int MinBarsRequired = TradingDaysPerYear;

    private const int MaWindow = 20;
    private const int AmountWindow = 20;
    /// <summary>日均波幅的统计窗口（交易日）。</summary>
    private const int VolatilityWindow = 60;

    /// <summary>20日均成交额下限（元）。1亿是"进得去出得来"的经验线，也是回测基准用的口径。</summary>
    public double MinAvgAmount { get; init; } = 100_000_000;

    /// <summary>距一年高点至少回撤多少才算"已经回调过"。</summary>
    public double MinPullbackFromHigh { get; init; } = 0.10;

    // ── 两类候选各自的门槛 ──
    //
    // 分成两类的原因：**位置判断和用途判断是两回事**。高股息蓝筹（中国移动股息率4.75%、
    // 招商银行5.00%）常年待在MA20上方，永远进不了"低于MA20"这个回调信号，但它们正是
    // 底仓该买的；反过来跌到位的票（宁波韵升 现金流/净利仅0.49、股息率0.90%）位置对了，
    // 却不适合长期拿。所以底仓不看回调、主动仓才看。
    //
    /// <summary>底仓：股息率下限。要能提供实质现金流，持满一年分红还免税。</summary>
    public double BaseMinDividendYield { get; init; } = 0.03;
    /// <summary>底仓：经营现金流至少要覆盖净利润（利润得变成真钱，长期持有才踏实）。</summary>
    public double BaseMinOcfCoverage { get; init; } = 1.0;
    /// <summary>底仓：日均波幅上限。要能扛住、能放大仓位，不能天天心跳。</summary>
    public double BaseMaxDailyVolatility { get; init; } = 0.015;

    /// <summary>主动仓：低于MA20的幅度必须落在回测验证过的档位内。
    /// 下限3%——0~3%那档平均0.29%/胜率51.3%，接近随机；
    /// 上限25%——更深的历史样本不足10个，属于外推（见 <see cref="DepthNote"/>）。</summary>
    public double ActiveMinBelowMa20 { get; init; } = 0.03;
    public double ActiveMaxBelowMa20 { get; init; } = 0.25;
    /// <summary>主动仓：日均波幅上限4.5%。这是唯一有回测支持的波动率护栏——按波幅分档，
    /// 0~1.5%档0.94%/54.4%、1.5~2.5%档1.67%/58.3%、2.5~3.5%档1.67%/58.4%、
    /// 3.5~4.5%档2.10%/60.5%，**只有 >4.5% 那档垮掉：0.03%/50.2%，等于随机**。
    /// 更严的波幅上限（≤3.5%、≤2.5%）在回测里都没有增益，所以只挡这一档、不多设。</summary>
    public double ActiveMaxDailyVolatility { get; init; } = 0.045;

    public const string CategoryBase = "底仓";
    public const string CategoryActive = "主动仓";

    /// <summary>一手（100股）的金额上限（元）。默认 = 总资产100万×15%；调用方按用户实际总资产
    /// 和单票上限覆盖。这条是专门为高价股设的：茅台一手约13.5万，在55万本金下占到24.5%，
    /// 买第一笔就已经重仓、补一次仓就变半仓，到需要减仓时只剩"全清"一个选项。
    /// 仓位的选择余地必须在建仓时留出来。</summary>
    public double MaxLotAmount { get; init; } = 150_000;

    // ── 两个止盈目标：都只用于算出参考价写进结果，不参与筛选 ──
    //
    // 上面那份回测是按 +10% 止盈 / -10% 止损校准的，**用户实盘的实际目标却是 +2%**
    // （2026-08-03 券商流水：7笔已清仓里6笔涨幅在 -1.37%~+2.24%，平均 +1.34%，只有宁德时代
    // 那笔 6 天 +12.84% 是例外）。按 +2%/-5%/最长60日 重跑同一批 186226 个样本：
    // 平均 **-0.39%**、胜率 65.8%、平均持有 3.2 天——**胜率高但期望为负**
    // （0.66×2% − 0.34×5% = −0.38%），且不追高/基本面/深跌/大盘状态所有条件组合在这个口径下
    // 全部为负。也就是说筛选条件的取舍只在 +10% 口径下成立，换到 +2% 口径不成立。
    //
    // 用户 2026-08-03 决定：**保留 +10% 校准，同时把两个目标价都列出来自己选**。所以这里
    // 两个都算，并在结果里标明各自的依据，不替用户做决定。
    /// <summary>快目标（用户当前打法的实际止盈幅度）。注意这个口径本身期望为负，见上方注释。</summary>
    public const double DefaultQuickTargetPct = 0.02;
    /// <summary>大目标（回测校准用的止盈幅度）。</summary>
    public const double DefaultTargetPct = 0.10;
    public const double DefaultStopPct = 0.10;

    public double QuickTargetPct { get; init; } = DefaultQuickTargetPct;
    public double TargetPct { get; init; } = DefaultTargetPct;
    public double StopPct { get; init; } = DefaultStopPct;

    private readonly IBarRepository _barRepository;
    private readonly IReadOnlyDictionary<string, FinancialSnapshot> _financials;
    private readonly IReadOnlyDictionary<string, double> _dividendPerShare;

    public PullbackAnalysisEngine(
        IBarRepository barRepository,
        IReadOnlyDictionary<string, FinancialSnapshot> financials,
        IReadOnlyDictionary<string, double> dividendPerShare)
    {
        _barRepository = barRepository;
        _financials = financials;
        _dividendPerShare = dividendPerShare;
    }

    /// <summary>把"低于MA20的幅度"落到回测验证过的档位上，并明确标出哪些是没有样本支撑的外推——
    /// 回测里 -8~-20% 是效果最好且样本充足的区间（-8~-15%: 3650个样本/胜率65.0%，
    /// -15~-20%: 319个/78.1%），-20~-25% 只有45个样本、更深只有10个，不能当结论用。</summary>
    /// <summary>档位短标签（结果表的一列）："档位名 胜率% / 样本数"。样本数必须一起显示——
    /// 超跌档胜率78.1%看着最诱人，但只有319个样本，可信度远不如深跌档的3650个。
    /// 入参同 <see cref="DepthNote"/>：负数表示低于MA20。</summary>
    private static string DepthBucketLabel(double belowMa20)
    {
        double d = belowMa20 * 100;
        if (d >= 0) return "在MA20上方";
        if (d > -3) return "浅跌 51% / 9718";
        if (d > -8) return "中跌 54% / 10603";
        if (d > -15) return "深跌 65% / 3650";
        if (d > -20) return "超跌 78% / 319";
        if (d > -25) return "⚠ 20~25% / 仅45";
        return "⚠ 超验证范围 / <10";
    }

    private static string DepthNote(double belowMa20)
    {
        double d = belowMa20 * 100;
        if (d > -3) return "（浅跌档：历史平均0.29%、胜率51.3%，接近随机）";
        if (d > -8) return "（中跌档：历史平均0.73%、胜率53.6%）";
        if (d > -15) return "（深跌档：历史平均2.98%、胜率65.0%，样本3650个）";
        if (d > -20) return "（超跌档：历史平均5.61%、胜率78.1%，样本319个）";
        if (d > -25) return "⚠（-20~-25%：仅45个历史样本，参考价值有限）";
        return "⚠（深于-25%：历史样本不足10个，已超出回测验证范围，属于外推——这种跌幅通常有个股利空）";
    }

    public StockScreenResult Analyze(string code)
    {
        var bars = _barRepository.Query(code, Granularity.Day);
        if (bars.Count < MinBarsRequired)
        {
            return new StockScreenResult
            {
                Code = code,
                Granularity = Granularity.Day,
                Error = $"历史数据不足（仅 {bars.Count} 条），至少需要 {MinBarsRequired} 条（要算一年内高点）",
            };
        }

        int i = bars.Count - 1;
        double close = bars[i].Close;

        double ma20 = 0;
        for (int t = i - MaWindow + 1; t <= i; t++) ma20 += bars[t].Close;
        ma20 /= MaWindow;

        double avgAmount = 0;
        for (int t = i - AmountWindow + 1; t <= i; t++) avgAmount += bars[t].Amount;
        avgAmount /= AmountWindow;

        double yearHigh = double.MinValue;
        for (int t = i - TradingDaysPerYear + 1; t <= i; t++) yearHigh = Math.Max(yearHigh, bars[t].High);

        // 日均波幅：近60个交易日 |当日涨跌| 的均值。分类要用——底仓要低波动（能扛能放大仓位），
        // 主动仓要挡掉 >4.5% 那档（-10%止损在那种波动下两三天就会被噪音打掉）。
        double volatility = 0;
        int volDays = Math.Min(VolatilityWindow, i);
        for (int t = i - volDays + 1; t <= i; t++)
            if (bars[t - 1].Close > 0) volatility += Math.Abs(bars[t].Close / bars[t - 1].Close - 1);
        volatility = volDays > 0 ? volatility / volDays : 0;

        double belowMa20 = ma20 > 0 ? close / ma20 - 1 : 0;      // 负数=低于MA20
        double pullback = yearHigh > 0 ? (yearHigh - close) / yearHigh : 0;
        double lotAmount = close * 100;

        var fin = _financials.GetValueOrDefault(code);
        double? netProfit = fin?.Get(FinancialKeys.NetProfitParent);
        double? ocf = fin?.Get(FinancialKeys.Ocf);
        double? ocfCoverage = netProfit is > 0 && ocf.HasValue ? ocf.Value / netProfit.Value : null;

        var dps = _dividendPerShare.GetValueOrDefault(code);
        double dividendYield = close > 0 ? dps / close : 0;

        var result = new StockScreenResult
        {
            Code = code,
            Granularity = Granularity.Day,
            DataDate = bars[i].PeriodStart,
            LastClose = close,
            DividendYield = dividendYield,
            DailyVolatility = volatility,
            DepthBucket = DepthBucketLabel(belowMa20),
            SortScore = -belowMa20 * 100,       // 跌得越深排越前
            Criteria = new List<CriterionResult>
            {
                new()
                {
                    Name = "净利润为正（最新一期财报）",
                    Satisfied = netProfit is > 0,
                    DataMissing = fin == null || netProfit == null,
                    Basis = fin == null || netProfit == null
                        ? "本地没有该股财务数据（需要在 Fetcher 里拉取财务报表）"
                        : $"{fin.ReportDate:yyyy-MM-dd} 归母净利润={netProfit.Value / 1e8:F2}亿元",
                },
                new()
                {
                    Name = "经营现金流为正（赚的是真钱）",
                    Satisfied = ocf is > 0,
                    DataMissing = fin == null || ocf == null,
                    Basis = fin == null || ocf == null
                        ? "本地没有该股财务数据（需要在 Fetcher 里拉取财务报表）"
                        : $"{fin.ReportDate:yyyy-MM-dd} 经营活动现金流净额={ocf.Value / 1e8:F2}亿元",
                },
                new()
                {
                    Name = $"20日均成交额≥{MinAvgAmount / 1e8:F1}亿（进得去出得来）",
                    Satisfied = avgAmount >= MinAvgAmount,
                    Basis = $"20日均成交额={avgAmount / 1e8:F2}亿元",
                },
                new()
                {
                    Name = $"一手金额≤{MaxLotAmount / 1e4:F1}万（留出仓位余地）",
                    Satisfied = lotAmount <= MaxLotAmount,
                    Basis = $"一手(100股)={lotAmount:N0}元" +
                            (lotAmount > MaxLotAmount ? "；买一手就超过单票上限，没有分批和减仓的余地" : ""),
                },
            },
        };
        bool commonOk = result.Criteria.AllSatisfiedIgnoringMissingData();

        // ── 底仓判定：能不能长期拿着吃分红。**故意不看"是否低于MA20"** ——
        //    高股息蓝筹常年在MA20上方，用回调信号去卡它们等于永远买不到。
        bool baseYield = dividendYield >= BaseMinDividendYield;
        bool baseCoverage = ocfCoverage is not null && ocfCoverage >= BaseMinOcfCoverage;
        bool baseVol = volatility <= BaseMaxDailyVolatility;
        bool isBase = commonOk && baseYield && baseCoverage && baseVol;

        // ── 主动仓判定：位置对不对、波动扛不扛得住止损。
        double depth = -belowMa20;                      // 正数=低于MA20多少
        bool actDepth = depth >= ActiveMinBelowMa20 && depth <= ActiveMaxBelowMa20;
        bool actPullback = pullback >= MinPullbackFromHigh;
        bool actVol = volatility <= ActiveMaxDailyVolatility;
        bool isActive = commonOk && actDepth && actPullback && actVol;

        result.Category = (isBase, isActive) switch
        {
            (true, true) => $"{CategoryBase}+{CategoryActive}",
            (true, false) => CategoryBase,
            (false, true) => CategoryActive,
            _ => null,
        };
        result.Passed = isBase || isActive;

        result.Criteria.Add(new CriterionResult
        {
            Name = $"【底仓判定】{(isBase ? "✓ 符合" : "✗ 不符合")}（长期持有吃分红，不看回调）",
            Satisfied = isBase,
            Basis =
                $"{(baseYield ? "✓" : "✗")} 股息率 {dividendYield * 100:F2}%（需≥{BaseMinDividendYield * 100:F0}%）\n" +
                $"    {(baseCoverage ? "✓" : "✗")} 经营现金流/净利润 " +
                $"{(ocfCoverage.HasValue ? ocfCoverage.Value.ToString("F2") : "无数据")}" +
                $"（需≥{BaseMinOcfCoverage:F1}，利润要变成真钱）\n" +
                $"    {(baseVol ? "✓" : "✗")} 日均波幅 {volatility * 100:F2}%" +
                $"（需≤{BaseMaxDailyVolatility * 100:F1}%，能扛住才敢放大仓位）",
        });
        result.Criteria.Add(new CriterionResult
        {
            Name = $"【主动仓判定】{(isActive ? "✓ 符合" : "✗ 不符合")}（到价就走）",
            Satisfied = isActive,
            Basis =
                $"{(actDepth ? "✓" : "✗")} 低于MA20 {depth * 100:F2}%" +
                $"（需在{ActiveMinBelowMa20 * 100:F0}%~{ActiveMaxBelowMa20 * 100:F0}%的回测验证档位内）" +
                $"　{DepthNote(belowMa20)}\n" +
                $"    {(actPullback ? "✓" : "✗")} 距一年高点 {pullback * 100:F1}%" +
                $"（需≥{MinPullbackFromHigh * 100:F0}%；一年内最高 {yearHigh:F2}）\n" +
                $"    {(actVol ? "✓" : "✗")} 日均波幅 {volatility * 100:F2}%" +
                $"（需≤{ActiveMaxDailyVolatility * 100:F1}%；>4.5%那档回测只有0.03%/胜率50.2%，等于随机）",
        });

        // 执行参考（不参与筛选）：两个止盈目标 + 止损价。两个目标各有依据，由用户按当时判断
        // 选一个，见上面 QuickTargetPct/TargetPct 的注释。底仓不用这组价——它靠时间和分红。
        result.Criteria.Add(new CriterionResult
        {
            Name = "【执行参考】两个止盈目标 / 止损价（主动仓用）",
            Satisfied = true,
            Basis =
                $"现价 {close:F2}\n" +
                $"    快目标 {close * (1 + QuickTargetPct):F2}（+{QuickTargetPct * 100:F0}%，你当前的打法）" +
                $"——⚠ 这个口径回测平均 -0.39%、胜率65.8%、平均持有3.2天：胜率高但期望为负\n" +
                $"    大目标 {close * (1 + TargetPct):F2}（+{TargetPct * 100:F0}%，本方法条件校准所用）" +
                $"——回测平均 +0.96%、胜率54.7%\n" +
                $"    止损价 {close * (1 - StopPct):F2}（-{StopPct * 100:F0}%）" +
                $"——不设止损会把亏损推迟成尾部风险，不是消除亏损\n" +
                $"    底仓不用这组价：它靠持有时间和分红，按月定投建仓、持满一年分红免税",
        });

        return result;
    }
}
