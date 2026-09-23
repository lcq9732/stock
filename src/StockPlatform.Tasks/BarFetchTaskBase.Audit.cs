using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Tasks;

/// <summary>
/// 【全库数据体检】查出来的两类待办怎么补（2026-09-21 从 <c>FetchOrchestrator.Steps</c> 搬过来）。
///
/// 它俩跟前两类（失败名单、当天缺着）最大的不同是**自己抓自己存**、不走骨架的流式落库——
/// 因为必须"抓完这一批立刻复查"，而骨架是先 yield 再存，复查会跑在存之前、一律判成"还缺"。
/// </summary>
public abstract partial class BarFetchTaskBase
{
    /// <summary>
    /// 补历史空洞。每只标的按**区间**抓一次（数据源一页固定返回 640 根，抓一段跟抓一天成本几乎一样），
    /// 抓完立刻复查这一段还缺不缺：补上了就划掉；还缺但没到两轮就留着；补满两轮还缺就写进
    /// 「确认没有」白名单（多半是停牌，以后体检不再报——不收敛的话它们年复一年地被报出来、
    /// 每次都白抓一遍）。
    /// </summary>
    private async Task FillGapAsync(IManifestStore store, List<RetryTarget> pending, CancellationToken ct)
    {
        // 按每一段自己的口径分组——同一份名单里可能混着 day 和 day_raw（见类注释规矩③）。
        var flat = pending
            .GroupBy(t => string.IsNullOrEmpty(t.Gran) ? Granularity.Day : t.Gran)
            .SelectMany(g => g.Select(t => (Target: t, Gran: g.Key)))
            .ToList();

        var audit = new SqliteMissingBarRepository(Paths.CurrentDb);
        var survived = new List<RetryTarget>();
        int processed = 0, filled = 0, confirmed = 0, batchNo = 0;
        int batchTotal = (flat.Count + AuditBatchSize - 1) / AuditBatchSize;

        // 已处理的换成复查结果，没轮到的原样留着——少了后半句就会把还没跑的那部分整个清掉，
        // 比不落库更糟（安静的错）。
        void SaveProgress()
        {
            lock (SqliteWriteGate.Local)
            {
                var m = store.Load();
                m.SetTodo(TaskId, RetryTodoKind.Gap,
                    survived.Concat(flat.Skip(processed).Select(x => x.Target)).ToList());
                store.Save(m);
            }
        }

        Report($"补历史空洞：{flat.Count} 段、共 {flat.Sum(x => x.Target.Days)} 个交易日"
             + $"（每段按区间抓一次，分 {batchTotal} 批，每批跑完就落账）…");

        foreach (var batch in flat.Chunk(AuditBatchSize))
        {
            batchNo++;

            // 这一批里可能有几种口径，分开抓、分开复查。
            var byGran = batch.GroupBy(x => x.Gran).ToList();
            var batchStill = new List<RetryTarget>();
            int filledInBatch = 0, confirmedInBatch = 0;

            foreach (var g in byGran)
            {
                if (!CanFetch(g.Key, $"{GranLabel(g.Key)}空洞 {g.Count()} 段"))
                {
                    // Tries 一动不动地原样留着（见类注释规矩②）
                    batchStill.AddRange(g.Select(x => x.Target));
                    continue;
                }

                foreach (var (r, gran) in g)
                {
                    ct.ThrowIfCancellationRequested();
                    // 顺序抓、不并发：这批可能上千只，并发只会更快撞数据源配额。
                    var got = await FetchOneAsync(
                        r.Code, gran,
                        r.From ?? IncrementalWindowCalculator.AShareMarketOpen,
                        r.To ?? DateTime.Today, driftCheck: false, ct);
                    if (got != null) await SaveBatchAsync([got], ct);
                }

                // 抓完立刻复查这一批还缺不缺——判据仍是"交易日历里有、这只票没有"
                var gaps = await Task.Run(() => audit.FindGaps(
                    g.Select(x => x.Target.Code).ToList(), g.Key,
                    MarketIndexCatalog.ShanghaiCompositeSymbol), ct);

                var toConfirm = new List<(string Code, DateTime Day)>();
                int stillHere = 0;
                foreach (var (r, gran) in g)
                {
                    if (!gaps.TryGetValue(r.Code, out var days)) continue;      // 补齐了
                    var left = days.Where(d => d >= (r.From ?? DateTime.MinValue)
                                            && d <= (r.To ?? DateTime.MaxValue)).ToList();
                    if (left.Count == 0) continue;

                    int tries = r.Tries + 1;
                    if (tries >= AuditMaxTries)
                        toConfirm.AddRange(left.Select(d => (r.Code, d)));      // 认了：数据源就是没有
                    else
                    {
                        stillHere++;
                        batchStill.Add(new RetryTarget
                        {
                            Code = r.Code, Gran = gran,
                            From = left[0], To = left[^1], Days = left.Count, Tries = tries,
                        });
                    }
                }

                if (toConfirm.Count > 0)
                    await Task.Run(() =>
                    {
                        lock (SqliteWriteGate.Local) audit.Confirm(toConfirm, g.Key, AuditMaxTries);
                    }, ct);

                int confirmedHere = toConfirm.Select(x => x.Code).Distinct().Count();
                confirmedInBatch += confirmedHere;
                filledInBatch += Math.Max(0, g.Count() - stillHere - confirmedHere);
            }

            survived.AddRange(batchStill);
            filled += filledInBatch;
            confirmed += confirmedInBatch;
            processed += batch.Length;
            SaveProgress();

            Report($"　空洞 第 {batchNo}/{batchTotal} 批已落账：补上 {filledInBatch} 段、"
                 + $"还缺 {batchStill.Count} 段、{confirmedInBatch} 段判定数据源确实没有"
                 + (batchNo < batchTotal ? "（现在停也不会丢前面几批的进度）" : ""));
        }

        SaveProgress();
        Report($"　空洞：补上 {filled} 段、还缺 {survived.Count} 段（下轮再试）、"
             + $"{confirmed} 段判定数据源确实没有"
             + (confirmed > 0 ? "（多半是停牌，以后体检不再报）" : ""));
        BacklogParts.Add($"空洞 {flat.Count} 段");
    }

