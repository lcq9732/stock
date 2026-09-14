namespace StockPlatform.Logic.Services;

/// <summary>
/// 一笔成交的**归因码**（2026-09-14）。落在交易记录的 <c>Reason</c> 字段上。
///
/// ════ 为什么要这个东西 ════
/// 软件记下来的实盘只有 12 笔成交（买 8 / 卖 4、跨 5 周），现在做不了任何归因统计——
/// 4 笔平仓上拆出来的结论都是噪音。要算得出"赚的钱是纪律带来的还是判断带来的"，
/// 得等几十笔、跨过一次明显的涨跌。
///
/// 但**原因必须现在就记**：等半年后数据攒够了再回头补，人已经想不起来那笔到底是
/// "到 +2% 止盈走的"还是"看着不对提前跑的"——而这两者恰恰就是「纪律 vs 判断」的分界，
/// 也就是最想知道的那件事。
///
/// ════ 取值为什么这么分 ════
/// 不是随便列的清单，**每个码都要能归进 <see cref="GroupOf"/> 的三组之一**，否则统计时拆不开：
///   · 纪律 —— 规则说到点了就动，不掺判断
///   · 判断 —— 人看着不对 / 看到更好的，主动动
///   · 外部 —— 跟行情无关（要用钱），归因时**该剔除**，不然两边都被污染
/// 加新码之前先问：它属于哪一组？归不进去说明分类本身要重想（有用例钉着）。
///
/// ⚠ 不做校验（比如"选了 target 就检查收益是否真 ≥ +2%"）。填错是人的问题，
///   机器一较真人就懒得填 —— 先让这个字段有数据，比让它正确更重要。
///
/// 放 Logic 而不是 Analyzer：**这是领域知识不是界面细节**，跟
/// <see cref="TradeDiscipline"/>（止盈止损幅度）是同一类东西。
/// 顺带也让它能被测——Analyzer 是自包含单文件 exe，测试项目引用它会撞 NETSDK1151。
/// </summary>
public static class TradeReasons
{
    // ── 卖出 · 纪律 ──────────────────────────────────────────────────────
    /// <summary>到止盈目标（+2% 快目标 / +10% 大目标，见 <see cref="TradeDiscipline"/>）。</summary>
    public const string Target = "target";

    /// <summary>到止损线。</summary>
    public const string Stop = "stop";

    /// <summary>持有到期（60 日 / 120 日），不管当时是赚是亏。</summary>
    public const string Timeout = "timeout";

    // ── 卖出 · 判断 ──────────────────────────────────────────────────────
    /// <summary>没到线，但看着不对，提前跑。</summary>
    public const string Judgment = "judgment";

    /// <summary>换标的：有更值得拿的，把仓位腾出来。</summary>
    public const string Switch = "switch";

    /// <summary>基本面变了（业绩爆雷、逻辑证伪）。</summary>
    public const string Fundamental = "fundamental";

    // ── 卖出 · 外部 ──────────────────────────────────────────────────────
    /// <summary>要用钱，跟这只票本身无关。**归因时剔除**。</summary>
    public const string Cash = "cash";

    // ── 买入 ────────────────────────────────────────────────────────────
    /// <summary>选股方法给出的信号（纪律）。具体哪个方法看自选记录的 Method。</summary>
    public const string Signal = "signal";

    /// <summary>自己看好，不是方法选出来的（判断）。</summary>
    public const string Manual = "manual";

    /// <summary>回调补仓（判断）。</summary>
    public const string Dip = "dip";

    /// <summary>加仓 —— 已有底仓，金字塔往上加（判断）。</summary>
    public const string Add = "add";

    /// <summary>归因分组。<c>null</c> = 这个码不认识（老数据、或手改过的 JSON）。</summary>
    public static string? GroupOf(string? reason) => reason switch
    {
        Target or Stop or Timeout or Signal => Discipline,
        Judgment or Switch or Fundamental or Manual or Dip or Add => Judgement,
        Cash => External,
        _ => null,
    };

    public const string Discipline = "纪律";
    public const string Judgement = "判断";
    public const string External = "外部";

    /// <summary>
    /// 界面下拉用：(码, 显示名)。
    /// 用 <paramref name="isSell"/> 而不是枚举，是为了不依赖 Analyzer 里的 TradeSide——
    /// Logic 层不认识上面那一层的类型。
    /// </summary>
    public static IReadOnlyList<(string Code, string Label)> For(bool isSell) =>
        isSell
            ? new[]
            {
                (Target, "到止盈目标（纪律）"),
                (Stop, "到止损线（纪律）"),
                (Timeout, "持有到期（纪律）"),
                (Judgment, "看着不对，提前跑（判断）"),
                (Switch, "换更好的标的（判断）"),
                (Fundamental, "基本面变了（判断）"),
                (Cash, "要用钱（外部，归因时剔除）"),
            }
            : new[]
            {
                (Signal, "方法给出信号（纪律）"),
                (Manual, "自己看好（判断）"),
                (Dip, "回调补仓（判断）"),
                (Add, "加仓（判断）"),
            };
}
