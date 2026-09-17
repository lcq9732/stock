using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// **当日完整性体检**的判据与编排（2026-09-17 从 <c>FetchOrchestrator</c> 抽出来）。
///
/// ════ 它挡的是什么 ════
/// 数据源盘后是**逐步**更新的，请求发过去时那只票的当天数据可能还没出来——接口正常返回、
/// 只是里面没有那一天，代码算作"请求成功但无新数据"，既不报错也不进任何失败名单、更不会重试。
/// 2026-08-20 那轮 19:00 开跑的"拉取全部"，个股前复权只拿到 1773/5539 只，而界面上一切正常、
/// 失败名单是空的，用户第二天看盘才发现一半股票的"最新收盘"还停在前一天。
///
/// 判定**不看抓取过程中的统计，而是直接查库**：不管漏抓的原因是数据源没出、请求失败还是
/// 整项被跳过，查库都能一网打尽。交易日锚是上证指数最新一根日线
/// （<see cref="MarketIndexCatalog.ShanghaiCompositeSymbol"/> 就是为此存在的，指数不停牌、不退市）。
///
/// ════ 三段，按"当天期望有多确定"分 ════
/// 一套判据套所有表是不行的——期望的确定性差着量级，硬套只会天天误报，那比不报还糟。
///
///   ① **全市场逐只必有**（K线）→ 按只数对齐："上一个交易日有、这一天没有"就是漏抓。
///      个股三个口径（三条线水位线独立，缺一个不代表另外两个也缺）、**ETF**、**指数**，
///      各记各自任务的待办。
///      ⚠ ETF 和指数是 2026-09-16 才纳入的：此前那条"六位纯数字代码"的过滤把带前缀存的
///      sh510300/sh000001 全挡在外面，1,665 只 ETF 和 9 条指数当天整批没抓到也不会有任何告警。
///
///   ② **每交易日必有一批、但只数不定**（资金净流入、融资余额、龙虎榜、席位、大宗交易）
///      → 走 <see cref="SqliteDailyTableAuditor.CheckOneDay"/>：空日 / 行数远低于邻近水平 /
///      某个交易所整天没有。问题记成残缺日待办，【重新拉取失败】能直接认领。
///
///   ③ **覆盖式快照**（板块行情、总股本/流通市值）→ 库里只留最新一份、不留历史，
///      能判的只有"它停在哪天"。
///
/// 不查的：分档资金流（当天由【分档资金流快照】自己收尾核对，见 <see cref="SqliteMoneyFlowDayAudit"/>）；
/// 公告、业绩预告、股东增减持、机构调研、限售解禁这些**按事件出**的表——某天一条都没有很正常，
/// 放进来只会天天误报；"这一项连着几天没抓成"该由计划的运行记录回答，不该靠数据反推。
///
/// ════ 为什么判据和落账要分开（2026-09-17 抽类的理由）════
/// 有两个调用方：计划里的【当日完整性体检】那一项（<c>DayCompletenessTask</c>），
/// 和【重新拉取失败】收尾时的那次重建（补过一轮之后名单必须重算）。
/// 这个项目在"同一判据两处各写一份"上栽过（见 <see cref="SqliteAdjSeriesAuditor"/> 的由来），
/// 所以这里只出**结论**（<see cref="DayFinding"/>），怎么写进 manifest 由 <see cref="Apply"/> 统一做，
/// 什么时候落盘由调用方决定。
/// </summary>
public sealed class SqliteDayCompletenessAuditor(string dbPath)
{
    /// <summary>交易日锚往回取多少天。近两个月足够拿到最后两根交易日（含长假），
    /// 也够第②段判"该有数据的那天"往前数几天。</summary>
    private const int AnchorLookbackDays = 60;

