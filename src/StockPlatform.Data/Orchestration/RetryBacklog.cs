namespace StockPlatform.Data.Orchestration;

using StockPlatform.Logic.Models;

/// <summary>待办的类别——执行侧按它分派，显示侧按它排版。</summary>
public enum RetryKind
{
    BarCodes, MissingDay, MarketCap, NetInflow,
    IndexCons, IndexWeight, Shareholder, Dividend,
    Gaps, NetInflowDays, ValueIssues,
    /// <summary>残缺日：那天有行但不全（2026-09-16）。跟 <see cref="NetInflowDays"/>
    /// （整天一行都没有）是两回事，复查判据不同，不能合。</summary>
    PartialDays,
}

/// <summary>
/// 一件待重试的事：补什么、有多少、归谁补。
/// </summary>
/// <param name="Kind">类别（执行侧分派用）。</param>
/// <param name="TaskId">归属任务（<c>FetchActionId</c> 的枚举名）。这里用字符串是因为
/// StockPlatform.Data 不引用 StockPlatform.Scheduling——要中文任务名由 UI 层查 catalog。
/// 一期还用不到它（执行链没改），先带上是为了二期「按任务Id 分派」时接口不用动。</param>
/// <param name="Label">显示用的短名，如 "不复权空洞"。</param>
/// <param name="Count">数量，也是 <see cref="RetryBacklog.Any"/> 的判据。</param>
/// <param name="Unit">量词："只" / "段" / "天" / "个" / "轮"。各类的粒度根本不一样，
/// 硬加成一个总数会得出极具误导性的数字（市值是整轮扫描，失败时整批代码都进名单，
/// 加总后"5547 支失败"读起来像 5547 只票丢了数据，实际是"1 次市值快照没取到 + 3 只资金流"）。</param>
/// <param name="Actionable">点【重新拉取失败】之后这个数字会不会降。见 <see cref="RetryBacklog"/> 类注释。</param>
/// <param name="Days">涉及多少个交易日（只有区间类待办有）。段数≈请求数、决定要跑多久，
/// 交易日数才是数据量，两个都给才看得出这是不是个几小时的活。</param>
public sealed record RetryItem(
    RetryKind Kind,
    string TaskId,
    string Label,
    int Count,
    string Unit,
    bool Actionable,
    int? Days = null)
{
    public string Describe() =>
        Days is { } d && d > 0
            ? $"{Label} {Count} {Unit}/{FormatDays(d)}"
            : $"{Label} {Count} {Unit}";

    /// <summary>20.8万交易日 比 208251个交易日 好读一个数量级。</summary>
    private static string FormatDays(int days) =>
        days >= 10000 ? $"{days / 10000.0:0.#}万交易日" : $"{days:N0}交易日";
}

/// <summary>
/// 【重新拉取失败】的待办清单（2026-09-13，取代 <c>FailedRetrySummary</c>）。
///
/// ════ 为什么换掉 FailedRetrySummary ════
/// 老的那个是**一份手抄的清单**：它把 manifest 上的九个名单逐个列成 int 字段，
/// 而同一份清单在代码里还抄了三遍——<c>Describe()</c> 一遍、<c>Any</c> 一遍、
/// <c>RunRetryFailedInternalAsync</c> 开头的 early-return 又手写八个 <c>.Count == 0</c>。
///
/// 于是 2026-09-02 加【全库体检】的历史空洞（<see cref="Manifest.MissingBars"/>）、
/// 2026-09-06 加资金流缺失日（<see cref="Manifest.MissingNetInflowDays"/>）时，
/// 两处都只改了执行链、没人回头抄摘要——界面上显示"09-11日线 1 只"，点下去实际跑了
/// **1909 段、20.8 万个交易日**，几个小时（2026-09-13 用户发现：
/// "说只有一只，给人感觉也不是什么大事，但一执行，时间却那么久"）。
///
/// 更要命的是 <c>Any</c> 也漏了这两类：等 MissingDayCodes 那 1 只补上、别的名单清零，
/// ① 手动点【重新拉取失败】会在入口的 early-return 直接被回绝"不需要重试"；
/// ② <c>ScheduleAutoRetry</c> 认为名单已清零、**不再排自动重试**（无人值守就彻底没人管了）。
/// 那 1909 段于是永远补不上，而且一声不吭。
///
/// 漏抄不是谁粗心：那两项由 StockPlatform.Tasks.FullAuditTask 写进 manifest，
/// 加它的时候完全不用碰 FetchOrchestrator 这边的任何一行代码，没有任何东西会提醒作者。
/// 所以这里改成**一处派生**：<see cref="From"/> 是唯一知道"manifest 上哪些字段代表待办"的地方，
/// 显示、可点判定、early-return、自动重试计数全部读它。新增一类待办必须在 From 里登记，
/// 否则它既不显示、按钮也不认——不再是"加了没人知道"。
///
/// ════ Actionable：点了会不会降 ════
/// 判据只有一条：点【重新拉取失败】之后这个数字会不会下降。不会降的不进
/// <see cref="Actionable"/>——那样重取那行不显示它，也不会让 <see cref="Any"/> 为真
/// （否则自动重试会为一件补不动的事一轮轮空跑），
/// 免得出现**点一次不降、再点还不降**的数字（比漏报更让人上火）。
/// 【全库数据体检】那行的 tooltip 用 <see cref="Items"/>，看得到全貌。
///
/// ⚠ **目前所有类别都是可执行的**（含值问题——FillValueIssuesAsync 真的会去抓去改）。
/// 这个机制先留着：以后再挂进来"只报不补"的待办时，登记时标一下就行，
/// 不用再回头改显示、按钮、early-return 三处。
///
/// ⚠ 另有一种"这一轮不会降"是**运行时**才知道的：数据源不支持某口径时整组跳过
/// （<c>!source.Fetcher.SupportsHfq</c>，切到新浪时后复权/不复权两组原样留着、连 Tries 都不加）。
/// 那取决于当前选的数据源，这里只读 manifest、判不了，**不做分流**——
/// 执行时日志里已经有明确的一句（"要补请把数据源切到 Tencent"），够了。
///
/// 完整设计见 doc/retry-backlog-design.md。
/// </summary>
public sealed class RetryBacklog
{
    /// <summary>显示时最多列几项，多的收成"等 N 项"。</summary>
    public const int MaxDescribeParts = 3;

