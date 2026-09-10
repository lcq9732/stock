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
    /// </summary>
    public sealed record Plan(
        IReadOnlyList<string> Codes,
        IReadOnlyDictionary<string, DateTime> AdjLatest,
        IReadOnlyDictionary<string, DateTime> AdjEarliest,
        HashSet<string> StaleEvents)
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
    /// </summary>
    public Plan BuildPlan()
    {
        var repo = new SqliteBarRepository(_dbPath);
        var rawLatest = repo.GetLatestPeriodStartByCode(Granularity.DayRaw);
        var adjLatest = repo.GetLatestPeriodStartByCode(Granularity.DayAdj);
        var rawEarliest = repo.GetEarliestPeriodStartByCode(Granularity.DayRaw);
        var adjEarliest = repo.GetEarliestPeriodStartByCode(Granularity.DayAdj);
        var staleEvents = CodesWithStaleEvents();
        var fresherRaw = CodesWithFresherRawBars();

        var codes = rawLatest
            .Where(kv => !adjLatest.TryGetValue(kv.Key, out var a) || a.Date < kv.Value.Date
                      || !adjEarliest.TryGetValue(kv.Key, out var ae)
                      || (rawEarliest.TryGetValue(kv.Key, out var re) && ae.Date > re.Date)
                      || staleEvents.Contains(kv.Key)
                      || fresherRaw.Contains(kv.Key))
            .Select(kv => kv.Key)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        return new Plan(codes, adjLatest, adjEarliest, staleEvents);
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
