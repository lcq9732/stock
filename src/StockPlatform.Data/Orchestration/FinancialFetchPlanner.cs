using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// 财务抓取的待抓清单。**不发任何网络请求**，只查本地库，所以界面可以随时调用
/// （"空闲时自动补"每隔几分钟问一次也没有负担）。
///
/// 2026-09-10 从 <see cref="FetchOrchestrator"/> 抽出来：【拉取财务报表】迁成新式任务
/// （<c>StockPlatform.Tasks.FinancialTask</c>）之后，这份判据有**两个**使用方——
/// 任务自己要用它决定抓哪些票，界面要用它回答"还剩多少只没补"。留在 orchestrator 里的话，
/// 新任务就得反过来依赖 orchestrator，等于没解耦。照 <c>SqliteAdjSeriesAuditor</c> 的先例抽走。
/// </summary>
public class FinancialFetchPlanner(FetchPaths paths)
{
    // 原来这里有个 MaxPerRun = 300（每轮最多抓多少只）。2026-10-21 随【拉取财务报表】挪进日更删掉
    // （用户定）：当天没抓完的票本地报告期还是旧的，第二天照样会被算进名单——**漏不掉，只会晚一天**；
    // 而日更是"跑一轮就算今天做完"，留着上限的话披露高峰（一天 1200 只）要积压好几天。
    // "一轮跑太久"改成开工时报只数和预计耗时（见 FinancialFetchPlan.Describe），由人决定要不要停。

    /// <summary>
    /// 多久没有过成交就算"已经不交易了"，财务报表不再反复去问它。
    ///
    /// 取一年是往保守里选：停牌三五个月的公司照样会披露半年报，一年一根K线都没有的
    /// 基本都在退市流程里了。实测这个阈值筛掉 106 只、留下 8 只，没有误伤还在交易的。
    /// </summary>
    private static readonly TimeSpan DormantAfterNoTrading = TimeSpan.FromDays(365);

    /// <summary>
    /// "上一轮就是冲着这个报告期去问的、数据源当时就是没有"——隔多久再问一次（2026-09-19）。
    ///
    /// 这是最后一层兜底，接在退市日封顶和"退市且无K线"之后：前两层靠的是**名单**，
    /// 而名单永远会有赶不上现实的时候——实测 920305「云创退」名字都带退了，DelistedStock 里
    /// 却没有它（退市名单只有沪深两所官网，北交所那一路没有来源）；002731「*ST萃华」还在交易，
    /// 把披露日从 04-29 一路改到 08-22 至今没出。这类票任何名单都证明不了"它这期就是没有"，
    /// 只能靠"问过了、没有、隔阵子再问"。
    ///
    /// 取 7 天：公司延期披露后补上一般在一周内，而这批常年只有个位数只票，代价可以忽略。
    ///
    /// ⚠ 只管目标期来自**法定截止日/退市日**的票。目标期来自**实际披露日**的不走这个冷却
    /// （2026-10-21，见 <see cref="Plan"/> 里 ③ 那段）：公司已经发了，数据源只是还没更新，第二天就该再问。
    /// </summary>
    private static readonly TimeSpan RetryAfterMiss = TimeSpan.FromDays(7);

    /// <summary>
    /// 已实际披露、数据源却还没有这一期——过了几天就从"等数据源更新"升级成报错（2026-10-21）。
    /// 东财 F10 平时当天或次日就有；连着 3 天还没有，多半是这只票在数据源那边出了问题，要让人知道。
    /// </summary>
    public static readonly TimeSpan SourceLagAlarm = TimeSpan.FromDays(3);

    /// <summary>估预计耗时时，取不到实测速度就按这个算（东财 F10 09-11 实测 7–12 秒一只）。</summary>
    internal const double DefaultSecondsPerCode = 10;