    private RetryBacklog(IReadOnlyList<RetryItem> items) => Items = items;

    /// <summary>全部待办，含重取治不了的（值问题）。【全库数据体检】那行的 tooltip 用它。</summary>
    public IReadOnlyList<RetryItem> Items { get; }

    /// <summary>只有点【重新拉取失败】治得了的。按 Count 降序——大头排前面，
    /// 一眼看得出这轮的工作量在哪。</summary>
    public IReadOnlyList<RetryItem> Actionable =>
        Items.Where(i => i.Actionable).OrderByDescending(i => i.Count).ToList();

    /// <summary>这一轮有没有活要干。**只看 Actionable**：补不动的东西不该让
    /// 入口放行、也不该让自动重试一轮轮空跑。
    /// 用它的三处：重取入口的 early-return、<c>MainViewModel.HasFailed</c>（自动重试的判据）、
    /// 以及那行字显不显示内容。</summary>
    public bool Any => Items.Any(i => i.Actionable);

    /// <summary>待重试的总量——自动重试用它判断"这一轮有没有进展"。</summary>
    public int ActionableCount => Items.Where(i => i.Actionable).Sum(i => i.Count);

    /// <summary>【重新拉取失败】那行字：只列 Actionable，按量降序，超出的收成"等 N 项"。</summary>
    public string Describe()
    {
        var list = Actionable;
        if (list.Count == 0) return "无失败";
        var parts = list.Take(MaxDescribeParts).Select(i => i.Describe()).ToList();
        if (list.Count > MaxDescribeParts) parts.Add($"等 {list.Count - MaxDescribeParts} 项");
        return string.Join(" · ", parts);
    }

