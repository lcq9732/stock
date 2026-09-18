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
    /// <summary>
    /// 一轮最多抓多少只。这个接口是所有抓取项里最慢的（每只 3 个请求、限流到约 10 请求/分钟），
    /// 一次跑完全市场要一两个小时，中间什么都干不了；分轮跑才能跟别的任务共处。
    /// </summary>
    public const int MaxPerRun = 300;

    /// <summary>
    /// 多久没有过成交就算"已经不交易了"，财务报表不再反复去问它。
    ///
    /// 取一年是往保守里选：停牌三五个月的公司照样会披露半年报，一年一根K线都没有的
    /// 基本都在退市流程里了。实测这个阈值筛掉 106 只、留下 8 只，没有误伤还在交易的。
    /// </summary>
    private static readonly TimeSpan DormantAfterNoTrading = TimeSpan.FromDays(365);

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
        var codes = SqliteStockMetaUpsert.GetAll(paths.CurrentDb).Select(s => s.Code).ToList();
        var delisted = new SqliteDelistedRepository(paths.CurrentDb).GetAll()
            .Where(r => r.DelistDate == null || r.DelistDate.Value.Year >= 2016)
            .Select(r => r.Code);
        codes = codes.Concat(delisted).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();

        var stateByCode = repo.GetFetchStateByCode();
        var expected = LatestExpectedReportPeriod(DateTime.Today);

        // 「这只票到底披露了没有」——按只看实际披露日，而不是拿法定截止日一刀切。
        // 详见 SqliteEarningsScheduleRepository.GetLatestDisclosedPeriodByCode 的注释：
        // 66% 的公司挤在法定截止日前那五天披露，等截止日过完再认这一期，5478 只会同时
        // 涌进待补队列，按每轮 300 只要补三四天。
        // 查不到记录的（新股/B股/老退市股）不在这个字典里，下面会退回 expected 兜底。
        var disclosed = new SqliteEarningsScheduleRepository(paths.CurrentDb)
            .GetLatestDisclosedPeriodByCode(DateTime.Today);

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

        int stale = 0, outdatedPeriod = 0, dormant = 0;
        var pending = new List<string>();
        foreach (var c in codes)
        {
            if (!stateByCode.TryGetValue(c, out var st))
            {
                // 没有状态记录：要么从没抓过，要么是这张表出现之前抓的（版本按 0 算）
                pending.Add(c);
                stale++;
                continue;
            }
            // 该抓到哪一期：这只票已经披露的最新一期；没有披露记录的退回法定截止日。
            // ⚠ 兜底方向只能是"多取"——查不到就按老判据来，绝不能因为查不到而漏掉一只。
            var target = disclosed.TryGetValue(c, out var d) ? d : expected;
            bool periodOld = st.ReportDate.Date < target;
            bool versionOld = st.KeysVersion < FinancialKeys.Version;
            if (!periodOld && !versionOld) continue;

            // ⚠ 只有"报告期落后"这一条才认停牌豁免。科目集版本落后的照抓不误——
            //    那是本地数据不全（旧版代码抓的科目少），历史财报还在数据源上，
            //    补回来对回测有用，跟这只票现在还交不交易没关系。
            if (periodOld && !versionOld
                && lastBarByCode.TryGetValue(c, out var lastBar) && lastBar < dormantBefore)
            {
                dormant++;
                continue;
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

        var thisRun = pending.Take(cap is > 0 ? cap.Value : MaxPerRun).ToList();
        return new FinancialFetchPlan(codes.Count, pending, thisRun, outdatedPeriod, stale, watchedCount, expected, dormant);
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
    int Dormant = 0)
{
    /// <summary>还剩多少只没补（本轮之外的）。</summary>
    public int Remaining => AllPending.Count - ThisRun.Count;

    public string Describe(int cap) =>
        $"财务报表：目标 {TotalCodes} 只，需要抓 {AllPending.Count} 只" +
        $"——其中 {OutdatedPeriod} 只报告期落后（按各自的**实际披露日**判断；" +
        $"查不到披露记录的那些按法定截止日算，当前是 {ExpectedPeriod:yyyy-MM-dd}）、" +
        $"{StaleVersion} 只科目集版本落后（本地数据是旧版代码抓的、科目不全，当前 v{FinancialKeys.Version}）；" +
        $"{TotalCodes - AllPending.Count - Dormant} 只已是最新、跳过。" +
        (Dormant > 0
            ? $"另有 {Dormant} 只报告期虽然落后，但已经一年多没有过任何成交（退市/长期停牌），" +
              "公司本身不再披露新报告期，不再反复去问；哪天恢复交易，K线一到它自己会回到名单里。"
            : "") +
        (WatchedCount > 0 ? $"你关注的 {WatchedCount} 只（自选/底仓/主动仓）已排到最前。" : "") +
        (Remaining > 0 ? $"⚠ 本轮上限 {cap} 只，其余 {Remaining} 只下轮自动继续（抓过的不会重抓）。" : "") +
        (ThisRun.Count > 0
            ? $"本轮 {ThisRun.Count} 只 × 3 个请求，该接口已降速到约 10 请求/分钟，预计 {ThisRun.Count * 3 / 10} 分钟..."
            : "全部已是最新，无需抓取。");
}
