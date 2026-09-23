using System.Runtime.CompilerServices;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>一个市场一轮校正的统计（给日志、界面那一格和测试用）。</summary>
public sealed class EtfTurnoverAuditResult
{
    public string Market { get; init; } = "";
    /// <summary>交易所份额表里出现过的 ETF 只数。</summary>
    public int Codes { get; set; }
    public int CodesWithoutBars { get; set; }
    /// <summary>本地名册里有、交易所份额表里一天都没出现过的 ETF（沪市多为货币 ETF）——校正不了，只报数。</summary>
    public List<string> NotInExchangeList { get; } = [];
    public int Consistent { get; set; }
    public int Wrong { get; set; }
    public int Missing { get; set; }
    /// <summary>判不了：前一交易日早于这个源开始有数据的那天（上交所 2012-01-04、深交所 2016-09-26）。</summary>
    public int UnjudgeableBeforeFirstDay { get; set; }
    /// <summary>判不了：其余（上市首日、交易所那天没列这只）。</summary>
    public int UnjudgeableOther { get; set; }
    public int Written { get; set; }
    /// <summary>按口径分的 (错值, 空值)。</summary>
    public Dictionary<string, (int Wrong, int Missing)> ByGranularity { get; } = new();
    /// <summary>错值样例（最多 <see cref="EtfTurnoverRecalcTask.MaxSamples"/> 条）。</summary>
    public List<string> WrongSamples { get; } = [];
}

