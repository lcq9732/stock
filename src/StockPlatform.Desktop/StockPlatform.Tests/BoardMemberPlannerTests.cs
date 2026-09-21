using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【板块成分股】的排序与熔断判据（2026-09-21 随迁移抽出来，见 <see cref="BoardMemberPlanner"/>）。
///
/// ⚠ 排序错了是**静默**的：大板块排前面时，一轮里能落库的板块数会少一大截，
/// 而日志上看不出任何异常——只是"这轮又没抓完"。
/// </summary>
public class BoardMemberPlannerTests
{
    private static Board B(string code, int members = 0) => new() { BoardCode = code, MemberCount = members };

    [Fact]
    public void 先小后大()
    {
        var plan = BoardMemberPlanner.Plan([B("BK3", 800), B("BK1", 20), B("BK2", 300)], new HashSet<string>());

        Assert.Equal(["BK1", "BK2", "BK3"], plan.Select(b => b.BoardCode));
    }

    /// <summary>没抓过的（MemberCount=0）按中等对待：排在已知小板块之后、已知大板块之前。</summary>
    [Fact]
    public void 没抓过的按中等排()
    {
        var plan = BoardMemberPlanner.Plan([B("BIG", 800), B("NEW"), B("SMALL", 20)], new HashSet<string>());

        Assert.Equal(["SMALL", "NEW", "BIG"], plan.Select(b => b.BoardCode));
    }

    [Fact]
    public void 还新鲜的跳过()
    {
        var plan = BoardMemberPlanner.Plan([B("BK1", 20), B("BK2", 30)],
                                           new HashSet<string> { "BK1" });

        Assert.Equal(["BK2"], plan.Select(b => b.BoardCode));
    }

    /// <summary>同样大小时按代码定序，两轮日志才对得上。</summary>
    [Fact]
    public void 同样大小时按代码定序()
    {
        var plan = BoardMemberPlanner.Plan([B("BKb", 50), B("BKa", 50)], new HashSet<string>());

        Assert.Equal(["BKa", "BKb"], plan.Select(b => b.BoardCode));
    }

    [Fact]
    public void 数大板块个数()
        => Assert.Equal(2, BoardMemberPlanner.CountBig([B("a", 500), B("b", 401), B("c", 400), B("d", 10)]));

    // ── 统计口径 ──

    /// <summary>
    /// 不节流的通道（<c>MemberFreshFor &lt;= 0</c>）下统计界是**今天**，不是 MaxValue——
    /// 2026-09-07 踩过：用抓取判据那条线统计，会把刚抓成功的 1031 个全算成"待重试"。
    /// </summary>
    [Fact]
    public void 不节流通道的统计界是今天()
    {
        var today = new DateTime(2026, 9, 21);

        Assert.Equal(today, BoardMemberPlanner.StatsSince(TimeSpan.Zero, today));
    }

    [Fact]
    public void 节流通道的统计界往前推()
    {
        var today = new DateTime(2026, 9, 21);

        Assert.Equal(today.AddDays(-7), BoardMemberPlanner.StatsSince(TimeSpan.FromDays(7), today));
    }
}

/// <summary>连续失败熔断（<see cref="ConsecutiveFailureGate"/>）。</summary>
public class ConsecutiveFailureGateTests
{
    /// <summary>阈值 15 是跟限流器对齐的：实测正常波动里连续失败能到 7，设 10 会误判。</summary>
    [Theory]
    [InlineData(7, false)]
    [InlineData(14, false)]
    [InlineData(15, true)]
    [InlineData(30, true)]
    public void 阈值(int consecutive, bool stop)
        => Assert.Equal(stop, ConsecutiveFailureGate.ShouldStop(consecutive));
}

/// <summary>
/// 【拉取市场事件】每张表从哪天抓起（<see cref="MarketEventWindowRule"/>）。
/// </summary>
public class MarketEventWindowRuleTests
{
    private static readonly DateTime Floor = new(2016, 1, 1);

    [Fact]
    public void 整段回补_从历史起点()
    {
        var (start, mode) = MarketEventWindowRule.Start(
            fullBackfill: true, watermark: new DateTime(2026, 9, 1), Floor, 30);

        Assert.Equal(Floor, start);
        Assert.Contains("整段回补", mode);
    }

    [Fact]
    public void 首次_从历史起点()
    {
        var (start, mode) = MarketEventWindowRule.Start(false, watermark: null, Floor, 30);

        Assert.Equal(Floor, start);
        Assert.Contains("首次", mode);
    }

    /// <summary>增量从水位线**那一天本身**再往前推回看天数——公告全天陆续发，还会补发/修订。</summary>
    [Fact]
    public void 增量_从水位线再往前回看()
    {
        var mark = new DateTime(2026, 9, 1);
        var (start, mode) = MarketEventWindowRule.Start(false, mark, Floor, 30);

        Assert.Equal(mark.AddDays(-30), start);
        Assert.Contains("回看 30 天", mode);
    }

    /// <summary>回看不许越过历史起点。</summary>
    [Fact]
    public void 回看不越过历史起点()
    {
        var (start, _) = MarketEventWindowRule.Start(false, Floor.AddDays(3), Floor, 30);

        Assert.Equal(Floor, start);
    }
}
