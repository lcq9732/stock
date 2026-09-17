using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【ETF日K·不复权】（2026-09-17）——给 ETF 补 <c>day_raw</c>，跟个股的
/// <c>StepStockRawBars</c> 对称。
///
/// ════ 为什么要它 ════
/// ETF 至今只有 <c>day</c>（源给的**减法式前复权**），⇒ **ETF 完全不能回测**：
/// 非除权日的收益率也是错的。同期板块指数那组实测能说明失真量级——拿东财官方板块指数当标尺，
/// 用前复权算收益率相关系数只有 0.454、平均每天差 5.88%，换成 <c>day_adj</c> 是 0.945 / 0.30%。
/// 顺带还修一处：融券余额补算对 ETF 只能回退 <c>day</c>（约 5% 偏低），有了 <c>day_raw</c> 就准了。
///
/// ════ 省掉八成请求：大多数 ETF 的 day == day_raw ════
/// 1659 只 ETF 里，东财终端的 <c>fund_cqcx</c> 只覆盖 **323 只**有过分红/折算的；
/// 其余 **1336 只从没除权过** ⇒ 它们的前复权序列**逐值等于**不复权序列
/// （159915 实测 raw vs qfq 0 天不同）。所以这些直接**从库里的 <c>day</c> 复制**，零请求。
///
/// ⚠ 但复制必须有**闸门**，否则一旦 <c>fund_cqcx</c> 漏记了某只 ETF 的事件，就会静默把错的序列
/// 复制进去——**这类错没有任何地方会报**。两处闸门，都便宜：
///
/// | 情形 | 闸门 | 代价 |
/// |---|---|---|
/// | 首次（库里还没有 day_raw） | 抓最近一页真 <c>day_raw</c>，跟库里 <c>day</c> 在重叠段**逐值比** | 1 个请求/只 |
/// | 日常增量 | 比库里 <c>day</c> 和 <c>day_raw</c> 在**同一天**的收盘价 | **0 个请求**（纯本地） |
///
/// 增量那条判据成立的道理：前复权的基准是最新价，**一旦除权，整条 <c>day</c> 历史都会变**，
/// 而 <c>day_raw</c> 不变。所以"两条序列在老的那一天上还相等"就等于"此后没发生过除权"，
/// 可以安全复制；一旦不等，说明除权了，这只就老老实实抓。
///
/// ════ 代码带前缀 ════
/// ETF 在 <c>Bar</c> 里是 <c>sh510300</c> 这种 8 位符号，写 <c>day_raw</c> 也用同一个 code——
/// 否则会漏进个股选股全集（<c>GetAllCodes</c> 认 6 位纯数字）。名单直接读库里
/// <c>StockMeta(type='etf')</c>，不再联网取一遍（那是【ETF日K】那一项的事）。
///
/// ════ 跟【导入基金除权除息】和【重算回测序列】的关系 ════
/// 三件事凑齐才有 ETF 的 <c>day_adj</c>：事件（<see cref="FundExDividendImportTask"/>）
/// + 不复权序列（本项）+ 因子计算（【重算回测序列】，**零改动**，它按"有 day_raw 的票"枚举）。
/// </summary>
public sealed class EtfRawBarTask(
    FetchPaths paths,
    SqliteBarRepository bars,
    IDividendRepository dividends,
    IBarDataFetcher fetcher) : FetchTaskBase<Bar>
{
    /// <summary>闸门那一页往回取多久——腾讯一页硬顶 640 根，700 个自然日约 480 个交易日，
    /// 稳稳落在一页内（只发一个请求）。</summary>
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromDays(700);

    /// <summary>"全历史"的起点。ETF 最早的也在 2004 年之后，1990 足够早。</summary>
    private static readonly DateTime HistoryStart = new(1990, 1, 1);

    public override FetchActionId Id => FetchActionId.StepEtfRawBars;

    private int _copied, _fetched, _probeFailed, _skipped, _rows;

    protected override async IAsyncEnumerable<IReadOnlyList<Bar>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _copied = _fetched = _probeFailed = _skipped = _rows = 0;
        bool whole = args.Mode.HasFlag(FetchMode.FirstBackfill);

        // 名单和"哪些有除权事件"都要读库——同步重活，推线程池（feedback_task_must_offload_heavy_sync）
        var (codes, withEvents) = await Task.Run(() =>
        {
            var list = SqliteStockMetaUpsert.GetByTypes(paths.CurrentDb, SqliteStockMetaUpsert.TypeEtf)
                .Select(x => x.Code)
                .Where(c => c.Length == 8)          // 只认带前缀的
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();
            var ev = list.Where(c => dividends.GetByCode(c).Any(d => d.ExDate.HasValue))
                         .ToHashSet(StringComparer.Ordinal);
            return (list, ev);
        }, ct);

        Report($"ETF {codes.Count} 只，其中 {withEvents.Count} 只有除权事件（必须抓），"
               + $"{codes.Count - withEvents.Count} 只没有（先验后复制）", 0, codes.Count);

        var today = DateTime.Today;
        int done = 0;
        foreach (var code in codes)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await OneAsync(code, withEvents.Contains(code), whole, today, ct);
            done++;
            if (done % 100 == 0 || done == codes.Count)
                Report($"{done}/{codes.Count}（抓 {_fetched} 只、复制 {_copied} 只、跳过 {_skipped} 只）",
                       done, codes.Count);
            if (batch.Count > 0) { _rows += batch.Count; yield return batch; }
        }
    }

    private async Task<IReadOnlyList<Bar>> OneAsync(
        string code, bool hasEvents, bool whole, DateTime today, CancellationToken ct)
    {
        var rawLatest = await Task.Run(() => bars.GetLatestPeriodStart(code, Granularity.DayRaw), ct);

        // ── ① 有除权事件：day != day_raw，只能抓 ──
        if (hasEvents)
        {
            var from = whole || rawLatest is null ? HistoryStart : rawLatest.Value.AddDays(1);
            if (!whole && rawLatest is not null && rawLatest.Value.Date >= today) { _skipped++; return []; }
            return await FetchAsync(code, from, today, ct);
        }

        // ── ② 没有事件 + 库里还没有 day_raw：闸门一页，过了就整段复制 ──
        if (rawLatest is null)
        {
            var probe = await FetchAsync(code, today - ProbeWindow, today, ct);
            if (probe.Count == 0) { _skipped++; return []; }

            var dayBars = await Task.Run(() => bars.Query(code, Granularity.Day), ct);
            if (dayBars.Count == 0) { _skipped++; return []; }

            if (!SameOnOverlap(dayBars, probe))
            {
                // 闸门没过：fund_cqcx 里没记这只的事件，但它的 day 和 day_raw 确实不一样
                // ——老老实实抓全历史，并报一条（这说明事件源漏了，值得查）
                _probeFailed++;
                Report($"　{code}：day 与 day_raw 在重叠段不一致，事件源可能漏记——改为抓全历史");
                return await FetchAsync(code, HistoryStart, today, ct);
            }

            _copied++;
            return dayBars.Select(b => Retag(b, Granularity.DayRaw)).ToList();
        }

        // ── ③ 没有事件 + 已有 day_raw：本地判有没有新除权，没有就复制新增那几根 ──
        var newer = await Task.Run(
            () => bars.Query(code, Granularity.Day).Where(b => b.PeriodStart.Date > rawLatest.Value.Date).ToList(), ct);
        if (newer.Count == 0) { _skipped++; return []; }

        bool stillIdentical = await Task.Run(() =>
        {
            var d = bars.Query(code, Granularity.Day).FirstOrDefault(b => b.PeriodStart.Date == rawLatest.Value.Date);
            var r = bars.Query(code, Granularity.DayRaw).FirstOrDefault(b => b.PeriodStart.Date == rawLatest.Value.Date);
            return d != null && r != null && NearlyEqual(d.Close, r.Close);
        }, ct);

        if (stillIdentical) { _copied++; return newer.Select(b => Retag(b, Granularity.DayRaw)).ToList(); }

        // 两条序列在老日子上已经不等 ⇒ 期间除权了，前复权整条历史都变了 ⇒ 整只重抓
        Report($"　{code}：day 与 day_raw 在 {rawLatest:yyyy-MM-dd} 已不相等（期间发生了除权），重抓全历史");
        return await FetchAsync(code, HistoryStart, today, ct);
    }

    private async Task<List<Bar>> FetchAsync(string code, DateTime from, DateTime to, CancellationToken ct)
    {
        _fetched++;
        var (_, list) = await fetcher.FetchAsync(code, Granularity.DayRaw, from, to, ct);
        return list;
    }

    /// <summary>把 <c>day</c> 的一根原样改挂到另一个粒度上（值不动，只换 granularity 和抓取时刻）。</summary>
    private static Bar Retag(Bar b, string granularity) => new()
    {
        Code = b.Code,
        Granularity = granularity,
        PeriodStart = b.PeriodStart,
        Open = b.Open, Close = b.Close, High = b.High, Low = b.Low,
        Volume = b.Volume, Amount = b.Amount, Turnover = b.Turnover,
        FetchedAt = DateTime.Now,
    };

    /// <summary>两条序列在**重叠的日期**上逐值相同？只比收盘价——开高低同源同理，收盘价最敏感。</summary>
    private static bool SameOnOverlap(List<Bar> day, List<Bar> raw)
    {
        var byDate = day.ToDictionary(b => b.PeriodStart.Date, b => b.Close);
        int compared = 0;
        foreach (var r in raw)
        {
            if (!byDate.TryGetValue(r.PeriodStart.Date, out var c)) continue;
            compared++;
            if (!NearlyEqual(c, r.Close)) return false;
        }
        return compared > 0;      // 一天都没重叠上就不算验过
    }

    /// <summary>价格按分比——源两边都是两三位小数，浮点直接 == 不可靠。</summary>
    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 0.005;

    protected override Task SaveBatchAsync(IReadOnlyList<Bar> batch, CancellationToken ct)
    {
        bars.InsertOrRefreshUnconfirmed(batch);
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        Report($"完成：写入 {_rows:N0} 行——抓了 {_fetched} 只、从 day 复制 {_copied} 只、"
               + $"无事可做 {_skipped} 只");
        if (_probeFailed > 0)
            Report($"　⚠ {_probeFailed} 只没过闸门（day 与 day_raw 不一致但事件源里没记）——"
                   + "已改为老实抓，但这说明 fund_cqcx 漏了事件，值得查");
        Report("下一步：点【重算回测序列】，ETF 就有 day_adj 了（那一项零改动，按\"有 day_raw 的票\"枚举）。");
        return Task.FromResult<TaskRunResult?>(null);
    }
}
