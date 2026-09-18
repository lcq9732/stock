using System.Diagnostics;
using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【股票名册与流通市值】（2026-09-18 从编排器迁到新框架）。见 doc/index-roster-task-design.md。
///
/// ════ 一批 ＝ 一轮 ════
/// 这一项不是逐只查：默认实现（<c>SinaListMarketCapFetcher</c>）一次扫回全市场列表，
/// 顺带带出每只的流通市值。于是：
///   · <b>"失败"的粒度是一轮</b>——扫描失败就把这批代码整体记进待办，语义是"有一轮要重来"，
///     不是"这些票各自失败了"。界面上也按"1 轮"报，别改成按只数。
///   · <c>MaxItems</c>/<c>Deadline</c> 对这一项没意义（骨架照常支持，只是用不上）。
///   · <b>顺带发现新股</b>：扫描天然会看到本地还不知道的代码，直接写进名册，不额外发请求。
///
/// ════ as_of_date 记的是"值属于哪个交易日"，不是"哪天跑的" ════
/// 市值接口只给"当下"的快照、不带日期，而快照的基准价在盘前/周末/节假日是**上一个交易日的收盘**。
/// 一律写 <c>DateTime.Today</c> 的话，周六跑一次就会凭空多出一行"周六的市值"。
/// 判法见 <see cref="ResolveAsOfDate"/>——2026-09-18 起先问本地交易日历，问不出才抓指数。
/// </summary>
public sealed class RosterMarketCapTask(
    IMarketCapFetcher marketCap,
    IFundamentalMetricRepository fundamentals,
    ITradingDayRepository tradingDays,
    IStockListProvider stockList,
    IBarDataFetcher anchorFetcher,
    IManifestStore manifestStore,
    FetchPaths paths) : FetchTaskBase<RosterMarketCapTask.Scan>
{
    public override FetchActionId Id => FetchActionId.StepRoster;

    /// <summary>待办只有"整轮重来"一类（<c>round</c>），补法就是再扫一轮。</summary>
    public override bool HandlesBacklog => true;

    /// <summary>日历最近这么多天内没有交易日，就判定它没跟上、不能用来定日期。
    /// 15 天足够覆盖春节这种最长连休。</summary>
    public const int CalendarStaleDays = 15;

    /// <summary>一轮扫描的结果。</summary>
    public sealed record Scan(MarketCapFetchResult Result, DateTime AsOfDate);

    private readonly Stopwatch _sw = new();
    private readonly List<string> _errors = [];

    private int _rows, _newStocks;
    private bool _rosterRefreshed, _scanFailed;
    private DateTime _asOfDate;
    private List<string> _codes = [];

    protected override async IAsyncEnumerable<IReadOnlyList<Scan>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _errors.Clear();
        _rows = _newStocks = 0;
        _rosterRefreshed = _scanFailed = false;
        _sw.Restart();

        // 读本地名册/待办都是同步 IO——骨架不替子类推线程池。
        //
        // ⚠ 这一批代码有**两个用途**：喂给市值扫描判断"谁是新发现的"，以及收尾时当作
        //   "本轮碰过的"去更新 round 待办。所以【只补待办】模式下必须取**待办里那批**——
        //   取当前名册的话，待办里那些代码（可能早就不在名册里了）永远移不出去，
        //   待办就再也不会清空。2026-09-18 实机验证时就是这么暴露的。
        _codes = await Task.Run(() => args.Mode == FetchMode.FillBacklog
            ? (manifestStore.Load().Todo(RetryTaskIds.Roster, RetryTodoKind.Round)?.Targets ?? [])
                .Select(t => t.Code).Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.Ordinal).ToList()
            : SqliteStockMetaUpsert.GetAll(paths.CurrentDb).Select(s => s.Code).ToList(), ct);

        Report(args.Mode == FetchMode.FillBacklog
            ? "股票名册与流通市值：上一轮整轮扫描失败，这一轮重来（名单里那批代码是当时那批的全体，不是逐只失败）..."
            : "正在扫描全市场股票列表以刷新流通市值（顺带发现新股），会比较慢...");

        MarketCapFetchResult? result = null;
        try
        {
            result = await marketCap.GetMarketCapsAsync(_codes, ProgressSink, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _scanFailed = true;
            _errors.Add($"获取流通市值失败：{ex.Message}");
            Report($"⚠ 获取流通市值失败（不影响别的项）：{ex.Message}");
        }

        if (result == null) yield break;

        _asOfDate = await ResolveAsOfDate(result.QuotesAreLive, ct);
        yield return [new Scan(result, _asOfDate)];
    }

    /// <summary>一批＝一轮：写市值 + 刷名册 + 收新股。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<Scan> batch, CancellationToken ct)
    {
        foreach (var (result, asOf) in batch)
        {
            var fetchedAt = DateTime.Now;
            fundamentals.Upsert(result.Entries.Select(e => new FundamentalMetric
            {
                Code = e.Code,
                MetricKey = MetricKeys.CirculatingMarketCap,
                AsOfDate = asOf,
                Value = e.CirculatingMarketCap,
                Source = marketCap.GetType().Name,
                FetchedAt = fetchedAt,
            }));
            _rows += result.Entries.Count;
            Report($"流通市值写入完成，共 {result.Entries.Count} 条，归到交易日 {asOf:yyyy-MM-dd}");

            // 扫描把全市场的代码+名称都带回来了（新浪列表实现）——直接整体刷新名册：
            // 新股顺带入库、改过名的也跟着更新，省掉单独再扫一遍列表接口的约 55 个请求。
            if (result.AllStocks is { Count: > 0 } allStocks)
            {
                SqliteStockMetaUpsert.Upsert(paths.CurrentDb, allStocks);
                _rosterRefreshed = true;
            }

            if (result.NewlyDiscoveredCodes.Count > 0)
            {
                // 名册没被整体刷新时（逐只查询式的市值实现）才需要单独把新股写进去
                if (!_rosterRefreshed) SqliteStockMetaUpsert.Upsert(paths.CurrentDb, result.NewlyDiscoveredCodes);
                _newStocks = result.NewlyDiscoveredCodes.Count;
                Report($"发现本地股票表里没有的新股票 {_newStocks} 只，已加入本地列表");
            }
        }
        return Task.CompletedTask;
    }

    protected override async Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        // 这个市值实现看不到全市场名单（逐只查询式），只好单独再取一次名册。
        // ⚠ 默认实现走不到这里，但别因此删掉——换回逐只实现时名册就没人刷了。
        if (!_scanFailed && !_rosterRefreshed)
        {
            Report("市值来源给不出全市场名单，单独获取一次股票列表...");
            try
            {
                var stocks = await stockList.GetAllStocksAsync(ProgressSink, ct);
                SqliteStockMetaUpsert.Upsert(paths.CurrentDb, stocks.Select(s => (s.Code, s.Name)));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _errors.Add($"单独获取股票列表失败：{ex.Message}"); }
        }

        SaveRoundTodo();

        int total = SqliteStockMetaUpsert.GetAll(paths.CurrentDb).Count;
        var summary = _scanFailed
            ? $"股票名册与流通市值：整轮扫描失败，已记进待办（下次点【重新拉取失败】重来一轮）。"
            : $"名册已刷新，本地个股 {total} 只；流通市值 {_rows} 条（归到 {_asOfDate:yyyy-MM-dd}）"
              + (_newStocks > 0 ? $"，新发现 {_newStocks} 只" : "")
              + $"，用时 {ElapsedText.Format(_sw.Elapsed)}。";
        Report(summary);

        return _scanFailed
            ? new TaskRunResult(TaskState.Failed, _errors, NothingToDo: false, summary)
            : new TaskRunResult(TaskState.Completed, _errors, NothingToDo: false, summary);
    }

    /// <summary>
    /// 这批快照属于哪个交易日。
    ///
    /// ① <paramref name="quotesAreLive"/>＝true（扫描时全市场大多数股票都有最新价 → 今天已开盘）
    ///    → 值就是当日行情，记今天。**盘中跑属于这一档**：日期是对的，只是值还不是收盘价，
    ///    收盘后再跑一次会被更准的值覆盖（Upsert 同 as_of_date 覆盖）。
    /// ② 否则（盘前、周末、节假日）→ 快照的基准价是**上一个交易日的收盘**：
    ///    先问**本地交易日历**（2026-09-18 起，稳态下一个请求都不用发），
    ///    问不出再退回抓上证指数日线（原来的做法）。
    ///
    /// ⚠ 日历"问不出"包括**过期**，不只是"为空"：日历自己缺哪段就瞎哪段
    /// （project_trading_calendar_pitfall 栽过一次）。所以判据是"最近
    /// <see cref="CalendarStaleDays"/> 天内有没有交易日"——没有就说明它没跟上，别信它。
    ///
    /// 两条都失败就回退到今天并在日志里说明：市值本身已经抓到了，不值得为了日期把整项判失败；
    /// 回退的后果就是退回更早的老行为（可能多出一行非交易日的市值），不会丢数据。
    /// </summary>
    private async Task<DateTime> ResolveAsOfDate(bool? quotesAreLive, CancellationToken ct)
    {
        var today = DateTime.Today;
        if (quotesAreLive == true) return today;

        // ① 本地交易日历
        try
        {
            var from = DateOnly.FromDateTime(today.AddDays(-CalendarStaleDays));
            var days = tradingDays.GetBetween(from, DateOnly.FromDateTime(today));
            if (days.Count > 0)
            {
                var latest = days.Max().ToDateTime(TimeOnly.MinValue);
                if (latest != today)
                    Report($"当前不在交易时段，流通市值快照归到上一个交易日 {latest:yyyy-MM-dd}（本地交易日历）");
                return latest;
            }
            Report($"⚠ 本地交易日历最近 {CalendarStaleDays} 天里没有交易日——它多半没跟上，"
                 + "这一轮改用上证指数日线判交易日（跑一次【交易日历】就能省掉这个请求）。");
        }
        catch (Exception ex)
        {
            Report($"读交易日历失败（{ex.Message}），改用上证指数日线判交易日。");
        }

        // ② 退路：抓上证指数最近几根日线，最新那根的日期就是最近的交易日。
        //    用指数是因为它不停牌、不退市，永远有最新一根。
        try
        {
            var (_, bars) = await anchorFetcher.FetchAsync(
                MarketIndexCatalog.ShanghaiCompositeSymbol, Granularity.Day, today.AddDays(-15), today, ct);
            var latest = bars.Count > 0 ? bars[^1].PeriodStart.Date : default;
            if (latest != default && latest <= today)
            {
                if (latest != today)
                    Report($"当前不在交易时段，流通市值快照归到上一个交易日 {latest:yyyy-MM-dd}（上证指数日线）");
                return latest;
            }
            Report($"未能从上证指数日线判断最新交易日（返回 {bars.Count} 根），流通市值按今天记");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Report($"判断最新交易日失败（不影响市值抓取，按今天记）：{ex.Message}");
        }
        return today;
    }

    /// <summary>
    /// 整轮扫描的"失败名单"：**成功就整批移出，失败就整批记进去**。
    /// 粒度是一轮，不是逐只——这份名单只是用来标记"有一轮要重来"。
    /// </summary>
    private void SaveRoundTodo()
    {
        if (_codes.Count == 0 && !_scanFailed) return;
        var manifest = manifestStore.Load();
        var current = (manifest.Todo(RetryTaskIds.Roster, RetryTodoKind.Round)?.Targets ?? [])
            .Select(t => t.Code).ToList();
        var stillFailed = new HashSet<string>(current, StringComparer.Ordinal);
        stillFailed.ExceptWith(_codes);
        if (_scanFailed) stillFailed.UnionWith(_codes);
        manifest.SetTodo(RetryTaskIds.Roster, RetryTodoKind.Round,
                         stillFailed.OrderBy(c => c, StringComparer.Ordinal)
                                    .Select(c => new RetryTarget { Code = c }).ToList());
        manifestStore.Save(manifest);
    }
}
