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
/// 【补全退市名单】（2026-09-17）——拿巨潮的全市场名单减去在市名单，把差集里**确实交易过**的
/// 补进 <c>DelistedStock</c>。
///
/// ════ 为什么两所官网的名单不够 ════
/// <c>ExchangeDelistedListProvider</c> 抓的是两所"终止上市公司"名单，漏两类：
///
/// ① **科创板退市股整类缺失**。上交所 <c>getStockListData2.do</c> 的终止上市名单
///    （<c>stockType=5</c>）里 **68 开头的是 0 只**；<c>688086 紫晶存储</c>、
///    <c>688555 泽达易盛</c>、<c>688287 观典防务</c> 在 <c>stockType</c>
///    1/5/8/9/10/11/12/13/20/21 **全都查不到**（2026-09-17 逐个试过）。
/// ② **已换代码的老号**（<c>600849 上海医药</c>、<c>601313 江南嘉捷</c> 这类）——
///    原代码不再交易，实质等同退市，但两所的终止上市名单里没有。
///
/// 巨潮那份名单（见 <see cref="StockPlatform.Data.Remote.CninfoStockListProvider"/>）都有，
/// 而且实测是我们名单的**超集**（"库里有、巨潮没有的" = 0 只）。
///
/// ════ 为什么不把巨潮直接并进在市名单 ════
/// 试过，**不行**：巨潮含大批已退市股，而 <see cref="SqliteStockMetaUpsert.Upsert"/> 默认写
/// <c>type='stock'</c> 且是 <c>INSERT OR REPLACE</c>——并进去会把库里几百行
/// <c>type='delisted'</c> **冲成 <c>'stock'</c>**，退市股重新进入日常轮询，每天几百个必然落空的
/// 请求（<c>project_dividend_delisted_gap</c> 当初正是为了避免这个才把它们标成 delisted 的）。
/// 所以巨潮的正确用法是补**退市**名单，不是补在市名单。
///
/// ════ 判据：差集 + 两道确认 ════
/// <code>
/// 候选 = 巨潮(A股号段) − StockMeta(type='stock') − DelistedStock
/// 判为已退市 ⇔ 数据源给得出日K（交易过） 且 最后一根K线距今 > 30 天（已经不交易了）
/// </code>
///
/// **两道都不能少**，各挡一类误判（2026-09-17 实机跑第一版时两类都撞上了）：
///
/// ① **给不出日K ⇒ 从未上市**（<c>688688 蚂蚁集团</c>、<c>603361 浙江国祥</c> 这类过会后撤回/暂缓的）。
///    把它们写进退市表是错的——退市表会被分红抓取和 FactorLab 的选池用到。
///
/// ② ⚠ **最后一根K线还很新 ⇒ 它还在交易，不是退市**。"给得出日K"只能证明**交易过**。
///    第一版漏了这条，于是：
///    · <c>601091 沈鼓集团</c>（**当天刚上市的新股**，只有 1 根K线）被判成退市；
///    · <c>600355 *ST精伦</c>、<c>603388 *ST元成</c>、以及那批已改名"退市XX"的票被判成退市——
///      它们其实在**退市整理期**里照常交易（上交所也还把它们放在在市名单 stockType=1/10 里）。
///    标错的代价是它们被踢出日常轮询，K线从此不再更新，而且**没有任何地方会报**。
///
/// 这两类在"在市名册刚刷新过"时本来就不会成为候选（它们在 <c>StockMeta</c> 里），
/// 但这一项不能依赖执行顺序——名册漏一只、或者新股当天还没进名册，判据就得自己扛住。
///
/// 候选通常只有几十只，一只一个请求，代价可以忽略。
///
/// <c>delist_date</c> 一律留 <c>null</c>——巨潮不给终止日，而这一列本来就允许缺失
/// （上交所那边转板/吸收合并的行也是 null）。<c>CatchUpDelistedTailsAsync</c> 的"补最后几天"
/// 只处理有终止日的行，所以留空不会让它去做无意义的重抓。
/// </summary>
public sealed class DelistedSupplementTask(
    FetchPaths paths,
    IStockListProvider cninfoList,
    IBarDataFetcher fetcher) : FetchTaskBase<DelistedStockRow>
{
    /// <summary>探测用的窗口——只要能回一根就说明交易过，不需要全历史。</summary>
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromDays(3650);

    /// <summary>
    /// 最后一根K线距今超过这么久，才算"已经不交易了"。
    /// 30 天足够跨过退市整理期的尾巴，又不会把长期停牌的在市股误判——
    /// 那种票本来就该在在市名册里，压根不会走到这儿。
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    public override FetchActionId Id => FetchActionId.StepDelistedSupplement;

    private int _candidates, _added, _neverTraded, _stillTrading, _listFailed;
    private readonly List<string> _neverTradedCodes = [];
    private readonly List<string> _stillTradingCodes = [];

    protected override async IAsyncEnumerable<IReadOnlyList<DelistedStockRow>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _candidates = _added = _neverTraded = _stillTrading = _listFailed = 0;
        _neverTradedCodes.Clear();
        _stillTradingCodes.Clear();

        List<StockListEntry> all;
        try
        {
            all = await cninfoList.GetAllStocksAsync(ProgressSink, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _listFailed = 1;
            Report($"取巨潮名单失败，本项跳过：{ex.Message}");
            yield break;
        }

        // 读库是同步重活，推线程池（feedback_task_must_offload_heavy_sync）
        var (live, known) = await Task.Run(() =>
        {
            var l = SqliteStockMetaUpsert.GetAll(paths.CurrentDb).Select(x => x.Code).ToHashSet(StringComparer.Ordinal);
            var k = new SqliteDelistedRepository(paths.CurrentDb).GetAll().Select(x => x.Code).ToHashSet(StringComparer.Ordinal);
            return (l, k);
        }, ct);

        var candidates = all.Where(x => !live.Contains(x.Code) && !known.Contains(x.Code))
                            .OrderBy(x => x.Code, StringComparer.Ordinal)
                            .ToList();
        _candidates = candidates.Count;
        Report($"巨潮 {all.Count} 只 − 在市 {live.Count} 只 − 已知退市 {known.Count} 只 ⇒ 候选 {_candidates} 只",
               0, _candidates);
        if (_candidates == 0) yield break;

        var end = DateTime.Today;
        int done = 0;
        foreach (var entry in candidates)
        {
            ct.ThrowIfCancellationRequested();
            done++;

            List<Bar> bars;
            try
            {
                (_, bars) = await fetcher.FetchAsync(entry.Code, Granularity.Day, end - ProbeWindow, end, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Report($"　{entry.Code} {entry.Name} 探测失败，跳过：{ex.Message}");
                continue;
            }

            if (bars.Count == 0)
            {
                // ① 从未上市（过会后撤回/暂缓）——写进退市表是错的
                _neverTraded++;
                _neverTradedCodes.Add($"{entry.Code} {entry.Name}");
                continue;
            }

            var last = bars.Max(b => b.PeriodStart).Date;
            if (end - last <= StaleAfter)
            {
                // ② 还在交易（新股、*ST、退市整理期）——标成退市会把它踢出日常轮询，
                //    而且没有任何地方会报。顺带说明在市名册漏了它，值得看一眼。
                _stillTrading++;
                _stillTradingCodes.Add($"{entry.Code} {entry.Name}(至 {last:MM-dd})");
                continue;
            }

            _added++;
            Report($"　{entry.Code} {entry.Name}：{bars.Count} 根日K，最后一根 {last:yyyy-MM-dd}，判定为已退市",
                   done, _candidates);
            yield return new[]
            {
                new DelistedStockRow
                {
                    Code = entry.Code,
                    Name = entry.Name,
                    Exchange = MarketClassifier.ExchangeOf(entry.Code) == Exchange.Shanghai ? "sse" : "szse",
                    ListDate = null,
                    DelistDate = null,     // 巨潮不给终止日，这一列本来就允许缺失
                },
            };
        }
    }

    protected override Task SaveBatchAsync(IReadOnlyList<DelistedStockRow> batch, CancellationToken ct)
    {
        new SqliteDelistedRepository(paths.CurrentDb).Upsert(batch);
        // 跟 CatchUpDelistedTailsAsync 一样，同时把 StockMeta 标成 delisted——
        // 不标的话它们不在任何名单里，分红抓取（GetByTypes(stock+delisted)）也看不到
        SqliteStockMetaUpsert.Upsert(paths.CurrentDb, batch.Select(r => (r.Code, r.Name)),
                                     SqliteStockMetaUpsert.TypeDelisted);
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (_listFailed > 0)
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Ok(nothingToDo: true, progress: "名单源不可达，未做改动"));

        Report($"完成：候选 {_candidates} 只 → 补进退市名单 {_added} 只、"
               + $"跳过 {_neverTraded} 只（从未上市）+ {_stillTrading} 只（还在交易）");
        if (_stillTradingCodes.Count > 0)
            Report("　⚠ 还在交易却不在在市名册里的（名册漏了它们，值得看一眼）："
                   + string.Join("、", _stillTradingCodes.Take(12))
                   + (_stillTradingCodes.Count > 12 ? $" 等 {_stillTradingCodes.Count} 只" : ""));
        if (_neverTradedCodes.Count > 0)
            Report("　跳过的（数据源一根日K都给不出，多半是过会后撤回/暂缓上市）："
                   + string.Join("、", _neverTradedCodes.Take(12))
                   + (_neverTradedCodes.Count > 12 ? $" 等 {_neverTradedCodes.Count} 只" : ""));
        return Task.FromResult<TaskRunResult?>(null);
    }
}
