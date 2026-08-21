using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// "底仓法" —— 筛选**能长期拿着吃分红**的股票（2026-08-20 由"回调法"改造而来）。
///
/// ════ 这次改造改了什么 ════
/// 原"回调法"一个引擎里同时判两类候选：底仓（高股息+低波动）和主动仓（跌到回测验证档位内）。
/// 本次按用户要求只保留底仓这一路，主动仓选股全部交给 <see cref="ShortTermAnalysisEngine"/>
/// ——那套8条硬条件有 89个时点/28020个样本的回测（3.35%/胜率66.8%），比原回调法的主动仓判定强，
/// 去掉不会有功能缺口。同时删掉了"距一年高点回撤≥10%"和两个止盈目标：底仓本来就不看回调位置，
/// 也不用价格纪律（止盈止损常量已挪到 <see cref="TradeDiscipline"/>）。
///
/// ════ ⚠ 条件的证据等级：本方法**没有回测支持** ════
/// 这一点必须说在最前面，因为本项目其它方法（短线法/阶梯低点法等）的每个阈值背后都有一份回测表，
/// 而底仓法没有。原因是口径不同：那些回测是"+10%止盈/-10%止损/最长持有120日"，而底仓的持有期是
/// **数年**，差一个量级，那批结论对这里不成立。
///
/// 尤其注意：原回调法注释里那条"重基本面筛选（ROE/净利增长）是负贡献、所以只留净利+现金流"的
/// 结论，**在底仓法里不再沿用**——它是短线口径下测出来的。持有数年的票不能是连亏公司，所以本
/// 方法把"近3年净利"从原来的"仅提示"升级成了硬条件（条件③）。
///
/// 下面这些阈值是按"拿分红"的目的推出来的，不是拟合出来的。要真正验证需要另做一轮长周期回测：
/// 用 <see cref="Granularity.DayHfq"/>（后复权；前复权在十年尺度上会失真）算价差，加上 Dividend
/// 表的实收分红，按持有 1/3/5 年分别统计——这是独立的一个活，**还没做**。
///
/// ════ 硬条件（AND，全部满足才入选）════
///   ① 净利润为正（最新一期财报）
///   ② 经营现金流为正
///   ③ 近3年归母净利：累计为正 **且** 最新年度为正   ← 本次由"仅提示"升级为硬条件
///   ④ 20日均成交额 ≥ <see cref="MinAvgAmount"/>
///   ⑤ 股息率 ≥ <see cref="MinDividendYield"/>
///   ⑥ 经营现金流 / 净利润 ≥ <see cref="MinOcfCoverage"/>
///   ⑦ 日均波幅(60日) ≤ <see cref="MaxDailyVolatility"/>
///   ⑧ 连续分红年数 ≥ <see cref="MinConsecutiveDividendYears"/>   ← 新增，见该属性注释
///
/// **2026-08-20 去掉了原"一手金额 ≤ 单票上限"那条**（用户要求）——它防的是茅台那种高价股，
/// 但 4.5% 股息率的池子里现价基本都在个位数到几十元、一手几百到几千块，这条从来没真正
/// 生效过（实测：入选的15只里一手最贵的是青岛啤酒 5137 元，门槛却是 15 万），留着只是白占
/// 一个条件位和一个输入框。删掉前后入选名单完全一致，15 只一只不差。
///
/// <see cref="StockScreenResult.SortScore"/> = **股息率×100**（越高排越前）——底仓比的是现金流，
/// 不是位置。原来这个字段装的是"低于MA20的幅度"，对底仓没有意义。
/// </summary>
public class CorePositionAnalysisEngine
{
    /// <summary>一年按 243 个交易日算。底仓法虽然不再算"一年高点"，但仍要求至少一年数据——
    /// 连续分红5年的票必然上市够久，而次新股既没有分红历史，波动率也估不准。</summary>
    private const int TradingDaysPerYear = 243;
    private const int MinBarsRequired = TradingDaysPerYear;

    private const int AmountWindow = 20;
    /// <summary>日均波幅的统计窗口（交易日）。</summary>
    private const int VolatilityWindow = 60;

    /// <summary>派息趋势/平均股息率的回看年数。</summary>
    public const int DividendLookbackYears = 5;