    /// <summary>
    /// 算出财务数据还有哪些票要抓。
    ///
    /// 增量判断有**两个**条件，缺一不可（2026-08-27 修）：
    ///   ① 报告期不够新 → 要抓
    ///   ② 科目集版本落后 → 也要抓
    /// 只看①的话，扩充科目后老数据的 report_date 仍是"最新"，新科目永远补不上：那天科目从 8 个
    /// 扩到 52 个之后跑全量拉取，5780 只里 5552 只被判定无需重抓，44 个新科目一条都没进库。
    /// 见 <see cref="FinancialKeys.Version"/>。
    /// </summary>
    public FinancialFetchPlan Plan(int? cap = null)
    {
        var repo = new SqliteFinancialRepository(paths.CurrentDb);
        repo.EnsureSchema();

        // 目标：在市个股 + 2016年后退市的（回测池同款；更早退市的没有K线、抓了也用不上）
        var live = SqliteStockMetaUpsert.GetAll(paths.CurrentDb)
            .Select(s => s.Code).ToHashSet(StringComparer.Ordinal);
        // ⚠ 退市日要留着，不能只取 Code：它是"这只票最多可能有哪一期财报"的上界，见下面 target 封顶。
        //   注意 DelistDate 可以为 null（名单只给了代码、没给日期），那种票下面单独有一条判据。
        var delisted = new SqliteDelistedRepository(paths.CurrentDb).GetAll()
            .Where(r => r.DelistDate == null || r.DelistDate.Value.Year >= 2016)
            .ToList();
        var delistDateByCode = delisted
            .Where(r => r.DelistDate != null)
            .GroupBy(r => r.Code, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Min(r => r.DelistDate!.Value), StringComparer.Ordinal);
        var codes = live.Concat(delisted.Select(r => r.Code))
            .Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();

        var stateByCode = repo.GetFetchStateByCode();
        var expected = LatestExpectedReportPeriod(DateTime.Today);

        // 「这只票到底披露了没有」——按只看实际披露日，而不是拿法定截止日一刀切。
        // 详见 SqliteEarningsScheduleRepository.GetLatestDisclosedPeriodByCode 的注释：
        // 66% 的公司挤在法定截止日前那五天披露，等截止日过完再认这一期，5478 只会同时
        // 涌进待补队列，按每轮 300 只要补三四天。
        // 查不到记录的（新股/B股/老退市股）不在这个字典里，下面会退回 expected 兜底。
        // 连实际披露日一起取（2026-10-21）：判"数据源还没更新"已经拖了几天要靠它，见 SourceLagAlarm。
        var disclosed = new SqliteEarningsScheduleRepository(paths.CurrentDb)
            .GetLatestDisclosureByCode(DateTime.Today);

        // 「已经不交易了就别再问」（2026-09-03 用户提）：ST/退市/长期停牌那批，公司本身早就
        // 不出新报告期了，每轮拿去问一遍纯属浪费配额——实测 114 只"报告期落后"里，
        // 有 106 只一年多没有过任何一根K线，真正还在交易的只有 1 只。
        //
        // 判据故意用"最近还有没有成交"而不是退市标记，因为它**自愈**：哪天恢复交易，
        // K线一到 gap 就缩回来，这只票自动回到待抓名单，不需要谁去手工恢复。
        // 基准取上证指数的最新交易日（本地判交易日一贯拿它当锚）。
        var lastBarByCode = new SqliteBarRepository(paths.CurrentDb)
            .GetLatestPeriodStartByCode(Granularity.Day);
        var marketLatest = lastBarByCode.TryGetValue("sh000001", out var mkt) ? mkt : DateTime.Today;
        var dormantBefore = marketLatest - DormantAfterNoTrading;

        int stale = 0, never = 0, outdatedPeriod = 0, dormant = 0, unusable = 0, asked = 0;
        var pending = new List<string>();
        var targetByCode = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        // 目标期来自实际披露的那些票，以及披露日——任务收工时判"数据源还没更新"要用
        var disclosedOnByCode = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var c in codes)
        {
            // 该抓到哪一期：这只票已经披露的最新一期；没有披露记录的退回法定截止日。
            // ⚠ 兜底方向只能是"多取"——查不到就按老判据来，绝不能因为查不到而漏掉一只。
            var target = expected;
            if (disclosed.TryGetValue(c, out var d))
            {
                target = d.Period;
                disclosedOnByCode[c] = d.ActualDate;
            }

            // ══ 退市日封顶（2026-09-19）══
            // 退市公司不会再披露退市日之后的报告期，所以"应该有哪一期"要按它自己的退市日算，
            // 不能拿全市场统一的法定截止日一刀切——截止日每季往前走一格，这批票就被判"落后"
            // 一次，抓回来的最新期却永远追不上，于是每轮重抓、永远出不来（实测 8 只票空转四天）。
            // LatestExpectedReportPeriod 本来就是"到这一天为止法定该披露的最后一期"，正好复用。
            if (delistDateByCode.TryGetValue(c, out var delistDate))
            {
                var lastPossible = LatestExpectedReportPeriod(delistDate);
                if (lastPossible < target)
                {
                    target = lastPossible;
                    disclosedOnByCode.Remove(c);   // 目标期已不是那次披露的那一期
                }
            }
            targetByCode[c] = target;

            if (!stateByCode.TryGetValue(c, out var st))
            {
                // 没有状态记录：要么从没抓过（新股），要么是这张表出现之前抓的（版本按 0 算）
                pending.Add(c);
                never++;
                continue;
            }
            bool periodOld = st.ReportDate.Date < target;
            bool versionOld = st.KeysVersion < FinancialKeys.Version;
            if (!periodOld && !versionOld) continue;

            // ⚠ 下面三条豁免都只认"报告期落后"。科目集版本落后的照抓不误——
            //    那是本地数据不全（旧版代码抓的科目少），历史财报还在数据源上，
            //    补回来对回测有用，跟这只票现在还交不交易没关系。
            if (periodOld && !versionOld)
            {
                bool hasBar = lastBarByCode.TryGetValue(c, out var lastBar);

                // ① 一年没有过任何成交：退市/长期停牌，公司本身不再出新报告期。
                if (hasBar && lastBar < dormantBefore) { dormant++; continue; }

                // ② 已经退市、本地却一根K线都没有（2026-09-19 补）——回测池根本用不到它，
                //    问也是白问。这类票原先两道闸门全穿：退市名单只给了代码没给退市日，
                //    躲过①要"有K线且早于一年前"的前提（TryGetValue 取不到值就不豁免）。
                //    实测 5 只（603388 *ST元成、688086 退市紫晶、688287 退市观典、
                //    688555 退市泽达、920680 广道退）。**只排除不在在市名册里的**，
                //    在市新股哪天有了K线自己会回到名单里。
                if (!hasBar && !live.Contains(c)) { unusable++; continue; }

                // ③ 上一轮就是冲着同一个目标期问的、数据源当时就是没有 ⇒ 冷却期内不再问。
                //    名单赶不上现实时的最后一层兜底，见 RetryAfterMiss。
                //    ⚠ 目标期来自**实际披露**的只冷却到当天（2026-10-21）：公司傍晚刚发公告、
                //      17:30 那轮去问时东财还没更新，是常事——按 7 天冷却的话这只票要等一周，
                //      正好抵消挪进日更的意义。今天问过就不再问，明天日更再来。
                if (st.TargetDate is { } askedFor && askedFor >= target
                    && st.FetchedAt is { } at
                    && (disclosedOnByCode.ContainsKey(c)
                        ? at.Date == DateTime.Today
                        : DateTime.Now - at < RetryAfterMiss))
                {
                    asked++;
                    continue;
                }
            }

            pending.Add(c);
            if (versionOld) stale++; else outdatedPeriod++;
        }

