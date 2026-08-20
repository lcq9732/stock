using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// "短线法" —— **2026-08-07 规则整体替换**。旧版是"均线多头启动+放量+突破20日新高+主力净流入+
/// 流通市值区间+当日涨幅≤7%"的追涨型7条，本次按用户要求全部废弃，换成下面这套**回调埋伏型**。
///
/// 两版的方向是相反的：旧版找"已经启动、放量突破"的票；新版找"跌到位、动能刚要转、但市场还没
/// 放量追进去"的票。换的依据是一轮系统回测（下方），旧版那套"放量突破"在同一批样本里恰恰是被
/// 证伪的一侧——入场当日放量的平均收益 0.14%、缩量的 1.01%，差了七倍。
///
/// **8条硬条件（AND，全部必须满足）**：质量2条 + 位置2条 + 动能4条。
/// 日均波幅原本是第9条，2026-08-07 按用户要求降级为"仅提示"——它是回测里唯一站得住的护栏
/// （&gt;4.5%那档 0.03%/胜率50.2%），所以超限时仍在条件详情里显式警告，只是不再拦截。
///
/// **第7条按大盘状态分两个分支（2026-08-07）**：熊市(上证&lt;MA60)额外要求"收盘仍低于MA5"，
/// 牛市不要求。依据见 <see cref="_marketAboveMa60"/> 附近的注释——熊市反弹又快又短，
/// 等价格站上MA5第一段就没了；牛市那侧本来测出"确认型"更优(5.17%)但样本仅116条、
/// 且命中率0.41%等于工具沉默，与用户确认后不改（用户判断："牛市遍地好股，可能就不用这套"）。
///
/// **回测**（2022-06~2026-02，89个时点、28020个入场样本，财报按法定披露截止日滞后；
/// 口径 = +10%止盈 / -10%止损 / 最长持有120交易日）逐层叠加：
///
/// | 条件 | 平均收益 | 胜率 | 样本 |
/// |---|---|---|---|
/// | 基准（位置+波幅+流动性） | 0.59% | 52.9% | 28020 |
/// | + MACD柱连续2天收窄且为负 | 0.96% | 54.8% | 9322 |
/// | + 当日未放量（量比&lt;1.2） | 1.05% | 55.2% | 8345 |
/// | + MA5拐头向上 | 2.77% | 63.9% | 2915 |
/// | + KDJ金叉状态且非当日刚叉 | **3.35%** | **66.8%** | 2424 |
///
/// 注意最后一行是**回测时的口径**：2026-08-18 按用户要求，KDJ 这条已放宽成"只要 K&gt;D 就算过"，
/// 不再排除当日刚金叉（见下面第1点）。所以现行版本的实际表现会落在 3.35% 和"只要金叉"之间，
/// 上表那个 3.35%/66.8% 不再是现行口径的预期值。当日刚叉的样本仍会在条件说明里被标出来。
///
/// 大盘上行(&gt;MA60) 1.83%/59.1%、下行 4.28%/71.6%——**两种环境都为正**，这是它比之前几版稳的地方。
///
/// **两个反直觉的点（都验证过）**：
/// 1. **交叉那一天是最差的买点**。KDJ"当日刚金叉" -1.23%/胜率43.9%，而金叉后的延续状态 3.35%/66.8%；
///    MACD 同理，"只收窄一天" -0.46%/47.6%。指标交叉的瞬间噪音最大。
///    → MACD 那半条**仍在执行**（要求连续≥2天收窄）；KDJ 那半条 2026-08-18 按用户要求已取消，
///      当日刚金叉现在也算通过，只在条件说明里加⚠标注。改回去只需把 kdjOk 恢复成
///      <c>kdjAbove &amp;&amp; !kdjCrossedToday</c>。
/// 2. **K值不是越低越好**。K在40~60 是 5.37%/76.9%，K在0~40 只有 1.93%/59.8%——太低说明还在
///    超卖磨底，中位说明动能起来了但没超买。所以只要求 K&gt;D，不额外要求低位。
/// </summary>
public class ShortTermAnalysisEngine
{
    private const int TradingDaysPerYear = 243;
    private const int MinBarsRequired = TradingDaysPerYear;
    private const int MaWindow = 20;
    private const int AmountWindow = 20;
    private const int VolatilityWindow = 60;
    private const int VolBaseWindow = 5;      // 量比的基准窗口（不含当日）

