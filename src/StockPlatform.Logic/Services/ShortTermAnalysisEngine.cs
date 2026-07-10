using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// "短线法" — a daily-bar, close-of-day short-term (波段) entry screen: catch stocks that just
/// started a momentum move with volume, money coming in, a manageable float, and NOT already
/// chased up. Suited to A-share T+1 (you'll hold overnight regardless, so intraday precision buys
/// little) — it produces "明天值得关注的启动票" rather than an intraday/打板 signal.
///
/// 7 hard conditions (AND, but a condition whose data the Fetcher hasn't populated — 主力净流入 /
/// 流通市值 — is skipped rather than failing the whole stock, same DataMissing pattern as 耀哥法/
/// 彬哥法) + 1 filter (排除ST/北交). 近期涨停是加分项，不作硬性门槛：它只写进 SortScore，让有资金
/// 关注（近期涨停多）的票在结果里排前面，不影响是否入选。
///
/// The indicator choices (MA5/MA10、前20日最高价突破线、成交量对比5日均量、MACD) deliberately line up
/// with what GoldenCrossChartBuilder already draws, so 短线法 reuses GoldenCrossDetailWindow for its
/// "条件详情" chart — the rules are essentially a subset of what that chart shows, with the breakout
/// line using the same 前20日最高价 price口径, so图和条件文字对得上（这正是 doc 3.2 强调的那条原则）。
/// Key thresholds (放量倍数/涨幅上限/流通市值区间) are caller-supplied so the user can tune them.
/// </summary>
public class ShortTermAnalysisEngine
{
    private const int MinBarsRequired = 60;      // 也顺带排除上市不足60个交易日的次新
    private const int BreakoutLookback = 20;     // 突破"前20日最高价"——跟 GoldenCrossChartBuilder 的压力线同口径
    private const int VolMaPeriod = 5;
    private const int LimitUpWindowDays = 15;    // 近15日涨停次数（加分项）

    private readonly IBarRepository _barRepository;
    private readonly INetInflowRepository _netInflowRepository;
    private readonly IFundamentalMetricRepository _fundamentalRepository;

    public ShortTermAnalysisEngine(IBarRepository barRepository, INetInflowRepository netInflowRepository, IFundamentalMetricRepository fundamentalRepository)
    {
        _barRepository = barRepository;
        _netInflowRepository = netInflowRepository;
        _fundamentalRepository = fundamentalRepository;
    }