        // 自选/底仓/主动仓里的票排最前——它们是真正会被拿来分析的，先补上就能立刻用；
        // 剩下几千只不看的票慢慢磨。
        // 自选/底仓/主动仓的名单读取 2026-09-18 提成 WatchedCodes 共用——
        // 【拉取股东数据】也要这份名单，抄第二遍早晚会出现两处不一致。
        var watched = WatchedCodes.Read(paths);
        int watchedCount = pending.Count(watched.Contains);
        if (watchedCount > 0)
            pending = pending
                .OrderByDescending(watched.Contains)
                .ThenBy(c => c, StringComparer.Ordinal)
                .ToList();

        // 不设上限（2026-10-21）：cap 只在调用方显式给了时才截，见类开头的说明。
        var thisRun = cap is > 0 ? pending.Take(cap.Value).ToList() : pending.ToList();
        return new FinancialFetchPlan(codes.Count, pending, thisRun, outdatedPeriod, stale, watchedCount,
            expected, dormant, unusable, asked, targetByCode)
        {
            NeverFetched = never,
            DisclosedOnByCode = disclosedOnByCode,
            SecondsPerCode = MeasuredSecondsPerCode(stateByCode.Values),
        };
    }

    /// <summary>
    /// 最近一批连续抓取的实测速度（秒/只），给开工那行估预计耗时（2026-10-21）。
    ///
    /// 不另查库：<see cref="FinancialFetchState.FetchedAt"/> 本来就在手里。取最近 60 只的抓取时刻，
    /// 相邻间隔里**超过 2 分钟的不算**（那是两轮之间的空档，不是抓一只要的时间），剩下的取中位数。
    /// 样本太少（不到 10 个间隔）就返回 null，调用方按 <see cref="DefaultSecondsPerCode"/> 估。
    /// </summary>
    internal static double? MeasuredSecondsPerCode(IEnumerable<FinancialFetchState> states)
    {
        var times = states.Select(s => s.FetchedAt).OfType<DateTime>()
                          .OrderByDescending(t => t).Take(60).ToList();
        var gaps = new List<double>();
        for (int i = 1; i < times.Count; i++)
        {
            var g = (times[i - 1] - times[i]).TotalSeconds;
            if (g > 0 && g <= 120) gaps.Add(g);
        }
        if (gaps.Count < 10) return null;
        gaps.Sort();
        return gaps[gaps.Count / 2];
    }

    /// <summary>今天应该已经能拿到的最新报告期——按法定披露截止日：一季报 4-30、半年报 8-31、
    /// 三季报 10-31、年报次年 4-30。用于财报抓取的"按报告期跳过"。</summary>
    internal static DateTime LatestExpectedReportPeriod(DateTime today)
    {
        var candidates = new List<(DateTime Period, DateTime Deadline)>();
        for (int y = today.Year - 1; y <= today.Year; y++)
        {
            candidates.Add((new DateTime(y, 3, 31), new DateTime(y, 4, 30)));
            candidates.Add((new DateTime(y, 6, 30), new DateTime(y, 8, 31)));
            candidates.Add((new DateTime(y, 9, 30), new DateTime(y, 10, 31)));
            candidates.Add((new DateTime(y, 12, 31), new DateTime(y + 1, 4, 30)));
        }
        return candidates.Where(c => c.Deadline <= today).Max(c => c.Period);
    }

}