    /// <summary>
    /// 体检一轮，**一段一批**产出结论（① K线 → ② 日更表 → ③ 覆盖式快照）。
    ///
    /// 分段产出不是为了好看：任务框架按批落账、按批判断要不要收尾，
    /// 段与段之间才是能停下来的地方（取消只在单元之间生效，不打断进行中的单元）。
    ///
    /// 交易日锚拿不到（本地上证指数日线不足两根）时**一批都不产出**，
    /// <paramref name="tradingDay"/> 回 null——那是"判不了"，不是"当天全齐"。
    /// </summary>
    public IEnumerable<IReadOnlyList<DayFinding>> RunSegments(out DateTime? tradingDay)
    {
        var bars = new SqliteBarRepository(dbPath);
        var anchor = bars.Query(MarketIndexCatalog.ShanghaiCompositeSymbol, Granularity.Day,
                                DateTime.Today.AddDays(-AnchorLookbackDays), null);
        if (anchor.Count < 2)
        {
            tradingDay = null;
            return [];
        }

        var days = anchor.Select(b => b.PeriodStart.Date).ToList();
        tradingDay = days[^1];
        return Segments(bars, days);
    }

    private IEnumerable<IReadOnlyList<DayFinding>> Segments(SqliteBarRepository bars, List<DateTime> days)
    {
        yield return CheckBars(bars, days[^1], days[^2]);
        yield return CheckDailyTables(days);
        yield return CheckSnapshotLag(days);
    }

    /// <summary>
    /// 第①段：K线按**标的类型**查。个股三个口径并成一份名单（重补时已经齐了的那条线会在
    /// 水位线判定里直接跳过、不发请求），ETF 和指数各自一份——它们只有前复权一路，
    /// 名单分开记，【重新拉取失败】那边才不会拿 ETF 去跑后复权白发一轮请求。
    /// </summary>
    private List<DayFinding> CheckBars(SqliteBarRepository bars, DateTime latest, DateTime previous)
    {
        var stock = bars.GetCodesMissingDay(Granularity.Day, latest, previous)
            .Union(bars.GetCodesMissingDay(Granularity.DayHfq, latest, previous))
            .Union(bars.GetCodesMissingDay(Granularity.DayRaw, latest, previous))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        var etf = bars.GetCodesMissingDay(Granularity.Day, latest, previous, SqliteStockMetaUpsert.TypeEtf);
        // ETF 的不复权是 2026-09-17 才有的第二条线（【ETF日K·不复权】，回测序列的输入），
        // **单独记单独补**：它跟前复权是两个任务、两条水位线，缺一个不代表另一个也缺。
        // 不加这一条的话，ETF 的 day_raw 哪天整批没抓到照样没人报——跟当初 ETF 整个漏在
        // 体检之外是同一类缺口。
        var etfRaw = bars.GetCodesMissingDay(Granularity.DayRaw, latest, previous, SqliteStockMetaUpsert.TypeEtf);
        var index = bars.GetCodesMissingDay(Granularity.Day, latest, previous, SqliteStockMetaUpsert.TypeIndex);

        return
        [
            BarFinding("个股日K(三口径)", stock, RetryTaskIds.StockDayBars, latest),
            BarFinding("ETF日K", etf, RetryTaskIds.EtfBars, latest),
            BarFinding("ETF日K·不复权", etfRaw, RetryTaskIds.EtfRawBars, latest),
            BarFinding("指数日K", index, RetryTaskIds.IndexBars, latest),
        ];
    }

    private static DayFinding BarFinding(string label, List<string> missing, string taskId, DateTime day)
        => new(label,
            IsBad: missing.Count > 0,
            Summary: missing.Count == 0 ? $"{label}齐" : $"{label}缺 {missing.Count} 只",
            Detail: missing.Count == 0 ? null
                : $"{label}：{day:yyyy-MM-dd} 还有 {missing.Count} 只没有日线"
                  + "——多半是数据源盘后还没更新到它们（不是抓取失败），"
                  + "已记入待重试名单，点【重新拉取失败】补上",
            // 名单每次体检都**重建**、不累加：一只票今天补上了，名单里就该没有它。
            Todo: DayTodoAction.RebuildMissingDay, TaskId: taskId, Day: day, Codes: missing);

