using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【板块指数合成】"能不能只追加"的判据（2026-09-21，见 <see cref="BoardIndexIncrementalRule"/>）。
///
/// ⚠ 这是整个增量改造里**唯一会静默出错**的地方：判据漏判一种失效来源，错的历史就会一直留着，
/// 而板块指数只有本地这一份、没有官方值可对。所以三种失效来源各钉一条。
/// </summary>
public class BoardIndexIncrementalRuleTests
{
    private static readonly DateTime Today = new(2026, 9, 21);
    private static readonly DateTime LastBar = new(2026, 9, 18);
    private static readonly DateTime Earliest = new(2016, 1, 4);
    private const string Hash = "ABC123";

    private static BoardIndexState State(string hash = Hash, DateTime? last = null, DateTime? earliest = null)
        => new(hash, last ?? LastBar, earliest ?? Earliest);

    // ⚠ 用 noLastBar 而不是 lastBar: null 来表达"库里没有指数K"——
    //   默认参数配 ?? 的话，"显式传 null"跟"没传"分不开（第一版就是这么写错的）。
    private static (BoardIndexPlanKind Kind, DateTime From) Decide(
        BoardIndexState? state, string hash = Hash, DateTime? memberEarliest = null,
        DateTime? lastBar = null, bool forceFull = false, bool noLastBar = false)
        => BoardIndexIncrementalRule.Decide(
            state, hash, memberEarliest ?? Earliest,
            noLastBar ? null : lastBar ?? LastBar, Today, forceFull);

    // ── 能追加的那一路 ──

    [Fact]
    public void 一切没变_只追加下一天()
    {
        var (kind, from) = Decide(State());

        Assert.Equal(BoardIndexPlanKind.Append, kind);
        Assert.Equal(LastBar.AddDays(1), from);
    }

    [Fact]
    public void 已经算到今天_不用动()
        => Assert.Equal(BoardIndexPlanKind.UpToDate,
            Decide(State(last: Today), lastBar: Today).Kind);

    // ── 三种失效来源，一个都不能漏 ──

    /// <summary>① 成分股名单变了：历史每天的均值都是按当前名单算的，整条都得重来。</summary>
    [Fact]
    public void 名单指纹变了_整段重算()
        => Assert.Equal(BoardIndexPlanKind.Full, Decide(State(), hash: "DIFFERENT").Kind);

    /// <summary>② day_adj 被【重算回测序列】重写——由外部置标记传进来。</summary>
    [Fact]
    public void 强制全量_整段重算()
        => Assert.Equal(BoardIndexPlanKind.Full, Decide(State(), forceFull: true).Kind);

    /// <summary>③ 成分股补上了更早的历史：早期那些天的均值变了。</summary>
    [Fact]
    public void 成分股最早日期往前挪了_整段重算()
        => Assert.Equal(BoardIndexPlanKind.Full,
            Decide(State(), memberEarliest: Earliest.AddDays(-1)).Kind);

    /// <summary>往后挪不算失效（老票退出板块了，历史均值不受影响——名单变了那条会管）。</summary>
    [Fact]
    public void 成分股最早日期往后挪_仍可追加()
        => Assert.Equal(BoardIndexPlanKind.Append,
            Decide(State(), memberEarliest: Earliest.AddDays(30)).Kind);

    // ── 状态本身不可信时一律重算 ──

    [Fact]
    public void 没有状态_整段重算()
        => Assert.Equal(BoardIndexPlanKind.Full, Decide(null).Kind);

    [Fact]
    public void 库里没有指数K_整段重算()
        => Assert.Equal(BoardIndexPlanKind.Full, Decide(State(), noLastBar: true).Kind);

    /// <summary>状态记的最后一根跟库里对不上 → 有人动过这个板块的 bar，别猜。</summary>
    [Fact]
    public void 状态与库里对不上_整段重算()
        => Assert.Equal(BoardIndexPlanKind.Full,
            Decide(State(last: LastBar.AddDays(-5))).Kind);

    /// <summary>老状态行没记过最早日期 → 先重算一次把它补上，别拿 null 当"没变"。</summary>
    [Fact]
    public void 老状态没记最早日期_整段重算()
        => Assert.Equal(BoardIndexPlanKind.Full,
            Decide(new BoardIndexState(Hash, LastBar, null)).Kind);

    // ── 指纹 ──

    /// <summary>名单顺序不影响指数（等权平均），顺序变了却判成"变了"会白白触发整段重算。</summary>
    [Fact]
    public void 指纹与顺序无关()
        => Assert.Equal(BoardIndexIncrementalRule.MemberFingerprint(["600000", "000001", "300750"]),
                        BoardIndexIncrementalRule.MemberFingerprint(["300750", "600000", "000001"]));

    [Fact]
    public void 指纹与重复无关()
        => Assert.Equal(BoardIndexIncrementalRule.MemberFingerprint(["600000", "000001"]),
                        BoardIndexIncrementalRule.MemberFingerprint(["600000", "000001", "600000"]));

    [Fact]
    public void 成分不同则指纹不同()
        => Assert.NotEqual(BoardIndexIncrementalRule.MemberFingerprint(["600000", "000001"]),
                           BoardIndexIncrementalRule.MemberFingerprint(["600000", "000002"]));

    [Fact]
    public void 空名单也有稳定指纹()
        => Assert.Equal(BoardIndexIncrementalRule.MemberFingerprint([]),
                        BoardIndexIncrementalRule.MemberFingerprint([]));
}