    /// <summary>20日均成交额下限（元）。1亿是"进得去出得来"的经验线。底仓虽然不常动，但真要
    /// 减仓时得出得去，而且流动性差的票分红往往也不稳。</summary>
    public double MinAvgAmount { get; init; } = 100_000_000;

    /// <summary>股息率下限。**4.5%（2026-08-20 用户定）**——要跟国债/定存拉开足够差距，拿分红
    /// 才有意义；持满一年分红免税后实际收益更高（见 <see cref="DividendMetrics"/> 的税档说明）。
    /// 代价是池子明显变小、行业集中度高（主要是银行/电力/高速/煤炭），所以单票上限和行业分散要
    /// 跟着收紧，否则底仓会全压在一两个行业上。</summary>
    public double MinDividendYield { get; init; } = 0.045;

    /// <summary>经营现金流至少要覆盖净利润——利润得变成真钱，长期持有才踏实，也是"这个分红发得
    /// 出来"的前提。</summary>
    public double MinOcfCoverage { get; init; } = 1.0;

    /// <summary>日均波幅上限。底仓要低波动不是为了控制止损（底仓没有价格止损），而是因为要往下
    /// 分档加仓、仓位会放大，天天大幅波动的票拿不住。</summary>
    public double MaxDailyVolatility { get; init; } = 0.015;

    /// <summary>连续分红年数下限。**5年（2026-08-20 用户定）**——这是本方法最关键的新增条件：
    /// 底仓赌的就是"未来还会不会继续分红"，而只看近12个月的股息率完全分不出"连分十年的电力股"
    /// 和"去年头一回分红、今年就停了"的票，后者的高股息率纯属巧合。5年足以滤掉一次性大额分红，
    /// 又不会把2020年后上市的优质公司全挡掉。算法（含"今年还没到分红季不算中断"的处理）见
    /// <see cref="DividendMetrics.ConsecutiveYears"/>。</summary>
    public int MinConsecutiveDividendYears { get; init; } = 5;

    /// <summary>目标年化股息（元，税前）——**不参与筛选**，只用来把"想每年收多少分红"换算成
    /// "这只票要投多少钱"（目标 ÷ 股息率）。跟底仓页的同名设置是同一个数。0 表示没填，
    /// 此时不输出这项换算。</summary>
    public double TargetAnnualDividend { get; init; }

    private readonly IBarRepository _barRepository;
    private readonly IReadOnlyDictionary<string, FinancialSnapshot> _financials;
    private readonly IReadOnlyDictionary<string, double> _dividendPerShare;
    private readonly IReadOnlyDictionary<string, List<(int Year, double PerShare)>> _annualDividends;
    private readonly IReadOnlyDictionary<string, List<(int Year, double NetProfitParent)>> _annualProfits;

    public CorePositionAnalysisEngine(
        IBarRepository barRepository,
        IReadOnlyDictionary<string, FinancialSnapshot> financials,
        IReadOnlyDictionary<string, double> dividendPerShare,
        IReadOnlyDictionary<string, List<(int Year, double PerShare)>> annualDividends,
        IReadOnlyDictionary<string, List<(int Year, double NetProfitParent)>> annualProfits)
    {
        _barRepository = barRepository;
        _financials = financials;
        _dividendPerShare = dividendPerShare;
        _annualDividends = annualDividends;
        _annualProfits = annualProfits;
    }

    /// <summary>条件③判定用的年报净利年数。**注意跟图表口径分开**：入参
    /// <see cref="_annualProfits"/> 现在装的是**全部**历年年报（2026-08-20 改，为了让条件详情里的
    /// 柱状图能画出完整历史），但判定只看最近这几年——放宽判定窗口会改变筛选结果，那是另一回事。</summary>
    private const int ProfitJudgeYears = 3;

