using System.Diagnostics;
using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【拉取分档资金流】（2026-09-11 从 FetchOrchestrator 迁成新式任务）。
///
/// ════ 为什么迁 ════
/// 老实现每 100 只才报一句进度。2026-09-11 那轮待办只剩 72 只——一句都报不出来，而
/// push2his 是 5 秒间隔、每 15 个请求歇 2 分钟、单只失败还要静默重试 2s+10s（三次 25 秒超时
/// 摊下来近 90 秒），于是**必然**哑过 5 分钟，被静默看门狗当成卡死掐断。掐断本身不丢数据
/// （落库的都在），但这一项每天记一次失败，而且待办数只要少于 100 只就永远卡在这儿。
/// 框架的形状正好治这个：一批＝一只票，抓一只存一只，进度按时间报，到点/到量由骨架收尾。
///
/// ════ 它是什么 ════
/// 超大单/大单/中单/小单各自的净额与净占比。跟【资金净流入】那张 1077 万行的表是**同一件事的
/// 不同精度**、不是替换：那边每行只有一个"主力净额合计"。判断资金性质要看结构不看合计——
/// 同样"主力净流入 1 亿"，超大单进、小单出（机构建仓）跟大单进、超大单出（游资接力）含义
/// 完全相反，合计数把这个信息抹平了。
///
/// ════ 两条通道，一前一后跑，各干各的 ════
/// <b>① 全市场当日快照</b>（<see cref="EastMoneyMoneyFlowSnapshotProvider"/>，push2delay）——
/// 约 60 个请求把**当天全市场**拿全，一两分钟。日常增量全靠它。
/// <b>② 逐股补历史</b>（<see cref="EastMoneyMoneyFlowProvider"/>，push2his）——
/// 一只票一个请求、给它最近 120 个交易日。快照只有当天，历史缺口只有它补得了。
/// 两条通道的数据**逐条比对过、零差异**（见快照 provider 的类注释），所以混写同一张表是安全的。
///
/// ════ ⚠ 快照不走骨架的批，直接落库 ════
/// 快照是"一整天要么有要么没有"的事：整批 Upsert，中断这一批就不落库、下次重来。它要是也
/// 当成一批 yield 出去，就会占掉 <see cref="TaskRunArgs.MaxItems"/> 一个额度，而那个额度的
/// 语义应该纯粹是"补历史这一轮抓几只"。所以快照在 <see cref="FetchAsync"/> 里自己写完，
/// 只有逐股那段才 yield。
/// </summary>
public sealed class MoneyFlowDetailTask(
    FetchPaths paths,
    INetInflowDetailRepository repository,
    EastMoneyMoneyFlowProvider? perStock,
    EastMoneyMoneyFlowSnapshotProvider? snapshot,
    TimeSpan? progressInterval = null) : FetchTaskBase<NetInflowDetail>
{
    public override FetchActionId Id => FetchActionId.FetchMoneyFlowDetail;

    /// <summary>逐股那段，两句进度之间最多隔这么久。
    /// 看门狗默认阈值 5 分钟，20 秒留了十几倍余量，同时 5900 只的大轮次也不会把日志刷爆。
    /// 构造时可以覆盖，但那只给测试用——生产代码一律用默认值。</summary>
    private readonly TimeSpan _progressInterval = progressInterval ?? TimeSpan.FromSeconds(20);

    private readonly Stopwatch _sw = new();
    private readonly List<string> _errors = [];

    /// <summary>本轮没开工的原因（熔断中／还没收盘清算）。第一条为准，跟老实现一致。</summary>
    private string? _skipped;

    private bool _snapshotWrote;
    private int _ok, _failed, _empty, _rows, _consecutiveFail, _todoCount;

    protected override async IAsyncEnumerable<IReadOnlyList<NetInflowDetail>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        repository.EnsureSchema();
        _errors.Clear();
        _skipped = null;
        _snapshotWrote = false;
        _ok = _failed = _empty = _rows = _consecutiveFail = _todoCount = 0;
        _sw.Restart();

        if (perStock == null && snapshot == null)
        {
            Report("没有配置分档资金流数据源（东财），跳过。");
            yield break;
        }

        // ── 通道①：全市场当日快照（整批落库，不走骨架的批）──
        await RunSnapshotAsync(ct);

        // ── 通道②：逐股补历史 ──
        if (perStock == null) yield break;

        if (perStock.PausedUntil is { } until)
        {
            var mins = Math.Max(1, (int)Math.Ceiling((until - DateTime.Now).TotalMinutes));
            var reason = $"东财 push2his 限流熔断中，预计 {until:HH:mm} 恢复（还有约 {mins} 分钟）";
            Report($"{reason}，本轮不补历史。已抓到的都在库里，恢复后接着来。");
            _skipped ??= reason;
            yield break;
        }

        var codes = MoneyFlowBackfillPlan.LocalStockCodes(paths.CurrentDb);
        if (codes.Count == 0)
        {
            _errors.Add("本地还没有股票名册，补历史这一段没有可抓的标的——请先跑一次【股票名册与流通市值】");
            yield break;
        }

        var (todo, never) = MoneyFlowBackfillPlan.Build(codes, repository);
        if (todo.Count == 0)
        {
            Report("分档资金流：每只票的 120 天历史都齐了，不用补——日常增量走快照就够。");
            yield break;
        }

        _todoCount = todo.Count;
        Report($"分档资金流补历史：{todo.Count} 只不足 {MoneyFlowBackfillPlan.FullWindowRows} 行"
             + (never > 0 ? $"（其中 {never} 只库里一行都没有）" : "")
             + "，按最久没抓的先抓（到点或做满本轮上限就收尾，下轮接着来）。",
               0, todo.Count);

        void Forward(string s) => Report(s);
        perStock.OnStatus += Forward;
        try
        {
            var lastReport = DateTime.Now;
            for (int i = 0; i < todo.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var code = todo[i];

                // yield 不能待在 try/catch 里，所以先把这一只的结果接住，出了 try 再吐出去。
                List<NetInflowDetail>? rows = null;
                try
                {
                    rows = await perStock.FetchAsync(code, ct);
                    _consecutiveFail = 0;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _failed++; _consecutiveFail++;
                    if (_failed <= 5) _errors.Add($"{code} 分档资金流失败：{ex.Message}");
                    // 连续失败＝已被限流，继续打只会让封禁更久；抓到的都落库了，下轮接着来。
                    if (_consecutiveFail >= 15)
                    {
                        Report($"⚠ 连续 {_consecutiveFail} 只失败，判定被限流，补历史提前收尾。"
                             + $"已成功 {_ok} 只，剩余 {todo.Count - i - 1} 只下轮继续。");
                        _errors.Add($"push2his 限流，本轮只补到 {_ok}/{todo.Count} 只。");
                        break;
                    }
                }

                if (rows is { Count: > 0 })
                {
                    _ok++;
                    yield return rows;
                }
                else if (rows != null)
                {
                    // 空返回要单独计数：它既不是成功也不是失败，原来两个计数器都不动——于是
                    // 342 只 920 开头的票因为 secid 拼错常年抓不到，日志上却什么都看不出来。
                    _empty++;
                }

                // 按**时间**报进度，不按只数（老实现每 100 只一句，待办不足 100 只时全程哑火，
                // 正是这一项被看门狗掐断的原因）。最后一只一定报，收尾时有个准数。
                var now = DateTime.Now;
                if (now - lastReport >= _progressInterval || i + 1 == todo.Count)
                {
                    lastReport = now;
                    Report($"分档资金流补历史：{i + 1}/{todo.Count}"
                         + $"（成功 {_ok}、失败 {_failed}、接口没数据 {_empty}、{_rows} 行）",
                           i + 1, todo.Count);
                }
            }
        }
        finally
        {
            perStock.OnStatus -= Forward;
        }
    }

    /// <summary>一批＝一只票的 120 天。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<NetInflowDetail> batch, CancellationToken ct)
    {
        _rows += repository.Upsert(batch);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 通道①：全市场当日快照。
    ///
    /// 三种不写库的情况，都不算失败：没配这条通道、通道正在熔断、**还没收盘清算**。
    /// 最后一种要紧——盘中拿到的是半天的资金流，写进去会污染当天那一行，事后完全看不出来。
    /// </summary>
    private async Task RunSnapshotAsync(CancellationToken ct)
    {
        if (snapshot == null) return;

        if (snapshot.PausedUntil is { } until)
        {
            var mins = Math.Max(1, (int)Math.Ceiling((until - DateTime.Now).TotalMinutes));
            var reason = $"东财 push2delay 限流熔断中，预计 {until:HH:mm} 恢复（还有约 {mins} 分钟）";
            Report($"{reason}，本轮不抓快照。");
            _skipped ??= reason;
            return;
        }

        void Forward(string s) => Report(s);
        snapshot.OnStatus += Forward;
        var sw = Stopwatch.StartNew();
        try
        {
            Report($"分档资金流快照：从 {snapshot.Host} 拉当日全市场"
                 + $"（每页 {EastMoneyMoneyFlowSnapshotProvider.PageSize} 只、约 60 页）…",
                   phase: "快照");
            var snap = await snapshot.FetchAllAsync(ProgressSink, ct);

            if (snap.TradeDate is not { } day || snap.Rows.Count == 0)
            {
                var msg = "分档资金流快照一行都没拿到（接口变了或被限流），本轮跳过快照。";
                Report($"⚠ {msg}");
                _errors.Add(msg);
                return;
            }

            if (snap.IsIntraday)
            {
                var reason = $"分档资金流快照要等收盘清算（行情时间 {snap.QuoteTime:M-d HH:mm}）";
                Report($"⚠ {reason}——这会儿拿到的是半天的资金流，不入库。收盘后再跑这一项。");
                _skipped ??= reason;
                return;
            }

            // 整批写：中断这一批就不落库，下次重来即可（所以不走骨架的流式落库）。
            int rows = repository.Upsert(snap.Rows);
            _snapshotWrote = true;
            Report($"分档资金流快照：{day:yyyy-MM-dd} 写入 {rows} 行"
                 + $"（全市场 {snap.Total} 只，停牌等没数据的 {snap.Suspended} 只），"
                 + $"用时 {Fmt(sw.Elapsed)}。", phase: "快照");

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
        }
        catch (OperationCanceledException)
        {
            Report("分档资金流快照中断。快照是整批写的，中断这一批不落库，下次重来即可。");
            throw;
        }
        catch (Exception ex)
        {
            var msg = $"分档资金流快照失败：{ex.Message}";
            Report($"⚠ {msg}（补历史那一段照跑）");
            _errors.Add(msg);
        }
        finally
        {
            snapshot.OnStatus -= Forward;
        }
    }

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        Report($"分档资金流补历史中断，本轮已落库 {_rows} 行（{_ok} 只），下次接着来。");
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (_todoCount > 0)
        {
            Report($"分档资金流补历史：本轮成功 {_ok} 只、失败 {_failed} 只、接口没数据 {_empty} 只、"
                 + $"写入 {_rows} 行，用时 {Fmt(_sw.Elapsed)}。");

            // 大面积"接口没数据"不是数据的问题，是我们请求拼错了——secid 前缀、代码段判断这类。
            // 它不会抛异常，所以不主动喊一声就永远没人知道（2026-09-06 那 342 只就是这么埋了两天）。
            if (_empty > 0 && _empty >= _ok + _failed)
            {
                var msg = $"分档资金流补历史：{_empty} 只接口返回空（占本轮 {_empty}/{_ok + _failed + _empty}）"
                        + "——多半是请求拼错了（secid 前缀/代码段），不是这些票真没数据。";
                Report($"⚠ {msg}");
                _errors.Add(msg);
            }
        }

        var summary = $"分档资金流：本地共 {repository.CountCodes()} 只 / {repository.Count()} 行，"
                    + $"用时 {Fmt(_sw.Elapsed)}。";
        Report(summary);

        // 有跳过原因时记「本轮没开工」而不是完成——那是"这次没干成"，记成完成会让计划
        // 以为这一期做完了、把下次间隔拉长（见 TaskRunResult.Skipped）。
        if (_skipped is { } why)
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Skipped(why, _errors.ToList()));

        // 两段都没活干才算"这一期做完了"。
        bool nothingToDo = !_snapshotWrote && _ok == 0 && _errors.Count == 0;
        return Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, _errors.ToList(), nothingToDo, summary));
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} 小时 {t.Minutes} 分"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒"
        : $"{t.TotalSeconds:F1} 秒";
}
