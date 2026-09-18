using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// 股东数据"这一轮抓哪些票"的判据（2026-09-18）。**不发任何网络请求，只查本地库**——
/// 照 <see cref="FinancialFetchPlanner"/> 的先例抽成独立类：任务要用它选票，
/// 界面以后想显示"还剩多少只没补"也能直接调，不用反过来依赖任务。
///
/// ════ 判据（三条，见 doc/shareholder-task-design.md §2）════
///   ① **报告期落后**：库里这只票股东户数的最新报告期 &lt; 它**已披露**的最新报告期。
///      已披露＝按只看实际披露日（来自【拉取财报预约日】），查不到记录的退回法定截止日。
///      为什么不拿法定截止日一刀切：66% 的公司挤在截止日前五天披露，等截止日过完再认，
///      5500 只会同一天涌进队列（财务那边踩过）。
///   ② **时间兜底**：<see cref="StaleDays"/> 天没抓过的也抓。光看报告期的话，
///      那 4% 的不定期期次（增发/协议转让后的股东名单变动公告）要等下个季报才入库。
///      它同时兜住"【拉取财报预约日】好几天没跑成"的情况。
///   ③ **停牌豁免**：最近一年一根日K都没有的跳过。ST/退市/长停那批公司本身早就不出新
///      报告期了，每轮问一遍纯浪费配额（财务那边实测筛掉 106 只、误伤 0 只）。
///      判据故意用"最近还有没有成交"而不是退市标记，因为它**自愈**：哪天恢复交易，
///      K线一到就自动回到待抓名单。
///
///   ④ **退市股只走自己那一档**（2026-09-18 纳入）："从没抓过"立刻抓（否则纳入这件事第一轮
///      就不会发生），抓过之后**不看报告期落后**、只按 <see cref="DelistedStaleDays"/>（365 天）
///      重刷。不这么分的话它们每轮都会被判"报告期落后"——法定报告期早停了，而那道
///      "一年没成交"的豁免挡不住全部（18 只一根日K都没有、12 只今年刚退市）。
///      实测新浪对退市股有完整数据，而纳入前库里 337 只只有 5 只有。
///
/// ⚠ 兜底方向只能是"多取"：查不到披露记录、报告期解析不出来，一律判成该抓。
/// 绝不能因为查不到而漏掉一只——漏抓是静默的，多抓只是多花请求。
///
/// ════ 不设人为的每轮上限 ════
/// 分次是为限流付的税，不是目标。季度披露期 5513 只同时到期，一轮 11026 个请求、
/// 3 并发/1 秒 ⇒ 约 60 分钟，一轮跑得完。真撞限流由任务侧的熔断 + 断点续跑自然分次。
/// 财务那项要 <c>MaxPerRun=300</c> 是因为它配额极严（约 10 请求/分钟），不是同一量级。
/// </summary>
public class ShareholderFetchPlanner(FetchPaths paths)
{
    /// <summary>
    /// 多久没抓过就整只重刷一遍（时间兜底）。100 天≈一个季度，跟"季度数据"的节奏对齐：
    /// 太长会让那 4% 不定期披露的股东名单变动等一个季度才入库，太短会让整轮全刷变频繁。
    /// </summary>
    public const int StaleDays = 100;

    /// <summary>
    /// 退市股多久重刷一遍（2026-09-18 纳入退市股时加）。它们的股东数据基本是静态历史，
    /// 没必要跟在市股同频——跟分红那一项的 <c>DelistedStaleDays</c> 取同一个值。
    ///
    /// 不设成"抓过就永不再抓"，是留一条自愈的路：数据源后来补录了历史，一年内会捞回来。
    /// ⚠ 实测新浪对退市股**有**完整股东数据（002898 39 期 / 000004 110 期 / 300029 78 期 /
    /// 000638 96 期，其中两只最新期还在 2026-08 之后），所以这不是白抓。
    /// </summary>
    public const int DelistedStaleDays = 365;

