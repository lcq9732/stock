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
/// 【板块指数合成】（2026-09-21 从编排器迁到新框架，见 doc/board-index-synth-design.md §3）——
/// **不联网**：用本地成分股 + 个股回测口径日K（<c>day_adj</c>）等权合成板块指数日K。
///
/// ════ 一批＝一个板块 ════
/// 落库粒度本来就是一个板块（先 <c>DeleteByCode</c> 再整段写，一个事务），批边界现成。
/// 迁过来新增的是 <see cref="TaskRunArgs.MaxItems"/>/<see cref="TaskRunArgs.Deadline"/>
/// 能在板块边界干净收尾。
///
/// ════ 为什么合成这一步要推线程池 ════
/// 它是**纯 CPU + 大量读库**的活（每个板块要把全部成分股的 day_adj 全历史读进来算），
/// 骨架不替子类推线程池，首个 await 之前干这些会把界面冻死
/// （见 feedback_task_must_offload_heavy_sync）。
///
/// ════ 进度按时间报，不按个数 ════
/// 板块之间的成分股数量差一个量级，按 40 个一报实测能哑到 2 分 35 秒——那是全程最长的一段静默。
/// 所以沿用 <see cref="ProgressThrottle"/>（按时间），心跳则每个板块一次。
///
/// ⚠ 末尾**主动回收 WAL**：这条路是全库写得最重的一条（950 个板块 × 约 4,900 根 ≈ 468 万行）。
/// SQLite 的 autocheckpoint 是被动的，只要有任何读连接活着就跳过——2026-09-11 就是因为一个
/// 残留进程握着库两个多小时，这 468 万行全堆在 WAL 里涨到 162 GB。拿到 busy 会明确报出来，
/// 那是"有人握着库"的唯一早期信号。
/// </summary>
public sealed class BoardIndexTask(
    FetchPaths paths,
    IBoardRepository boardRepository,
    IManifestStore? manifestStore = null) : FetchTaskBase<BoardIndexTask.BoardBars>
{
    public override FetchActionId Id => FetchActionId.StepBoardIndex;

    /// <summary>一个板块合成出来的指数日K。</summary>
    /// <param name="BoardCode">板块代码（<c>BKxxxx</c>），就是 Bar 表里的 code。</param>
    /// <param name="Name">板块名，写 StockMeta 用。</param>
    /// <param name="Bars">合成出来的日K；空＝成分股数据不够，那也要删掉旧的。</param>
    /// <param name="Append">true＝追加（只插新的那几根，不删旧的）；false＝整段重算（先删后写）。</param>
    /// <param name="State">这一轮该记下的合成状态；null＝别记（失败/没算出来）。</param>
    public sealed record BoardBars(string BoardCode, string Name, List<Bar> Bars,
                                   bool Append = false, BoardIndexState? State = null);

    private SqliteBarRepository Bars => _bars ??= new SqliteBarRepository(paths.CurrentDb);
    private SqliteBarRepository? _bars;

    private SqliteBoardIndexStateRepository States => _states ??= new SqliteBoardIndexStateRepository(paths.CurrentDb);
    private SqliteBoardIndexStateRepository? _states;

    private readonly List<string> _errors = [];
    private readonly List<(string Code, string Name)> _meta = [];
    private readonly List<(string BoardCode, double ChangePct, double Amount)> _quotes = [];
    private int _boards, _withBars, _totalBars;
    private int _full, _appended, _upToDate;
    private DateTime _asOf;
    private string? _nothingToDoReason;

    protected override async IAsyncEnumerable<IReadOnlyList<BoardBars>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _errors.Clear();
        _meta.Clear();
        _quotes.Clear();
        _boards = _withBars = _totalBars = 0;
        _nothingToDoReason = null;

        var boards = await Task.Run(() => boardRepository.QueryBoards(), ct);
        if (boards.Count == 0)
        {
            _nothingToDoReason = "本地还没有板块数据";
            Report("（本地还没有板块数据，跳过板块指数合成——先跑一次【概念和行业板块】和"
                 + "【板块成分股】才有成分可算）");
            yield break;
        }

        _boards = boards.Count;
        _asOf = DateTime.Now;
        var today = DateTime.Today;
        var tick = new ProgressThrottle(ProgressSink);
        int done = 0;

        // ── 整段重算的两个来由 ──
        // ①「首次整段回补」模式：人手动兜底，判据万一漏了什么就靠它。
        // ② day_adj 刚被【重算回测序列】重写过：板块指数是拿它累乘的，整条历史都失效了，
        //    而这一项自己看不出来（见 Manifest.BoardIndexNeedsFullRebuild）。
        bool forceFull = args.Mode.HasFlag(FetchMode.FirstBackfill);
        if (!forceFull && manifestStore != null)
        {
            var flagged = await Task.Run(() => manifestStore.Load().BoardIndexNeedsFullRebuild, ct);
            if (flagged)
            {
                forceFull = true;
                Report("【重算回测序列】重写过 day_adj，本轮**整段重算**所有板块指数（它是拿 day_adj 累乘的）。");
            }
        }
        if (args.Mode.HasFlag(FetchMode.FirstBackfill))
            Report("「首次整段回补」：不看合成状态，所有板块整段重算。");

        var states = forceFull
            ? new Dictionary<string, BoardIndexState>(StringComparer.Ordinal)
            : await Task.Run(() => States.GetAll(), ct);

        foreach (var board in boards)
        {
            ct.ThrowIfCancellationRequested();

            BoardBars? one = null;
            try
            {
                // 纯 CPU + 大量读库，推线程池（见类注释）
                one = await Task.Run(() => PlanAndSynthesize(board, states, today, forceFull), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _errors.Add($"板块「{board.Name}」({board.BoardCode}) 合成失败：{ex.Message}");
                // 失败了就把状态作废：别让下一轮以为它是好的、只追加
                try { States.Invalidate(board.BoardCode); } catch { /* 记账表，失败不致命 */ }
            }

            done++;
            // 日志按**时间**节流（板块之间成分股数差一个量级，按个数报会哑好几分钟）；
            // 心跳每个板块一次，免得被静默看门狗误判。
            tick.Report(() => $"合成板块指数：{done}/{boards.Count}"
                            + $"（整段重算 {_full}、只追加 {_appended}、已是最新 {_upToDate}；"
                            + $"共写 {_totalBars} 根日K）");
            ReportQuiet($"合成板块指数：{done}/{boards.Count}", done, boards.Count);

            if (one != null) yield return [one];
        }
    }

    /// <summary>
    /// 这个板块这一轮怎么算——判据在 <see cref="BoardIndexIncrementalRule"/>（Logic 层纯函数）。
    /// **同步方法**：调用方负责推线程池。
    /// </summary>
    private BoardBars PlanAndSynthesize(
        Board board, Dictionary<string, BoardIndexState> states, DateTime today, bool forceFull)
    {
        var members = boardRepository.QueryMembers(board.BoardCode);
        var hash = BoardIndexIncrementalRule.MemberFingerprint(members);
        var earliest = EarliestMemberDay(members);
        var lastBar = Bars.GetLatestBar(board.BoardCode, Granularity.Day);

        states.TryGetValue(board.BoardCode, out var state);
        var (kind, from) = BoardIndexIncrementalRule.Decide(
            forceFull ? null : state, hash, earliest, lastBar?.PeriodStart, today, forceFull);

        if (kind == BoardIndexPlanKind.UpToDate)
        {
            _upToDate++;
            return new BoardBars(board.BoardCode, board.Name, [], Append: true, State: null);
        }

        if (kind == BoardIndexPlanKind.Append && lastBar is { } baseBar)
        {
            _appended++;
            var add = BoardIndexSynthesizer.Append(
                board.BoardCode, members, Bars, _asOf, from, baseBar.Close);
            // 一根都没追加＝那几天成分股数据不够（MinMembersPerDay），状态不动：
            // 下一轮还从同一个 from 起试，不会漏
            return new BoardBars(board.BoardCode, board.Name, add, Append: true,
                                 State: add.Count > 0
                                     ? new BoardIndexState(hash, add[^1].PeriodStart.Date, earliest)
                                     : null);
        }

        _full++;
        var bars = BoardIndexSynthesizer.Synthesize(board.BoardCode, members, Bars, _asOf);
        return new BoardBars(board.BoardCode, board.Name, bars, Append: false,
                             State: bars.Count > 0
                                 ? new BoardIndexState(hash, bars[^1].PeriodStart.Date, earliest)
                                 : null);
    }

    /// <summary>这个板块的成分股里，<c>day_adj</c> 最早的那一天——"补了更早历史"就靠它发现。</summary>
    private DateTime? EarliestMemberDay(IReadOnlyList<string> members)
    {
        DateTime? min = null;
        foreach (var code in members)
        {
            var e = Bars.GetEarliestPeriodStart(code, Granularity.DayAdj);
            if (e is { } d && (min is null || d < min)) min = d;
        }
        return min;
    }

    /// <summary>
    /// 落一个板块：**先删该板块的旧 bar 再整段写**——成分股和个股数据都会变，这一项是全量重算。
    /// 删+写在同一把写闸里，避免中间态被别人读到。
    /// </summary>
    protected override Task SaveBatchAsync(IReadOnlyList<BoardBars> batch, CancellationToken ct)
        => Task.Run(() =>
        {
            foreach (var b in batch)
            {
                lock (SqliteWriteGate.Local)
                {
                    // 追加那一路**不删旧的**——历史就是要留着的，这正是增量的意义。
                    // 整段重算那一路必须先删：成分股变了之后，老序列的每一根都是错的，
                    // 而新序列可能更短（少了几天），不删就会留下一截旧的尾巴。
                    if (!b.Append) Bars.DeleteByCode(b.BoardCode, Granularity.Day);
                    if (b.Bars.Count > 0) Bars.InsertOrRefreshUnconfirmed(b.Bars);
                    if (b.State is { } st) States.Save(b.BoardCode, st, _asOf);
                }

                if (b.Bars.Count == 0) continue;
                _withBars++;
                _totalBars += b.Bars.Count;
                _meta.Add((b.BoardCode, b.Name));

                // 板块的涨跌幅/成交额从这里回填：合成出来的最后一根就是当日板块行情，
                // 成交额本身就是成分股求和。以前这两个值是从数据源的板块榜抓的，
                // 改成本地算之后既不依赖要人工过验证的 push2，口径也跟板块K线一致。
                var last = b.Bars[^1];
                // 只有一根K时没有前收，涨跌幅按 0 处理（新板块或成分股数据太短）。
                // ⚠ 追加那一路批里可能只有一根，前收得**从库里**取上一根——
                //   拿批内的第 2 根去算会在"只追加了一天"时静默算成 0。
                double prevClose = b.Bars.Count >= 2
                    ? b.Bars[^2].Close
                    : PrevCloseFromDb(b.BoardCode, last.PeriodStart);
                var pct = prevClose > 0 ? (last.Close / prevClose - 1) * 100 : 0;
                _quotes.Add((b.BoardCode, pct, last.Amount));
            }
        }, ct);

    /// <summary>库里这个板块**这一天之前**的最后一根收盘价；没有就返回 0（调用方按"没有前收"处理）。</summary>
    private double PrevCloseFromDb(string boardCode, DateTime day)
    {
        var prior = Bars.QueryForAppend(boardCode, Granularity.Day, day);
        var before = prior.LastOrDefault(x => x.PeriodStart.Date < day.Date);
        return before?.Close ?? 0;
    }

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        Report($"板块指数合成已停止：本轮已写入 {_withBars} 个板块、{_totalBars} 根日K（都已落库）。"
             + "下次跑是全量重算，不会留半截。");
        return Task.CompletedTask;
    }

    protected override async Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (_nothingToDoReason is { } idle)
            return new TaskRunResult(TaskState.Completed, _errors, NothingToDo: true, idle);

        await Task.Run(() =>
        {
            if (_quotes.Count > 0) boardRepository.UpdateQuotes(_quotes);
            // 有指数K的板块名写进 StockMeta（type=board）——让"查询"页能搜到板块、看行情
            // （不影响个股选股，那边扫的是 6 位纯数字）。
            if (_meta.Count > 0)
                SqliteStockMetaUpsert.Upsert(paths.CurrentDb, _meta, SqliteStockMetaUpsert.TypeBoard);
        }, ct);

        // 整段重算做完了，把标记清掉（清早了会让中途停止的那一轮"看起来做完了"）
        if (_full > 0 && manifestStore != null)
            await Task.Run(() =>
            {
                try
                {
                    var m = manifestStore.Load();
                    if (m.BoardIndexNeedsFullRebuild) { m.BoardIndexNeedsFullRebuild = false; manifestStore.Save(m); }
                }
                catch { /* 记账失败不该把一轮成功的合成判成失败；下轮再全量一次而已 */ }
            }, ct);

        // 主动回收 WAL——理由见类注释末尾那段
        var walNote = await Task.Run(() => new SqliteMaintenance(paths.CurrentDb).CheckpointWalAndDescribe(), ct);
        if (walNote != null) Report(walNote);

        // 增量之后这一项从"每天重写 468 万行"变成"通常每个板块补一根"，
        // 所以汇总要说清楚**这一轮到底重算了多少**——不然看不出判据有没有在起作用。
        var summary = $"板块指数合成完成：{_boards} 个板块——整段重算 {_full} 个、只追加 {_appended} 个、"
                    + $"已是最新 {_upToDate} 个；共写入 {_totalBars} 根日K"
                    + "（code=板块代码，不进个股选股）。";
        Report(summary);
        return new TaskRunResult(TaskState.Completed, _errors, NothingToDo: _withBars == 0, summary);
    }
}
