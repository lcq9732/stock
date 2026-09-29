using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 回测序列（<c>day_adj</c>）的"哪些票要重算"判据。2026-09-09 从 <c>FetchOrchestrator</c> 抽出来，
/// 两个理由：
///   ① 判据原来在那里**写了两遍**——`GetPendingAdjRebuildCount`（界面上的"待重算 N 只"）和
///      `RunRebuildAdjSeriesAsync` 里的 todo 计算各写一份，改一处漏一处就是静默不一致；
///   ② 【全库数据体检】迁成独立任务（见 doc/full-audit-task-migration-design.md）之后也要用它，
///      不能再依赖 orchestrator 的私有方法。
/// </summary>
public sealed class SqliteAdjSeriesAuditor
{
    private readonly string _dbPath;

    public SqliteAdjSeriesAuditor(string dbFilePath) => _dbPath = dbFilePath;

    /// <summary>
    /// 要重算的票（按代码有序）+ 判据用到的中间结果。
    ///
    /// 中间结果**必须带出来**：调用方（<c>RunRebuildAdjSeriesAsync</c>）逐只决定"能不能只补增量、
    /// 还是要整段重算"时还要用 <see cref="AdjLatest"/>/<see cref="AdjEarliest"/>/<see cref="StaleEvents"/>，
    /// 不带出去它就得再对 1300 万行的 Bar 表 GROUP BY 三遍。
    ///
    /// <see cref="RewrittenRows"/>：已算过的日子里 day_raw 被改写过的票，**不许走增量**
    /// （见 <see cref="CodesWithRewrittenRows"/>）。只有任务那条路会查它，界面计数给空集。
    /// </summary>
    public sealed record Plan(
        IReadOnlyList<string> Codes,
        IReadOnlyDictionary<string, DateTime> AdjLatest,
        IReadOnlyDictionary<string, DateTime> AdjEarliest,
        HashSet<string> StaleEvents,
        HashSet<string> RewrittenRows)
    {
        /// <summary>其中因为**除权事件更新**才要重算的只数（那一项要单独报给用户看，
        /// 见 <see cref="CodesWithStaleEvents"/> 里的代价说明）。</summary>
        public int StaleEventCount => StaleEvents.Count;
    }

    /// <summary>
    /// 判据五条，任一成立就要重算：
    ///   · 压根没算过；
    ///   · 不复权比它**新**（尾巴长出来了）；
    ///   · 不复权比它**长**（前面补了历史）——复权因子是从最早那天累乘上来的，起点一变整条线都变；
    ///   · 除权事件本身变了（<see cref="CodesWithStaleEvents"/>）；
    ///   · **不复权的值被改过**（<see cref="CodesWithFresherRawBars"/>，2026-09-10 补）。
    ///
    /// <paramref name="checkRewrittenRows"/>：再逐行查一遍 <see cref="CodesWithRewrittenRows"/>
    /// （全表 JOIN，生产库约 30 秒）。只有【重算回测序列】本身传 true——界面计数每项任务跑完都刷新一次，
    /// 扛不起这 30 秒；而它查出的票绝大多数本来就被上面第五条认出来了，界面漏报的只是存量里
    /// 时间戳已被增量盖新的那些（2026-09-29 那批 324 只），计数少报一轮不伤数据。
    /// </summary>
    public Plan BuildPlan(bool checkRewrittenRows = false)
    {
        var repo = new SqliteBarRepository(_dbPath);
        var rawLatest = repo.GetLatestPeriodStartByCode(Granularity.DayRaw);
        var adjLatest = repo.GetLatestPeriodStartByCode(Granularity.DayAdj);
        var rawEarliest = repo.GetEarliestPeriodStartByCode(Granularity.DayRaw);
        var adjEarliest = repo.GetEarliestPeriodStartByCode(Granularity.DayAdj);
        var staleEvents = CodesWithStaleEvents();
        var fresherRaw = CodesWithFresherRawBars();
        var rewritten = checkRewrittenRows
            ? CodesWithRewrittenRows()
            : new HashSet<string>(StringComparer.Ordinal);

        var codes = rawLatest
            .Where(kv => !adjLatest.TryGetValue(kv.Key, out var a) || a.Date < kv.Value.Date
                      || !adjEarliest.TryGetValue(kv.Key, out var ae)
                      || (rawEarliest.TryGetValue(kv.Key, out var re) && ae.Date > re.Date)
                      || staleEvents.Contains(kv.Key)
                      || fresherRaw.Contains(kv.Key)
                      || rewritten.Contains(kv.Key))
            .Select(kv => kv.Key)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        return new Plan(codes, adjLatest, adjEarliest, staleEvents, rewritten);
    }