    /// <summary>
    /// 修体检报出的**值问题**（行在但值错）。跟空洞那条路有三处关键不同：
    ///
    /// ① **抓法按 Reason 分**（判据在 <see cref="ValueIssueFixPlan"/>）："多口径量额对不上"
    ///    只覆盖 volume/amount/turnover 三列、**绝不动 OHLC**——历史行的价格是当年的复权基准，
    ///    覆盖会造成同一序列里新旧基准混杂。
    /// ② **复查用对应判据，不是 FindGaps**：值错的行**一直都在**，拿"行在不在"去复查会一律
    ///    判成"已补齐"划掉，哪怕值根本没被覆盖。
    /// ③ **不进「确认没有」白名单**：那份名单是给停牌用的，让错值进去等于发永久豁免。
    ///    <c>Tries</c> 到顶就一直留在名单里报警。
    ///
    /// ⚠ 写入**不走 <see cref="BarWritePlanner"/>**（2026-09-09 生产实测踩的）：那条路
    /// 只把"库里没有的行"放进待插队列，而值错的行是"**存在**但值错"，压根到不了覆盖那一步。
    /// 那一轮 5282 段盘中固化全部判"还在"，002650 的 OHLC 一直是四价合一 6.04。
    /// </summary>
    private async Task FillValueAsync(IManifestStore store, List<RetryTarget> pending, CancellationToken ct)
    {
        var ranges = pending.Select(ToRange).ToList();
        var auditor = new SqliteBarValueAuditor(Paths.CurrentDb);
        var cutoff = DateTime.Today.AddDays(-ValueRecheckSettleDays);
        var still = new List<MissingBarRange>();
        int fixedTotal = 0, skippedTotal = 0, batchNo = 0, processed = 0;
        int batchTotal = (ranges.Count + AuditBatchSize - 1) / AuditBatchSize;

        void Save(IEnumerable<MissingBarRange> current)
        {
            lock (SqliteWriteGate.Local)
            {
                var m = store.Load();
                m.SetTodo(TaskId, RetryTodoKind.ValueIssue, current.Select(ToTarget).ToList());
                store.Save(m);
            }
        }

        Report($"修体检报出的值问题：{ranges.Count} 段"
             + $"（{string.Join("、", ranges.GroupBy(r => r.EffectiveReason).Select(g => $"{ReasonLabel(g.Key)} {g.Count()}"))}）"
             + $"，分 {batchTotal} 批，每批跑完就落账…");

        foreach (var batch in ranges.Chunk(AuditBatchSize))
        {
            batchNo++;
            processed += batch.Length;
            var plan = ValueIssueFixPlan.Build(batch, Source.Fetcher.SupportsHfq);

            foreach (var f in plan.Fetches)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var (_, fresh) = await Source.Fetcher.FetchAsync(f.Code, f.Granularity, f.From, f.To, ct);
                    await Task.Run(() =>
                    {
                        lock (SqliteWriteGate.Local)
                        {
                            if (f.Write == ValueFixWrite.ThreeColumns)
                                Bars.UpdateVolumeAmountTurnover(fresh, f.Granularity);
                            else if (fresh.Count > 0)
                                Bars.InsertOrRefreshUnconfirmed(fresh);
                        }
                    }, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (Errors.Count < 50)
                        Errors.Add($"{f.Code} {GranLabel(f.Granularity)} 值修复失败：{ex.Message}");
                }
            }

            // ── 复查：按 Reason 用对应判据重查这一批（只查这批 code，不是全库扫描）──
            var codes = batch.Select(r => r.Code).Distinct().ToList();
            Dictionary<(string, string, string), List<DateTime>> live;
            try
            {
                live = await Task.Run(() =>
                {
                    var d = new Dictionary<(string, string, string), List<DateTime>>();
                    foreach (var i in auditor.RowIssues(cutoff, codes: codes)
                                             .Concat(auditor.CrossGranularityMismatch(cutoff, codes: codes)))
                    {
                        var key = (i.Code, i.Granularity, i.Kind);
                        if (!d.TryGetValue(key, out var days)) d[key] = days = [];
                        days.Add(i.Day);
                    }
                    return d;
                }, ct);
            }
            catch (Exception ex)
            {
                // 复查查不动就保守处理：这一批原样留着（宁可下轮重来，也不能当成"修好了"划掉）
                Errors.Add($"值问题复查失败（本批原样留着）：{ex.Message}");
                still.AddRange(batch);
                Save(still.Concat(ranges.Skip(processed)));
                continue;
            }

            int fixedInBatch = 0, stillInBatch = 0;
            foreach (var r in batch)
            {
                if (plan.Skipped.Contains(r)) { still.Add(r); skippedTotal++; continue; }

                live.TryGetValue((r.Code, r.Granularity, r.EffectiveReason), out var days);
                var survived = ValueIssueRecheck.Survives(r, days);
                if (survived == null) { fixedInBatch++; continue; }

                stillInBatch++;
                still.Add(survived);
            }
            fixedTotal += fixedInBatch;

            Save(still.Concat(ranges.Skip(processed)));
            Report($"　值问题 第 {batchNo}/{batchTotal} 批已落账：修好 {fixedInBatch} 段、还在 {stillInBatch} 段"
                 + (batchNo < batchTotal ? "（现在停也不会丢前面几批的进度）" : ""));
        }