    /// <summary>【全库数据体检】那行的 tooltip：逐行列**全部**，并注明哪些不是重取能补的。</summary>
    public string DescribeAll()
    {
        if (Items.Count == 0) return "没有待补的项目。";
        var lines = Items
            .OrderByDescending(i => i.Actionable)
            .ThenByDescending(i => i.Count)
            .Select(i => i.Actionable ? $"· {i.Describe()}" : $"· {i.Describe()}（这一项重取补不了）")
            .ToList();
        if (Items.Any(i => !i.Actionable))
        {
            lines.Add("");
            lines.Add("标注「这一项重取补不了」的，【重新拉取失败】那一行不会去动，所以那里也不显示它。");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 从统一的待办清单派生（2026-09-13 二期：manifest 上已经是 <see cref="RetryTodo"/> 了，
    /// 这里只负责把它翻译成能显示的文字）。
    ///
    /// "manifest 上哪些东西算待办"这份知识现在只在 <see cref="Manifest.MigrateLegacyTodos"/> 一处，
    /// 这里不再重新登记一遍——老版本正是因为这份清单被抄了四遍，才漏掉体检写进来的那两类。
    /// </summary>
    public static RetryBacklog From(Manifest m)
    {
        var items = new List<RetryItem>();
        foreach (var todo in m.Todos)
        {
            if (todo.Targets.Count == 0) continue;
            var (kind, label, unit, actionable) = Describe(todo);
            // 区间类待办才有"涉及多少交易日"；逐只重试的失败名单没有，显示成 0 会误导
            int days = todo.Targets.Sum(t => t.Days);
            items.Add(new RetryItem(kind, todo.TaskId, label,
                // 整轮扫描的按"1 轮"报：名单里那一大批代码只代表"有一轮要重来"，
                // 把代码数摆出来会被读成"这么多只票丢了数据"（见 FetchMarketCapAsync）。
                todo.Kind == RetryTodoKind.Round ? 1 : todo.Targets.Count,
                unit, actionable, days > 0 ? days : null));
        }
        return new RetryBacklog(items);
    }

    /// <summary>一条待办怎么显示：类别、短名、量词、以及**点重取会不会让它降**。</summary>
    private static (RetryKind Kind, string Label, string Unit, bool Actionable) Describe(RetryTodo t)
        => (t.Kind, t.TaskId) switch
        {
            (RetryTodoKind.Gap, _) => (RetryKind.Gaps, GranLabelOf(t) + "空洞", "段", true),
            // ⚠ 值问题**也是可执行的**——FillValueIssuesAsync（2026-09-09 加）真的会去抓去改，
            //   只是补法和复查方式跟缺行不同（见 doc/bar-value-audit-design.md §5）。
            //   FillAuditedGapsAsync 里"值类记录这一轮先原样留着"那句注释说的是**缺行那个循环**里
            //   先不动它们、等缺行跑完再单独处理，不是整轮不补（2026-09-13 一期照那句话
            //   把它标成了 false，是误读）。
            (RetryTodoKind.ValueIssue, _) => (RetryKind.ValueIssues, ValueReasonLabel(t), "段", true),
            (RetryTodoKind.MissingDay, _) =>
                (RetryKind.MissingDay, t.Day is { } d ? $"{d:MM-dd}日线" : "当天日线", "只", true),
            (RetryTodoKind.MissingDays, _) => (RetryKind.NetInflowDays, "资金流缺失日", "天", true),
            // 残缺日按**任务**分标签：MissingDays 那条把名字写死成"资金流缺失日"了，
            // 照抄的话两融的残缺日会顶着资金流的名字显示出来。
            (RetryTodoKind.PartialDay, RetryTaskIds.Margin) => (RetryKind.PartialDays, "两融残缺日", "天", true),
            (RetryTodoKind.PartialDay, RetryTaskIds.Lhb) => (RetryKind.PartialDays, "龙虎榜残缺日", "天", true),
            (RetryTodoKind.PartialDay, RetryTaskIds.LhbSeat) => (RetryKind.PartialDays, "席位残缺日", "天", true),
            (RetryTodoKind.PartialDay, RetryTaskIds.BlockTrade) => (RetryKind.PartialDays, "大宗残缺日", "天", true),
            (RetryTodoKind.PartialDay, RetryTaskIds.MoneyFlowDetail) => (RetryKind.PartialDays, "资金流残缺日", "天", true),
            (RetryTodoKind.PartialDay, _) => (RetryKind.PartialDays, "残缺日", "天", true),
            (RetryTodoKind.Round, _) => (RetryKind.MarketCap, "市值", "轮", true),
            (_, RetryTaskIds.NetInflow) => (RetryKind.NetInflow, "净流入", "只", true),
            (_, RetryTaskIds.IndexCons) => (RetryKind.IndexCons, "指数成分", "个", true),
            (_, RetryTaskIds.IndexWeight) => (RetryKind.IndexWeight, "指数权重", "个", true),
            (_, RetryTaskIds.Shareholder) => (RetryKind.Shareholder, "股东", "只", true),
            (_, RetryTaskIds.Dividend) => (RetryKind.Dividend, "分红", "只", true),
            // K线失败按口径报：后复权/不复权失败的票以前跟前复权混在**一个**名单里，
            // 重试时只能一律按前复权补——二期修掉的就是这个。
            _ => (RetryKind.BarCodes, GranLabelOf(t) + "K线失败", "只", true),
        };

    private static string GranLabelOf(RetryTodo t) => t.TaskId switch
    {
        RetryTaskIds.StockHfqBars => "后复权",
        RetryTaskIds.StockRawBars => "不复权",
        _ => "前复权",
    };

    private static string ValueReasonLabel(RetryTodo t) =>
        t.Targets.Select(x => x.Reason).FirstOrDefault() switch
        {
            AuditFindingKind.Intraday => "盘中固化",
            AuditFindingKind.NullValue => "空值",
            AuditFindingKind.Ohlc => "OHLC 不自洽",
            AuditFindingKind.Inconsistent => "多口径不一致",
            AuditFindingKind.Ratio => "比例异常",
            _ => "值问题",
        };
}
