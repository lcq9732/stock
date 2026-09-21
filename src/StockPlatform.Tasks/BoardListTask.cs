using System.Runtime.CompilerServices;
using StockPlatform.Data.Local;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【概念和行业板块】（2026-09-21 从编排器迁到新框架，
/// 见 doc/remaining-tasks-migration-design.md §2.6）——板块**名单**（不含成分股）。
///
/// ════ 三级回退 ════
/// ① 东财终端落在本地的那份文件（不联网，跟【板块成分股】同源同时点）
/// ② 行情中心左侧菜单那份静态 JSON（1 个请求拿全量、不碰 push2、不会弹图片验证码）
/// ③ push2 clist 分页（约 10 个请求，会撞验证码）——按页续传 + 暂存区提交
///
/// 最后再跑一次**不联网的层级树导入**：不管名单这一轮成没成功都跑，它只读本地一份文件，
/// 而正表里的板块名单就算是上一轮的，父子关系照样对得上。
///
/// ⚠ **暂存区那套语义一个字都没动**（<see cref="BoardListFetchLoop"/>，有单测）：
/// 半截列表得存下来（否则没法续），但**绝不能进正表**——<c>Board</c> 是快照语义，
/// "这一轮没出现的板块＝已下架"会连成分股一起删掉，而成分股约 2500 个请求、
/// 跨好几轮才攒得齐。迁移时整段是**原样搬过来**的，不是重写。
///
/// ════ 一批＝？ ════
/// 正常路径（①②）一次拿全量，退到 ③ 才有多页。这一项自己存（暂存区/正表切换是事务性的），
/// 不产出批——见设计文档 §1.3。
/// </summary>
public sealed partial class BoardListTask(
    BoardFetcherHolder fetcherHolder,
    IBoardRepository repository,
    EastMoneySideMenuBoardListProvider? sideMenu,
    EastMoneyTerminalHierarchyProvider? hierarchy,
    IStockBoardMapRepository? boardMap) : FetchTaskBase<int>
{
    public override FetchActionId Id => FetchActionId.StepBoardList;

    private IBoardFetcher Fetcher => fetcherHolder.Current;

    /// <summary>
    /// 东财行情接口在熔断中吗——在的话这一轮不开工，库里保留上一次的名单。
    /// 2026-09-21 跟这一项一起从编排器搬过来（原 <c>FetchOrchestrator.Push2PausedReason</c>）。
    /// </summary>
    private string? Push2PausedReason()
    {
        if (Fetcher is not EastMoneyBoardFetcherBase emb) return null;
        if (emb.PausedUntil is not { } until) return null;
        var mins = Math.Max(1, (int)Math.Ceiling((until - DateTime.Now).TotalMinutes));
        return $"东财行情接口限流熔断中，预计 {until:HH:mm} 恢复（还有约 {mins} 分钟）";
    }

    private readonly List<string> _errors = [];
    private string? _skippedReason;

    protected override async IAsyncEnumerable<IReadOnlyList<int>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _errors.Clear();
        _skippedReason = null;
        _skippedReason = await FetchBoardListCoreAsync(ct);
        yield break;   // 自己存（暂存区/正表切换是事务性的），不产出批
    }

    /// <summary>用不上——这一项自己存。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<int> batch, CancellationToken ct)
        => Task.CompletedTask;

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        // 「没开工」：限流熔断 / 名单没过护栏 / 这一轮没凑齐——今天恢复了还该再来。
        if (_skippedReason is { } why)
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Skipped(why, _errors));

        return Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, _errors, NothingToDo: false));
    }

    private async Task<string?> FetchBoardListCoreAsync(
        CancellationToken ct)
    {
        var skipped = await FetchBoardListNamesAsync(ct);

        // 层级树跟在名单后面跑（2026-09-07）。**不管名单这一轮成没成功都跑**：
        // 它只读本地一份文件、不发请求，而正表里的板块名单就算是上一轮的，父子关系照样对得上。
        // 名单被限流挡住的那些轮，正好是最该把这种"不花配额的活"干掉的时候。
        ImportBoardHierarchy();

        return skipped;
    }

    /// <summary>名单本体，三条路依次退：终端本地文件 → 菜单 JSON → push2 分页。</summary>
    private async Task<string?> FetchBoardListNamesAsync(
        CancellationToken ct)
    {
        repository.EnsureSchema();

        // ══ 最优先（2026-09-06）：东财终端落在本地的那份文件 ══
        //
        // 成分股已经走它了（BoardMemberChannel=terminal），名单也走同一份，图的是
        // **同源同时点**。名单和成分来自两个源时会对不上，而且对不上的那一个每轮都失败：
        // BK1362 就是活例子——sidemenu 的名单里有它，终端文件里没有，于是成分股那步
        // 每轮都为它报一次"板块不在本地文件里"。两边同源之后这类不一致从根上消失。
        //
        // 这一步本来就只取名单、不取行情（涨跌幅/成交额由【板块指数合成】用本地日K回填），
        // 所以本地文件够用。取不到就往下走菜单 JSON，名单这一项不会因此停摆。
        if (Fetcher is EastMoneyTerminalBoardFetcher term)
        {
            void ForwardTerm(string s) => Report(s);
            term.OnStatus += ForwardTerm;
            try
            {
                if (term.TryGetBoardList(out var fromFile, out var termMsg))
                {
                    Report(termMsg);
                    return CommitBoardListFromMenu(fromFile);
                }
                // ⚠ 走到这儿意味着**这一轮真的要发请求**，而 terminal 通道下这一项在占用表里
                // 是按"纯本地"登记的（见 FetchTaskCatalog.IsLocalOnlyNow）——也就是说它可能
                // 正跟别的东财任务并发跑。这是有意的取舍（要两层同时失效才会走到这儿，
                // 而菜单 JSON 只有 1 个请求），但**必须让人看见**：真被限流时，
                // 日志里没这一句的话，谁也想不到"不占源的那一项"会去打东财。
                Report($"⚠ {termMsg}——板块名单本轮改走网络（菜单 JSON，再不行退回 push2 分页）。"
                               + "注意：terminal 通道下这一项不登记数据源占用，可能跟别的东财任务同时在跑。");
            }
            finally { term.OnStatus -= ForwardTerm; }
        }

        // ══ 主路（2026-09-05）：行情中心左侧菜单那份静态 JSON，一个请求拿全量、不碰 push2 ══
        //
        // 换过来的理由和等价性实测见 EastMoneySideMenuBoardListProvider 的类注释（概念 504 个
        // 代码名称一个不差，行业只多一个三级行业）。收益不在"省下这 10 个请求"，而在于
        // 这一项从此**不需要人守着过图片验证码**，整轮 push2 配额也全留给了成分股。
        //
        // push2 那条路留着当回退：万一东财哪天把这个文件挪走或改结构，还能退回去抓。
        if (sideMenu != null)
        {
            void ForwardMenu(string s) => Report(s);
            sideMenu.OnStatus += ForwardMenu;
            try
            {
                var all = await sideMenu.FetchBoardListAsync(ct);
                return CommitBoardListFromMenu(all);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 取不到/解析不了才退回 push2。**护栏拒绝不走到这儿**——那是
                // CommitBoardListFromMenu 自己返回原因，数据可疑时再去烧 push2 配额没有意义。
                Report($"⚠ 板块菜单取数失败（{ex.Message}）——"
                               + "退回 push2 分页抓取，会慢很多、而且可能要人过图片验证。");
                _errors.Add($"板块菜单取数失败，已退回 push2：{ex.Message}");
            }
            finally { sideMenu.OnStatus -= ForwardMenu; }
        }

        // ══ 回退：push2 clist 分页（约 10 个请求，会撞验证码）══
        if (Push2PausedReason() is { } paused)
        {
            Report($"{paused}，本轮不开工。库里保留上一次的板块名单。");
            return paused;
        }

        void Forward(string s) => Report(s);
        Fetcher.OnStatus += Forward;
        try
        {
            if (Fetcher is EastMoneyBoardFetcherBase em2)
            {
                // 浏览器通道初始化要几秒（建 WebView2 + 打开东财页面拿 Cookie），
                // 放在这儿而不是第一个请求里，免得把那几秒算进限流节奏
                await em2.PrepareAsync(ct);
                // 网卡那段没配时是空串（走默认路由是常态，写进日志等于没说），整段跳过
                var nicem2 = em2.DescribeBinding();
                Report(em2.DescribeChannel()
                    + (string.IsNullOrWhiteSpace(nicem2) ? "" : "；" + nicem2));
            }

            repository.EnsureSchema();

            // 按页续传 + 凑齐才提交——循环本体和它藏过的两个 bug 见 BoardListFetchLoop
            if (Fetcher is not EastMoneyBoardFetcherBase pager)
            {
                Report("当前板块数据源不支持按页续传，跳过板块列表。");
                return "板块数据源不支持按页续传";
            }

            var (committed, unfinished) = await BoardListFetchLoop.RunAsync(
                fetchPage: (t, page, c) => pager.FetchBoardListPageAsync(t, page, c),
                repo: repository,
                report: m => Report(m),
                addError: _errors.Add,
                ct: ct);

            if (committed == 0)
            {
                Report("板块列表这一轮没有哪一类凑齐，正表保持上次的完整快照，"
                               + "已抓到的页存在暂存区，下轮从断点接着抓。");
                return $"板块列表未凑齐（{string.Join("、", unfinished)}）";
            }
            Report($"板块列表更新完成：本轮提交 {committed} 个"
                           + (unfinished.Count > 0
                                ? $"；{string.Join("、", unfinished)}还没凑齐，下轮从断点接着抓。" : "。"));
        }
        finally
        {
            Fetcher.OnStatus -= Forward;
        }
        return null;
    }

    /// <summary>
    /// 把菜单拿到的**全量名单**提交进正表（2026-09-05）：按类走护栏 → 暂存区 → 一次事务搬过去。
    ///
    /// 为什么还要绕暂存区那一道（明明一次就拿全了）：<c>CommitStaged</c> 里的"清僵尸 + 写入"
    /// 是在同一个事务里做的，直接调 <c>UpsertBoards</c> 也一样，但走同一条路能保证两种数据源
    /// 提交出来的正表状态完全一致，也顺手清掉 push2 那条路可能留下的暂存内容。
    /// </summary>
    /// <returns>一类都没提交时返回原因（调用方当作"本轮没开工"）；提交了就返回 null。</returns>
    private string? CommitBoardListFromMenu(
        List<Board> all)
    {
        int committedTotal = 0;
        var rejected = new List<string>();

        // 遍历**所有**类型而不是写死两个（2026-09-06 加地域时改）：写死的话，以后再加类型
        // 会漏在这儿，而且不报错——只是那一类永远不写库。
        foreach (var type in Enum.GetValues<BoardType>())
        {
            var label = type.Label();
            var items = all.Where(b => b.Type == type).ToList();
            var existing = repository.QueryBoards(type);

            // 这一批数据源**根本不提供**这一类（菜单 JSON 就没有地域板块）——那是"没有"，
            // 不是"掉光了"，跟护栏要防的情况是两回事。快照语义下空名单本来也不该提交，
            // 直接跳过；库里已有的原样留着，也别当成错误刷屏。
            if (items.Count == 0)
            {
                if (existing.Count > 0)
                    Report($"{label}板块：这个源不提供这一类，库里 {existing.Count} 个原样保留。");
                continue;
            }

            // 护栏：名单掉得太多就不写。正表是快照语义，少掉的会被当成已下架，
            // 连 BoardMember 和 BoardMemberFetchState 一起删——而成分股跨好几轮才攒得齐。
            if (EastMoneySideMenuBoardListProvider.CheckAgainstExisting(
                    type, items.Count, existing.Count) is { } why)
            {
                rejected.Add(label);
                _errors.Add(why);
                Report("⚠ " + why);
                continue;
            }

            // ⚠ 菜单里**没有行情**，而 CommitStaged 是拿暂存区的值去覆盖正表的。不把库里现有的
            //   涨跌幅/成交额带上，就会把【板块指数合成】刚回填的值清成 0，直到下次合成跑完——
            //   热度页会有一段时间全是 0。领涨股同理（虽然眼下没人读它）。
            //   新板块在库里没有旧值，保持 0，等合成那一步补上。
            var carry = existing.ToDictionary(
                b => b.BoardCode, b => (b.ChangePct, b.Amount, b.LeaderCode, b.LeaderName));
            foreach (var b in items)
                if (carry.TryGetValue(b.BoardCode, out var q))
                    (b.ChangePct, b.Amount, b.LeaderCode, b.LeaderName) = q;

            repository.ClearStaged(type);
            repository.StageBoards(items);
            var (committed, pruned) = repository.CommitStaged(type);

            // push2 那条路可能留着页级断点。菜单一次拿全之后它就作废了——留着的话，
            // 万一下轮退回 push2，会从一个半截的暂存区接着抓。
            repository.ClearListState(type);

            committedTotal += committed;
            Report($"{label}板块已更新：{committed} 个"
                           + (pruned > 0 ? $"（清掉 {pruned} 条下架板块的残留）" : "") + "。");
        }

        if (committedTotal == 0)
            return $"板块名单没通过护栏（{string.Join("、", rejected)}），本轮不更新，库里保留上次的快照";

        Report(
            $"板块列表更新完成：共 {committedTotal} 个，取自东财行情中心菜单，**整轮没用到 push2**。"
            + (rejected.Count > 0
                ? $" ⚠ {string.Join("、", rejected)}没通过护栏，那一类保持上次的快照。" : ""));
        return null;
    }

    /// <summary>
    /// 把东财终端本地文件里的板块父子关系导进 Board.parent_code / Board.board_level（2026-09-07）。
    ///
    /// 补的是一个说小不小的窟窿：我们库里只有"股票 → 属于哪个板块 + 第几级"，**没有"板块 → 父板块"**。
    /// 少了这层，三级行业就没法往上卷成一级来看，而风口分析里"这波钱落在哪个大行业"恰恰要按一级聚合。
    /// 东财网页侧不给这份数据（查过 /cjhy/、站内搜、板块页面都没有），终端把它落在了本地。
    ///
    /// ⚠ 那个文件格式是**逆向出来的、没有文档**，所以写库前拿 <c>StockIndustryEm.board_level</c>
    ///   对一遍——那是走网络接口拿的，跟本地文件完全独立的一份来源。东财哪天改了文件结构，
    ///   解析结果会是一堆"看着像模像样的错关系"，不校验的话没人会发现。
    ///
    /// 三种情况都不算失败，只记一句：没装终端（文件不存在）、库里还没有 StockIndustryEm（对不了）、
    /// 校验不过（这一轮不写，留着上一次的树）。层级树缺了不影响任何现有功能。
    /// </summary>
    private void ImportBoardHierarchy()
    {
        if (hierarchy == null) return;

        void Forward(string m) => Report(m);
        hierarchy.OnStatus += Forward;
        List<BoardHierarchyEdge> edges;
        try { edges = hierarchy.Read(); }
        catch (Exception ex)
        {
            Report($"⚠ 板块层级树读取失败（{ex.Message}），库里保留上一次的树。");
            return;
        }
        finally { hierarchy.OnStatus -= Forward; }

        if (edges.Count == 0) return;   // 文件不存在时 Read 自己已经报过一句了

        // ── 交叉校验：拿 StockIndustryEm 还原出的真实父子链逐条对 ──
        //
        // ⚠ 对的是**父子归属**，不是层级数字。这是拿真数据换来的教训：解析器第一版用层级栈，
        //   层级数字 932 处全对、看着毫无破绽，实际 51 条边的父是错的（"银行Ⅱ 挂在石油石化下"）。
        //   错位之后每个子板块照样挂在一个层级正确的父上，只对层级的校验会一路放行。
        var known = boardMap?.GetBoardParents();
        if (known is { Count: > 0 })
        {
            var bad = new List<string>();
            int compared = 0;
            foreach (var e in edges)
            {
                if (!known.TryGetValue(e.BoardCode, out var truth)) continue;   // 我们没有的板块，对不了
                compared++;
                if (truth.Level != e.Level)
                    bad.Add($"{e.BoardCode} 文件说 {e.Level} 级、库里是 {truth.Level} 级");
                else if (truth.Parent != null && !string.Equals(truth.Parent, e.ParentCode, StringComparison.OrdinalIgnoreCase))
                    bad.Add($"{e.BoardCode} 文件说父是 {e.ParentCode}、库里是 {truth.Parent}");
            }

            // 拒绝的门槛：**一条都不能错**。这不是洁癖——对不上通常意味着解析错位，
            // 那种情况下剩下那些"对得上"的边也未必是真的，挑着写进去比整批不写更糟。
            if (bad.Count > 0)
            {
                var sample = string.Join("；", bad.Take(5));
                Report($"⚠ 板块层级树跟库里的行业分类对不上（{bad.Count}/{compared} 条，如 {sample}），"
                               + "这一轮不写，库里保留上一次的树。多半是东财改了文件格式——"
                               + "去看 EastMoneyTerminalHierarchyProvider 的格式说明。");
                _errors.Add($"板块层级树校验未通过（{bad.Count}/{compared} 条父子关系不一致），已拒绝写入。");
                return;
            }
            Report($"板块层级树校验通过：{compared} 条父子关系跟库里的行业分类逐条一致。");
        }
        else
        {
            // StockIndustryEm 还没抓过。写还是不写？写——层级树本身不会让任何现有数据变糟，
            // 而且【个股行业题材】那一步跑完之后下一轮自然就校验上了。
            Report("⚠ 库里还没有东财行业分类，板块层级树这一轮没法交叉校验，先按文件写入。");
        }

        var (updated, unknown, cleared) = repository.UpdateHierarchy(edges);
        Report($"板块层级树已更新：{updated} 个板块写入父子关系"
                       + (cleared > 0 ? $"（覆盖原有 {cleared} 行）" : "")
                       + (unknown > 0 ? $"；另有 {unknown} 个板块终端有、我们的板块表里没有，已跳过。" : "。"));
    }
}
