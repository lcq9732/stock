using System.Runtime.CompilerServices;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>一轮校正的统计（给日志、界面那一格和测试用）。</summary>
public sealed class EtfTurnoverAuditResult
{
    public int Codes { get; set; }
    public int CodesWithoutBars { get; set; }
    /// <summary>本地名册里有、上交所份额表里一天都没出现过的沪市 ETF（多为货币 ETF）——校正不了，只报数。</summary>
    public List<string> NotInSseList { get; } = [];
    public int Consistent { get; set; }
    public int Wrong { get; set; }
    public int Missing { get; set; }
    /// <summary>判不了：前一交易日在 2012-01-04 以前（上交所那时还没有份额数据）。</summary>
    public int UnjudgeableBefore2012 { get; set; }
    /// <summary>判不了：其余（上市首日、上交所那天没列这只）。</summary>
    public int UnjudgeableOther { get; set; }
    public int Written { get; set; }
    /// <summary>按口径分的 (错值, 空值)。</summary>
    public Dictionary<string, (int Wrong, int Missing)> ByGranularity { get; } = new();
    /// <summary>错值样例（最多 <see cref="EtfTurnoverRecalcTask.MaxSamples"/> 条）。</summary>
    public List<string> WrongSamples { get; } = [];
}

/// <summary>
/// 【ETF换手率校正】（2026-09-23，见 doc/etf-turnover-recalc-design.md）——
/// 拿上交所官方的每日 ETF 份额，按「成交量 ÷ 前一交易日份额」统一重算**沪市 ETF** 的换手率。
///
/// 修的是两件事（证据都在 <see cref="EtfTurnoverRule"/> 的类注释里）：
///   ① **错值**：腾讯自己的口径不统一（2024-10 以前 ÷T-1、之后多数 ÷T 又夹着 ÷T-1），
///      同一天两个接口还可能不一样——那种被体检报成「多口径不一致」、【重新拉取失败】修不好；
///   ② **空值**：腾讯的 ETF 换手率大约 2022 年年中才开始有，之前全是 0（沪市约 29.8 万行），
///      两边都是 0，体检看不出来。
///
/// ════ 流程 ════
/// 1. 补份额：交易日历里 2012-01-04 起、EtfShare 表还没有、也没被确认为空的日子，一天一个请求。
///    一天一批落库，停在哪都不丢。
/// 2. 检查（两档都做）：逐只 ETF 读三个口径的日K，按判据分成一致/错值/空值/无法裁判。
/// 3. 写回（只有「彻底重查」做）：错值和空值改成期望值。只写 turnover 一列。
///
/// ════ 两档 ════
/// 「日常增量」＝只补份额、只报告，**不写 Bar**；「彻底重查」＝同上再写回。
/// 选 Thorough 而不是 FirstBackfill 是按 2026-09-11 定的词义：彻底重查＝不管原来有没有、全部重来，
/// 这里正是要覆盖已有的错值。先跑增量看报告，数字对了再跑彻底重查。
///
/// ════ 名单从哪来 ════
/// 从 EtfShare 表本身（上交所列过的全部 ETF），Bar 里按 'sh' + 代码找——这是 ETF 在 Bar 里存的形式
/// （前缀是新浪 ETF 列表接口原样给的，见 SqliteMarginShortBalanceFiller.IsShanghaiByStoredPrefix）。
/// 不按代码规则猜哪些是沪市 ETF。
///
/// ⚠ 深市不在这一项里：深交所的份额源还没核。
/// </summary>
public sealed class EtfTurnoverRecalcTask(
    IEtfShareRepository shares,
    IEtfShareProvider provider,
    ITradingDayRepository tradingDays,
    IDailyFetchNoDataRepository noData,
    SqliteEtfTurnoverStore bars) : FetchTaskBase<EtfShareRow>
{
    /// <summary>上交所 ETF 份额的第一天（2026-09-23 实测：2011-12-30 空、2012-01-04 有 23 只）。更早的一个请求都不发。</summary>
    public static readonly DateOnly SseFirstDay = new(2012, 1, 4);

    /// <summary>
    /// 空响应要过几个自然日才认定"上交所那天确实没有"。当天的份额收盘后才发布，
    /// 近几天的空可能只是还没出来，记进名单就永远不会再问了。
    /// </summary>
    private const int ConfirmEmptyAfterDays = 3;

    /// <summary>日志里列几条错值样例。</summary>
    public const int MaxSamples = 8;

    /// <summary>补份额时每多少天打一条日志（其余只喂看门狗）。</summary>
    private const int ReportEveryDays = 100;

    /// <summary>检查时每多少只打一条日志。</summary>
    private const int ReportEveryCodes = 100;

    public override FetchActionId Id => FetchActionId.StepEtfTurnoverFix;

    /// <summary>最近一轮的统计。测试读它。</summary>
    public EtfTurnoverAuditResult? LastResult { get; private set; }

    protected override async IAsyncEnumerable<IReadOnlyList<EtfShareRow>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var (calendar, have, confirmed) = await Task.Run(() => (
            tradingDays.GetAll().Select(DateOnly.FromDateTime).ToList(),
            shares.GetDays(),
            noData.GetConfirmed(IDailyFetchNoDataRepository.EtfShareDataset)), ct);

        var todo = calendar
            .Where(d => d >= SseFirstDay && d <= today && !have.Contains(d) && !confirmed.Contains(d))
            .OrderBy(d => d)
            .ToList();

        if (todo.Count == 0)
        {
            Report($"上交所 ETF 份额：本地已有 {have.Count} 个交易日，没有要补的");
            yield break;
        }
        Report($"上交所 ETF 份额：要补 {todo.Count} 个交易日（{todo[0]:yyyy-MM-dd} ~ {todo[^1]:yyyy-MM-dd}），"
             + "一天一个请求，一天一批落库（停在哪都不丢）…");

        using var _ = ForwardStatus(h => provider.OnStatus += h, h => provider.OnStatus -= h);
        int done = 0, empty = 0, rowsTotal = 0;
        foreach (var day in todo)
        {
            ct.ThrowIfCancellationRequested();
            var rows = await provider.GetDayAsync(day, ct);
            done++;

            if (rows.Count == 0)
            {
                empty++;
                if (day < today.AddDays(-ConfirmEmptyAfterDays))
                    await Task.Run(() =>
                    {
                        lock (SqliteWriteGate.Local)
                            noData.Confirm(IDailyFetchNoDataRepository.EtfShareDataset, day);
                    }, ct);
                ReportQuiet($"份额 {day:yyyy-MM-dd}：上交所没有数据", done, todo.Count);
            }
            else
            {
                rowsTotal += rows.Count;
                ReportQuiet($"份额 {day:yyyy-MM-dd}：{rows.Count} 只", done, todo.Count);
            }

            if (done % ReportEveryDays == 0 || done == todo.Count)
                Report($"　份额 {done}/{todo.Count} 天（当前 {day:yyyy-MM-dd}），共 {rowsTotal:N0} 行"
                     + (empty > 0 ? $"，{empty} 天上交所没有数据" : ""), done, todo.Count);

            if (rows.Count > 0) yield return rows;
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
            ? "开始检查并**写回**沪市 ETF 换手率（彻底重查：错值和空值改成「成交量 ÷ 前一交易日官方份额」）…"
            : "开始检查沪市 ETF 换手率（日常增量：只报告，不写库）…");

        // ⚠ 整段是同步的重活（九百多只 × 三四千天 × 3 个口径），必须自己推线程池——
        // 骨架不会替你推，同步跑会把 UI 整段冻死（见 feedback_task_must_offload_heavy_sync）。
        var r = await Task.Run(() => Audit(write, ct), ct);
        LastResult = r;

        foreach (var line in Describe(r, write)) Report(line);

        bool hasIssues = r.Wrong + r.Missing > 0;
        return TaskRunResult.Ok(
            nothingToDo: stats.Items == 0 && r.Written == 0 && !hasIssues,
            progress: Summary(r, write));
    }

    private EtfTurnoverAuditResult Audit(bool write, CancellationToken ct)
    {
        var calendar = tradingDays.GetAll().Select(DateOnly.FromDateTime).OrderBy(d => d).ToList();
        var prevOf = new Dictionary<DateOnly, DateOnly>(calendar.Count);
        for (int i = 1; i < calendar.Count; i++) prevOf[calendar[i]] = calendar[i - 1];

        var codes = shares.GetCodes();
        var r = new EtfTurnoverAuditResult { Codes = codes.Count };
        var listed = codes.Select(c => "sh" + c).ToHashSet(StringComparer.Ordinal);
        r.NotInSseList.AddRange(bars.ListShanghaiEtfCodes().Where(c => !listed.Contains(c)));
        foreach (var g in SqliteEtfTurnoverStore.Granularities) r.ByGranularity[g] = (0, 0);

        for (int i = 0; i < codes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var code = codes[i];
            var barCode = "sh" + code;
            var rows = bars.ReadBars(barCode);
            if (rows.Count == 0)
            {
                r.CodesWithoutBars++;
                continue;
            }

            var shareByDay = shares.GetByCode(code);
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
                        if (!hasPrev || prev < SseFirstDay) r.UnjudgeableBefore2012++;
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

            ReportQuiet($"检查 {barCode}", i + 1, codes.Count);
            if ((i + 1) % ReportEveryCodes == 0)
                Report($"　已检查 {i + 1}/{codes.Count} 只：错值 {r.Wrong:N0}、空值 {r.Missing:N0}"
                     + (write ? $"、已写回 {r.Written:N0}" : ""), i + 1, codes.Count);
        }
        return r;
    }

    private static IEnumerable<string> Describe(EtfTurnoverAuditResult r, bool write)
    {
        yield return $"检查完成：上交所列过的 ETF {r.Codes} 只"
                   + (r.CodesWithoutBars > 0 ? $"（其中 {r.CodesWithoutBars} 只本地没有日K，跳过）" : "")
                   + $"；有成交的行里一致 {r.Consistent:N0}、错值 {r.Wrong:N0}、空值 {r.Missing:N0}、"
                   + $"无法裁判 {r.UnjudgeableBefore2012 + r.UnjudgeableOther:N0}"
                   + $"（2012 年以前上交所没有份额 {r.UnjudgeableBefore2012:N0}、"
                   + $"上市首日或上交所那天没列这只 {r.UnjudgeableOther:N0}，原样不动）";
        yield return "　按口径：" + string.Join("；", r.ByGranularity.Select(kv =>
            $"{GranLabel(kv.Key)} 错值 {kv.Value.Wrong:N0}、空值 {kv.Value.Missing:N0}"));
        if (r.WrongSamples.Count > 0)
            yield return "　错值样例：" + string.Join("；", r.WrongSamples);
        if (r.NotInSseList.Count > 0)
            yield return $"　⚠ 本地名册里另有 {r.NotInSseList.Count} 只沪市 ETF 上交所份额表里从没出现过（多为货币 ETF，"
                       + "上交所「ETF 规模」不列它们），这一项校正不了，换手率原样不动："
                       + string.Join("、", r.NotInSseList.Take(MaxSamples))
                       + (r.NotInSseList.Count > MaxSamples ? " 等" : "");
        if (write)
            yield return $"　已写回 {r.Written:N0} 行（只改换手率，价格和量额一行没动）。"
                       + "体检报的「多口径不一致」下次复查时就会消掉。";
        else if (r.Wrong + r.Missing > 0)
            yield return "　这是「日常增量」，只检查、一行没写。确认数字没问题后，把这一项的模式切到「彻底重查」再跑一次即可写回。";
    }

    /// <summary>计划行那一格显示的一句话。</summary>
    private static string Summary(EtfTurnoverAuditResult r, bool write) =>
        write
            ? $"已写回 {r.Written:N0} 行（错值 {r.Wrong:N0}、空值 {r.Missing:N0}）"
            : r.Wrong + r.Missing > 0
                ? $"错值 {r.Wrong:N0}、空值 {r.Missing:N0}，未写库（切「彻底重查」写回）"
                : "沪市 ETF 换手率全部一致";

    private static string GranLabel(string g) => g switch
    {
        Granularity.Day => "前复权",
        Granularity.DayRaw => "不复权",
        Granularity.DayAdj => "回测序列",
        _ => g,
    };
}
