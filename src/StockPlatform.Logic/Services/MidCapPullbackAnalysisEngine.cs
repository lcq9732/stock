using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// "彬哥法"（原名中盘起爆法，类名沿用 MidCapPullback——描述算法本身，不随人名而变）— 10 conditions
/// (doc/analysis-app-design.md section 3.2.4), all AND, fixed to
/// daily bars for most rules but also reads week/month bars (already derived by
/// FetchOrchestrator on every fetch, see doc/data-platform-design.md) for the MACD rules.
///
/// Rule 4 (流通市值) depends on FundamentalMetric data that, as of this writing, no fetcher in
/// this repo ever populates (see MetricKeys.CirculatingMarketCap's doc comment). Deliberately
/// NOT treated as an Error/skip like insufficient day/week/month history — every stock would
/// error out today with zero market-cap data anywhere, hiding whether rules 1/2/3/5-10 even work.
/// Instead a missing market cap just fails rule 4 (Satisfied=false, Basis explains why), same as
/// any other unmet condition — the other 9 stay fully visible/verifiable today, and once a future
/// data-fetching change starts writing that metric, rule 4 starts actually discriminating with no
/// changes needed here.
/// </summary>
public class MidCapPullbackAnalysisEngine
{
    private const int MinDayBarsRequired = 40;
    private const int MinWeekBarsRequired = 35;
    private const int MinMonthBarsRequired = 35;
    private const int LimitUpWindowDays = 15;
    private const int MinLimitUpCount = 1; // "大于1次" = 至少2次
    private const int MaPeriod = 15;
    private const double MinMarketCap = 80 * 1e8;  // 80亿元
    private const double MaxMarketCap = 300 * 1e8; // 300亿元

    private readonly IBarRepository _barRepository;
    private readonly IFundamentalMetricRepository _fundamentalRepository;

    public MidCapPullbackAnalysisEngine(IBarRepository barRepository, IFundamentalMetricRepository fundamentalRepository)
    {
        _barRepository = barRepository;
        _fundamentalRepository = fundamentalRepository;
    }

    public StockScreenResult Analyze(string code, string name)
    {
        var dayBars = _barRepository.Query(code, Granularity.Day);
        if (dayBars.Count < MinDayBarsRequired)
            return Error(code, $"日线历史数据不足（仅 {dayBars.Count} 条），至少需要 {MinDayBarsRequired} 条");

        var weekBars = _barRepository.Query(code, Granularity.Week);
        if (weekBars.Count < MinWeekBarsRequired)
            return Error(code, $"周线历史数据不足（仅 {weekBars.Count} 条），至少需要 {MinWeekBarsRequired} 条");

        var monthBars = _barRepository.Query(code, Granularity.Month);
        if (monthBars.Count < MinMonthBarsRequired)
            return Error(code, $"月线历史数据不足（仅 {monthBars.Count} 条），至少需要 {MinMonthBarsRequired} 条");

        // Query 按 as_of_date 升序排列，最后一条就是最新；没有任何数据获取程序写过这个 key（见
        // MetricKeys.CirculatingMarketCap），所以目前 hasMarketCap 恒为 false——rule4 恒不满足，
        // 但不阻止其余9条规则照常计算和展示。
        var marketCapRows = _fundamentalRepository.Query(code, MetricKeys.CirculatingMarketCap);
        bool hasMarketCap = marketCapRows.Count > 0;
        double marketCap = hasMarketCap ? marketCapRows[^1].Value : double.NaN;

        var board = MarketClassifier.Classify(code);
        var closes = dayBars.Select(b => b.Close).ToList();
        int i = dayBars.Count - 1;
        var ma15 = TechnicalIndicators.SMA(closes, MaPeriod);

        bool rule1 = board != MarketBoard.ShanghaiStar;
        bool rule2 = board != MarketBoard.Beijing;
        bool rule3 = !name.Contains("ST");
        bool rule4 = hasMarketCap && marketCap > MinMarketCap && marketCap < MaxMarketCap;

        int limitUpCount = 0;
        int windowStart = Math.Max(1, i - LimitUpWindowDays + 1);
        for (int t = windowStart; t <= i; t++)
        {
            double pct = (closes[t] - closes[t - 1]) / closes[t - 1] * 100;
            if (LimitUpClassifier.IsLimitUp(code, name, pct)) limitUpCount++;
        }
        bool rule5 = limitUpCount > MinLimitUpCount;

        var monthCloses = monthBars.Select(b => b.Close).ToList();
        var (monthDif, monthDea) = TechnicalIndicators.MACD(monthCloses);
        int mi = monthBars.Count - 1;
        double monthHist = double.IsNaN(monthDif[mi]) || double.IsNaN(monthDea[mi]) ? double.NaN : (monthDif[mi] - monthDea[mi]) * 2;
        bool rule6 = monthHist > 0;

        var weekCloses = weekBars.Select(b => b.Close).ToList();
        var (weekDif, weekDea) = TechnicalIndicators.MACD(weekCloses);
        int wi = weekBars.Count - 1;
        double weekHist = double.IsNaN(weekDif[wi]) || double.IsNaN(weekDea[wi]) ? double.NaN : (weekDif[wi] - weekDea[wi]) * 2;
        bool rule7 = weekHist > 0;

        bool rule8 = dayBars[i].Open < ma15[i];
        bool rule9 = dayBars[i].Close > ma15[i];
        bool rule10 = i >= 1 && !double.IsNaN(ma15[i - 1]) && dayBars[i - 1].Close < ma15[i - 1];

        var result = new StockScreenResult
        {
            Code = code,
            Name = name,
            Granularity = Granularity.Day,
            DataDate = dayBars[i].PeriodStart,
            LastClose = dayBars[i].Close,
            Criteria = new List<CriterionResult>
            {
                new() { Name = "上市板块不包含科创板", Satisfied = rule1, Basis = $"板块={MarketClassifier.DisplayName(board)}" },
                new() { Name = "股票市场类型不包含北交所", Satisfied = rule2, Basis = $"板块={MarketClassifier.DisplayName(board)}" },
                new() { Name = "股票简称不包含ST、*ST", Satisfied = rule3, Basis = $"简称={name}" },
                new() { Name = "最新流通市值大于80亿元且小于300亿元", Satisfied = rule4, DataMissing = !hasMarketCap, Basis = hasMarketCap ? $"流通市值={marketCap / 1e8:F1}亿元" : "缺少流通市值数据（数据获取程序尚未提供，请先在Fetcher里重新执行一次拉取）" },
                new() { Name = "最近15个交易日涨停次数大于1次", Satisfied = rule5, Basis = $"最近{LimitUpWindowDays}个交易日涨停次数={limitUpCount}（按收盘涨停统计）" },
                new() { Name = "月线MACD柱值大于0", Satisfied = rule6, Basis = $"月线MACD柱={monthHist:F3}" },
                new() { Name = "周线MACD柱值大于0", Satisfied = rule7, Basis = $"周线MACD柱={weekHist:F3}" },
                new() { Name = "当前交易日开盘价低于MA15", Satisfied = rule8, Basis = $"今日开盘={dayBars[i].Open:F2}；MA15={ma15[i]:F2}" },
                new() { Name = "当前交易日收盘价高于MA15", Satisfied = rule9, Basis = $"今日收盘={dayBars[i].Close:F2}；MA15={ma15[i]:F2}" },
                new() { Name = "前一交易日收盘价低于前一交易日MA15", Satisfied = rule10, Basis = i >= 1 ? $"昨日收盘={dayBars[i - 1].Close:F2}；昨日MA15={ma15[i - 1]:F2}" : "无前一交易日数据" },
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