    /// <summary>
    /// 第②段：日更表的当天。判据本体在 <see cref="SqliteDailyTableAuditor.CheckOneDay"/>，
    /// 跟【全库数据体检】用的是同一套——两处各写一份迟早漂移。
    ///
    /// "该有数据的那天"不一定是最新交易日：融资余额是交易所 T+1（见 Spec.LagDays），
    /// 而【龙虎榜席位】在 <c>DailyOrder</c> 里排在**整组最末**（首轮要几小时），体检跑的时候
    /// 它今天那批还没抓，查当天必然误报，所以也往前挪一天。
    /// </summary>
    private List<DayFinding> CheckDailyTables(List<DateTime> days)
    {
        var auditor = new SqliteDailyTableAuditor(dbPath);
        var found = new List<DayFinding>();

        foreach (var spec in SqliteDailyTableAuditor.DailyTables)
        {
            // 分档资金流不在这儿查：它的窗口只有一个交易日，等到日更末尾才发现就晚了，
            // 所以那一项在自己收尾时就回查库（见 SqliteMoneyFlowDayAudit）。
            if (spec.SkipDayCheck) continue;

            // 排在体检之后的那几项，今天那批还没抓，查当天必然误报——往前挪一个交易日。
            int lag = spec.LagDays + (spec.RunsAfterDayCheck ? 1 : 0);
            int idx = days.Count - 1 - lag;
            if (idx < 0) continue;
            var day = days[idx];

            var check = auditor.CheckOneDay(spec, MarketIndexCatalog.ShanghaiCompositeSymbol, day);
            // null＝判不了（表还不存在、本地还没抓过这类数据、日历里没有这一天）。
            // 判不了不是告警——报出来只会让人以为出了问题。
            if (check == null) continue;

            found.Add(check.IsBad
                ? new DayFinding(spec.Label, IsBad: true,
                    Summary: $"{spec.Label}不齐",
                    Detail: $"{check.Text}——{spec.HowToFill}",
                    Todo: spec.OwnerTaskId.Length > 0 ? DayTodoAction.AddPartialDay : DayTodoAction.None,
                    TaskId: spec.OwnerTaskId, Day: day)
                // 体检既是发现者也是复查者——判据就是同一个方法，所以这天要是补齐了，
                // 它自己就该把单子撤掉。不撤的话待办会一直挂着，人白跑一轮【重新拉取失败】
                // （龙虎榜那种按天重抓是真发请求的）。只撤这一天，历史残缺日一个不动。
                : new DayFinding(spec.Label, IsBad: false,
                    Summary: $"{spec.Label}齐", Detail: null,
                    Todo: spec.OwnerTaskId.Length > 0 ? DayTodoAction.RemovePartialDay : DayTodoAction.None,
                    TaskId: spec.OwnerTaskId, Day: day));
        }
        return found;
    }

    /// <summary>
    /// 第③段：覆盖式快照表停在哪天。这类表库里只留最新一份，"每天多少行"那套判据对它们不适用，
    /// 能判的只有落后没落后——而"板块行情停在三天前"这种失效，界面上一点都看不出来，
    /// 热度页和板块指数合成却在拿三天前的涨跌幅算。
    /// </summary>
    private List<DayFinding> CheckSnapshotLag(List<DateTime> days)
    {
        var auditor = new SqliteDailyTableAuditor(dbPath);
        var latest = days[^1];
        var found = new List<DayFinding>();

        foreach (var spec in SqliteDailyTableAuditor.SnapshotTables)
        {
            var at = auditor.LatestDayOf(spec);
            if (at == null) continue;   // 本地还没抓过这类数据

            int behind = days.Count(d => d > at.Value.Date);
            found.Add(behind <= spec.LagDays
                ? new DayFinding(spec.Label, IsBad: false, Summary: $"{spec.Label}齐", Detail: null)
                : new DayFinding(spec.Label, IsBad: true,
                    Summary: $"{spec.Label}落后 {behind} 天",
                    Detail: $"{spec.Label}：库里停在 {at:yyyy-MM-dd}，比最新交易日 {latest:yyyy-MM-dd} "
                          + $"落后 {behind} 个交易日——{spec.HowToFill}"));
        }
        return found;
    }