    /// <summary>20日均成交额下限（元）——进得去出得来。</summary>
    public double MinAvgAmount { get; init; } = 100_000_000;
    /// <summary>距一年高点的最小回撤。</summary>
    public double MinPullbackFromHigh { get; init; } = 0.10;
    /// <summary>低于MA20的幅度区间：下限3%（0~3%那档0.29%/51.3%接近随机），
    /// 上限25%（更深的历史样本不足10个，属于外推）。</summary>
    public double MinBelowMa20 { get; init; } = 0.03;
    public double MaxBelowMa20 { get; init; } = 0.25;
    /// <summary>日均波幅上限4.5%——&gt;4.5%那档回测0.03%/胜率50.2%，等于随机（且放宽止损只会更差）。</summary>
    public double MaxDailyVolatility { get; init; } = 0.045;
    /// <summary>量比上限：当日量 / 前5日均量。缩量&lt;0.8 是 4.63%/73.2%，平量1.64%，放量&gt;1.2 是 -0.07%。
    /// 取1.2是"还没被市场发现"的分界，想更严可以调到0.8。</summary>
    public double MaxVolumeRatio { get; init; } = 1.2;
    /// <summary>MACD柱至少连续收窄几天。1天是 -0.46%/47.6%（全表最差），2天才转正。</summary>
    public int MinHistNarrowDays { get; init; } = 2;

    /// <summary>止盈/止损参考幅度——只写进结果供执行，不参与筛选。</summary>
    public double TargetPct { get; init; } = 0.10;
    public double StopPct { get; init; } = 0.10;

    /// <summary>8条全过。回测 4.62%/胜率73.2%（大盘&lt;MA60分支）。</summary>
    public const string CategoryStrict = "严格";
    /// <summary>其它7条全过、只差"入场时机"那一条（价格已站上MA5，埋伏窗口刚过）。
    /// 回测 3.29%/胜率66.6% —— 仍是正期望，所以留在结果里单独成组，不直接丢弃。</summary>
    public const string CategoryNearMiss = "次优·已站上MA5";

    private readonly IBarRepository _barRepository;
    private readonly IReadOnlyDictionary<string, FinancialSnapshot> _financials;
    /// <summary>近3个完整年度的归母净利（升序）——**只用于展示**，不参与筛选，理由见
    /// <see cref="ProfitTrendText"/>。</summary>
    private readonly IReadOnlyDictionary<string, List<(int Year, double NetProfitParent)>> _annualProfits;

    /// <summary>大盘（上证）是否在MA60上方。**决定用哪个分支的入场时机条件**，见 <see cref="Analyze"/>
    /// 里第7条。调用方在扫描前算一次传进来（全市场共用同一个大盘状态，不必每只票重算）。</summary>
    private readonly bool _marketAboveMa60;

    public ShortTermAnalysisEngine(IBarRepository barRepository,
        IReadOnlyDictionary<string, FinancialSnapshot> financials,
        IReadOnlyDictionary<string, List<(int Year, double NetProfitParent)>> annualProfits,
        bool marketAboveMa60)
    {
        _barRepository = barRepository;
        _financials = financials;
        _annualProfits = annualProfits;
        _marketAboveMa60 = marketAboveMa60;
    }