    /// <summary>
    /// 多久没有过成交就算"已经不交易了"。沿用 <see cref="FinancialFetchPlanner"/> 的取值和理由：
    /// 停牌三五个月的公司照样会披露半年报，一年一根K线都没有的基本都在退市流程里。
    /// </summary>
    private static readonly TimeSpan DormantAfterNoTrading = TimeSpan.FromDays(365);

    /// <summary>
    /// 算出这一轮要抓哪些票、按什么顺序。<paramref name="all"/> 为 true＝无视判据全都要
    /// （【整段回补】那个模式）。
    /// </summary>
    public ShareholderFetchPlan Plan(bool all = false)
    {
        var repo = new SqliteShareholderRepository(paths.CurrentDb);
        repo.EnsureSchema();

        // 名单：在市个股 + **退市股**（2026-09-18 纳入）。
        //
        // 口径跟分红那一项一致，用 StockMeta 的 type（337 只），不用财务那边的"2016 年后退市"
        // （249 只）：这两项都是"逐只抓全历史"的慢数据，而且实测新浪对老退市股也有数据，
        // 多那 88 只的成本是每年 176 个请求。
        //
        // ⚠ 退市名单是【补全退市名单】（StepDelistedSupplement）往 StockMeta 写进去的——
        // 那一项没跑过，这里就少抓那批票，而且**不报错、只是少**。所以目录里给这一项
        // 挂了软依赖，日志会提一句。
        //
        // 为什么要纳入：回测要消除幸存者偏差，而库里 337 只退市股**只有 5 只有股东数据**。
        // 十大流通股东里的「香港中央结算」是北向持股的唯一来源，退市股缺这一块就等于
        // 那段历史的北向分析只能看活下来的票。分红那一项 2026-09-06 已经因为同样的理由纳入过。
        var delisted = SqliteStockMetaUpsert
            .GetByTypes(paths.CurrentDb, SqliteStockMetaUpsert.TypeDelisted)
            .Select(s => s.Code).ToHashSet(StringComparer.Ordinal);
        var codes = SqliteStockMetaUpsert
            .GetByTypes(paths.CurrentDb, SqliteStockMetaUpsert.TypeStock, SqliteStockMetaUpsert.TypeDelisted)
            .Select(s => s.Code)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        var watched = WatchedCodes.Read(paths);

        if (all)
            return new ShareholderFetchPlan(
                codes.Count, Order(codes, watched), codes.Count, 0, 0,
                FinancialFetchPlanner.LatestExpectedReportPeriod(DateTime.Today), delisted.Count);

        var state = repo.GetFetchStateByCode();
        var expected = FinancialFetchPlanner.LatestExpectedReportPeriod(DateTime.Today);
        var disclosed = new SqliteEarningsScheduleRepository(paths.CurrentDb)
            .GetLatestDisclosedPeriodByCode(DateTime.Today);

        var lastBarByCode = new SqliteBarRepository(paths.CurrentDb)
            .GetLatestPeriodStartByCode(Granularity.Day);
        // 基准取上证指数的最新交易日（本地判交易日一贯拿它当锚），而不是 DateTime.Today——
        // 库里数据本来就可能落后几天，拿今天当基准会把正常的票误判成停牌。
        var marketLatest = lastBarByCode.TryGetValue("sh000001", out var mkt) ? mkt : DateTime.Today;
        var dormantBefore = marketLatest - DormantAfterNoTrading;
        var staleBefore = DateTime.Now.AddDays(-StaleDays);
        // 退市股走更长的那一档：它们的股东数据基本是静态历史，没必要每季度重刷一遍
        // （337 只×2 请求）。⚠ 只影响**时间兜底**这一条；"从没抓过"仍然立刻抓，
        // 否则纳入退市股这件事第一轮就不会发生。
        var delistedStaleBefore = DateTime.Now.AddDays(-DelistedStaleDays);

        int newPeriod = 0, timeStale = 0, dormant = 0, delistedPending = 0;
        var pending = new List<string>();
        foreach (var c in codes)
        {
            bool isDelisted = delisted.Contains(c);
            if (!state.TryGetValue(c, out var st))
            {
                // 一行都没有：从没抓过（或抓过但那次数据源什么都没给）。该抓。
                pending.Add(c);
                newPeriod++;
                if (isDelisted) delistedPending++;
                continue;
            }

            var target = disclosed.TryGetValue(c, out var d) ? d.Date : expected;
            // ⚠ 退市股**不看"报告期落后"**，只看时间兜底那一档（2026-09-18 实测后定的）：
            //   退市公司的法定报告期早就停了，这个判据对它们永远成立 ⇒ 每轮都排进队列。
            //   而"一年没成交"那道豁免挡不住全部：337 只里 18 只**一根日K都没有**
            //   （TryGetValue 直接 false，豁免不触发）、另有 12 只今年刚退市（K线还在一年内），
            //   合计 30 只会每轮白抓 60 个请求、永远抓不完，日志上还一直挂着"待抓 30 只"。
            //
            //   代价说清楚：退市股**仍会披露**股东户数（实测 000004 最新到 2026-09-09、
            //   002898 到 2026-08-28），所以这么改之后它们退市前后那一两期最长晚
            //   DelistedStaleDays（365 天）才入库。可以接受——退市股的股东数据是给回测
            //   消除幸存者偏差用的历史资料，不需要季度级新鲜度。
            bool periodOld = !isDelisted && st.ReportDate < target;
            bool tooLong = st.FetchedAt < (isDelisted ? delistedStaleBefore : staleBefore);
            if (!periodOld && !tooLong) continue;

            // 停牌豁免只对"报告期落后"生效。时间兜底那条**照抓不误**：
            // 那是"整只重刷一遍"，跟这只票现在还交不交易无关；而且长停的票本来也就那么几十只。
            if (periodOld && !tooLong
                && lastBarByCode.TryGetValue(c, out var lastBar) && lastBar < dormantBefore)
            {
                dormant++;
                continue;
            }

            pending.Add(c);
            if (periodOld) newPeriod++; else timeStale++;
            if (isDelisted) delistedPending++;
        }

        return new ShareholderFetchPlan(
            codes.Count, Order(pending, watched), newPeriod, timeStale, dormant, expected, delistedPending);
    }

