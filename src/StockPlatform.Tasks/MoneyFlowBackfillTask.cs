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
/// 【分档资金流·补历史】——逐股把 120 天窗口里缺的行补回来（东财 push2his）。
///
/// ════ 它是什么 ════
/// 超大单/大单/中单/小单各自的净额与净占比。跟【资金净流入】那张 1077 万行的表是**同一件事的
/// 不同精度**、不是替换：那边每行只有一个"主力净额合计"。判断资金性质要看结构不看合计——
/// 同样"主力净流入 1 亿"，超大单进、小单出（机构建仓）跟大单进、超大单出（游资接力）含义
/// 完全相反，合计数把这个信息抹平了。
///
/// ════ 只剩这一条通道（2026-09-12 拆分）════
/// 当日增量归 <see cref="MoneyFlowSnapshotTask"/>（push2delay，59 个请求、一两分钟，已归日更）。
/// 拆开是因为两段的时效性正好相反：快照漏一天就永久没了，补历史 120 天内随时补都来得及。
/// 合在一项里时整项被"耗时长、没时效压力"归进了季度组，快照跟着遭殃——2026-09-09 全市场
/// 整天缺失就是这么丢的。两条通道的数据**逐条比对过、零差异**，写同一张表是安全的。
///
/// ════ 走哪条通道由配置定（2026-09-14）════
/// 两条通道打的 URL 一模一样，差别只在请求由谁发出去，见 <see cref="IMoneyFlowDetailFetcher"/>：
/// 本机网关把 push2his 按域名拦了，HttpClient 那条发不出去，默认走**真浏览器**那条。
/// 所以这个任务里**不该再出现通道细节**——阈值、收尾措辞、节奏一律由通道自报。
/// （2026-09-12 那版把「每 15 个请求歇 2 分钟」写死在判据和注释里，换通道时两头都得改。）
///
/// ════ 一批＝一只票（2026-09-11 迁成新式任务时定的）════
/// 老实现每 100 只才报一句进度。那轮待办只剩 72 只——一句都报不出来，而 push2his 是 5 秒间隔、
/// 每 15 个请求歇 2 分钟、单只失败还要静默重试 2s+10s，于是**必然**哑过 5 分钟，被静默看门狗
/// 当成卡死掐断（掐断不丢数据，但这一项每天记一次失败）。现在抓一只存一只、进度按时间报，
/// <see cref="TaskRunArgs.MaxItems"/>／Deadline 的语义也正好是"这一轮补几只、到点收尾"。
/// </summary>
public sealed class MoneyFlowBackfillTask(
    FetchPaths paths,
    INetInflowDetailRepository repository,
    IMoneyFlowDetailFetcher perStock,
    TimeSpan? progressInterval = null,
    int? maxPerRun = null) : FetchTaskBase<NetInflowDetail>
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

    private int _ok, _failed, _empty, _rows, _consecutiveFail, _todoCount;

    /// <summary>本轮的每轮上限。收尾那句要照实说「每轮只做 N 只」——
    /// 拿默认常量顶的话，换了通道（上限不一样）之后那句话就是错的。</summary>
    private int _capThisRun;

    /// <summary>整个队列还有多少只（不是本轮抓的那几只）——有每轮上限在，
    /// "完成"很容易被读成"补齐了"，所以收尾时必须把还欠多少报出来。</summary>
    private int _pendingCount;

    /// <summary>最后一次失败的原因——收尾时要报出来。
    /// "连不上"和"被限流"的处置完全不同（前者要换网络，后者等着就行），
    /// 只报一句"失败 N 只"等于把这个区别藏起来。</summary>
    private string? _lastFailure;

    /// <summary>有缺口但没到门槛、这一轮故意不补的只数与合计行数。
    /// 必须报出来——"待办 0"很容易被读成"一行不缺"。</summary>
    private int _minorCodes, _minorRows;

    protected override async IAsyncEnumerable<IReadOnlyList<NetInflowDetail>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        repository.EnsureSchema();
        _errors.Clear();
        _skipped = null;
        _ok = _failed = _empty = _rows = _consecutiveFail = _todoCount = _pendingCount = 0;
        _lastFailure = null;
        _sw.Restart();

        if (perStock.PausedUntil is { } until)
        {
            var mins = Math.Max(1, (int)Math.Ceiling((until - DateTime.Now).TotalMinutes));
            var reason = $"分档资金流通道（{perStock.ChannelName}）熔断中，"
                       + $"预计 {until:HH:mm} 恢复（还有约 {mins} 分钟）";
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

        // 门槛：「首次整段回补」＝缺一行就补；「增量」＝缺 3 行以上才补。单日缺口全市场补一遍是
        // 20 小时机时，不该由程序默认替人花掉（用户 2026-09-11 拍板），所以只报不补。
        // 这个模式 2026-09-11 之前挂的是 FetchMode.Thorough，换成 FirstBackfill 是因为它只补缺的、
        // 齐了的票一个请求都不发——按词义那是回补，不是"不管有没有全部重来"的彻底重查。
        bool backfill = args.Mode == FetchMode.FirstBackfill;
        var queue = MoneyFlowBackfillPlan.Build(
            paths.CurrentDb, codes, repository,
            backfill ? MoneyFlowBackfillPlan.BackfillGapThreshold
                     : MoneyFlowBackfillPlan.DefaultGapThreshold);
        _minorCodes = queue.MinorCodes;
        _minorRows = queue.MinorRows;

        if (queue.Unavailable is { } why)
        {
            _errors.Add(why);
            yield break;
        }

        var pending = queue.Todo;
        int never = queue.Never;
        // 每轮上限：调度给了就听调度的，没给就按 MaxPerRun 兜底（跟财务报表同一个形状）。
        // 截在**问几只**上、不是"成功几只"：限流限的是请求数，失败的那几只照样花掉了配额，
        // 骨架的 MaxItems 只数成功批次，单靠它会在一轮里把 5000 只全问一遍。
        int cap = args.MaxItems is > 0 ? args.MaxItems.Value
                : maxPerRun is > 0 ? maxPerRun.Value
                : MoneyFlowBackfillPlan.MaxPerRun;
        _capThisRun = cap;
        var todo = pending.Count > cap ? pending.Take(cap).ToList() : pending;
        _pendingCount = pending.Count;
        if (todo.Count == 0)
        {
            Report($"分档资金流：窗口内（{queue.From:MM-dd}~{queue.To:MM-dd}，"
                 + $"{MoneyFlowBackfillPlan.WindowTradingDays} 个交易日）该有的都有了，不用补"
                 + "——日常增量走快照就够。"
                 + MinorNote(backfill));
            yield break;
        }

        _todoCount = todo.Count;
        Report($"分档资金流补历史：待补 {pending.Count} 只，本轮抓 {todo.Count} 只"
             + (pending.Count > todo.Count ? $"（每轮上限 {cap} 只，剩下的下轮接着来）" : "")
             + "——在窗口内缺 "
             + (backfill ? "1" : $"{MoneyFlowBackfillPlan.DefaultGapThreshold}") + " 行以上"
             + $"（窗口 {queue.From:MM-dd}~{queue.To:MM-dd}，期望按本地日K根数算）"
             + (never > 0 ? $"，其中 {never} 只窗口内一行都没有" : "")
             + "，按最久没抓的先抓（到点或做满本轮上限就收尾，下轮接着来）。"
             + MinorNote(backfill),
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
                    _lastFailure = ex.Message;
                    if (_failed <= 5) _errors.Add($"{code} 分档资金流失败：{ex.Message}");
                    // 连续失败＝这条通道已经被切，继续打只会让封禁更久；抓到的都落库了，下轮接着来。
                    // 阈值和这句话都**由通道自报**（见 IMoneyFlowDetailFetcher）：
                    // 「连不上」（换通道，等多久都不会好）跟「被限流」（等着就行）的处置完全相反，
                    // 2026-09-11 把网关拦截报成限流，就把人往「等一会儿就好了」的方向带了一整天。
                    if (_consecutiveFail >= perStock.GiveUpAfterConsecutiveFailures)
                    {
                        var verdict = perStock.ExhaustedVerdict(_lastFailure);
                        Report($"⚠ 连续 {_consecutiveFail} 只失败，{verdict}，补历史提前收尾。"
                             + $"最后一次的原因：{_lastFailure}。"
                             + $"已成功 {_ok} 只，剩余 {todo.Count - i - 1} 只下轮继续。");
                        _errors.Add($"{verdict}，本轮只补到 {_ok}/{todo.Count} 只。"
                                  + $"最后一次的原因：{_lastFailure}");
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

    /// <summary>
    /// 「有缺口但没补」的那一句。<paramref name="backfill"/>＝true 时门槛已经是 1 行，
    /// 不会再有"没到门槛"的缺口，所以不说话。
    /// </summary>
    private string MinorNote(bool backfill)
        => _minorCodes == 0 || backfill
            ? ""
            : $" ⚠ 另有 {_minorCodes} 只各缺 1~{MoneyFlowBackfillPlan.DefaultGapThreshold - 1} 行"
              + $"（合计 {_minorRows} 行），按当前门槛不补——多半是某天的全市场快照漏了。"
              + "要补的话把这一项的模式切成「首次整段回补」跑一轮（一只一个请求，会跨好几轮）。";

    /// <summary>一批＝一只票的 120 天。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<NetInflowDetail> batch, CancellationToken ct)
    {
        _rows += repository.Upsert(batch);
        return Task.CompletedTask;
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
            int left = Math.Max(0, _pendingCount - _ok - _empty);
            Report($"分档资金流补历史：本轮成功 {_ok} 只、失败 {_failed} 只、接口没数据 {_empty} 只、"
                 + $"写入 {_rows} 行，用时 {Fmt(_sw.Elapsed)}。"
                 + (left > 0 ? $"⚠ 还有 {left} 只没补——这一项每轮只做 "
                             + $"{_capThisRun} 只（这个接口限流太凶：累计十几个请求就被切），"
                             + "勾上【空闲时自动补】让它一轮一轮补完，或者再点几次执行。"
                             : ""));

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

        // 一只都没补、也没出错，才算"这一期做完了"（窗口内本来就齐的时候就是这样）。
        bool nothingToDo = _ok == 0 && _errors.Count == 0;
        return Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, _errors.ToList(), nothingToDo, summary));
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} 小时 {t.Minutes} 分"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒"
        : $"{t.TotalSeconds:F1} 秒";
}
