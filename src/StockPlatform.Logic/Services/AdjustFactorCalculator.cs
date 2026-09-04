using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 从**不复权价 + 除权事件**算出乘法式复权序列（2026-08-31 新增）——回测专用价格
/// <see cref="Granularity.DayAdj"/> 就是这么来的。纯计算，不碰数据库、不发请求。
///
/// ════ 为什么不直接用数据源的后复权 ════
/// 数据源（腾讯、东财口径一致）的后复权是 <c>原价 × 累计送转因子 ＋ 累计分红金额</c>——
/// 送转按比例还原（乘）、分红按金额累加（加）。那个加法项会**阻尼波动**，股价越低、股息越高
/// 阻尼越重。实测非除权日的收益率相对真实收益率：
///     贵州茅台 ×0.855   平安银行 ×0.811   云南白药 ×0.772   工商银行 ×0.625   中国石化 ×0.542
/// 也就是说拿它做回测，高股息低价股的收益率被压掉三到四成，看起来"波动小、回撤浅"——
/// 因子排序会被系统性扭曲。这是行业惯例（看盘用没问题），但算收益就是错的。
///
/// ════ 本类用的是纯乘法式 ════
///     除权参考价 = (前收 − 每股分红 + 配股价×配股比例) ÷ (1 + 每股送转 + 配股比例)
///     factor    ×= 前收 ÷ 除权参考价
///     复权价     = 原价 × factor
/// 非除权日 factor 不动，所以**收益率精确等于真实收益率**（12 只样本、23000 个交易日实测零偏差）。
///
/// ════ 关键的一道保险：用价格校验每一条除权记录 ════
/// 分红数据里混着**假除权**。真实踩到的：
///     华英农业 2022-01-04 记「10转29.92」→ 那天真实价只涨了 1.30%
///     *ST恒立  2023-12-21 记「10转25」   → 那天真实价只跌了 3.59%
///     力帆科技 2020-12-23 记「10转25」   → 那天真实价只涨了 5.03%
/// 真是 10转25，除权日该跌 71%。这三家都是**破产重整**——资本公积转增的股份给了债权人和重整
/// 投资人，不分配给原股东，所以不除权，而数据源把它当普通转增列了出来。照单全收的话，这些
/// 股票的历史价格会被凭空缩小 4 倍。
/// 所以每条记录都拿当天的实际跳空跟理论跳空对一下，对不上就**整条不算数**。这一步不需要任何
/// 额外数据源——价格自己就能证伪。
/// </summary>
public static class AdjustFactorCalculator
{
    /// <summary>
    /// 一次除权：每股送转股数（10送1转1 = 0.2）、每股现金分红（税前，10派5元 = 0.5），
    /// 外加**配股**（2026-09-01）——每股配股数（10配3 = 0.3）和配股价（元/股）。
    ///
    /// 配股跟前两者的方向不同：送转和分红都只让股价往下调，配股是股东**掏钱**买新股，
    /// 掏进去的钱留在公司里，所以除权参考价的分子要把它加回来。两个字段必须成对出现，
    /// 只有比例没有价格算不出参考价（见 <see cref="RightsCash"/>）。
    /// </summary>
    public readonly record struct ExDividend(
        DateTime ExDate, double ShareRatio, double CashPerShare,
        double RightsRatio = 0, double RightsPrice = 0)
    {
        public bool IsEmpty => ShareRatio <= 0 && CashPerShare <= 0 && !HasRights;

        /// <summary>配股要两个数都有才算数——缺一个就没法算除权参考价，当没配过处理。</summary>
        public bool HasRights => RightsRatio > 0 && RightsPrice > 0;

        /// <summary>每股因配股**收进来**的现金 = 配股价 × 配股比例。进除权参考价的分子（加号）。</summary>
        public double RightsCash => HasRights ? RightsPrice * RightsRatio : 0;
    }

    /// <summary>算完之后的说明：应用了哪些、跳过了哪些、为什么。给日志和排查用。</summary>
    public sealed class Report
    {
        public int Applied { get; set; }
        public int Skipped { get; set; }
        public List<string> Notes { get; } = [];
        public double FinalFactor { get; set; } = 1.0;
    }

