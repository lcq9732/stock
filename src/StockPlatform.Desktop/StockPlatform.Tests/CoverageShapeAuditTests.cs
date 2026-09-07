using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 覆盖形状体检的判据（2026-09-06 新增）——盯的是 FindGaps 天生看不见的两种形状。
///
/// 为什么要单独一套：FindGaps 只在每只票**自己**的 [最早, 最晚] 区间里找洞，于是
/// ① 某个口径只抓到最近两年（别的口径有十年）、② 最近几天整体没抓到，
/// 这两种在它眼里都是"区间内一个洞都没有"——干干净净的假阴性。
/// 2026-09-06 实测：37 只次新股的 day_raw 比 day 少约 20 根，龙虎榜落后 3 个交易日，
/// 体检都是一个字没报。
/// </summary>
public class CoverageShapeAuditTests
{
    /// <summary>10 个交易日，故意跨周末（判据必须按交易日历数，不能按自然日）。</summary>
    private static readonly List<DateTime> Cal =
    [
        new(2026, 8, 17), new(2026, 8, 18), new(2026, 8, 19), new(2026, 8, 20), new(2026, 8, 21),
        new(2026, 8, 24), new(2026, 8, 25), new(2026, 8, 26), new(2026, 8, 27), new(2026, 8, 28),
    ];

    private static Dictionary<string, DateTime> Map(params (string Code, int CalIndex)[] items) =>
        items.ToDictionary(x => x.Code, x => Cal[x.CalIndex], StringComparer.Ordinal);

    // ───────────────── 起点晚了 ─────────────────

    [Fact]
    public void 起点比基准晚一大截_报出来()
    {
        // 前复权从头就有、不复权只抓到最后两天——中间那 8 天 FindGaps 一个洞都报不出来
        var late = CoverageShapeAuditor.FindLateStarts(Cal, Map(("600000", 0)), Map(("600000", 8)));

        var gap = Assert.Single(late);
        Assert.Equal("600000", gap.Code);
        Assert.Equal(8, gap.TradingDays);
    }

    [Fact]
    public void 起点只差一两天_不报()
    {
        // 数据源起点的正常抖动，报了就是噪声
        Assert.Empty(CoverageShapeAuditor.FindLateStarts(Cal, Map(("600000", 0)), Map(("600000", 2))));
    }

    [Fact]
    public void 跨周末不按自然日算()
    {
        // Cal[4]=8/21(周五) → Cal[5]=8/24(周一)：自然日差 3 天，交易日只差 1 天，不该报
        Assert.Empty(CoverageShapeAuditor.FindLateStarts(Cal, Map(("600000", 4)), Map(("600000", 5))));
    }

    [Fact]
    public void 新上市的票不报()
    {
        // 基准口径自己也只有最后两天（2026-09-04 上市的 920289 就是这样）——
        // 三个口径都只有一两根K线，拿它比什么都是噪声
        Assert.Empty(CoverageShapeAuditor.FindLateStarts(Cal, Map(("920289", 8)), Map(("920289", 9))));
    }

    [Fact]
    public void 被查口径一根都没有的_不在这里报()
    {
        // "整只票没有这个口径"是另一类问题（那一项从没排进计划），全库体检里单独数，
        // 混进来只会让两边的数字都说不清
        Assert.Empty(CoverageShapeAuditor.FindLateStarts(Cal, Map(("600000", 0)), Map()));
    }

    // ───────────────── 尾巴停了 ─────────────────

    [Fact]
    public void 尾巴停在几天前_报出来()
    {
        var tails = CoverageShapeAuditor.FindLateTails(Cal, Map(("600000", 6)));

        var gap = Assert.Single(tails);
        Assert.Equal(3, gap.TradingDays);
        Assert.Equal(Cal[^1], gap.Expected);
        Assert.Equal(Cal[6], gap.Actual);
    }

    [Fact]
    public void 尾巴跟日历齐平_不报()
    {
        Assert.Empty(CoverageShapeAuditor.FindLateTails(Cal, Map(("600000", 9))));
    }

    [Fact]
    public void 刚上市的票尾巴短_不算落后()
    {
        // 起点在最后两天之内 → 历史太短，跳过（否则每只新股上市当天都会被报一次）
        Assert.Empty(CoverageShapeAuditor.FindLateTails(
            Cal, Map(("920289", 8)), earliestByCode: Map(("920289", 8))));
    }
}
