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
/// 【补全退市名单】（2026-09-17）——把各路源认得出、而我们名单里还没有的退市股补进
/// <c>DelistedStock</c>（并同步 <c>StockMeta.type='delisted'</c>）。
///
/// <b>这是退市名单的唯一写入口之一</b>（另一个是两所官网那条主路径）。所有消费方——
/// 财报/股东/分红的待抓判据、日常轮询的过滤、全库体检、FactorLab 选池——都只读这张表和
/// <c>StockMeta.type</c>，**谁都不该自己去拼"这只票是不是退市了"的判据**。
/// 名单不全就在这里补，不要在消费端各算一遍。
///
/// ════ 两个候选来源 ════
/// ① <b>巨潮名单 − 在市名册 − 已知退市</b>（见下）；
/// ② <b>本地公司档案里 <c>listing_state='2'</c> 的票</b>（2026-09-19 加）——**这一路不减在市名册**。
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
/// ════ 为什么要第二个来源：差集本身有个盲区 ════
/// 候选集要**减去在市名册**，于是「还挂在在市名册里的已退市票」永远不是候选——名册说在市、
/// 档案说退市，两边谁也纠正不了谁，这只票就一直卡在中间。实测 <c>920305 云创退</c>
/// 正是这样：K线停在 2026-07-29、名字都带"退"了，<c>StockMeta.type</c> 还是 <c>'stock'</c>，
/// 于是每天的K线/资金流/名册轮询都在白抓它，财报判据也一直把它算成"该有新报告期"。
///
/// 东财档案的 <c>LISTING_STATE</c> 不受名册影响，正好补这个盲区，而且它比两所官网名单更全
/// （北交所、科创板退市股都认得出）。<b>零新增请求</b>——那一列早就随【公司档案】落库了。
///
/// ⚠ <b>只认 <c>'2'</c></b>：9 是待上市/暂缓上市（蚂蚁集团那批，有几只还在正常交易）、
///   10 是换代码吸收合并（深赤湾A→招商港口）。见 <c>ICompanyProfileRepository.GetDelistedCodes</c>。
///
/// ════ 判据：两个来源合并 + 同样的两道确认 ════
/// <code>
/// 候选 = [ 巨潮(A股号段) − StockMeta(type='stock') ] ∪ [ 档案 listing_state='2'（A股号段） ] − DelistedStock
/// 判为已退市 ⇔ 数据源给得出日K（交易过） 且 最后一根K线距今 > 30 天（已经不交易了）
/// </code>
///
/// **两道都不能少**，各挡一类误判（2026-09-17 实机跑第一版时两类都撞上了）：
///
/// ① **给不出日K ⇒ 从未上市**（<c>688688 蚂蚁集团</c>、<c>603361 浙江国祥</c> 这类过会后撤回/暂缓的）。
///    把它们写进退市表是错的——退市表会被分红抓取和 FactorLab 的选池用到。
///    ⚠ 这一条的前提是**探测区间覆盖到全部历史**，否则退得早的老股会整类被它误判——
///    见 <see cref="ProbeStart"/>，那里踩过一次。
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
/// 而第二个来源**本来就不减名册**，这两道确认对它更是唯一的防线：实测库里 319 只
/// <c>listing_state='2'</c> 且有K线的票，最后一根K线全都在 30 天以前（零误伤），
/// 但万一东财哪天把"退市整理期"也标成 2，挡住它的就是这两道。
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
    ICompanyProfileRepository profiles,
    IBarDataFetcher fetcher) : FetchTaskBase<DelistedStockRow>
{
    /// <summary>
    /// 探测的起点。⚠ <b>必须是固定日期，不能是"今天往前 N 年"的滚动窗口</b>（2026-09-19 修）。
    ///
    /// 原来是 <c>TimeSpan.FromDays(3650)</c>，于是探测区间只有最近十年，而
    /// <b>2016 年以前退市的老股在那个区间里必然一根K线都没有</b>——判据"给不出日K ⇒ 从未上市"
    /// 于是把它们整类误判。实机跑时撞上的正是这个：<c>600087 退市长油</c>（2014 年退）和
    /// <c>600849 上海医药</c>老号（2010 年换代码）双双落进"从未上市"被跳过，而后者恰恰是
    /// 类注释里点名"两所名单给不出、要靠这一项补进来"的那类。两只都是档案
    /// <c>listing_state='2'</c> 的真退市股，却因为退得太早而永远补不进名单。
    ///
    /// 代价是零：K线接口一次请求返回整段，请求数不变，只是多返回几千根（探测完就丢）。
    /// 1990-12-19 是上交所开市日，没有比它更早的 A 股K线。
    /// </summary>
    private static readonly DateTime ProbeStart = new(1990, 12, 19);

    /// <summary>
    /// 最后一根K线距今超过这么久，才算"已经不交易了"。
    /// 30 天足够跨过退市整理期的尾巴，又不会把长期停牌的在市股误判——
    /// 那种票本来就该在在市名册里，压根不会走到这儿。
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    public override FetchActionId Id => FetchActionId.StepDelistedSupplement;

    private int _candidates, _added, _neverTraded, _stillTrading, _listFailed;
    /// <summary>候选里来自"档案说已退市、名册却还当它在市"的那批——这批是名册和档案打架的地方，
    /// 值得单独报一句。</summary>
    private int _fromProfileOnly;
    private readonly List<string> _neverTradedCodes = [];
    private readonly List<string> _stillTradingCodes = [];

    protected override async IAsyncEnumerable<IReadOnlyList<DelistedStockRow>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _candidates = _added = _neverTraded = _stillTrading = _listFailed = _fromProfileOnly = 0;
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
        var (live, known, profileDelisted, fixedExchanges) = await Task.Run(() =>
        {
            var l = SqliteStockMetaUpsert.GetAll(paths.CurrentDb).Select(x => x.Code).ToHashSet(StringComparer.Ordinal);
            var repo = new SqliteDelistedRepository(paths.CurrentDb);
            var rows = repo.GetAll();
            var k = rows.Select(x => x.Code).ToHashSet(StringComparer.Ordinal);
            var fixes = FixStaleExchanges(repo, rows);
            profiles.EnsureSchema();
            var p = profiles.GetDelistedCodes();
            return (l, k, p, fixes);
        }, ct);
        if (fixedExchanges.Count > 0)
            Report($"存量 exchange 自检：改对 {fixedExchanges.Count} 行——"
                   + string.Join("、", fixedExchanges.Take(12))
                   + (fixedExchanges.Count > 12 ? $" 等 {fixedExchanges.Count} 行" : ""));

        // ① 巨潮差集：名册和退市名单都没有的（巨潮那份在 provider 里已按 A 股号段过滤过）
        var fromList = all.Where(x => !live.Contains(x.Code) && !known.Contains(x.Code))
                          .Select(x => (x.Code, x.Name))
                          .ToList();

        // ② 档案说已终止上市的：**这一路不减在市名册**，那正是它要补的盲区（见类注释）。
        //    号段白名单在这儿要自己过——库里 41 只"档案说退市、名单没有"的票，
        //    40 只是 B股(200/900)和老三板(832317/833874/833994)，只有 1 只是真缺口。
        var fromProfile = profileDelisted
            .Where(x => MarketClassifier.IsAShareCode(x.Code) && !known.Contains(x.Code))
            .ToList();
        _fromProfileOnly = fromProfile.Count(x => live.Contains(x.Code));

        var candidates = fromList.Concat(fromProfile)
                                 .GroupBy(x => x.Code, StringComparer.Ordinal)
                                 .Select(g => g.First())
                                 .OrderBy(x => x.Code, StringComparer.Ordinal)
                                 .ToList();
        _candidates = candidates.Count;
        Report($"巨潮 {all.Count} 只 − 在市 {live.Count} 只 − 已知退市 {known.Count} 只 ⇒ {fromList.Count} 只；"
               + $"档案说已终止上市又不在名单里的 {fromProfile.Count} 只"
               + (_fromProfileOnly > 0 ? $"（其中 {_fromProfileOnly} 只名册还当它在市——名册和档案打架，正是这一路要补的）" : "")
               + $" ⇒ 合并去重后候选 {_candidates} 只",
               0, _candidates);
        if (_candidates == 0) yield break;

        var end = DateTime.Today;
        int done = 0;
        foreach (var (code, name) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            done++;

            List<Bar> bars;
            try
            {
                (_, bars) = await fetcher.FetchAsync(code, Granularity.Day, ProbeStart, end, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Report($"　{code} {name} 探测失败，跳过：{ex.Message}");
                continue;
            }

            if (bars.Count == 0)
            {
                // ① 从未上市（过会后撤回/暂缓）——写进退市表是错的
                _neverTraded++;
                _neverTradedCodes.Add($"{code} {name}");
                continue;
            }

            var last = bars.Max(b => b.PeriodStart).Date;
            if (end - last <= StaleAfter)
            {
                // ② 还在交易（新股、*ST、退市整理期）——标成退市会把它踢出日常轮询，
                //    而且没有任何地方会报。顺带说明在市名册漏了它，值得看一眼。
                _stillTrading++;
                _stillTradingCodes.Add($"{code} {name}(至 {last:MM-dd})");
                continue;
            }

            _added++;
            Report($"　{code} {name}：{bars.Count} 根日K，最后一根 {last:yyyy-MM-dd}，判定为已退市",
                   done, _candidates);
            yield return new[]
            {
                new DelistedStockRow
                {
                    Code = code,
                    Name = name,
                    Exchange = ExchangeTag(code),
                    ListDate = null,
                    // ⚠ 终止日一律留 null，**不拿最后一根K线去推**：实测 318 只有官方终止日的票，
                    //   「终止日 − 最后K线」中位数 15 天、尾部到 2525 天（摘牌在退市整理期结束之后），
                    //   推出来会偏早，可能把该有的一期财报判成"不该有"——那是静默漏抓。
                    //   这一列本来就允许缺失（上交所转板/吸收合并的行也是 null）。
                    DelistDate = null,
                },
            };
        }
    }

    /// <summary>
    /// 存量 exchange 自检（2026-09-19 加）——**纯本地、零请求**。
    ///
    /// 为什么光修 <see cref="ExchangeTag"/> 不够：候选集第一步就减掉了"已知退市"，
    /// <b>已经在名单里的行永远不会再被走一遍</b>，写错的值不会自愈。实测
    /// <c>920680 广道退</c> 09-17 被旧规则写成了 <c>szse</c>，09-19 修完再跑也纹丝不动——
    /// 修了判据却没人回头看存量，等于没修。
    ///
    /// ⚠ 只动 <see cref="MarketClassifier"/> <b>认得出</b>交易所的行。<see cref="ExchangeTag"/>
    /// 的兜底分支把未知号段也写成 <c>szse</c>，那是"写新行时总得给个值"的将就；
    /// 拿它来覆盖存量就成了把没根据的猜测写进库里，反而可能改坏本来对的值。
    /// </summary>
    private static List<string> FixStaleExchanges(SqliteDelistedRepository repo, List<DelistedStockRow> rows)
    {
        var stale = rows
            .Where(r => MarketClassifier.ExchangeOf(r.Code) != Exchange.Unknown)
            .Select(r => (r.Code, r.Name, Want: ExchangeTag(r.Code), Had: r.Exchange))
            .Where(x => !string.Equals(x.Want, x.Had, StringComparison.Ordinal))
            .ToList();
        if (stale.Count == 0) return [];

        repo.UpdateExchange(stale.Select(x => (x.Code, x.Want)));
        return [.. stale.Select(x => $"{x.Code} {x.Name} {x.Had}→{x.Want}")];
    }

    /// <summary>
    /// 交易所标记。⚠ 原来是 <c>== Exchange.Shanghai ? "sse" : "szse"</c>，**北交所会被写成深市**
    /// （2026-09-19 修）——920 号段走 <see cref="MarketClassifier"/>，别自己拼前缀规则
    /// （feedback_market_prefix_via_classifier）。
    /// 存量里写错的由 <see cref="FixStaleExchanges"/> 回头改。
    /// </summary>
    private static string ExchangeTag(string code) => MarketClassifier.ExchangeOf(code) switch
    {
        Exchange.Shanghai => "sse",
        Exchange.Beijing => "bse",
        _ => "szse",
    };

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
               + $"跳过 {_neverTraded} 只（从未上市）+ {_stillTrading} 只（还在交易）"
               + (_fromProfileOnly > 0
                   ? $"。其中 {_fromProfileOnly} 只是档案说退市、在市名册却还留着的——补进来之后它们"
                     + "才会退出日常轮询（K线/资金流/名册每天都在白抓）"
                   : ""));
        if (_stillTradingCodes.Count > 0)
            Report("　⚠ 还在交易却不在在市名册里的（名册漏了它们，值得看一眼）："
                   + string.Join("、", _stillTradingCodes.Take(12))
                   + (_stillTradingCodes.Count > 12 ? $" 等 {_stillTradingCodes.Count} 只" : ""));
        if (_neverTradedCodes.Count > 0)
            // ⚠ 措辞只说事实、不下结论（2026-09-19 改）：原来写的是"多半是过会后撤回/暂缓上市"，
            //   而 600087 退市长油被十年滚动窗口误判成这一类时，这句话让日志读起来完全正常。
            //   "给不出日K"只是数据源的回答，不等于"从未上市"。
            Report("　跳过的（探测区间内数据源一根日K都给不出，通常是过会后撤回/暂缓上市；"
                   + "如果里面有你认得的老退市股，那是探测没覆盖到它的年代，要查）："
                   + string.Join("、", _neverTradedCodes.Take(12))
                   + (_neverTradedCodes.Count > 12 ? $" 等 {_neverTradedCodes.Count} 只" : ""));
        return Task.FromResult<TaskRunResult?>(null);
    }
}
