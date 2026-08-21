using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// "彬哥法"（原名中盘起爆法，类名沿用 MidCapPullback——描述算法本身，不随人名而变）— 11 conditions
/// (doc/analysis-app-design.md section 3.2.4), all AND, fixed to
/// daily bars for most rules but also reads week/month bars (already derived by
/// FetchOrchestrator on every fetch, see doc/data-platform-design.md) for the MACD rules.
///
/// Rule 4 (流通市值) reads FundamentalMetric / MetricKeys.CirculatingMarketCap. **这条现在是真的在
/// 判了**——早期注释说"没有任何 fetcher 写过这个 key，所以 rule4 恒不满足"，那已经不成立：
/// FetchOrchestrator.FetchMarketCapAsync 从 2026-07-09 起每次"拉取全部"/"拉取当天"都写，2026-08-04
/// 实测 publish/data/local/current.sqlite 有 80,515 行 / 5,539 只股票。按最新 as_of_date 的值分档：
/// 1,328 只（24%）落在 (80亿, 300亿) 区间内 → rule4 满足；3,660 只 ≤80亿、551 只 ≥300亿 → 不满足。
/// 也就是说 rule4 已经是一条实际起筛选作用的硬条件，不再是"恒 false 的占位"——改这个阈值会真的
/// 改变彬哥法的选股结果。阈值本身经核对无误：库里存的是"元"（600036 的 value ÷ 同日收盘 = 恒定
/// 20,628,944,429 股），跟 MinMarketCap/MaxMarketCap 的 80*1e8 / 300*1e8 单位一致。
///
/// 缺数据时走的是 **DataMissing=true → 被 AllSatisfiedIgnoringMissingData 整条跳过**（跟 rule11/
/// rule12 同一套处理），不是早期注释说的"当成普通未满足条件判负"。两者对 Passed 的影响完全相反，
/// 别照着旧描述推断行为。仍然缺数据的是"新浪实时列表里查不到"的那批：实测 5,853 只纯6位A股代码里
/// 有 314 只没有任何市值行（退市/长期停牌为主）；ETF（sh5*/sz1*）和本地合成的板块指数（gn_*）也没有，
/// 但它们本来就不在本引擎的股票池里。这批股票的 rule4 被跳过，其余 11 条照常算——所以一只票可以
/// 在完全没有市值数据的情况下依然 Passed=true。
///
/// Rule 11 (股东户数环比下降，2026-07-17新增) reads 股东户数时间序列 (<see cref="IShareholderRepository"/>)。
/// 硬条件=最新报告期户数 &lt; 上一报告期（环比下降，筹码集中的方向）；"是否接近近2年最低"只写进依据文字
/// 供参考，不做硬门槛（用户口径"最好接近最低"）。跟市值不同——股东数据是**独立按钮"拉取股东数据"**才抓的
/// 慢变数据，很多时候本地还没有，所以缺数据时标 DataMissing=true（走 AllSatisfiedIgnoringMissingData
/// 被跳过、不拖垮其余条），而不是像 rule4 那样判负。抓过数据后自动生效。
///
/// Rule 12 (融资余额增长，2026-07-17新增) reads 融资余额时间序列 (<see cref="IMarginRepository"/>，
/// MarginDetail 表)。硬条件=最新交易日融资余额 &gt; 上一有数据交易日（资金加杠杆流入的方向）。同 rule11
/// 一样是需单独触发/回补的数据，缺数据时 DataMissing=true 跳过，同步到本地后自动生效。
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
    private readonly IShareholderRepository _shareholderRepository;
    private readonly IMarginRepository _marginRepository;

    public MidCapPullbackAnalysisEngine(IBarRepository barRepository, IFundamentalMetricRepository fundamentalRepository, IShareholderRepository shareholderRepository, IMarginRepository marginRepository)
    {
        _barRepository = barRepository;
        _fundamentalRepository = fundamentalRepository;
        _shareholderRepository = shareholderRepository;
        _marginRepository = marginRepository;
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

        // Query 按 as_of_date 升序排列（SQL 里就是 ORDER BY as_of_date），所以 [^1] 就是最新快照。
        // hasMarketCap 现在绝大多数股票都是 true（见类注释：5,539 只有数据），rule4 是一条真的在起
        // 作用的硬条件；只有新浪实时列表查不到的退市/长期停牌股才会 false，那时按 DataMissing 整条
        // 跳过（不判负），其余 11 条照常计算和展示。
        // as_of_date 是"这个值属于哪个交易日"（2026-08-04 起，见 FetchOrchestrator.ResolveMarketCapAsOfDateAsync；
        // 更早写入的行是"抓取那天"的旧语义）。这里只用来取最新值、不跟 dayBars[i].PeriodStart 对齐——
        // 市值只在每次抓取时刷新，落后交易日一两天是常态，判 80亿/300亿 区间不受这点价格波动影响。
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

        // ── rule11 股东户数环比下降（硬）+ 距近2年最低（参考）──
        var holderSeries = _shareholderRepository.GetCountSeries(code); // 按报告期升序
        bool hasHolder = holderSeries.Count >= 2;
        bool rule11 = false; string rule11Basis;
        if (!hasHolder)
            rule11Basis = holderSeries.Count == 0
                ? "缺少股东户数数据（请先在 Fetcher 里点\"拉取股东数据\"，再把库拷贝过来）"
                : "股东户数只有1个报告期，无法比较环比";
        else
        {
            var latest = holderSeries[^1];
            var prev = holderSeries[^2];
            rule11 = latest.HolderNum < prev.HolderNum;
            double qoqPct = prev.HolderNum > 0 ? (latest.HolderNum - prev.HolderNum) / (double)prev.HolderNum * 100 : 0;
            // 距近2年最低（参考，不做硬门槛）
            var since = latest.ReportDate.AddYears(-2);
            var recent = holderSeries.Where(r => r.ReportDate >= since).ToList();
            long min2y = recent.Count > 0 ? recent.Min(r => r.HolderNum) : latest.HolderNum;
            double aboveLowPct = min2y > 0 ? (latest.HolderNum - min2y) / (double)min2y * 100 : 0;
            rule11Basis = $"最新{latest.ReportDate:yyyy-MM-dd}户数{latest.HolderNum:N0} vs 上一报告期{prev.ReportDate:yyyy-MM-dd}户数{prev.HolderNum:N0}，环比{qoqPct:+0.0;-0.0}%（需<0=下降）；"
                        + $"距近2年最低{min2y:N0}高{aboveLowPct:+0.0;-0.0}%（参考：越接近0越靠近历史低位、筹码越集中）";
        }

        // ── rule12 融资余额增长（硬）──最新交易日融资余额 > 上一有数据交易日
        var marginSeries = _marginRepository.GetBalanceSeries(code); // 按交易日升序
        bool hasMargin = marginSeries.Count >= 2;
        bool rule12 = false; string rule12Basis;
        if (!hasMargin)
            rule12Basis = marginSeries.Count == 0
                ? "缺少融资余额数据（请先在 Fetcher 里\"回补融资余额\"/让日常抓取积累，再把库拷贝过来）"
                : "融资余额只有1个交易日，无法比较增长";
        else
        {
            var mLatest = marginSeries[^1];
            var mPrev = marginSeries[^2];
            rule12 = mLatest.MarginBalance > mPrev.MarginBalance;
            double chgPct = mPrev.MarginBalance > 0 ? (mLatest.MarginBalance - mPrev.MarginBalance) / mPrev.MarginBalance * 100 : 0;
            rule12Basis = $"最新{mLatest.TradeDate:yyyy-MM-dd}融资余额{mLatest.MarginBalance / 1e8:F2}亿 vs 上一交易日{mPrev.TradeDate:yyyy-MM-dd}{mPrev.MarginBalance / 1e8:F2}亿，{chgPct:+0.00;-0.00}%（需>0=增长）";
        }

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
                new() { Name = "最新股东户数环比上一报告期下降（越接近近2年最低越好）", Satisfied = rule11, DataMissing = !hasHolder, Basis = rule11Basis },
                new() { Name = "最新融资余额较上一交易日增长", Satisfied = rule12, DataMissing = !hasMargin, Basis = rule12Basis },
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
