using System.Diagnostics;
using System.Runtime.CompilerServices;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【分档资金流快照】（2026-09-12 从 <see cref="MoneyFlowBackfillTask"/> 拆出来）。
///
/// ════ 为什么拆 ════
/// 原来快照和逐股补历史挤在一项里，于是整项被"补历史耗时长、没有时效压力"这个理由归进了
/// 季度定期组·空闲时补。可这两段的时效性正好相反：
///
///   · 快照（这一项）——接口只给<b>最近一个交易日</b>。当天收盘后没跑，下一个交易日开盘
///     一到就滚到新一天，那天的分档资金流<b>永久取不回来</b>。2026-09-09 全市场整天缺失
///     就是这么来的：那天没轮到跑，事后想补只能走逐股通道，5500 个请求换回一天数据。
///   · 补历史——120 天窗口内随时补都来得及，慢慢补就行。
///
/// 混在一项里还有个副作用：push2his 被网关拦掉之后，这一项每轮都带一堆错误，
/// "今天的快照到底抓没抓到"反而被淹没了。拆开之后两项各自成败分明。
///
/// ════ 一批＝一整天 ════
/// 快照是"一整天要么有要么没有"的事，所以整批 yield 一次：中断就整批不落库、下次重来，
/// 不会留下半个市场的当天数据（那种残缺事后完全看不出来）。
/// </summary>
public sealed class MoneyFlowSnapshotTask(
    INetInflowDetailRepository repository,
    EastMoneyMoneyFlowSnapshotProvider snapshot) : FetchTaskBase<NetInflowDetail>
{
    public override FetchActionId Id => FetchActionId.FetchMoneyFlowSnapshot;

    private readonly Stopwatch _sw = new();
    private readonly List<string> _errors = [];

    /// <summary>本轮没开工的原因（熔断中／还没收盘清算）——这两种都不是失败。</summary>
    private string? _skipped;

    private int _rows;
    private DateTime? _day;

    protected override async IAsyncEnumerable<IReadOnlyList<NetInflowDetail>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        repository.EnsureSchema();
        _errors.Clear();
        _skipped = null;
        _rows = 0;
        _day = null;
        _sw.Restart();

        if (snapshot.PausedUntil is { } until)
        {
            var mins = Math.Max(1, (int)Math.Ceiling((until - DateTime.Now).TotalMinutes));
            var reason = $"东财 push2delay 限流熔断中，预计 {until:HH:mm} 恢复（还有约 {mins} 分钟）";
            Report($"{reason}，本轮不抓快照。");
            _skipped = reason;
            yield break;
        }

        void Forward(string s) => Report(s);
        snapshot.OnStatus += Forward;

        MoneyFlowSnapshot? snap;
        try
        {
            Report($"分档资金流快照：从 {snapshot.Host} 拉当日全市场"
                 + $"（每页 {EastMoneyMoneyFlowSnapshotProvider.PageSize} 只、约 60 页）…");
            // 抓取本身不吞异常：这一项漏一天就永久没了，"抓不到"必须记成失败被人看见，
            // 而不是记一条 error 就算完（原实现在合并那一项里是后者，因为还要让补历史接着跑）。
            snap = await snapshot.FetchAllAsync(ProgressSink, ct);
        }
        finally
        {
            snapshot.OnStatus -= Forward;
        }

        if (snap.TradeDate is not { } day || snap.Rows.Count == 0)
            throw new InvalidOperationException(
                "分档资金流快照一行都没拿到（接口变了或被限流）——这一项漏一天就永久补不回来了，"
                + "请看上面的日志确认 push2delay 通不通。");

        if (snap.IsIntraday)
        {
            var reason = $"分档资金流快照要等收盘清算（行情时间 {snap.QuoteTime:M-d HH:mm}）";
            Report($"⚠ {reason}——这会儿拿到的是半天的资金流，不入库。收盘后再跑这一项。");
            _skipped = reason;
            yield break;
        }

        _day = day;

        // 对账：服务端自报的总数减去停牌的，就是本该拿到的行数。差额是**静默丢数据**的唯一
        // 信号——翻页少翻一页、某页被限流截断，表现出来都只是"今天少几百只"，没人会发现。
        int missing = snap.Total - snap.Suspended - snap.Rows.Count;
        if (missing > 0)
        {
            var msg = $"分档资金流快照少了 {missing} 只（自报 {snap.Total}、停牌 {snap.Suspended}、"
                    + $"实收 {snap.Rows.Count}）——多半是某页被限流截断，下轮会补上。";
            Report($"⚠ {msg}");
            _errors.Add(msg);
        }

        yield return snap.Rows;
    }

    /// <summary>一批＝一整天的全市场，所以就落这一次库。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<NetInflowDetail> batch, CancellationToken ct)
    {
        _rows += repository.Upsert(batch);
        return Task.CompletedTask;
    }

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        Report("分档资金流快照中断。快照是整批写的，中断这一批不落库，下次重来即可。");
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        // 有跳过原因时记「本轮没开工」而不是完成——那是"这次没干成"，记成完成会让计划
        // 以为这一期做完了、今天不再来（见 TaskRunResult.Skipped）。盘中跑的那一次全靠这个。
        if (_skipped is { } why)
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Skipped(why, _errors.ToList()));

        var summary = $"分档资金流快照：{_day:yyyy-MM-dd} 写入 {_rows} 行，用时 {Fmt(_sw.Elapsed)}。"
                    + $"本地共 {repository.CountCodes()} 只 / {repository.Count()} 行。";
        Report(summary);
        return Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, _errors.ToList(), NothingToDo: false, summary));
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒" : $"{t.TotalSeconds:F1} 秒";
}
