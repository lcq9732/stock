using System.Diagnostics;
using System.Runtime.CompilerServices;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
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
///
/// ════ 跑完必须回查库（2026-09-16 用户要求）════
/// 抓取侧的对账（服务端自报 total − 停牌 − 实收）只证明"这一轮请求收全了"，证明不了
/// "写进库了"。所以收尾时再用 <see cref="SqliteMoneyFlowDayAudit"/> 查一次库：
/// 当天有日K的个股有多少只，库里的资金流就该有多少行。不齐就**整项判失败**（状态列红字），
/// 并在日志里写清是哪天、差多少、拿什么比的——因为这一项漏一天就永久补不回来，
/// 而别的日更表隔天都还能重抓。
/// </summary>
public sealed class MoneyFlowSnapshotTask(
    INetInflowDetailRepository repository,
    EastMoneyMoneyFlowSnapshotProvider snapshot,
    SqliteMoneyFlowDayAudit? dayAudit = null) : FetchTaskBase<NetInflowDetail>
{
    public override FetchActionId Id => FetchActionId.FetchMoneyFlowSnapshot;

    private readonly Stopwatch _sw = new();
    private readonly List<string> _errors = [];

    /// <summary>本轮没开工的原因（熔断中／还没收盘清算）——这两种都不是失败。</summary>
    private string? _skipped;

    private int _rows;
    private DateTime? _day;

    /// <summary>这一轮的抓取结果——落库之后要拿它把"哪些页到手了"记进进度表。</summary>
    private MoneyFlowSnapshot? _snap;

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
            // 浏览器通道先起来（建 WebView2 + 打开东财页面拿 Cookie，要几秒）。
            // ⚠ 这一步不是可有可无的优化：2026-09-21 起 HttpClient 这条在东财已经一个请求
            //   都过不去了，而浏览器通道照样能抓（见 EastMoneyMoneyFlowSnapshotProvider 的
            //   browser 参数注释）。没起来就照常往下走——抓不到会如实报失败，
            //   但日志里得先把"这轮走的是哪条路"说清楚，否则失败了没人知道该查什么。
            await snapshot.PrepareAsync(ct);

            Report($"分档资金流快照：{snapshot.DescribeChannel()}，拉当日全市场"
                 + $"（每页 {EastMoneyMoneyFlowSnapshotProvider.PageSize} 只、约 60 页）…");
            // 抓取本身不吞异常：这一项漏一天就永久没了，"抓不到"必须记成失败被人看见，
            // 而不是记一条 error 就算完（原实现在合并那一项里是后者，因为还要让补历史接着跑）。
            //
            // 跨轮续抓：哪些页已经在手，按**数据自己报的交易日**问库（不是按今天、也不是按
            // 本地交易日历——日历滞后的话会跳过今天没抓过的页，静默丢一整片数据）。
            snap = await snapshot.FetchAllAsync(ProgressSink, repository.GetSnapshotPages, ct);
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
        _snap = snap;

        // 缺页是常态不是意外（2026-09-21）：东财一轮只放过约 16 页，全市场约 60 页。
        // 所以这里只如实报进度，**不记成错误**——整项成败由收尾时查库的那个判据定，
        // 那个才回答得了"这天到底齐没齐"，而它不依赖某一轮跑成什么样。
        if (snap.MissingPages.Count > 0)
        {
            Report($"　这一轮拿到 {snap.RowsByPage.Count} 页 / {snap.Rows.Count} 只"
                 + (snap.SkippedPages > 0 ? $"（另有 {snap.SkippedPages} 页上几轮已抓）" : "")
                 + $"，还缺 {snap.MissingPages.Count} 页："
                 + string.Join("、", snap.MissingPages.Take(10))
                 + (snap.MissingPages.Count > 10 ? " …" : "")
                 + "。下一轮只补这些页。");
        }

        // 对账：服务端自报的总数减去停牌的，就是本该拿到的行数。差额是**某一页被截断**
        // 的唯一信号——那一页回了 200 状态、解析得动、于是被记成"抓到了"，但里面只有三五行；
        // 表现出来只是"今天少几百只"，缺页清单里也看不见，没人会发现。
        //
        // ⚠ 只在"这一轮自己把所有页都走完了"时才判（2026-09-21）：跨轮续抓的轮次手上
        //   只有一部分页，跟全市场 total 对不上是正常的。那种情形交给收尾时查库的判据，
        //   它看的是库里这天到底有多少行，比抓取侧的对账更靠得住。
        if (snap.Complete && snap.SkippedPages == 0)
        {
            int shortfall = snap.Total - snap.Suspended - snap.Rows.Count;
            if (shortfall > 0)
            {
                var msg = $"分档资金流快照少了 {shortfall} 只（自报 {snap.Total}、停牌 {snap.Suspended}、"
                        + $"实收 {snap.Rows.Count}）——页都走完了还差这么多，多半是某页被截断。";
                Report($"⚠ {msg}");
                _errors.Add(msg);
            }
        }

        yield return snap.Rows;
    }

    /// <summary>
    /// 一轮＝这一轮抓到的那些页，所以就落这一次库。
    ///
    /// ⚠ 落库**之后**才记页号进度（2026-09-21）。顺序反了的话，写库失败时进度里已经记着
    /// "这些页抓过了"，下一轮就会跳过它们——那些票当天的数据从此谁也不会再去补。
    /// </summary>
    protected override Task SaveBatchAsync(IReadOnlyList<NetInflowDetail> batch, CancellationToken ct)
    {
        _rows += repository.Upsert(batch);

        if (_day is { } day && _snap is { } snap && snap.RowsByPage.Count > 0)
            repository.MarkSnapshotPages(day, snap.RowsByPage, snap.QuoteTime ?? DateTime.Now);

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
        var day = CheckDay();
        var errors = _errors.ToList();

        // 有跳过原因时记「本轮没开工」而不是完成——那是"这次没干成"，记成完成会让计划
        // 以为这一期做完了、今天不再来（见 TaskRunResult.Skipped）。盘中跑的那一次全靠这个。
        //
        // ⚠ 没开工**也要回查库**：熔断/盘中跳过的这一轮什么都没抓，而当天要是还空着，
        //   那正是最危险的状态——没人告诉你的话，下一个交易日开盘它就永久没了。
        if (_skipped is { } why)
        {
            if (day is { IsAlert: true })
            {
                Report($"⚠ 本轮没开工（{why}），而且 {WhyRed(day)}");
                errors.Add($"分档资金流{day.Text}——本轮没开工（{why}）");
            }
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Skipped(why, errors));
        }

        var pages = _snap is { } s && _day is { } dd
            ? $" 页进度 {repository.GetSnapshotPages(dd).Count}/{TotalPages(s)}"
              + (s.MissingPages.Count > 0 ? $"，还缺 {s.MissingPages.Count} 页（下轮补）" : "，已补齐")
            : "";
        var summary = $"分档资金流快照：{_day:yyyy-MM-dd} 写入 {_rows} 行，用时 {Fmt(_sw.Elapsed)}。{pages}。"
                    + $"本地共 {repository.CountCodes()} 只 / {repository.Count()} 行。";
        if (day != null) summary += $" 核对：{day.Text}。";
        Report(summary);

        // 这一轮还有页没拿到 → 整项**不算完成**（2026-09-21）。
        //
        // 为什么不能记成完成：缺页就是这一天还没抓齐，而记成完成会让计划以为这一期做完了、
        // 当天不再回来（AlreadyRanOn 只认 Ok）——那正好废掉跨轮续抓，每天只补一轮 16 页。
        // 记成失败，计划会排自动重试，一轮补 15 页，四五轮就齐了。
        //
        // ⚠ 这条判据摆在查库那条**前面**，因为它不依赖本地有没有交易日历和当天的日K：
        //   老库、新建的空库、日历还没更新的机器上，查库那条会"判不了"而放行，
        //   而缺页是抓取侧当场就知道的事实，任何时候都作数。
        if (_snap is { MissingPages.Count: > 0 } s2)
        {
            var msg = $"分档资金流快照这天还缺 {s2.MissingPages.Count} 页（共 {TotalPages(s2)} 页）"
                    + $"——东财一轮只放过十几页，这是常态；下一轮只补缺的页，四五轮能齐。";
            errors.Add(msg);
            if (day is { IsAlert: true }) Report($"⚠ {WhyRed(day)}");
            return Task.FromResult<TaskRunResult?>(
                new TaskRunResult(TaskState.Failed, errors, NothingToDo: false, summary));
        }

        // 库里就是不齐 → 整项失败（状态列标红）。为什么这一项要这么狠，而别的表不：
        // 快照接口只给最近一个交易日，今天不补上，下一个交易日开盘后就**永久**取不回来了。
        if (day is { IsAlert: true })
        {
            Report($"⚠ {WhyRed(day)}");
            errors.Add($"分档资金流{day.Text}");
            return Task.FromResult<TaskRunResult?>(
                new TaskRunResult(TaskState.Failed, errors, NothingToDo: false, summary));
        }

        return Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, errors, NothingToDo: false, summary));
    }

    /// <summary>
    /// 回查库：当天该有多少只、实际有多少只。判据本体在 <see cref="SqliteMoneyFlowDayAudit"/>——
    /// 界面上那一格显示的是同一个判据，两处各写一份迟早漂移。
    ///
    /// 查不成（库被写锁占着、老库还没这些表）就返回 null：核对不了不等于数据不对，
    /// 不能因此把一轮成功的抓取判成失败。
    /// </summary>
    private MoneyFlowDayStatus? CheckDay()
    {
        if (dayAudit == null) return null;
        try { return dayAudit.Check(); }
        catch (Exception ex)
        {
            Report($"（当天齐整度没核对成：{ex.Message}）");
            return null;
        }
    }

    /// <summary>标红时必须说清**为什么红**、以及为什么它比别的项急。</summary>
    private static string WhyRed(MoneyFlowDayStatus d) =>
        $"{d.Day:yyyy-MM-dd} 的分档资金流库里只有 {d.Have} 只，而当天有日线的个股是 {d.Expect} 只，"
      + $"差 {d.Missing} 只（容差 {SqliteMoneyFlowDayAudit.Tolerance}）。"
      + "多半是翻页被限流截断、或整项没跑成。"
      + "⚠ 这份数据的接口**只给最近一个交易日**，下一个交易日开盘后就永久取不回来了"
      + "（事后只能逐股补，5500 个请求换一天）——请现在就重跑本项，"
      + "整天按主键 upsert 去重，已有的行不受影响。";

    /// <summary>全市场一共多少页——服务端自报的只数除以每页 100。拿不到 total 就按已知的算。</summary>
    private static int TotalPages(MoneyFlowSnapshot s) => s.Total > 0
        ? (int)Math.Ceiling(s.Total / (double)EastMoneyMoneyFlowSnapshotProvider.PageSize)
        : s.RowsByPage.Count + s.MissingPages.Count + s.SkippedPages;

    private static string Fmt(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒" : $"{t.TotalSeconds:F1} 秒";
}