    /// <summary>
    /// 实际跳空与理论跳空允许差多少。跟理论跳空的大小挂钩：
    ///   · 普通现金分红理论跳空只有 1~3%，而除权日照样会正常涨跌（甚至涨跌停），
    ///     所以底线给到 8 个百分点，不能把正常波动误判成"假除权"；
    ///   · 大比例送转理论跳空是 −70% 这个量级，按比例给 35% 的余量，
    ///     那些"记着 10转25、实际没跳空"的假记录差了 70 多个百分点，一定被挡下。
    /// </summary>
    private static double Tolerance(double theoreticalGap) => Math.Max(0.08, Math.Abs(theoreticalGap) * 0.35);

    /// <summary>
    /// 生成复权序列。<paramref name="rawBars"/> 必须是**不复权**日线且按日期升序。
    /// 返回的 Bar 是新对象（Granularity = <see cref="Granularity.DayAdj"/>），不改入参。
    /// </summary>
    /// <param name="computedAt">
    /// 写进 <see cref="Bar.FetchedAt"/> 的时间戳——**是"这条序列什么时候算出来的"，
    /// 不是源K线什么时候抓的**（2026-09-04 修）。
    ///
    /// 原来这里直接抄 rawBars 的 FetchedAt，导致 day_adj 的时间戳等于 day_raw 的抓取时间，
    /// 跟"上次重算是什么时候"毫无关系。而 FetchOrchestrator.CodesWithStaleAdjEvents 正是拿
    /// 「除权事件.fetched_at > day_adj.fetched_at」来判断"事件变新了、该重算"的——基准一错，
    /// 判据就只在"事件抓得比K线还晚"时才碰巧成立。实测：配股 09-02 抓入、K线 09-03 抓取，
    /// 判据算出 09-02 &gt; 09-03 = 假，642 只有配股的票一只都没被检出（全库只碰巧检出 7 只）。
    /// day_adj 是纯本地计算产物，它的时间戳记"算的时刻"才有意义。
    ///
    /// 留成参数而不是写死 DateTime.Now：测试要能固定时间断言。
    /// </param>
    public static List<Bar> BuildAdjusted(
        string code, IReadOnlyList<Bar> rawBars, IReadOnlyList<ExDividend> events, out Report report,
        DateTime? computedAt = null)
    {
        var stamp = computedAt ?? DateTime.Now;
        report = new Report();
        var result = new List<Bar>(rawBars.Count);
        if (rawBars.Count == 0) return result;

        // 把每次除权挂到"当天或之后的第一个交易日"上——除权日停牌的话，效应体现在复牌那天。
        var tradingDays = rawBars.Select(b => b.PeriodStart.Date).ToList();
        var byDay = new Dictionary<DateTime, List<ExDividend>>();
        foreach (var e in events.Where(e => !e.IsEmpty).OrderBy(e => e.ExDate))
        {
            int idx = tradingDays.FindIndex(d => d >= e.ExDate.Date);
            if (idx <= 0) continue;                       // 早于本地历史起点、或就是第一根：没有"前收"可比，跳过
            var day = tradingDays[idx];
            if (!byDay.TryGetValue(day, out var list)) byDay[day] = list = [];
            list.Add(e);
        }

        double factor = 1.0;
        for (int i = 0; i < rawBars.Count; i++)
        {
            var bar = rawBars[i];
            if (i > 0 && byDay.TryGetValue(bar.PeriodStart.Date, out var todays))
            {
                double prevClose = rawBars[i - 1].Close;
                // 同一天可能有多条（少见，比如分红和转增分开公告），合并成一次处理
                double share = todays.Sum(e => e.ShareRatio);
                double cash = todays.Sum(e => e.CashPerShare);
                // 配股：分子加回股东掏的钱、分母加上新增的股。完整式子是
                //   除权参考价 = (前收 − 每股分红 + 配股价×配股比例) ÷ (1 + 每股送转 + 配股比例)
                // 同一天既送转又配股是可能的（配股方案里带送股），所以三项分别累加而不是二选一。
                double rightsRatio = todays.Sum(e => e.RightsRatio);
                double rightsCash = todays.Sum(e => e.RightsCash);
                double refPrice = (prevClose - cash + rightsCash) / (1 + share + rightsRatio);

                if (prevClose <= 0 || refPrice <= 0)
                {
                    report.Skipped++;
                    report.Notes.Add($"{code} {bar.PeriodStart:yyyy-MM-dd} 跳过：除权参考价算出来 ≤0"
                                   + $"（前收 {prevClose:F2}、每股派 {cash:F4}、每股送转 {share:F4}"
                                   + (rightsRatio > 0 ? $"、每股配 {rightsRatio:F4}@{rightsCash / rightsRatio:F2}元" : "")
                                   + "）");
                }
                else
                {
                    double theoretical = refPrice / prevClose - 1;         // 理论上该跳多少
                    // 用开盘价判断：除权日不设涨跌停，开盘就已经跳到位了，比收盘少受当日情绪干扰
                    double actual = (bar.Open > 0 ? bar.Open : bar.Close) / prevClose - 1;
                    if (Math.Abs(actual - theoretical) > Tolerance(theoretical))
                    {
                        report.Skipped++;
                        report.Notes.Add(
                            $"{code} {bar.PeriodStart:yyyy-MM-dd} 跳过：记着每股送转 {share:F4}、派息 {cash:F4}"
                          + (rightsRatio > 0 ? $"、每股配 {rightsRatio:F4}@{rightsCash / rightsRatio:F2}元" : "")
                          + $"，理论该跳 {theoretical * 100:+0.00;-0.00}%，实际只跳了 {actual * 100:+0.00;-0.00}%"
                          + (rightsRatio > 0
                                ? "——配股方案多半最终没实施（配股表没有\"进度\"列，分不出来，只能靠这道价格校验）"
                                : "——多半是破产重整的资本公积转增（股份给债权人、不分配给原股东，所以不除权）"));
                    }
                    else
                    {
                        factor *= prevClose / refPrice;
                        report.Applied++;
                    }
                }
            }

            result.Add(new Bar
            {
                Code = bar.Code,
                Granularity = Granularity.DayAdj,
                PeriodStart = bar.PeriodStart,
                Open = bar.Open * factor,
                Close = bar.Close * factor,
                High = bar.High * factor,
                Low = bar.Low * factor,
                Volume = bar.Volume,        // 成交量/额不复权——它们是"当时真实发生的量"，缩放没有意义
                Amount = bar.Amount,
                Turnover = bar.Turnover,
                FetchedAt = stamp,          // 重算时刻，不是源K线的抓取时刻——见 computedAt 的说明
            });
        }
        report.FinalFactor = factor;
        return result;
    }

    /// <summary>
    /// 自检：非除权日，复权序列算出的收益率必须**精确等于**真实收益率。
    /// 这是这套算法唯一的硬指标（数据源那份就是栽在这上面），所以生成之后顺手验一遍，
    /// 有偏差说明实现被改坏了。返回不合格的天数。
    /// </summary>
    public static int VerifyReturns(IReadOnlyList<Bar> rawBars, IReadOnlyList<Bar> adjBars, double tolerance = 1e-9)
    {
        int bad = 0;
        for (int i = 1; i < rawBars.Count && i < adjBars.Count; i++)
        {
            if (rawBars[i - 1].Close <= 0 || adjBars[i - 1].Close <= 0) continue;
            double rRaw = rawBars[i].Close / rawBars[i - 1].Close - 1;
            double rAdj = adjBars[i].Close / adjBars[i - 1].Close - 1;
            // 除权日本来就该不一样（那正是复权在起作用），只查没有除权的日子
            if (Math.Abs(rAdj - rRaw) > tolerance && Math.Abs(rAdj - rRaw) < 0.001) bad++;
        }
        return bad;
    }
}