    /// <summary>把近几年年报净利拼成一行展示文本，连亏/累计为负时带 ⚠。
    /// **这是提示不是过滤**：回测里把"最近年度&gt;0"或"三年累计&gt;0"做成硬条件，收益反而从 2.55%
    /// 掉到 2.40%/2.47%——被剔掉的那批（困境反转3.15%、昔日辉煌5.63%、持续亏损3.33%）表现全都
    /// 高于基准，因为超跌反弹的弹性恰恰来自基本面最难看的票。但回测样本已排除ST/退市股，
    /// 测不出"踩雷退市"这类尾部风险，所以信息要摆出来让人自己判断。</summary>
    private static (string Text, double? Cum) ProfitTrendText(
        IReadOnlyDictionary<string, List<(int Year, double NetProfitParent)>> annuals, string code)
    {
        if (!annuals.TryGetValue(code, out var list) || list.Count == 0) return ("无年报数据", null);
        double cum = list.Sum(x => x.NetProfitParent);
        var parts = list.Select(x => $"{x.Year % 100}年{x.NetProfitParent / 1e8:+0.00;-0.00}");
        string warn = list[^1].NetProfitParent <= 0 || cum <= 0 ? " ⚠" : "";
        return ($"{string.Join(" ", parts)}｜累计{cum / 1e8:+0.00;-0.00}亿{warn}", cum);
    }