    /// <summary>近几年年报净利拼成一行，连亏或累计为负带 ⚠。同 ShortTermAnalysisEngine 的同名
    /// 方法，但这里额外返回 Cum/Latest——底仓法里它不再只是提示，要参与条件③的判定。
    /// 只取最近 <see cref="ProfitJudgeYears"/> 年，见该常量注释。</summary>
    private (string Text, double? Cum, double? Latest) ProfitTrendText(string code)
    {
        if (!_annualProfits.TryGetValue(code, out var all) || all.Count == 0)
            return ("无年报数据", null, null);
        var list = all.TakeLast(ProfitJudgeYears).ToList();
        double cum = list.Sum(x => x.NetProfitParent);
        var parts = list.Select(x => $"{x.Year % 100}年{x.NetProfitParent / 1e8:+0.00;-0.00}");
        string warn = list[^1].NetProfitParent <= 0 || cum <= 0 ? " ⚠" : "";
        return ($"{string.Join(" ", parts)}｜累计{cum / 1e8:+0.00;-0.00}亿{warn}", cum, list[^1].NetProfitParent);
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
                Error = $"历史数据不足（仅 {bars.Count} 条），至少需要 {MinBarsRequired} 条（上市满一年）",
            };
        }

        int i = bars.Count - 1;
        double close = bars[i].Close;

        double avgAmount = 0;
        for (int t = i - AmountWindow + 1; t <= i; t++) avgAmount += bars[t].Amount;
        avgAmount /= AmountWindow;

        double volatility = 0;
        int volDays = Math.Min(VolatilityWindow, i);
        for (int t = i - volDays + 1; t <= i; t++)
            if (bars[t - 1].Close > 0) volatility += Math.Abs(bars[t].Close / bars[t - 1].Close - 1);
        volatility = volDays > 0 ? volatility / volDays : 0;

        var fin = _financials.GetValueOrDefault(code);
        double? netProfit = fin?.Get(FinancialKeys.NetProfitParent);
        double? ocf = fin?.Get(FinancialKeys.Ocf);
        double? ocfCoverage = netProfit is > 0 && ocf.HasValue ? ocf.Value / netProfit.Value : null;

        var trend = ProfitTrendText(code);

        // ── 分红历史 ──
        var annual = _annualDividends.GetValueOrDefault(code) ?? new List<(int Year, double PerShare)>();
        int refYear = bars[i].PeriodStart.Year;
        int consecutive = DividendMetrics.ConsecutiveYears(annual, refYear);
        // Grid 那一列只放结论（递增/持平/波动/中断）；逐年明细走 AnnualDividends，
        // 由条件详情里的柱状图呈现——一行数字扫不出"哪年腰斩了"，柱子高低一眼就看出来。
        string dividendTrend = DividendMetrics.TrendShape(annual, DividendLookbackYears);

        // ── 股息率：**取"滚动12个月"和"最近一个年度"中的较小值** ──
        //
        // 2026-08-20 修正一个会让结果虚高的口径问题：滚动12个月窗口在分红季前后会把两个年度的
        // 年度分红同时框进来。实测华邦健康按滚动12个月算是 10.11%，而它近5年每年只派 0.20~0.25 元、
        // 现价 4.45，真实水平约 5.6%——多出来的是 2025 和 2026 两次派息被加在了一起。
        //
        // 取较小值而不是"哪个更准就用哪个"：底仓买错的代价是拿几年，宁可漏掉也不要虚高。
        // 副作用是当年分红还没实施完的票会被低估（此时年度值偏小），这个方向的错误可以接受。
        // 两个口径都写进条件说明里，差得多的时候用户看得见。
        double dpsTrailing = _dividendPerShare.GetValueOrDefault(code);
        var (latestYear, dpsLatestYear) = DividendMetrics.LatestYearDividend(annual);
        double dps = dpsLatestYear > 0 ? Math.Min(dpsTrailing, dpsLatestYear) : dpsTrailing;
        double dividendYield = close > 0 ? dps / close : 0;
        double trailingYield = close > 0 ? dpsTrailing / close : 0;
        // 近5年平均股息率：拿来跟当期股息率对照——当期明显高出一截时，多半是含了一次性大额分红，
        // 或者股价刚大跌（分母变小），两种情况都不该当成"这只票每年都能给这么多"。
        double avg5Dps = DividendMetrics.SumPerShare(annual, DividendLookbackYears, refYear + 1) / DividendLookbackYears;
        double avg5Yield = close > 0 ? avg5Dps / close : 0;

        var result = new StockScreenResult
        {
            Code = code,
            Granularity = Granularity.Day,
            DataDate = bars[i].PeriodStart,
            LastClose = close,
            DividendYield = dividendYield,
            AvgDividendYield = avg5Yield,
            DailyVolatility = volatility,
            ConsecutiveDividendYears = consecutive,
            DividendTrend = dividendTrend,
            // 图表要**全部**历史（2026-08-20 用户要求）：既然是画图，多几根柱子不占地方，
            // 而完整历史才看得出"这家公司分红是一贯的还是最近几年才开始的"。
            // 判定口径不受影响：趋势结论仍只看近5年、条件③仍只看近3年（见 ProfitJudgeYears）。
            AnnualDividends = annual.Count > 0 ? annual.OrderBy(x => x.Year).ToList() : null,
            AnnualProfits = _annualProfits.GetValueOrDefault(code)?
                .Select(x => (x.Year, NetProfit: x.NetProfitParent)).ToList(),
            ProfitTrend = trend.Text,
            ThreeYearCumProfit = trend.Cum,
            SortScore = dividendYield * 100,        // 股息率越高排越前
            Criteria = new List<CriterionResult>
            {
                new()
                {
                    Name = "① 净利润为正（最新一期财报）",
                    Satisfied = netProfit is > 0,
                    DataMissing = fin == null || netProfit == null,
                    Basis = fin == null || netProfit == null
                        ? "本地没有该股财务数据（需要在 Fetcher 里拉取财务报表）"
                        : $"{fin.ReportDate:yyyy-MM-dd} 归母净利润={netProfit.Value / 1e8:F2}亿元",
                },
                new()
                {
                    Name = "② 经营现金流为正（赚的是真钱）",
                    Satisfied = ocf is > 0,
                    DataMissing = fin == null || ocf == null,
                    Basis = fin == null || ocf == null
                        ? "本地没有该股财务数据（需要在 Fetcher 里拉取财务报表）"
                        : $"{fin.ReportDate:yyyy-MM-dd} 经营活动现金流净额={ocf.Value / 1e8:F2}亿元",
                },
                new()
                {
                    Name = "③ 近3年归母净利：累计为正且最新年度为正",
                    Satisfied = trend.Cum is > 0 && trend.Latest is > 0,
                    DataMissing = trend.Cum == null,
                    Basis = trend.Cum == null
                        ? "本地没有该股年报数据"
                        : trend.Text +
                          "\n    底仓要拿数年，不能是连亏公司。⚠ 这条在原回调法里只是提示——那份" +
                          "\"重基本面筛选是负贡献\"的回测是 +10%止盈/120日 的短线口径，对持有数年不成立。",
                },
                new()
                {
                    Name = $"④ 20日均成交额≥{MinAvgAmount / 1e8:F1}亿（真要减仓时出得去）",
                    Satisfied = avgAmount >= MinAvgAmount,
                    Basis = $"20日均成交额={avgAmount / 1e8:F2}亿元",
                },
                new()
                {
                    Name = $"⑤ 股息率≥{MinDividendYield * 100:F1}%（要跟国债/定存拉开差距）",
                    Satisfied = dividendYield >= MinDividendYield,
                    DataMissing = dps <= 0 && annual.Count == 0,
                    Basis = dps <= 0 && annual.Count == 0
                        ? "本地没有该股分红数据（需要在 Fetcher 里拉取分红送配）"
                        : $"取用 {dps:F3} 元/股 ÷ 现价 {close:F2} = {dividendYield * 100:F2}%\n" +
                          $"    ├ 滚动12个月派息 {dpsTrailing:F3} 元/股（{trailingYield * 100:F2}%）\n" +
                          $"    ├ {(latestYear > 0 ? $"{latestYear}年全年派息 {dpsLatestYear:F3} 元/股" : "无年度派息记录")}\n" +
                          $"    └ 近{DividendLookbackYears}年平均股息率 {avg5Yield * 100:F2}%\n" +
                          "    取前两者的**较小值**：滚动12个月窗口在分红季前后会把两个年度的分红" +
                          "框进来、算出近一倍的虚高股息率，底仓拿几年，宁可漏掉也不要虚高。" +
                          (dpsLatestYear > 0 && dpsTrailing > dpsLatestYear * 1.5
                              ? "\n    ⚠ 这只票正是那种情况：滚动12个月的数明显高于年度数，已按年度口径取值。"
                              : "") +
                          (dividendYield > avg5Yield * 1.5 && avg5Yield > 0
                              ? "\n    ⚠ 当期仍明显高于5年均值——可能含一次性大额分红，或者股价刚大跌（分母变小）。"
                              : ""),
                },
                new()
                {
                    Name = $"⑥ 经营现金流/净利润≥{MinOcfCoverage:F1}（分红发得出来）",
                    Satisfied = ocfCoverage is not null && ocfCoverage >= MinOcfCoverage,
                    DataMissing = ocfCoverage == null,
                    Basis = ocfCoverage == null
                        ? "缺财务数据或净利润非正，无法计算覆盖率"
                        : $"经营现金流/净利润 = {ocfCoverage.Value:F2}",
                },
                new()
                {
                    Name = $"⑦ 日均波幅(60日)≤{MaxDailyVolatility * 100:F1}%（能扛住才敢放大仓位）",
                    Satisfied = volatility <= MaxDailyVolatility,
                    Basis = $"日均波幅={volatility * 100:F2}%" +
                            "（底仓没有价格止损，低波动是为了往下分档加仓时拿得住）",
                },
                new()
                {
                    Name = $"⑧ 连续分红≥{MinConsecutiveDividendYears}年（未来还会继续分的证据）",
                    Satisfied = consecutive >= MinConsecutiveDividendYears,
                    DataMissing = annual.Count == 0,
                    Basis = annual.Count == 0
                        ? "本地没有该股分红数据（需要在 Fetcher 里拉取分红送配）"
                        : $"连续 {consecutive} 年有现金分红（按除权除息日所属年计）\n" +
                          $"    派息趋势：{dividendTrend}（逐年金额见上方柱状图）" +
                          (consecutive == 0
                              ? "\n    ⚠ 去年和今年都没有派息记录——分红已中断"
                              : ""),
                },
            },
        };

        result.Passed = result.Criteria.AllSatisfiedIgnoringMissingData();

        // ── 以下都是执行参考，不参与筛选 ──

        if (TargetAnnualDividend > 0 && dividendYield > 0)
        {
            double needCapital = TargetAnnualDividend / dividendYield;
            int needShares = (int)Math.Floor(needCapital / close / 100) * 100;
            result.RequiredCapitalForTarget = needCapital;
            result.Criteria.Add(new CriterionResult
            {
                Name = "【执行参考】达成目标年化股息需要投入多少",
                Satisfied = true,
                Basis =
                    $"目标年化股息 {TargetAnnualDividend:N0} 元 ÷ 股息率 {dividendYield * 100:F2}% = " +
                    $"需投入 {needCapital / 1e4:F1} 万元（约 {needShares:N0} 股）\n" +
                    "    这是\"全压这一只\"的口径——把目标除以计划配置的只数，再逐只按这个式子算。\n" +
                    "    ⚠ 税前口径。持股未超1年时派息虽然全额到账，但卖出时会由券商按持股期限倒扣" +
                    "（≤1个月20%、1个月~1年10%、超1年免征）。",
            });
        }

        result.Criteria.Add(new CriterionResult
        {
            Name = "【执行参考】分红免税时点与先进先出",
            Satisfied = true,
            Basis =
                $"若今日（{DateTime.Today:yyyy-MM-dd}）买入，最早可免税卖出日 = " +
                $"{DividendMetrics.TaxFreeSellDate(DateTime.Today):yyyy-MM-dd}\n" +
                "    持股期限算到卖出日**前一日**，且要严格超过1年才免征（1年整仍按10%）。\n" +
                "    ⚠ 先进先出：同一账户同一只票，卖出时按最早买入的那批认定。底仓和波段混在同一只" +
                "票上，卖波段会把底仓的免税时钟往后推——软件里打标记改变不了这个认定。\n" +
                "    底仓不设价格止盈止损（那套 ±10% 是主动仓口径），退出条件是分红中断或基本面变坏。",
        });

        return result;
    }
}