    /// <param name="volumeSurgeRatio">放量倍数：当日量 ≥ 前5日均量 × 此值（默认1.5）。</param>
    /// <param name="maxDayGainPct">不追高：当日涨幅上限%（默认7）。</param>
    /// <param name="minCapYi">流通市值下限（亿元，默认30）。</param>
    /// <param name="maxCapYi">流通市值上限（亿元，默认300）。</param>
    public StockScreenResult Analyze(string code, string name,
        double volumeSurgeRatio = 1.5, double maxDayGainPct = 7, double minCapYi = 30, double maxCapYi = 300)
    {
        var bars = _barRepository.Query(code, Granularity.Day);
        if (bars.Count < MinBarsRequired)
            return Error(code, $"日线历史数据不足（仅 {bars.Count} 条），至少需要 {MinBarsRequired} 条（含排除次新）");

        var closes = bars.Select(b => b.Close).ToList();
        var highs = bars.Select(b => b.High).ToList();
        var volumes = bars.Select(b => b.Volume).ToList();
        int i = bars.Count - 1;

        var ma5 = TechnicalIndicators.SMA(closes, 5);
        var ma10 = TechnicalIndicators.SMA(closes, 10);
        var volMa5 = TechnicalIndicators.SMA(volumes, VolMaPeriod);
        var (dif, dea) = TechnicalIndicators.MACD(closes);
        double Hist(int t) => double.IsNaN(dif[t]) || double.IsNaN(dea[t]) ? double.NaN : (dif[t] - dea[t]) * 2;

        var board = MarketClassifier.Classify(code);

        // 1. 均线多头启动：收盘 > MA5 > MA10，且 MA10 拐头向上
        bool rule1 = !double.IsNaN(ma5[i]) && !double.IsNaN(ma10[i]) && !double.IsNaN(ma10[i - 1])
                     && closes[i] > ma5[i] && ma5[i] > ma10[i] && ma10[i] > ma10[i - 1];

        // 2. 放量：当日量 ≥ 前5日均量 × ratio（用 i-1 的均量做基准，今天不计入自己的基准）
        double baseVol = volMa5[i - 1];
        bool rule2 = !double.IsNaN(baseVol) && baseVol > 0 && volumes[i] >= baseVol * volumeSurgeRatio;

        // 3. 突破：收盘创近20日（不含今日）新高（最高价口径）
        double prevHigh = double.MinValue;
        for (int t = Math.Max(0, i - BreakoutLookback); t < i; t++) prevHigh = Math.Max(prevHigh, highs[t]);
        bool rule3 = closes[i] > prevHigh;

        // 4. 动能确认：MACD柱转正且放大，或 DIF 在0轴上方金叉
        double h0 = Hist(i), h1 = Hist(i - 1);
        bool goldenAboveZero = dif[i] > dea[i] && dif[i - 1] <= dea[i - 1] && dif[i] > 0;
        bool histRisingPositive = !double.IsNaN(h0) && !double.IsNaN(h1) && h0 > 0 && h0 > h1;
        bool rule4 = histRisingPositive || goldenAboveZero;

        // 5. 主力资金流入：最新一个交易日主力净流入 > 0（数据缺失则跳过此条，不判整只股票失败）
        var inflowRows = _netInflowRepository.Query(code, bars[i].PeriodStart.AddDays(-10));
        bool hasInflow = inflowRows.Count >= 1;
        double lastInflow = hasInflow ? inflowRows[^1].MainNetInflow : double.NaN;
        bool rule5 = hasInflow && lastInflow > 0;

        // 6. 盘子适中：流通市值在[minCap, maxCap]（数据缺失则跳过此条）
        var capRows = _fundamentalRepository.Query(code, MetricKeys.CirculatingMarketCap);
        bool hasCap = capRows.Count > 0;
        double cap = hasCap ? capRows[^1].Value : double.NaN;
        double minCap = minCapYi * 1e8, maxCap = maxCapYi * 1e8;
        bool rule6 = hasCap && cap >= minCap && cap <= maxCap;

        // 7. 不追高：当日涨幅 ≤ 上限
        double dayGain = closes[i - 1] != 0 ? (closes[i] - closes[i - 1]) / closes[i - 1] * 100 : 0;
        bool rule7 = dayGain <= maxDayGainPct;

        // 8. 过滤：排除 ST/*ST、北交所
        bool rule8 = !name.Contains("ST") && board != MarketBoard.Beijing;

        // 加分项（不参与入选判定）：近15日涨停次数——越多说明越有资金关注，用于结果排序。
        int limitUpCount = 0;
        for (int t = Math.Max(1, i - LimitUpWindowDays + 1); t <= i; t++)
        {
            double pct = closes[t - 1] != 0 ? (closes[t] - closes[t - 1]) / closes[t - 1] * 100 : 0;
            if (LimitUpClassifier.IsLimitUp(code, name, pct)) limitUpCount++;
        }

        var result = new StockScreenResult
        {
            Code = code,
            Name = name,
            Granularity = Granularity.Day,
            DataDate = bars[i].PeriodStart,
            LastClose = closes[i],
            SortScore = limitUpCount, // 近15日涨停次数：加分排序用，不影响 Passed
            Criteria = new List<CriterionResult>
            {
                new() { Name = "均线多头启动（收盘>MA5>MA10 且 MA10 拐头向上）", Satisfied = rule1,
                    Basis = $"收盘={closes[i]:F2}；MA5={ma5[i]:F2}；MA10={ma10[i]:F2}；MA10昨日={ma10[i - 1]:F2}" },
                new() { Name = $"放量（当日量≥前5日均量×{volumeSurgeRatio:F1}）", Satisfied = rule2,
                    Basis = $"当日量={volumes[i]:F0}；前5日均量={(double.IsNaN(baseVol) ? "—" : baseVol.ToString("F0"))}；" +
                            $"倍数={(double.IsNaN(baseVol) || baseVol == 0 ? "—" : (volumes[i] / baseVol).ToString("F2"))}" },
                new() { Name = $"突破（收盘创近{BreakoutLookback}日新高）", Satisfied = rule3,
                    Basis = $"收盘={closes[i]:F2}；前{BreakoutLookback}日最高价={prevHigh:F2}" },
                new() { Name = "MACD动能确认（柱转正放大 或 0轴上方金叉）", Satisfied = rule4,
                    Basis = $"DIF={dif[i]:F3}；DEA={dea[i]:F3}；MACD柱={(double.IsNaN(h0) ? "—" : h0.ToString("F3"))}（昨柱={(double.IsNaN(h1) ? "—" : h1.ToString("F3"))}）" },
                new() { Name = "主力资金净流入（最新交易日>0）", Satisfied = rule5, DataMissing = !hasInflow,
                    Basis = hasInflow
                        ? $"最新交易日({inflowRows[^1].PeriodStart:yyyy-MM-dd})主力净流入={lastInflow / 1e4:F0}万"
                        : "缺少主力净流入数据（请先在Fetcher里重新执行一次拉取）" },
                new() { Name = $"流通市值适中（{minCapYi:F0}亿~{maxCapYi:F0}亿）", Satisfied = rule6, DataMissing = !hasCap,
                    Basis = hasCap ? $"流通市值={cap / 1e8:F1}亿元" : "缺少流通市值数据（请先在Fetcher里重新执行一次拉取）" },
                new() { Name = $"不追高（当日涨幅≤{maxDayGainPct:F1}%）", Satisfied = rule7,
                    Basis = $"当日涨幅={dayGain:F2}%" },
                new() { Name = "排除ST/*ST、北交所", Satisfied = rule8,
                    Basis = $"简称={name}；板块={MarketClassifier.DisplayName(board)}" },
            },
        };
        result.Passed = result.Criteria.AllSatisfiedIgnoringMissingData();
        return result;
    }

    private static StockScreenResult Error(string code, string message) => new()
    {
        Code = code,
        Granularity = Granularity.Day,
        Error = message,
    };
}