/// <summary>
/// 财务抓取的待抓清单（2026-08-27 抽出来公开，2026-09-10 随 <see cref="FinancialFetchPlanner"/>
/// 一起从 FetchOrchestrator 挪成顶层类型）——界面靠它回答"还剩多少没补"，
/// 从而决定"空闲时自动补"要不要继续跑、什么时候可以停。
/// </summary>
public record FinancialFetchPlan(
    int TotalCodes,
    List<string> AllPending,
    List<string> ThisRun,
    int OutdatedPeriod,
    int StaleVersion,
    int WatchedCount,
    DateTime ExpectedPeriod,
    int Dormant = 0,
    int Unusable = 0,
    int AskedRecently = 0,
    IReadOnlyDictionary<string, DateTime>? TargetByCode = null)
{
    /// <summary>还剩多少只没补（本轮之外的）。不设上限之后只有调用方显式给了 cap 才会大于 0。</summary>
    public int Remaining => AllPending.Count - ThisRun.Count;

    /// <summary>
    /// 从没抓过（没有状态记录）的只数——多半是新股（2026-10-21 从 <see cref="StaleVersion"/> 里拆出来：
    /// 开工那行要分原因报只数，"科目版本落后一大片"和"来了几只新股"是两回事）。
    /// </summary>
    public int NeverFetched { get; init; }

    /// <summary>
    /// 目标期来自**实际披露**的票 → 披露日（2026-10-21）。任务收工时拿它分辨
    /// "数据源还没更新"（刚披露，明天再取）和"拖了好几天还没有"（报错），见 <see cref="FinancialFetchPlanner.SourceLagAlarm"/>。
    /// </summary>
    public IReadOnlyDictionary<string, DateTime> DisclosedOnByCode { get; init; }
        = new Dictionary<string, DateTime>();

    /// <summary>最近一批抓取的实测速度（秒/只）；null＝样本不够，按默认值估。</summary>
    public double? SecondsPerCode { get; init; }

    /// <summary>
    /// 预计超过多久就在开工那行挂 ⚠（2026-10-21）。取的是 8 月底高峰日的量级（≈750 只），
    /// 4 月底那几天会触发，平日不会。
    /// </summary>
    public static readonly TimeSpan LongRunWarning = TimeSpan.FromHours(2);

    /// <summary>本轮预计耗时。</summary>
    public TimeSpan Estimate => TimeSpan.FromSeconds(
        ThisRun.Count * (SecondsPerCode ?? FinancialFetchPlanner.DefaultSecondsPerCode));

    /// <summary>这只票这一轮是**冲着哪个报告期**去抓的——落库时要记进
    /// <c>FinancialFetchState.target_date</c>，下一轮靠它认出"问过了但数据源没有"。</summary>
    public DateTime? TargetOf(string code)
        => TargetByCode != null && TargetByCode.TryGetValue(code, out var d) ? d : null;

    /// <summary>
    /// 开工那一行（2026-10-21 改写）：要抓多少只、为什么、预计多久。
    ///
    /// 不设上限之后，"这一轮会跑很久"只能靠这一行让人知道——所以只数按原因分开报，
    /// 预计超过 <see cref="LongRunWarning"/> 时挂 ⚠ 并说清停掉的后果。
    /// </summary>
    public string Describe()
    {
        if (ThisRun.Count == 0)
            return "财务报表：没有新披露、也没有要补的，全部已是最新，无需抓取。" + SkippedNote();

        var reasons = new List<string>();
        if (OutdatedPeriod > 0) reasons.Add($"新披露/报告期落后 {OutdatedPeriod}");
        if (StaleVersion > 0) reasons.Add($"科目集版本落后 {StaleVersion}");
        if (NeverFetched > 0) reasons.Add($"从没抓过 {NeverFetched}");

        var est = Estimate;
        bool longRun = est >= LongRunWarning;
        var speed = SecondsPerCode is { } s ? $"按最近实测约 {s:0} 秒/只" : $"按约 {FinancialFetchPlanner.DefaultSecondsPerCode:0} 秒/只估";

        return (longRun ? "⚠ " : "")
             + $"财务报表：本轮要抓 {ThisRun.Count} 只（{string.Join("、", reasons)}），{speed}，预计 {Fmt(est)}。"
             + (longRun
                 ? "这一轮会跑比较久——可以随时停，停掉不丢已抓的，剩下的明天日更接着补。"
                   + (StaleVersion > ThisRun.Count / 2
                       ? $"大头是科目集版本落后（当前 v{FinancialKeys.Version}）：那是代码改了科目集造成的一次性重抓，不是数据源出了问题。"
                       : "")
                 : "")
             + (WatchedCount > 0 ? $"你关注的 {WatchedCount} 只（自选/底仓/主动仓）排在最前。" : "")
             + (Remaining > 0 ? $"本轮只抓前 {ThisRun.Count} 只，其余 {Remaining} 只下轮接着来。" : "")
             + SkippedNote();
    }

    /// <summary>不抓的那几类各几只——平时都是个位数，有才说。</summary>
    private string SkippedNote() =>
        (Dormant > 0
            ? $"另有 {Dormant} 只一年多没成交（退市/长期停牌），不再反复去问。"
            : "") +
        (Unusable > 0
            ? $"{Unusable} 只已退市且本地一根K线都没有，不抓。"
            : "") +
        (AskedRecently > 0
            ? $"{AskedRecently} 只问过了、数据源暂时没有，先不问（刚披露的明天再问，其余隔几天）。"
            : "");

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} 小时 {t.Minutes} 分"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} 分钟"
        : $"{Math.Max(1, (int)t.TotalSeconds)} 秒";
}