/// <summary>
/// 【ETF换手率校正】（2026-09-23，见 doc/etf-turnover-recalc-design.md）——
/// 拿交易所官方的每日 ETF 份额，按「成交量 ÷ 前一交易日份额」统一重算**沪深两市 ETF** 的换手率。
///
/// 修的是两件事（证据都在 <see cref="EtfTurnoverRule"/> 的类注释里）：
///   ① **错值**：腾讯的沪市 ETF 口径不统一（2024-10 以前 ÷T-1、之后多数 ÷T 又夹着 ÷T-1），
///      同一天两个接口还可能不一样——那种被体检报成「多口径不一致」、【重新拉取失败】修不好；
///      深市一直是 ÷T-1，只有 2022 年几个故障日对不上；
///   ② **空值**：腾讯的 ETF 换手率大约 2022 年年中才开始有，之前全是 0（沪市约 24 万行、
///      深市约 11 万行有成交却没有换手率），两个口径都是 0，体检看不出来。
///
/// ════ 流程 ════
/// 1. 补份额：每个市场按 <see cref="EtfShareFetchPlan"/> 排请求——上交所一天一个、深交所一个月一个；
///    最近 3 天的份额每轮都重抓（深交所 T 日晚间的值只是参考）。一个请求一批落库，停在哪都不丢。
/// 2. 检查（两档都做）：逐只 ETF 读三个口径的日K，按判据分成一致/错值/空值/无法裁判。
/// 3. 写回（只有「彻底重查」做）：错值和空值改成期望值。只写 turnover 一列。
///
/// ════ 两档 ════
/// 「日常增量」＝只补份额、只报告，**不写 Bar**；「彻底重查」＝同上再写回。
/// 选 Thorough 而不是 FirstBackfill 是按 2026-09-11 定的词义：彻底重查＝不管原来有没有、全部重来，
/// 这里正是要覆盖已有的错值。先跑增量看报告，数字对了再跑彻底重查。
///
/// ════ 名单从哪来 ════
/// 从 EtfShare 表本身（交易所列过的全部 ETF），Bar 里按 市场 + 代码 找——这是 ETF 在 Bar 里存的形式
/// （前缀是新浪 ETF 列表接口原样给的，见 SqliteMarginShortBalanceFiller.IsShanghaiByStoredPrefix）。
/// 不按代码规则猜市场。
/// </summary>
public sealed class EtfTurnoverRecalcTask(
    IEtfShareRepository shares,
    IReadOnlyList<IEtfShareProvider> providers,
    ITradingDayRepository tradingDays,
    IDailyFetchNoDataRepository noData,
    SqliteEtfTurnoverStore bars) : FetchTaskBase<EtfShareRow>
{
    /// <summary>日志里列几条错值样例（每个市场）。</summary>
    public const int MaxSamples = 8;

    /// <summary>补份额时每多少个请求打一条日志（其余只喂看门狗）。</summary>
    private const int ReportEveryRequests = 100;

    /// <summary>检查时每多少只打一条日志。</summary>
    private const int ReportEveryCodes = 100;

    public override FetchActionId Id => FetchActionId.StepEtfTurnoverFix;

    /// <summary>最近一轮每个市场的统计（键是 "sh" / "sz"）。测试读它。</summary>
    public IReadOnlyDictionary<string, EtfTurnoverAuditResult>? LastResult { get; private set; }

    protected override async IAsyncEnumerable<IReadOnlyList<EtfShareRow>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var calendar = await Task.Run(() => tradingDays.GetAll().Select(DateOnly.FromDateTime).ToList(), ct);

        foreach (var p in providers)
        {
            var dataset = IDailyFetchNoDataRepository.EtfShareDatasetOf(p.Market);
            var (have, confirmed) = await Task.Run(() => (shares.GetDays(p.Market), noData.GetConfirmed(dataset)), ct);
            var plan = EtfShareFetchPlan.Build(calendar, have, confirmed, p.FirstDay, today, p.Batch);
            var label = MarketLabel(p.Market);

            if (plan.Count == 0)
            {
                Report($"{label} ETF 份额：本地已有 {have.Count} 个交易日，没有要补的");
                continue;
            }
            int dayCount = plan.Sum(x => x.Days.Count);
            Report($"{label} ETF 份额：要补 {dayCount} 个交易日（{plan[0].From:yyyy-MM-dd} ~ {plan[^1].To:yyyy-MM-dd}），"
                 + $"{plan.Count} 个请求（{(p.Batch == EtfShareBatch.Day ? "一天一个" : "一个月一个")}），"
                 + $"一个请求一批落库（停在哪都不丢）。最近 {EtfShareFetchPlan.RefreshRecentDays} 天的份额每轮都重抓…");

            using var _ = ForwardStatus(h => p.OnStatus += h, h => p.OnStatus -= h);
            int done = 0, emptyDays = 0, rowsTotal = 0;
            foreach (var req in plan)
            {
                ct.ThrowIfCancellationRequested();
                var rows = await p.GetAsync(req.From, req.To, ct);
                done++;

                var empty = EtfShareFetchPlan.ConfirmableEmpty(req, rows, today);
                if (empty.Count > 0)
                {
                    emptyDays += empty.Count;
                    await Task.Run(() =>
                    {
                        lock (SqliteWriteGate.Local)
                            foreach (var d in empty) noData.Confirm(dataset, d);
                    }, ct);
                }
                rowsTotal += rows.Count;
                ReportQuiet($"{label}份额 {req.From:yyyy-MM-dd}~{req.To:yyyy-MM-dd}：{rows.Count} 行", done, plan.Count);

                if (done % ReportEveryRequests == 0 || done == plan.Count)
                    Report($"　{label}份额 {done}/{plan.Count} 个请求（当前 {req.To:yyyy-MM-dd}），共 {rowsTotal:N0} 行"
                         + (emptyDays > 0 ? $"，{emptyDays} 天交易所没有数据" : ""), done, plan.Count);

                if (rows.Count > 0) yield return rows;
            }
        }
    }

    protected override Task SaveBatchAsync(IReadOnlyList<EtfShareRow> batch, CancellationToken ct)
        => Task.Run(() =>
        {
            lock (SqliteWriteGate.Local) shares.Upsert(batch);
        }, ct);

    protected override async Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        bool write = args.Mode == FetchMode.Thorough;
        Report(write
            ? "开始检查并写回 ETF 换手率（彻底重查：错值和空值改成「成交量 ÷ 前一交易日官方份额」）…"
            : "开始检查 ETF 换手率（日常增量：只报告，不写库）…");

        // ⚠ 整段是同步的重活（一千多只 × 几千天 × 3 个口径），必须自己推线程池——
        // 骨架不会替你推，同步跑会把 UI 整段冻死（见 feedback_task_must_offload_heavy_sync）。
        var results = await Task.Run(() => Audit(write, ct), ct);
        LastResult = results;

        foreach (var r in results.Values)
            foreach (var line in Describe(r, write)) Report(line);
        if (!write && results.Values.Any(r => r.Wrong + r.Missing > 0))
            Report("　这是「日常增量」，只检查、一行没写。确认数字没问题后，把这一项的模式切到「彻底重查」再跑一次即可写回。");

        bool hasIssues = results.Values.Any(r => r.Wrong + r.Missing > 0);
        bool wrote = results.Values.Any(r => r.Written > 0);
        return TaskRunResult.Ok(
            nothingToDo: stats.Items == 0 && !wrote && !hasIssues,
            progress: Summary(results.Values, write));
    }

    private Dictionary<string, EtfTurnoverAuditResult> Audit(bool write, CancellationToken ct)
    {
        var calendar = tradingDays.GetAll().Select(DateOnly.FromDateTime).OrderBy(d => d).ToList();
        var prevOf = new Dictionary<DateOnly, DateOnly>(calendar.Count);
        for (int i = 1; i < calendar.Count; i++) prevOf[calendar[i]] = calendar[i - 1];

        var allCodes = shares.GetCodes();
        var results = new Dictionary<string, EtfTurnoverAuditResult>();
        int total = allCodes.Count, index = 0;

        foreach (var p in providers)
        {
            var codes = allCodes.Where(c => c.Market == p.Market).Select(c => c.Code).ToList();
            var r = new EtfTurnoverAuditResult { Market = p.Market, Codes = codes.Count };
            results[p.Market] = r;
            var listed = codes.Select(c => p.Market + c).ToHashSet(StringComparer.Ordinal);
            r.NotInExchangeList.AddRange(bars.ListEtfCodes(p.Market).Where(c => !listed.Contains(c)));
            foreach (var g in SqliteEtfTurnoverStore.Granularities) r.ByGranularity[g] = (0, 0);

            foreach (var code in codes)
            {
                ct.ThrowIfCancellationRequested();
                index++;
                var barCode = p.Market + code;
                var rows = bars.ReadBars(barCode);
                if (rows.Count == 0)
                {
                    r.CodesWithoutBars++;
                    continue;
                }

                var shareByDay = shares.GetByCode(p.Market, code);
                var updates = new List<(string, string, double)>();
                foreach (var row in rows)
                {
                    bool hasPrev = prevOf.TryGetValue(row.Day, out var prev);
                    double? prevShare = hasPrev && shareByDay.TryGetValue(prev, out var s) ? s : null;
                    var verdict = EtfTurnoverRule.Judge(row.Volume, row.Turnover, prevShare);
                    switch (verdict)
                    {
                        case EtfTurnoverVerdict.Consistent:
                            r.Consistent++;
                            break;
                        case EtfTurnoverVerdict.Unjudgeable:
                            if (!hasPrev || prev < p.FirstDay) r.UnjudgeableBeforeFirstDay++;
                            else r.UnjudgeableOther++;
                            break;
                        case EtfTurnoverVerdict.Wrong or EtfTurnoverVerdict.Missing:
                            var expected = EtfTurnoverRule.Expected(row.Volume!.Value, prevShare)!.Value;
                            var (w, m) = r.ByGranularity.GetValueOrDefault(row.Granularity);
                            if (verdict == EtfTurnoverVerdict.Wrong)
                            {
                                r.Wrong++;
                                r.ByGranularity[row.Granularity] = (w + 1, m);
                                if (r.WrongSamples.Count < MaxSamples)
                                    r.WrongSamples.Add($"{barCode} {GranLabel(row.Granularity)} {row.Day:yyyy-MM-dd}："
                                                     + $"库里 {row.Turnover:0.##} → 应为 {expected:0.##}");
                            }
                            else
                            {
                                r.Missing++;
                                r.ByGranularity[row.Granularity] = (w, m + 1);
                            }
                            if (write) updates.Add((row.Granularity, row.PeriodStart, expected));
                            break;
                    }
                }

                if (updates.Count > 0)
                    lock (SqliteWriteGate.Local) r.Written += bars.UpdateTurnover(barCode, updates);

                ReportQuiet($"检查 {barCode}", index, total);
                if (index % ReportEveryCodes == 0)
                    Report($"　已检查 {index}/{total} 只：{MarketLabel(p.Market)} 错值 {r.Wrong:N0}、空值 {r.Missing:N0}"
                         + (write ? $"、已写回 {r.Written:N0}" : ""), index, total);
            }
        }
        return results;
    }

    private IEnumerable<string> Describe(EtfTurnoverAuditResult r, bool write)
    {
        var label = MarketLabel(r.Market);
        var first = providers.First(p => p.Market == r.Market).FirstDay;
        yield return $"{label}检查完成：交易所列过的 ETF {r.Codes} 只"
                   + (r.CodesWithoutBars > 0 ? $"（其中 {r.CodesWithoutBars} 只本地没有日K，跳过）" : "")
                   + $"；有成交的行里一致 {r.Consistent:N0}、错值 {r.Wrong:N0}、空值 {r.Missing:N0}、"
                   + $"无法裁判 {r.UnjudgeableBeforeFirstDay + r.UnjudgeableOther:N0}"
                   + $"（{first:yyyy-MM-dd} 以前交易所没有份额 {r.UnjudgeableBeforeFirstDay:N0}、"
                   + $"上市首日或交易所那天没列这只 {r.UnjudgeableOther:N0}，原样不动）";
        yield return $"　{label}按口径：" + string.Join("；", r.ByGranularity.Select(kv =>
            $"{GranLabel(kv.Key)} 错值 {kv.Value.Wrong:N0}、空值 {kv.Value.Missing:N0}"));
        if (r.WrongSamples.Count > 0)
            yield return $"　{label}错值样例：" + string.Join("；", r.WrongSamples);
        if (r.NotInExchangeList.Count > 0)
            yield return $"　⚠ 本地名册里另有 {r.NotInExchangeList.Count} 只{label} ETF 在交易所份额表里从没出现过"
                       + (r.Market == "sh" ? "（多为货币 ETF，上交所「ETF 规模」不列它们）" : "（多半是交易所开始有数据以前就退市了）")
                       + "，这一项校正不了，换手率原样不动："
                       + string.Join("、", r.NotInExchangeList.Take(MaxSamples))
                       + (r.NotInExchangeList.Count > MaxSamples ? " 等" : "");
        if (write)
            yield return $"　{label}已写回 {r.Written:N0} 行（只改换手率，价格和量额一行没动）。"
                       + "体检报的「多口径不一致」下次复查时就会消掉。";
    }

    /// <summary>计划行那一格显示的一句话。</summary>
    private static string Summary(IEnumerable<EtfTurnoverAuditResult> rs, bool write)
    {
        var list = rs.ToList();
        int wrong = list.Sum(r => r.Wrong), missing = list.Sum(r => r.Missing), written = list.Sum(r => r.Written);
        return write
            ? $"已写回 {written:N0} 行（错值 {wrong:N0}、空值 {missing:N0}）"
            : wrong + missing > 0
                ? $"错值 {wrong:N0}、空值 {missing:N0}，未写库（切「彻底重查」写回）"
                : "沪深 ETF 换手率全部一致";
    }

    private static string MarketLabel(string market) => market switch
    {
        "sh" => "沪市",
        "sz" => "深市",
        _ => market,
    };

    private static string GranLabel(string g) => g switch
    {
        Granularity.Day => "前复权",
        Granularity.DayRaw => "不复权",
        Granularity.DayAdj => "回测序列",
        _ => g,
    };
}