    /// <summary>
    /// 有不复权日线的**全部**票（按代码有序）。给「首次整段回补」模式
    /// 那条"全量重算、不看判据"的路用（2026-09-10）——FetchMode 在 Scheduling 里，Data 引用不到，
    /// 所以这里只能用名字说。
    ///
    /// 为什么不走 <see cref="BuildPlan"/>：那个要六趟 GROUP BY（raw/adj 各自的首末 + 事件 + 抓取时刻），
    /// 而全量重算压根不看判据，只需要"有哪些票"这一趟。
    /// </summary>
    public IReadOnlyList<string> AllCodesWithRawBars() =>
        new SqliteBarRepository(_dbPath)
            .GetLatestPeriodStartByCode(Granularity.DayRaw)
            .Keys
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// <c>day_raw</c> 里有行的 <c>fetched_at</c> 比 <c>day_adj</c> 的重算时刻还新——说明**源数据
    /// 被改过**（不是长出新日期，是同一天的值被覆盖了），复权序列得跟着重算。
    ///
    /// ⚠ 这条判据不能少（2026-09-10 补，踩过一次）：另外四条全看**日期范围**和**事件时间戳**，
    /// 而"修正已有行的值"这件事**一点日期都不变**。2026-09-09~10 修了 5312 只票的 day_raw
    /// （盘中固化的半天快照：002650 的 2026-09-01 从"四价合一 6.04"改成真实的
    /// 6.04/6.01/6.08/5.98），之后点【重算回测序列】，四条判据一条都不成立——只认出 2 只
    /// （还是新股），day_adj 那 5312 只票的价格仍然是从**盘中值**算出来的，而回测吃的就是它。
    ///
    /// 判的是"最新抓取时刻 vs 最新重算时刻"，所以日常也顺带成立：抓完 day_raw 还没重算的票会被
    /// 认出来（本来也该算），重算过之后 adj 的时间戳更新，下一轮不再命中。
    /// </summary>
    public HashSet<string> CodesWithFresherRawBars()
    {
        var stale = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                WITH raw AS (SELECT code, MAX(fetched_at) f FROM Bar WHERE granularity='day_raw' GROUP BY code),
                     adj AS (SELECT code, MAX(fetched_at) f FROM Bar WHERE granularity='day_adj' GROUP BY code)
                SELECT raw.code FROM raw JOIN adj ON adj.code = raw.code
                WHERE raw.f IS NOT NULL AND adj.f IS NOT NULL AND raw.f > adj.f;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read()) stale.Add(r.GetString(0));
        }
        catch { /* 判据取不到就当没有：宁可少算一轮，也不该让界面上的计数抛异常 */ }
        return stale;
    }

    /// <summary>
    /// **已经算过的日子**里 day_raw 被改写过、或者跟 day_adj 的量额换手对不上的票——
    /// 这种票必须整段重算，**不能走增量**（2026-09-29 补）。
    ///
    /// ⚠ 为什么 <see cref="CodesWithFresherRawBars"/> 不够：它只决定"要不要算"，不决定"怎么算"。
    /// 2026-09-17 盘中抓进 323 只 ETF 的 day_raw，09-18 凌晨照着算出了 day_adj；09-20 day_raw
    /// 被重抓修好，第五条判据也认出了这些票——可它们尾巴上同时长出了新日子，任务走了增量：
    /// 只追加新的几根、09-17 那行原样不动，时间戳却被盖成新的，此后五条判据**再也认不出来**。
    /// 体检报「day_adj 多口径不一致」让人去跑【重算回测序列】，而那一项修不了——死循环，
    /// 回测吃着盘中的半天量额跑了十几天。
    ///
    /// 两个条件任一成立：
    ///   · 时间戳：day_raw 某行的日期在 day_adj 已算过的范围里、而抓取时刻晚于上次重算。
    ///     已确认的行（当天 16:00 后抓的）写入路径不会刷新（见 <c>InsertOrRefreshUnconfirmed</c>），
    ///     所以这条只在"旧行真被改写"时成立，日常长尾巴不会误中；
    ///   · 值：volume/amount/turnover 是从 day_raw 原样抄过去的，逐值比。这一条兜住
    ///     时间戳已经被盖新的存量，以及改值不刷新时间戳的写入路径。
    /// </summary>
    public HashSet<string> CodesWithRewrittenRows()
    {
        var hit = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;   // 全表 JOIN，生产库约 30 秒，别让默认 30 秒超时掐掉
            cmd.CommandText = """
                WITH s AS (SELECT code, MAX(fetched_at) f FROM Bar WHERE granularity='day_adj' GROUP BY code)
                SELECT DISTINCT a.code
                FROM Bar a
                JOIN s ON s.code = a.code
                JOIN Bar b ON b.code = a.code AND b.granularity = 'day_raw' AND b.period_start = a.period_start
                WHERE a.granularity = 'day_adj'
                  AND (b.fetched_at > s.f
                       OR a.volume IS NOT b.volume
                       OR a.amount IS NOT b.amount
                       OR a.turnover IS NOT b.turnover);
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read()) hit.Add(r.GetString(0));
        }
        catch { /* 取不到就当没有：退回原来的行为（可能走增量），不该让整轮重算失败 */ }
        return hit;
    }

    /// <summary>只要个数（界面上的"待重算 N 只"）。判据跟 <see cref="BuildPlan"/> 是同一份，
    /// 取不到就当 0——界面上的计数不该让异常冒出去。</summary>
    public int PendingCount()
    {
        try { return BuildPlan().Codes.Count; }
        catch { return 0; }
    }

    /// <summary>
    /// 除权事件源（<c>Dividend</c> / <c>RightsIssue</c>）比 <c>day_adj</c> 新的那些票——它们的复权因子
    /// 是拿**旧的**除权记录算的，得重算。
    ///
    /// ⚠ 这条判据不能少（2026-09-02 补）：原来只比 day_raw 和 day_adj 的**日期范围**，
    /// 于是"价格没变、但除权记录变了"这种情况完全检测不到——
    /// 补录了一条漏掉的除权、配股方案入库、分红从"预案"变成"实施"填上了 ex_date，
    /// 全都会改变复权因子，而界面上的待办量还是 0。又是个不报错的静默错误：
    /// day_adj 静静地保持旧值，回测拿着错的收益率跑，没有任何地方会提示。
    ///
    /// **代价**：【拉取分红】是按 code 删旧写新的，跑完一轮全市场的 fetched_at 都会变新，
    /// 于是每月触发一次全量重算（5781 只，十几二十分钟）。这个代价是认的——
    /// 它纯本地、不占数据源配额、挂在「空闲时」跑，用一个月一次的机器时间换"因子永远跟事件一致"。
    /// 要更精准就得存"上次算用了哪些事件"的签名（多一张状态表），眼下不值得。
    /// </summary>
    public HashSet<string> CodesWithStaleEvents()
    {
        var stale = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            // ⚠ 这里比的是「事件抓取时刻 vs day_adj 的重算时刻」。后者从 2026-09-04 起才是真的
            // "重算时刻"——在那之前 day_adj 的 fetched_at 是从 day_raw 原样抄来的（源K线抓取
            // 时刻），跟重算没关系，于是判据只在"事件抓得比K线还晚"时碰巧成立：实测配股 09-02
            // 抓入、K线 09-03 抓取，642 只有配股的票一只都没被检出。见 AdjustFactorCalculator
            // 的 computedAt 参数。
            // 老数据的时间戳仍是旧的（偏早），只会让判据更倾向于"要重算"——偏保守，不会漏。
            //
            // RightsIssue 是后加的表，老库可能没有——用 sqlite_master 兜一下，缺表不该让整个判据失效
            bool hasRights;
            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='RightsIssue';";
                hasRights = Convert.ToInt32(probe.ExecuteScalar() ?? 0) > 0;
            }
            string events = hasRights
                ? "SELECT code, MAX(fetched_at) f FROM Dividend GROUP BY code " +
                  "UNION ALL SELECT code, MAX(fetched_at) f FROM RightsIssue GROUP BY code"
                : "SELECT code, MAX(fetched_at) f FROM Dividend GROUP BY code";
            cmd.CommandText = $"""
                WITH adj AS (SELECT code, MAX(fetched_at) f FROM Bar WHERE granularity='day_adj' GROUP BY code),
                     ev  AS ({events})
                SELECT DISTINCT ev.code FROM ev JOIN adj ON adj.code = ev.code
                WHERE ev.f IS NOT NULL AND adj.f IS NOT NULL AND ev.f > adj.f;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read()) stale.Add(r.GetString(0));
        }
        catch { /* 判据取不到就当没有：宁可少算一轮，也不该让界面上的计数抛异常 */ }
        return stale;
    }
}
