using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【分红对账】（2026-09-18 新增）。见 doc/dividend-reconcile-design.md。
///
/// ════ 为什么要有它 ════
/// 分红是复权因子的输入，**缺一条除权记录，那只票的复权序列整段错、而且不报错**。
/// 行都在、值也都对，只是少了一次事件——没有任何现成的体检能发现这种错，
/// 唯一的办法是拿另一个源比一遍。
///
/// 2026-09-18 第一次对账（东财 56,191 条 vs 库里新浪 59,081 条，按除权日对齐）：
///   · 两边都有 55,100 条
///   · **东财有、我们没有 1,050 条 / 237 只**——其中 1,043 条是北交所，
///     因为新浪对北交所覆盖不全（920061/920547/833171/430047 实测都是"暂时没有数据"）
///   · 我们有、东财没有 3,982 条——其中 2,516 条是退市股（东财那张表退市股全空）
///
/// ⇒ 两个源互补，谁也不是权威。所以这一项**只加不删**。
///
/// ════ 只加不删 ════
///   · 东财有、库里没有 → 补进去（带 <c>source='eastmoney'</c>，可追溯可回滚）
///   · 库里有、东财没有 → 什么都不做（多半是退市股，或东财还没收录）
///   · 两边都有但金额不一样 → 只报数，不动数据（口径差异比数据错更可能）
///
/// ════ ⚠ 判重按除权日，不按主键 ════
/// <c>Dividend</c> 的主键是 <c>(code, announce_date)</c>，而两边的"公告日"语义不同：
/// 新浪那列是页面上的公告日期，东财给的是预案公告日。按主键插，同一个方案会变成两行，
/// 库里凭空多出一次除权——那比原来缺一条还糟。所以判重用 <c>(code, ex_date)</c>，
/// 并留 ±7 天容差（两边对"除权日"的记法偶有一两天出入，实测 10 条）。
/// </summary>
public sealed class DividendReconcileTask(
    FetchPaths paths,
    IDividendRepository repository,
    IDividendCrossCheckSource source) : FetchTaskBase<DividendRow>
{
    /// <summary>除权日容差（天）。两边差几天的算同一条，不补。</summary>
    private const int ExDateToleranceDays = 7;

    /// <summary>从哪一年开始对。A 股最早的除权记录在 1990 年。</summary>
    private const int FirstYear = 1990;

    public override FetchActionId Id => FetchActionId.FetchDividendReconcile;

    private int _scanned, _missing, _bothSides, _inserted;
    private readonly List<(string Code, DateTime Ex, double Div, double Bonus, double Transfer)> _gaps = [];
    private Dictionary<string, HashSet<DateTime>> _mine = new(StringComparer.Ordinal);

    protected override async IAsyncEnumerable<IReadOnlyList<DividendRow>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _scanned = _missing = _bothSides = _inserted = 0;
        _gaps.Clear();

        // 库里已有的除权日——同步重活，推线程池（骨架不替子类推）。
        _mine = await Task.Run(() =>
        {
            repository.EnsureSchema();
            return repository.GetImplementedExDates();
        }, ct);
        Report($"库里已实施且有除权日的记录：{_mine.Values.Sum(v => v.Count):N0} 条 / {_mine.Count:N0} 只。"
             + $"开始跟{source.SourceName}对账（按年切片，约 3 分钟）...");

        void Forward(string s) => Report(s);
        source.OnStatus += Forward;
        try
        {
            int toYear = DateTime.Today.Year;
            await foreach (var yearRows in source.StreamImplementedAsync(FirstYear, toYear, ct))
            {
                _scanned += yearRows.Count;
                // ⚠ 必须**逐条**判、判完立刻登记，不能整批 Where 过滤：同一年里东财可能给出
                //   两条除权日只差一两天的记录，整批过滤时它们都还没进 _mine，于是两条都被判成
                //   "缺"，补进去就是自己造了一次重复除权。
                var missing = new List<DividendRow>();
                foreach (var r in yearRows)
                {
                    if (!IsMissing(r)) continue;
                    missing.Add(r);
                    _gaps.Add((r.Code, r.ExDate!.Value, r.DividendYuan, r.BonusShares, r.TransferShares));
                    if (!_mine.TryGetValue(r.Code, out var set))
                        _mine[r.Code] = set = new HashSet<DateTime>();
                    set.Add(r.ExDate.Value.Date);
                }
                _missing += missing.Count;
                _bothSides += yearRows.Count - missing.Count;
                yield return missing;
            }
        }
        finally { source.OnStatus -= Forward; }
    }

    /// <summary>库里这只票有没有除权日相同（或差几天）的记录。</summary>
    private bool IsMissing(DividendRow r)
    {
        if (r.ExDate is not { } ex) return false;
        if (!_mine.TryGetValue(r.Code, out var mine)) return true;
        if (mine.Contains(ex.Date)) return false;
        return !mine.Any(d => Math.Abs((d - ex.Date).Days) <= ExDateToleranceDays);
    }

    protected override Task SaveBatchAsync(IReadOnlyList<DividendRow> batch, CancellationToken ct)
    {
        // 记真正插进去的行数——判定为"缺"跟真的写进去是两回事（主键还可能撞），
        // 两个数不一致必须说出来，否则就成了"日志报补了 N 条、库里一行没多"。
        _inserted += repository.InsertMissing(batch);
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (_missing == 0)
        {
            var clean = $"分红对账完成：比了 {_scanned:N0} 条，库里一条都不缺。";
            Report(clean);
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Ok(nothingToDo: true, progress: clean));
        }

        // 哪些缺口真的会让复权序列出错——落在我们 day_adj 覆盖区间里的那些。
        // 落在起点之前的不影响现在的回测（北交所那 1,043 条就都在起点之前），
        // 但补了也不亏：将来补更早的历史K线时它们就是前提。
        var inRange = CountInAdjRange();

        var summary = $"分红对账完成：比了 {_scanned:N0} 条，两边都有 {_bothSides:N0} 条，"
                    + $"**补上 {_inserted:N0} 条**库里没有的";
        Report(summary);

        // 判成缺的比真写进去的多 ⇒ 有行被主键挡在外面了。不报的话这些缺口会每轮重来一遍、
        // 每轮都"看起来补上了"，永远收敛不了（2026-09-18 实机踩到，139 条）。
        int blocked = _missing - _inserted;
        if (blocked > 0)
            Report($"　⚠ 另有 {blocked} 条判成缺口却没能写进去（公告日跟库里已有的行撞了主键，"
                 + "换除权日重试也撞）。它们下一轮还会被报一次——需要人看一眼是不是口径问题。");
        Report($"　其中 {inRange} 条落在回测序列（day_adj）已覆盖的区间里——"
             + (inRange > 0
                 ? "这些票的复权序列此前是错的，**记得跑一次【重算回测序列】**。"
                 : "所以当前回测没受影响；补了是为了将来补更早的历史K线。"));

        var byMarket = _gaps.GroupBy(g => g.Code.StartsWith("92") || g.Code.StartsWith('8') ? "北交所" : "沪深")
                            .Select(g => $"{g.Key} {g.Count()} 条");
        Report($"　按市场：{string.Join("、", byMarket)}；含送股/转增的 "
             + $"{_gaps.Count(g => g.Bonus > 0 || g.Transfer > 0)} 条（这些对复权影响最大）。");

        return Task.FromResult<TaskRunResult?>(TaskRunResult.Ok(progress: summary));
    }

    /// <summary>补上的缺口里，有多少落在该票 day_adj 的已有区间内（纯本地查库）。</summary>
    private int CountInAdjRange()
    {
        try
        {
            // 一次取回全部代码的首根 day_adj——逐只查是几百次往返，这里一条 GROUP BY 就够。
            var firsts = new SqliteBarRepository(paths.CurrentDb)
                .GetEarliestPeriodStartByCode(Granularity.DayAdj);
            return _gaps.Count(g => firsts.TryGetValue(g.Code, out var first) && g.Ex >= first.Date);
        }
        catch (Exception ex)
        {
            Report($"　（算「有多少条落在回测区间内」时出错，不影响已补的数据：{ex.Message}）");
            return 0;
        }
    }
}