    public StockScreenResult Analyze(string code, string name)
    {
        var bars = _barRepository.Query(code, Granularity.Day);
        if (bars.Count < MinBarsRequired)
            return new StockScreenResult
            {
                Code = code,
                Name = name,
                Granularity = Granularity.Day,
                Error = $"历史数据不足（仅 {bars.Count} 条），至少需要 {MinBarsRequired} 条（要算一年内高点）",
            };

        var closes = bars.Select(b => b.Close).ToList();
        var highs = bars.Select(b => b.High).ToList();
        var lows = bars.Select(b => b.Low).ToList();
        int i = bars.Count - 1;
        double close = closes[i];

        // ── 位置 ──
        double ma20 = 0;
        for (int t = i - MaWindow + 1; t <= i; t++) ma20 += closes[t];
        ma20 /= MaWindow;
        double belowMa20 = ma20 > 0 ? (ma20 - close) / ma20 : 0;   // 正数=低于MA20多少

        double yearHigh = double.MinValue;
        for (int t = i - TradingDaysPerYear + 1; t <= i; t++) yearHigh = Math.Max(yearHigh, highs[t]);
        double pullback = yearHigh > 0 ? (yearHigh - close) / yearHigh : 0;

        double avgAmount = 0;
        for (int t = i - AmountWindow + 1; t <= i; t++) avgAmount += bars[t].Amount;
        avgAmount /= AmountWindow;

        double volatility = 0;
        for (int t = i - VolatilityWindow + 1; t <= i; t++)
            if (closes[t - 1] > 0) volatility += Math.Abs(closes[t] / closes[t - 1] - 1);
        volatility /= VolatilityWindow;

        // ── 动能 ──
        var (dif, dea) = TechnicalIndicators.MACD(closes);
        double Hist(int t) => (dif[t] - dea[t]) * 2;
        double h0 = Hist(i);
        // "连续收窄且在向0轴靠"：柱仍为负（在0轴下方）、且逐日抬升（绝对值变小=向0轴靠）
        int narrowDays = 0;
        for (int t = i; t > i - 10 && t > 0; t--)
        {
            if (Hist(t) < 0 && Hist(t) > Hist(t - 1)) narrowDays++;
            else break;
        }
        bool macdNarrowing = h0 < 0 && narrowDays >= MinHistNarrowDays;

        var ma5 = TechnicalIndicators.SMA(closes, 5);
        bool ma5TurningUp = !double.IsNaN(ma5[i]) && !double.IsNaN(ma5[i - 1]) && ma5[i] > ma5[i - 1];
        bool belowMa5 = close < ma5[i];
        // 大盘在MA60下方时额外要求"收盘仍低于MA5"——熊市反弹又快又短，站上MA5时第一段
        // 已经被吃掉了。回测(2022-06~2026-02，59679个样本)：大盘<MA60 时
        //   现行(不要求价格位置) 3.29%/胜率66.6%/样本2395
        //   加"仍低于MA5"        4.62%/胜率73.2%/样本1338   ← 采用
        // 大盘>MA60 时不加这条：牛市里"确认型"(MACD已转正+站上MA5)理论上更好(5.17%/75.9%)，
        // 但样本只有116条、且在该环境下命中率仅0.41%（等于牛市里工具沉默），
        // 2026-08-07 与用户确认：只改熊市分支，牛市保持现行统一版。
        bool entryTimingOk = _marketAboveMa60 ? ma5TurningUp : (ma5TurningUp && belowMa5);

        var (k, d, _) = TechnicalIndicators.KDJ(closes, highs, lows);
        bool kdjAbove = k[i] > d[i];
        bool kdjCrossedToday = kdjAbove && k[i - 1] <= d[i - 1];
        // 2026-08-18 按用户要求放宽：**只要 K>D 就算过**，不再排除"当日刚金叉"。
        // 回测上这一放宽是有代价的（当日刚叉那档 -1.23%/胜率43.9%，金叉延续档 3.35%/66.8%，
        // 见类注释的表），所以当日刚叉**仍然照常标注出来**（见下面这条的 Basis），只是不再否决。
        bool kdjOk = kdjAbove;

        double volBase = 0;
        for (int t = i - VolBaseWindow; t < i; t++) volBase += bars[t].Volume;
        volBase /= VolBaseWindow;
        double volRatio = volBase > 0 ? bars[i].Volume / volBase : 1;

        // ── 质量底线（不做优选：回测显示ROE/增长等重基本面筛选是负贡献，见 PullbackAnalysisEngine）──
        var fin = _financials.GetValueOrDefault(code);
        double? netProfit = fin?.Get(FinancialKeys.NetProfitParent);
        double? ocf = fin?.Get(FinancialKeys.Ocf);

        var trend = ProfitTrendText(_annualProfits, code);

        // 入场时机这一条要单独拿出来引用（判定"是否只差这一条"用），所以先建好再放进列表。
        var timingCriterion = new CriterionResult
        {
            Name = _marketAboveMa60
                ? "MA5 拐头向上【大盘>MA60分支】"
                : "MA5 拐头向上 且 收盘仍低于MA5【大盘<MA60分支：提前埋伏】",
            Satisfied = entryTimingOk,
            Basis = $"收盘={close:F2}；MA5={ma5[i]:F2}（昨={ma5[i - 1]:F2}，{(ma5TurningUp ? "上行" : "下行")}）" +
                    (_marketAboveMa60
                        ? "；大盘在MA60上方，不额外要求价格位置（该环境样本不足以支持更严的条件）"
                        : belowMa5
                            ? "；收盘仍在MA5下方 ✓ —— 熊市反弹又快又短，站上MA5时第一段已被吃掉。" +
                              "回测：加这条 4.62%/胜率73.2%，不加 3.29%/66.6%"
                            : $"；收盘已站上MA5 {(close / ma5[i] - 1) * 100:F2}%，埋伏窗口已过 → 归入次优组" +
                              "（该组历史 3.29%/胜率66.6%，仍是正期望，只是不如严格组）"),
        };

        var result = new StockScreenResult
        {
            Code = code,
            Name = name,
            Granularity = Granularity.Day,
            DataDate = bars[i].PeriodStart,
            LastClose = close,
            DailyVolatility = volatility,
            DepthBucket = DepthBucketLabel(belowMa20),
            KdjState = KdjStateLabel(k[i], kdjAbove, kdjCrossedToday),
            ProfitTrend = trend.Text,
            ThreeYearCumProfit = trend.Cum,
            SortScore = belowMa20 * 100,     // 跌得越深排越前
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
                    Name = $"20日均成交额≥{MinAvgAmount / 1e8:F1}亿",
                    Satisfied = avgAmount >= MinAvgAmount,
                    Basis = $"20日均成交额={avgAmount / 1e8:F2}亿元",
                },
                new()
                {
                    Name = $"位置：低于MA20 {MinBelowMa20 * 100:F0}%~{MaxBelowMa20 * 100:F0}%",
                    Satisfied = belowMa20 >= MinBelowMa20 && belowMa20 <= MaxBelowMa20,
                    Basis = $"收盘={close:F2}；MA20={ma20:F2}；低于MA20 {belowMa20 * 100:F2}%　{DepthNote(belowMa20)}",
                },
                new()
                {
                    Name = $"已回调：距一年高点≥{MinPullbackFromHigh * 100:F0}%",
                    Satisfied = pullback >= MinPullbackFromHigh,
                    Basis = $"一年内最高={yearHigh:F2}；已回撤={pullback * 100:F1}%",
                },
                // 【日均波幅】2026-08-07 按用户要求从硬条件降级为只显示。
                // 保留提示的原因：它是回测里唯一站得住的护栏——按波幅分档，0~1.5%档0.94%/54.4%、
                // 1.5~2.5%档1.67%/58.3%、2.5~3.5%档1.67%/58.4%、3.5~4.5%档2.10%/60.5%，
                // 唯独 >4.5% 那档垮成 0.03%/胜率50.2%（等于随机），且放宽止损只会更差
                // （+15/-15→0.25%、+20/-20→-0.36%）。所以超限时仍然显式标注，由用户自己取舍。
                // 【盈利趋势】只提示不过滤——理由见 ProfitTrendText 的注释。
                new()
                {
                    Name = "【仅提示】近年归母净利趋势",
                    Satisfied = true,
                    Basis = trend.Text +
                            (trend.Cum is <= 0
                                ? "\n    ⚠ 三年累计为负——单期微利可能是一次性损益或季节性，多年累计才看得出真实盈利能力。" +
                                  "\n    注意：回测里把这条做成硬条件反而降低收益（被剔掉的那批平均3.15%~5.63%，高于2.55%基准），" +
                                  "\n    因为超跌反弹的弹性正来自基本面难看的票。但回测样本已排除ST/退市股，测不出踩雷风险，" +
                                  "\n    所以这条留给你自己判断：要不要碰连续亏损的公司。"
                                : ""),
                },
                new()
                {
                    Name = "【仅提示】日均波幅(60日)",
                    Satisfied = true,
                    Basis = $"日均波幅={volatility * 100:F2}%" +
                            (volatility > MaxDailyVolatility
                                ? $"　⚠ 超过{MaxDailyVolatility * 100:F1}%：这一档回测只有0.03%/胜率50.2%，等于随机；" +
                                  $"且-10%止损在这种波动下两三天就会被噪音打掉，建议仓位减半"
                                : ""),
                },
                new()
                {
                    Name = $"MACD柱连续≥{MinHistNarrowDays}天收窄、仍在0轴下方（向0轴靠）",
                    Satisfied = macdNarrowing,
                    Basis = $"DIF={dif[i]:F3}；DEA={dea[i]:F3}；MACD柱={h0:F3}" +
                            $"（昨={Hist(i - 1):F3}，前={Hist(i - 2):F3}）；已连续收窄 {narrowDays} 天" +
                            (h0 >= 0 ? "；柱已转正，错过埋伏窗口" : "") +
                            (narrowDays == 1 ? "；只收窄一天是全表最差状态(-0.46%/胜率47.6%)" : ""),
                },
                timingCriterion,
                new()
                {
                    Name = "KDJ 处于金叉状态（K>D）",
                    Satisfied = kdjOk,
                    // 分档数字来自 2026-08-19 重跑的分档回测，口径见 KdjStateLabel 的注释；结果表的
                    // "KDJ状态"列用的是同一批数字，改文案时两处一起改。
                    Basis = $"K={k[i]:F1}；D={d[i]:F1}；{KdjStateLabel(k[i], kdjAbove, kdjCrossedToday)}" +
                            (!kdjAbove
                                ? "；K<D 未金叉，本条不通过——该状态回测 0.36%/胜率51.8%（16001样本），五档里最弱"
                                : kdjCrossedToday
                                    ? "；⚠ 今日刚金叉：2026-08-18 起这一档不再否决入选（K>D 即算通过），但它是四档里最差的一档" +
                                      "——0.63%/胜率53.0%（19221样本），只比未金叉好一点。多等一天等它变成“金叉延续”，" +
                                      "期望值就跳到 1.3~2.5%。要不要等自己定，要进建议仓位减半。" +
                                      "（本类早期注释里的 -1.23%/43.9% 出自2424样本的窄回测，全样本复现不出负期望）"
                                    : k[i] >= 40 && k[i] <= 60
                                        ? "；金叉延续 + K在40~60，四档里最稳的一档：2.53%/胜率62.6%（48120样本）"
                                        : k[i] < 40
                                            ? "；金叉延续但K仍在超卖区(<40)：1.25%/胜率56.1%（93724样本）——正期望，" +
                                              "但明显不如K回到40~60那档，说明反弹才刚起步、还没确认"
                                            : "；金叉延续且K>60：4.59%/胜率72.9%，但只有5046个样本（同时满足“低于MA20 3~25%”" +
                                              "和“K>60”本身就很少见），样本量不足以当成可靠优势，别为了凑这一档去放宽别的条件"),
                },
                new()
                {
                    Name = $"当日未放量（量比≤{MaxVolumeRatio:F1}）",
                    Satisfied = volRatio <= MaxVolumeRatio,
                    Basis = $"当日量比={volRatio:F2}（当日量÷前{VolBaseWindow}日均量）" +
                            (volRatio <= 0.8 ? "；缩量档 4.63%/胜率73.2%，最优"
                             : volRatio <= 1.2 ? "；平量档 1.64%/胜率58.3%"
                             : "；已放量，市场发现了，回测 -0.07%/胜率49.4%"),
                },
            },
        };
        // ── 严格 / 次优 两组 ──
        // 熊市分支的"收盘仍低于MA5"是个窄窗口（要求今天比5天前高、但仍低于近5日均价），
        // 遇到普涨日可能全市场一只都不满足。所以把"其它条件全过、只差这一条"的票也留下来，
        // 标为次优单独成组——它本身也是正期望（3.29%/胜率66.6%），只是不如严格组（4.62%/73.2%），
        // 由用户按当天有没有严格信号来决定要不要看。
        bool strictPass = result.Criteria.AllSatisfiedIgnoringMissingData();
        bool onlyTimingFails = !strictPass && !entryTimingOk &&
            result.Criteria.Where(c => !c.DataMissing && !ReferenceEquals(c, timingCriterion))
                           .All(c => c.Satisfied);

        result.Passed = strictPass || onlyTimingFails;
        result.Category = strictPass ? CategoryStrict : onlyTimingFails ? CategoryNearMiss : null;

        result.Criteria.Add(new CriterionResult
        {
            Name = "【执行参考】目标价 / 止损价",
            Satisfied = true,
            Basis = $"现价 {close:F2}；目标价 {close * (1 + TargetPct):F2}（+{TargetPct * 100:F0}%）；" +
                    $"止损价 {close * (1 - StopPct):F2}（-{StopPct * 100:F0}%）。" +
                    $"回测口径就是这组参数，换成 +2% 止盈会变成负期望（见回调法的说明）。",
        });
        return result;
    }

    /// <summary>结果表"KDJ状态"列的短标签："状态 + K值 + 该档历史期望/胜率·样本数"。
    ///
    /// 为什么要单独一列：2026-08-18 起"当日刚金叉"不再否决入选（第8条放宽成 K&gt;D 即可），
    /// 于是"严格组"里混进了两种质量差很远的信号。不把子状态摆出来，表面上都是"9条全过"。
    ///
    /// 分档数字口径（2026-08-19 重跑）：在**其余8条都满足**的样本上按KDJ子状态分组，
    /// 后复权日线、2016年至今、剔除688/8x/4x/920xxx，出场规则 +10%止盈/-10%止损/最长120交易日
    /// （跟本类其它注释里的回测口径一致，可以横向比）：
    ///   未金叉(K≤D)         16,001样本   0.36% / 胜率51.8%   ← 最弱，仍然否决
    ///   今日刚金叉           19,221样本   0.63% / 胜率53.0%   ← 放宽后能进来的那一档，四档里最差
    ///   延续·K在40~60       48,120样本   2.53% / 胜率62.6%   ← 最稳
    ///   延续·K&lt;40(超卖)     93,724样本   1.25% / 胜率56.1%
    ///   延续·K&gt;60            5,046样本   4.59% / 胜率72.9%   ← 数字最好但样本太少，不当依据
    ///
    /// ⚠ 修正：本类原注释里写的"当日刚叉 -1.23%/胜率43.9%"来自更早那次窄样本回测（2022-06~2026-02、
    /// 89个时点、2424样本），在 2016年至今的全样本上**复现不出负期望**——刚叉仍是四档里最差，
    /// 但期望是正的(+0.63%)。所以放宽第8条并没有放进一批负期望的信号，只是放进了最弱的一档。
    /// 样本数一起显示是刻意的，理由同 DepthBucketLabel。
    /// </summary>
    private static string KdjStateLabel(double kVal, bool above, bool crossedToday)
    {
        if (!above) return $"未金叉 K{kVal:F0} 0.4%/52%·1.6万";
        if (crossedToday) return $"⚠今日刚叉 K{kVal:F0} 0.6%/53%·1.9万";
        if (kVal >= 40 && kVal <= 60) return $"延续·最优 K{kVal:F0} 2.5%/63%·4.8万";
        if (kVal < 40) return $"延续·超卖 K{kVal:F0} 1.3%/56%·9.4万";
        return $"延续·K高 K{kVal:F0} 4.6%/73%·5046";
    }

    /// <summary>档位短标签（结果表的一列）："档位名 胜率% / 样本数"。样本数必须一起显示——
    /// 超跌档胜率78.1%看着最诱人，但只有319个样本，可信度远不如深跌档的3650个。</summary>
    private static string DepthBucketLabel(double below)
    {
        double d = below * 100;
        if (d < 3) return "浅跌 51% / 9718";
        if (d < 8) return "中跌 54% / 10603";
        if (d < 15) return "深跌 65% / 3650";
        if (d < 20) return "超跌 78% / 319";
        if (d <= 25) return "⚠ 20~25% / 仅45";
        return "⚠ 超验证范围 / <10";
    }

    /// <summary>低于MA20的幅度落在哪个回测档位——同 PullbackAnalysisEngine.DepthNote，
    /// 这里的入参是正数（低于多少）。</summary>
    private static string DepthNote(double below)
    {
        double d = below * 100;
        if (d < 3) return "（浅跌档：0.29%/胜率51.3%，接近随机）";
        if (d < 8) return "（中跌档：0.73%/胜率53.6%）";
        if (d < 15) return "（深跌档：2.98%/胜率65.0%，样本3650）";
        if (d < 20) return "（超跌档：5.61%/胜率78.1%，样本319）";
        if (d <= 25) return "⚠（20~25%：仅45个历史样本，参考价值有限）";
        return "⚠（深于25%：历史样本不足10个，超出验证范围）";
    }
}