    /// <summary>自选/底仓/主动仓的票排最前——被 <c>Deadline</c> 截断时先保住真会被拿来分析的。
    /// 其余按代码定序：顺序稳定，断点续跑才不会来回跳。</summary>
    private static List<string> Order(IEnumerable<string> codes, IReadOnlySet<string> watched)
        => codes.OrderByDescending(watched.Contains)
                .ThenBy(c => c, StringComparer.Ordinal)
                .ToList();
}

/// <summary>
/// 股东数据的待抓清单。<paramref name="Pending"/> 已经排好序（自选优先）。
/// </summary>
/// <param name="Total">名单总数（在市个股 + 退市股）。</param>
/// <param name="Pending">这一轮要抓的，已排序。</param>
/// <param name="NewPeriod">其中"有新报告期 / 从没抓过"的只数。</param>
/// <param name="TimeStale">其中"只是太久没抓"的只数（时间兜底捞回来的）。</param>
/// <param name="Dormant">因为一年没成交而跳过的只数——报出来，免得让人以为漏了。</param>
/// <param name="ExpectedPeriod">按法定截止日今天该有的最新报告期（日志里说明判据）。</param>
/// <param name="DelistedPending">待抓里有多少只是退市股——报出来，免得让人以为在抓没用的票
/// （它们是为了消除回测的幸存者偏差，见 Plan 里的注释）。</param>
public record ShareholderFetchPlan(
    int Total,
    IReadOnlyList<string> Pending,
    int NewPeriod,
    int TimeStale,
    int Dormant,
    DateTime ExpectedPeriod,
    int DelistedPending = 0);
