using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 【分档资金流】**当天到底齐没齐**的判据（2026-09-16）。
///
/// ════ 为什么这一项要单独有个判据，别的表没有 ════
/// 因为它是全库唯一**过了窗口就永久取不回来**的数据：push2delay 的快照只给"最近一个交易日"，
/// 下一个交易日行情一开始更新就滚走了。别的表（融资余额、龙虎榜、大宗…）隔天、隔周都还能重抓，
/// 所以它们的缺口交给体检报一句、待办里排着慢慢补就行；这一项不行，**当晚没发现就没了**。
/// 2026-09-09 全市场整天缺失就是这么来的：那天没轮到跑，事后只能靠逐股通道 5,500 个请求换回一天。
///
/// ════ 判据：期望 = 当天有日K的个股数 ════
/// 有K线的那天就该有资金流。这跟 <see cref="Orchestration.MoneyFlowBackfillPlan"/>（逐股补历史的
/// 排队判据）是**同一个口径**，故意如此——两处各写一份迟早漂移；口径本身是
/// <see cref="Orchestration.MoneyFlowBackfillPlan.ExpectGranularity"/>（**不复权**日线，因为它排在
/// 这一项之前跑；理由和等价性核对见那儿）。
///
/// 实测最近 25 个交易日，"当天个股日K只数 − 当天资金流行数"只出现过 0（9 天）和 +1（16 天），
/// 没有第三种值。所以 <see cref="Tolerance"/> 取 2 已经很宽。
///
/// 为什么不用体检里那条"行数低于邻近中位数 70%"：少 200 只 = 3.6%，那条判据连动都不会动。
/// 为什么不只信快照自报的 total：那是**同一次请求**里的数——整项根本没跑的时候它不存在。
/// 这里查的是**库里到底有没有**，是唯一不依赖抓取过程的证据。两条互补，都留着。
///
/// ════ ⚠ 期望值自己得可信 ════
/// 同向失效必须挡掉：个股日K今天也没抓完的话，期望跟着变小，两边一起少、判据会说"齐了"。
/// 所以先看 <see cref="MoneyFlowDayStatus.BarsReady"/>（当天日K ≥ 在市名册的
/// <see cref="BarsReadyRatio"/>），不够就**不下结论**——那是日K的事，不该记到这一项头上。
///
/// ════ 判哪一天：先看尺子在不在（2026-09-22 用户要求）════
/// 原来认死了"最近一个交易日（含今天）"，于是交易日早上八点就去问"今天齐了吗"——今天还没开盘、
/// 日K一根没有，尺子本身是空的，界面整个上午挂着"⚠ 无法核对：09-22 的个股日线只有 0/5554 只"。
/// 那不是缺口，是还没到时候。
///
/// 改成两步：<b>今天的日K已经到位就判今天</b>（收盘后日更先跑日K再跑本项，老路一步不变，
/// 当晚发现缺口的能力一点不丢）；<b>今天日K不到位、而且还没到当天的确认时刻</b>
/// （<see cref="IncrementalWindowCalculator.MarketCloseHour"/>，跟"K线算不算最终值"共用一个钟点，
/// 不另立一个）<b>就退一格</b>，改报上一个交易日齐没齐。过了那个点日K还不到位，才是真该提醒的
/// "无法核对，先把日K补齐"。
///
/// 为什么不简单按"收盘前一律看昨天"：15~16 点之间本项可能已经跑完了，一刀切会把当天的核对
/// 整个跳过——而这一项的全部意义就是当晚发现缺口。所以依据是"今天的日K在不在"，钟点只用来区分
/// 尺子不在时是"还没到时候"还是"出问题了"。
/// </summary>
public sealed class SqliteMoneyFlowDayAudit(string dbPath)
{
    /// <summary>允许差几只。实测差值只有 0/+1 两种，取 2 留一格余量。</summary>
    public const int Tolerance = 2;

    /// <summary>当天个股日K达到在市名册的这个比例，才认为"日K已经到位、期望值可信"。
    /// 实测 5,550/5,565 = 99.7%：停牌股当天既没有K线也没有资金流，两边同时缺、正好抵消。</summary>
    public const double BarsReadyRatio = 0.99;

    private const string DayFormat = "yyyy-MM-dd";

    /// <summary>
    /// 查"最近一个该有数据的交易日"的分档资金流齐不齐。
    ///
    /// 查的是**日历上最近一个已过去的交易日**而不是"今天"：周末和节假日也该能看出周五那天齐没齐
    /// （数据源那时还给得出周五的快照，补得回来）。交易日历走 <c>TradingDay</c> 表（深交所官方），
    /// 不拿上证指数K线当锚——那样会依赖"日更跑到哪一步了"，而这个判据要能随时问。
    ///
    /// 最近那天要是**今天、还没收盘、日K也还没到位**，就退一格报上一个交易日（理由见类注释）。
    /// </summary>
    /// <param name="now">"现在"是几点，只用来判当天收没收盘；不给就是 <see cref="DateTime.Now"/>
    /// （留这个口子是为了单测能把时间摆到盘前/盘后，判据本身不该靠"跑的时候正好几点"来验）。</param>
    public MoneyFlowDayStatus Check(DateTime? now = null)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        var days = RecentTradingDays(conn, 2);
        if (days.Count == 0) return new MoneyFlowDayStatus(null, 0, 0, 0);

        int roster = Scalar(conn, "SELECT COUNT(*) FROM StockMeta WHERE COALESCE(type,'stock')='stock';");
        var status = Measure(conn, days[0], roster);