    /// <summary>
    /// 把一批结论写进 manifest（**只改内存里的对象，不落盘**——落盘时机归调用方）。
    ///
    /// 三种写法对应三种语义，别合并：
    ///   · K线缺口 **重建**：今天补上的票就该从名单里消失；
    ///   · 残缺日 **并入**：同一张单子上还躺着全库体检查出的历史缺口，
    ///     整体替换会把它们连同各自的 Tries 一起抹掉，那些天就再也没人补了；
    ///   · 残缺日 **撤单**：只撤查过的这一天。
    /// </summary>
    public static void Apply(Manifest manifest, IEnumerable<DayFinding> findings)
    {
        foreach (var f in findings)
        {
            switch (f.Todo)
            {
                case DayTodoAction.RebuildMissingDay when f.Day is { } day:
                    var codes = f.Codes ?? [];
                    manifest.SetTodo(f.TaskId, RetryTodoKind.MissingDay,
                        codes.Select(c => new RetryTarget { Code = c }).ToList(),
                        codes.Count > 0 ? day : null);
                    break;

                case DayTodoAction.AddPartialDay when f.Day is { } day:
                {
                    var targets = manifest.Todo(f.TaskId, RetryTodoKind.PartialDay)?.Targets.ToList() ?? [];
                    if (targets.Any(t => t.Day?.Date == day.Date)) break;   // 已经记着了，别把 Tries 清零
                    targets.Add(new RetryTarget { Day = day });
                    manifest.SetTodo(f.TaskId, RetryTodoKind.PartialDay, targets);
                    break;
                }

                case DayTodoAction.RemovePartialDay when f.Day is { } day:
                {
                    var todo = manifest.Todo(f.TaskId, RetryTodoKind.PartialDay);
                    if (todo == null) break;
                    var kept = todo.Targets.Where(t => t.Day?.Date != day.Date).ToList();
                    if (kept.Count == todo.Targets.Count) break;   // 本来就没记着这一天
                    manifest.SetTodo(f.TaskId, RetryTodoKind.PartialDay, kept);
                    break;
                }
            }
        }
    }
}

/// <summary>体检结论要对 manifest 做什么。</summary>
public enum DayTodoAction
{
    None,
    /// <summary>重建这个任务的"当天日线还缺着"名单（空名单＝清空）。</summary>
    RebuildMissingDay,
    /// <summary>把这一天并进残缺日待办。</summary>
    AddPartialDay,
    /// <summary>这一天已经齐了，从残缺日待办里撤掉。</summary>
    RemovePartialDay,
}

/// <summary>
/// 一条体检结论。**齐的也产出**（<see cref="IsBad"/>＝false）：汇总行要把当天全貌一次说完，
/// 而且"齐了"本身就是撤单的依据。
/// </summary>
/// <param name="Label">这一项叫什么（"ETF日K"、"融资余额"）。</param>
/// <param name="Summary">汇总行里的短语（"ETF日K缺 1 只"）。</param>
/// <param name="Detail">⚠ 明细：缺了什么、拿什么比的、该跑哪一项。只有 <see cref="IsBad"/> 时才有。</param>
/// <param name="TaskId">待办归谁（<see cref="RetryTaskIds"/> 的取值）。</param>
/// <param name="Codes">K线缺口的代码名单。</param>
public sealed record DayFinding(
    string Label, bool IsBad, string Summary, string? Detail,
    DayTodoAction Todo = DayTodoAction.None, string TaskId = "",
    DateTime? Day = null, IReadOnlyList<string>? Codes = null);
