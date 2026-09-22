using System.Globalization;
using Microsoft.Data.Sqlite;

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
    /// 查"最近一个交易日"的分档资金流齐不齐。
    ///
    /// 查的是**日历上最近一个已过去的交易日**而不是"今天"：周末和节假日也该能看出周五那天齐没齐
    /// （数据源那时还给得出周五的快照，补得回来）。交易日历走 <c>TradingDay</c> 表（深交所官方），
    /// 不拿上证指数K线当锚——那样会依赖"日更跑到哪一步了"，而这个判据要能随时问。
    /// </summary>
    public MoneyFlowDayStatus Check()
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        var day = LastTradingDay(conn);
        if (day == null) return new MoneyFlowDayStatus(null, 0, 0, 0);

        int roster = Scalar(conn, "SELECT COUNT(*) FROM StockMeta WHERE COALESCE(type,'stock')='stock';");
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
    private static string? LastTradingDay(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT day FROM TradingDay WHERE day <= $t ORDER BY day DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$t", DateTime.Today.ToString(DayFormat, CultureInfo.InvariantCulture));
        return cmd.ExecuteScalar() as string;
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