        // 尺子不在、而且那天还没到收盘确认时刻 → 今天本来就不该有数据，改看上一个交易日。
        // days[0] 不是今天时（周末/节假日）这个条件天然不成立，行为跟以前一模一样。
        if (!status.BarsReady && days.Count > 1 && !IsClosed(days[0], now ?? DateTime.Now))
            status = Measure(conn, days[1], roster);

        return status;
    }

    /// <summary>某一天到没到"收盘确认"时刻——过去的日子天然成立。</summary>
    private static bool IsClosed(string day, DateTime now) =>
        now >= DateTime.ParseExact(day, DayFormat, CultureInfo.InvariantCulture)
            .AddHours(IncrementalWindowCalculator.MarketCloseHour);

    /// <summary>量某一个交易日：该有多少只（有日K的在市个股）、实有多少只。</summary>
    private static MoneyFlowDayStatus Measure(SqliteConnection conn, string day, int roster)
    {
        int expect = Scalar(conn, """
            SELECT COUNT(*) FROM Bar b JOIN StockMeta m ON m.code = b.code
            WHERE b.granularity = $g AND b.period_start >= $from AND b.period_start <= $to
              AND COALESCE(m.type,'stock') = 'stock';
            """,
            ("$g", Orchestration.MoneyFlowBackfillPlan.ExpectGranularity),
            ("$from", day + " 00:00:00"), ("$to", day + " 23:59:59"));
        int have = Scalar(conn, "SELECT COUNT(*) FROM NetInflowDetail WHERE trade_date = $d;", ("$d", day));

        return new MoneyFlowDayStatus(
            DateTime.ParseExact(day, DayFormat, CultureInfo.InvariantCulture), expect, have, roster);
    }

    /// <summary>
    /// 日历上最近一个已过去的交易日（含今天）——**快照进度记在哪一天头上**就问它
    /// （2026-09-21，跨轮续抓要用）。
    ///
    /// 为什么不能直接用"今天"：周末和节假日跑的时候数据源给的是上一个交易日的终值，
    /// 进度当然也该记在那一天头上，否则周六那一轮会另起一天、把周五攒下的页全当没抓过。
    /// 日历是空的（老库还没这张表）就返回 null，调用方按"从头抓"处理。
    /// </summary>
    public DateTime? LastTradingDay()
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var day = LastTradingDay(conn);
        return day == null ? null
            : DateTime.ParseExact(day, DayFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>日历上最近一个已过去的交易日（含今天）。日历是空的就返回 null。</summary>
    private static string? LastTradingDay(SqliteConnection conn) =>
        RecentTradingDays(conn, 1).FirstOrDefault();

    /// <summary>日历上最近的 <paramref name="count"/> 个已过去的交易日（含今天），新的在前。</summary>
    private static List<string> RecentTradingDays(SqliteConnection conn, int count)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT day FROM TradingDay WHERE day <= $t ORDER BY day DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$t", DateTime.Today.ToString(DayFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$n", count);

        var days = new List<string>(count);
        using var r = cmd.ExecuteReader();
        while (r.Read()) days.Add(r.GetString(0));
        return days;
    }

    private static int Scalar(SqliteConnection conn, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }
}

/// <summary>
/// 某个交易日分档资金流的齐整度。三个数都留着——出问题时要一眼看出是判据过严还是真缺了
/// （"期望 5550、实有 3200、名册 5565"跟"期望 0、实有 0"是完全不同的两回事）。
/// </summary>
/// <param name="Day">判的是哪个交易日；null＝本地交易日历还是空的，没法判。</param>
/// <param name="Expect">这天有日K的在市个股数。</param>
/// <param name="Have">这天 NetInflowDetail 的行数。</param>
/// <param name="Roster">在市个股名册数（用来判断 <see cref="Expect"/> 本身可不可信）。</param>
public sealed record MoneyFlowDayStatus(DateTime? Day, int Expect, int Have, int Roster)
{
    /// <summary>当天的个股日K到位了没有——没到位就不能拿 <see cref="Expect"/> 当期望。</summary>
    public bool BarsReady => Roster > 0 && Expect >= Roster * SqliteMoneyFlowDayAudit.BarsReadyRatio;

    public int Missing => Math.Max(0, Expect - Have);

    /// <summary>齐了（可以判、而且差在容差内）。</summary>
    public bool IsComplete => Day.HasValue && BarsReady && Missing <= SqliteMoneyFlowDayAudit.Tolerance;

    /// <summary>
    /// **要标红**：能判、而且真的缺了。
    ///
    /// ⚠ 判不了（日历空、日K还没到位）不算红——那是别的项的问题，红在这一行只会误导人
    /// 去重跑一个本来没错的任务。
    /// </summary>
    public bool IsAlert => Day.HasValue && BarsReady && Missing > SqliteMoneyFlowDayAudit.Tolerance;

    /// <summary>界面/日志共用的一句话。红的时候必须说清**为什么红**：哪天、差多少、拿什么比的。</summary>
    public string Text =>
        Day is not { } d ? "本地交易日历是空的，没法核对当天齐不齐"
        : !BarsReady ? $"⚠ 无法核对：{d:MM-dd} 的个股日线只有 {Expect}/{Roster} 只，先把日K补齐"
        : Missing > SqliteMoneyFlowDayAudit.Tolerance
            ? $"⚠ {d:MM-dd} 差 {Missing} 只（有日K {Expect} 只、资金流只有 {Have} 只）"
            : $"{d:MM-dd} 已齐（{Have}/{Expect} 只）";
}
