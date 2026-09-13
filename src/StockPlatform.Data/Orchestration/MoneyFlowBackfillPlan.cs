using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// 分档资金流「逐股补历史」的排队判据（2026-09-11 抽出来，当天又换了一次口径）。
///
/// 抽出来是因为同一份判据有两个调用方：任务本体（决定这一轮抓哪些票）和界面上那个
/// "还有 N 只历史不全"的计数。这个项目在"同一判据两处各写一份"上栽过
/// （见 <see cref="SqliteAdjSeriesAuditor"/> 的由来）——两处漂移之后，界面显示 0 而任务
/// 仍在抓、或者反过来，谁都不会发现。
///
/// ════ 判据：跟本地K线对齐，不用固定行数门槛 ════
/// <b>期望</b>＝这只票在窗口内的本地日K根数（有K线的那天就该有资金流），
/// <b>实有</b>＝同一窗口内的资金流行数，缺口≥门槛才排队。
///
/// 换掉的是"库里不足 100 行就算没补齐"。那个口径同时错两头（2026-09-11 查实）：
///   · <b>误报</b>：上市不足 100 个交易日的次新股永远凑不出 100 行，于是每轮都被重抓。
///     实测那 72 只的资金流行数跟本地K线根数一一对应——它们其实早就齐了。一轮白烧 72 个
///     请求、19 分钟，而且每上市一只新股这个数就 +1，要等它满 100 个交易日才自己退出去。
///   · <b>漏报</b>：2026-09-09 全市场的资金流整天缺失（那天收盘后没人跑快照，而快照只给
///     "最近一个交易日"，隔天就补不回来了）。5500 只老票各缺一行，但它们行数都远超 100，
///     判据认为齐了——**真缺口反倒没人管**。
///
/// ════ ⚠ 两边都必须按同一个窗口计数 ════
/// 资金流表是**累积**的（每天快照追一行），全表行数迟早超过任何固定门槛。而接口只给最近约
/// 120 个交易日，"补齐了没有"说的从来只是那个窗口里的事。
///
/// ════ ⚠ 窗口要排除最新那个交易日 ════
/// 收盘后K线先抓到、资金流快照还没跑，中间有几个小时。把当天算进期望的话，那段时间里
/// **全市场 5900 只**会一起进队——一轮 5900 个请求、二十小时。排除一天就没这回事。
///
/// ════ 排序：最久没抓的先抓 ════
/// 2026-09-04 那个坑：按代码顺序排的话，每天零点一到又从 000001 开始，靠后的票永远轮不到。
/// 这条能继续成立，是因为快照写库时把 fetched_at 写成**行情时间**而不是"现在"——
/// 逐股抓过的票时间戳必然比它新，两者仍分得开。
/// 故意不按"缺口大小"排：某只票要是一直抓不成功（网络失败），缺口大会让它永远占着队首。
/// </summary>
public static class MoneyFlowBackfillPlan
{
    /// <summary>窗口有多少个交易日。接口给的是最近约 120 个交易日，跟它对齐。</summary>
    public const int WindowTradingDays = 120;

    /// <summary>
    /// 「增量」模式的排队门槛：缺 3 行以上才值得为它发一个请求。
    ///
    /// 为什么不是 1（用户 2026-09-11 拍板）：全市场为 09-09 那一行各补一次是
    /// 5500 只 × 约 13 秒 ≈ <b>20 小时机时</b>，只为补回一天。单日/双日缺口改成
    /// 只在日志里报一句，想补的时候把这一项的模式切成「首次整段回补」跑一轮。
    /// </summary>
    public const int DefaultGapThreshold = 3;

    /// <summary>
    /// 一轮最多问多少只（2026-09-12 用户定）。
    ///
    /// 为什么要有它、为什么这么小：push2his 是全库限流最凶的接口——**累计 16~35 个请求就被切**
    /// （跟速率无关，实测 2.1 秒/个撑了 16 个、3.0 秒/个撑了 33 个），所以 provider 配的是
    /// 5 秒/只 + 每 15 只主动歇 2 分钟。一轮要是不限量，5000 多只就是十几个小时，
    /// 这期间别的任务全得让路；而这份数据**没有时效压力**（120 天窗口内随时补），
    /// 慢慢补十天半个月完全可以接受。
    ///
    /// 30 只＝两个主动歇的批次、约 5 分钟，跟【财务报表】每轮 300 只是同一个形状
    /// （见 FinancialFetchPlanner.MaxPerRun），只是因为这个接口更凶所以数字小一个量级。
    /// 调度给了 <see cref="StockPlatform.Scheduling.Tasks.TaskRunArgs.MaxItems"/> 时以调度的为准。
    /// </summary>
    public const int MaxPerRun = 30;
    /// <summary>「首次整段回补」模式的门槛：缺一行就补（跨轮慢慢补，Deadline/MaxItems 兜着）。</summary>
    public const int BackfillGapThreshold = 1;