        Save(still);
        Report($"　值问题：修好 {fixedTotal} 段、还在 {still.Count - skippedTotal} 段"
             + (skippedTotal > 0 ? $"、{skippedTotal} 段跳过（数据源不支持该口径 / 本地重算的口径）" : "")
             + "。⚠ 还在的**不会**进「数据源确实没有」白名单——那是给停牌用的，"
             + "值错进去等于发永久豁免，所以它会一直报到真修好为止。");

        // ETF 的「多口径不一致」重抓修不好（2026-09-23 查实）：腾讯的 ETF 换手率口径不统一，
        // 有时 ÷前一交易日份额、有时 ÷当天份额，同一天两个接口还可能各用一种，同一时刻重抓还是那个数。
        // 待办照常留着报警，这里只指一条能修的路。
        int etfInconsistent = still.Count(r => r.EffectiveReason == AuditFindingKind.Inconsistent
                                               && EtfTurnoverRule.LooksLikeEtfBarCode(r.Code));
        if (etfInconsistent > 0)
            Report($"　其中 {etfInconsistent} 段是 ETF 的多口径不一致——多半只是换手率对不上"
                 + "（腾讯两个接口那天一个按前一交易日份额、一个按当天份额算），重新拉取修不好，"
                 + "请跑【ETF换手率校正】（先「日常增量」看报告，再「彻底重查」写回）。");
        BacklogParts.Add($"值问题 修好 {fixedTotal}/{ranges.Count} 段");
    }

    private static MissingBarRange ToRange(RetryTarget t) => new()
    {
        Code = t.Code,
        Granularity = string.IsNullOrEmpty(t.Gran) ? Granularity.Day : t.Gran,
        From = t.From ?? DateTime.MinValue,
        To = t.To ?? DateTime.MaxValue,
        Days = t.Days,
        Tries = t.Tries,
        // 老 manifest 里没有这个字段，读出来是 null——MissingBarRange 的默认值就是 Gap，
        // 直接赋 null 会把它冲掉（EffectiveReason 那条兜底也就失效了）。
        Reason = t.Reason ?? AuditFindingKind.Gap,
    };

    private static RetryTarget ToTarget(MissingBarRange r) => new()
    {
        Code = r.Code, Gran = r.Granularity,
        From = r.From, To = r.To, Days = r.Days, Tries = r.Tries, Reason = r.Reason,
    };

    /// <summary>值问题的中文名，只用在日志里。</summary>
    private static string ReasonLabel(string reason) => reason switch
    {
        AuditFindingKind.Intraday => "盘中固化",
        AuditFindingKind.NullValue => "关键列NULL",
        AuditFindingKind.Ohlc => "OHLC不自洽",
        AuditFindingKind.Inconsistent => "多口径量额对不上",
        _ => reason,
    };
}