    /// <summary>本地已知的个股代码（<c>type='stock'</c>，指数/ETF/板块/退市股不在内）。</summary>
    public static List<string> LocalStockCodes(string dbPath)
        => SqliteStockMetaUpsert.GetAll(dbPath).Select(s => s.Code).ToList();

    /// <summary>
    /// 排出本轮的队。
    /// </summary>
    /// <param name="dbPath">库文件——K线期望和交易日窗口都从这儿查。</param>
    /// <param name="codes">本地个股名册（<see cref="LocalStockCodes"/>）。</param>
    /// <param name="repository">资金流仓储，用来数窗口内已有多少行。</param>
    /// <param name="gapThreshold">缺几行才排队（<see cref="DefaultGapThreshold"/> /
    /// <see cref="BackfillGapThreshold"/>）。</param>
    public static MoneyFlowBackfillQueue Build(
        string dbPath, IReadOnlyCollection<string> codes,
        INetInflowDetailRepository repository, int gapThreshold)
    {
        // 显式判 null：`is not var (a, b)` 那种写法永远为 false（var 模式总匹配），编译得过、判据失效。
        var window = ResolveWindow(dbPath);
        if (window == null)
            return new MoneyFlowBackfillQueue([], 0, 0, 0, null, null,
                "本地交易日历不足两天，排不出补历史的窗口——请先跑一次【交易日历】");
        var (from, to) = window.Value;

        var expected = new SqliteBarRepository(dbPath).CountByCodeBetween("day", from, to);
        var have = repository.GetRowCountByCode(from, to);
        var lastFetched = repository.GetLastFetchedAt();

        var todo = new List<string>();
        int never = 0, minorCodes = 0, minorRows = 0;
        foreach (var code in codes)
        {
            // 本地一根K线都没有＝还没抓过这只票的日线。期望 0、不排队：那时候去问资金流
            // 只会拿回一堆没法对账的行，等K线到了它自然进队。
            int want = Math.Min(WindowTradingDays, expected.TryGetValue(code, out var e) ? e : 0);
            int got = have.TryGetValue(code, out var h) ? h : 0;
            // ⚠ 只数**该有却一行都没有**的（2026-09-11 实机发现）：本地连日K都没有的票
            // 期望是 0、压根不排队，把它们也算进来的话，日志会出现"待补 2 只，其中 22 只
            // 一行都没有"这种自相矛盾的话。
            if (want > 0 && got == 0) never++;

            int gap = want - got;
            if (gap <= 0) continue;
            if (gap >= gapThreshold) todo.Add(code);
            else { minorCodes++; minorRows += gap; }
        }

        todo.Sort((a, b) =>
            (lastFetched.TryGetValue(a, out var ta) ? ta : DateTime.MinValue)
            .CompareTo(lastFetched.TryGetValue(b, out var tb) ? tb : DateTime.MinValue));

        return new MoneyFlowBackfillQueue(todo, never, minorCodes, minorRows, from, to, null);
    }

    /// <summary>
    /// 窗口＝最近 <see cref="WindowTradingDays"/> 个交易日，<b>排除最新那一个</b>（理由见类注释）。
    /// 交易日历不足两天时返回 null。
    /// </summary>
    private static (DateTime From, DateTime To)? ResolveWindow(string dbPath)
    {
        var days = new SqliteTradingDayRepository(dbPath).GetAll();
        var today = DateTime.Today;
        var past = days.Where(d => d <= today).ToList();
        if (past.Count < 2) return null;

        // 去掉最新那一个交易日，再往前取 120 个
        var usable = past.Take(past.Count - 1).ToList();
        var window = usable.Skip(Math.Max(0, usable.Count - WindowTradingDays)).ToList();
        return (window[0], window[^1]);
    }
}

/// <summary>
/// 排队结果。
/// </summary>
/// <param name="Todo">本轮待补的票，已按"最久没抓的先抓"排好。</param>
/// <param name="Never">窗口内一行资金流都没有的只数（界面上"从没抓过"那个数）。</param>
/// <param name="MinorCodes">有缺口、但没到门槛所以不补的只数。</param>
/// <param name="MinorRows">那些小缺口合计多少行——这个数要报出来，不然"待办 0"会被读成"一行不缺"。</param>
/// <param name="From">窗口起（含）。</param>
/// <param name="To">窗口止（含）。</param>
/// <param name="Unavailable">排不出队的原因（交易日历还没建起来）；能排就是 null。</param>
public sealed record MoneyFlowBackfillQueue(
    List<string> Todo, int Never, int MinorCodes, int MinorRows,
    DateTime? From, DateTime? To, string? Unavailable);
