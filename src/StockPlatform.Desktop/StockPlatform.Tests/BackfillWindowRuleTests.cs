using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 年份区间怎么收窄各任务自己的「整段」窗口（2026-09-22，见 <see cref="BackfillWindowRule"/>）。
///
/// ⚠ 这一条最要紧：**只收窄、永不放宽**。放宽了会让分档资金流那种"数据源只给 120 天"的任务
/// 白发二十年的空请求——而"成功返回空"会被记进 BarProbeFloor 当成"数据源没有更早数据"，
/// 污染那张表。2026-09-07 实测过这类空跑的代价：5558 只 × 3 个粒度、四个半小时、零写入。
/// </summary>
public class BackfillWindowRuleTests
{
    private static readonly DateTime Today = new(2026, 9, 22);
    private static readonly DateTime WholeFrom = new(1990, 12, 19);   // A股开市首日
    private static readonly DateTime WholeTo = Today;

    private static (DateTime Start, DateTime End) Narrow(int? ys, int? ye,
                                                         DateTime? from = null, DateTime? to = null)
        => BackfillWindowRule.Narrow(from ?? WholeFrom, to ?? WholeTo, ys, ye, Today);

    [Fact]
    public void 两头都不填_原样不动()
        => Assert.Equal((WholeFrom, WholeTo), Narrow(null, null));

    [Fact]
    public void 给了区间_收窄到那几年()
    {
        var (s, e) = Narrow(2016, 2022);

        Assert.Equal(new DateTime(2016, 1, 1), s);
        Assert.Equal(new DateTime(2022, 12, 31), e);
    }

    [Fact]
    public void 只填起始年_结束那头不动()
        => Assert.Equal((new DateTime(2016, 1, 1), WholeTo), Narrow(2016, null));

    [Fact]
    public void 只填结束年_起始那头不动()
        => Assert.Equal((WholeFrom, new DateTime(2022, 12, 31)), Narrow(null, 2022));

    // ── 只收窄，永不放宽 ──

    /// <summary>⭐ 起始年比任务自己的整段还早 → **不放宽**，仍从任务自己的起点开始。</summary>
    [Fact]
    public void 起始年比整段还早_不放宽()
    {
        var floor = new DateTime(2026, 4, 1);          // 比如分档资金流只给最近 120 天
        var (s, _) = Narrow(1990, null, from: floor);

        Assert.Equal(floor, s);
    }

    /// <summary>⭐ 结束年比任务自己的整段还晚 → 同样不放宽。</summary>
    [Fact]
    public void 结束年比整段还晚_不放宽()
    {
        var ceiling = new DateTime(2020, 6, 30);       // 比如退市股，最后一根就到那天
        var (_, e) = Narrow(null, 2030, to: ceiling);

        Assert.Equal(ceiling, e);
    }

    // ── 今年那一头 ──

    /// <summary>结束年＝今年 → 收到**今天**，不是 12-31：之后的日期还没发生。</summary>
    [Fact]
    public void 结束年是今年_收到今天()
        => Assert.Equal(Today.Date, Narrow(null, Today.Year).End);

    /// <summary>结束年比今年还晚（手滑填了 2030）→ 也只到今天，不往未来要数据。</summary>
    [Fact]
    public void 结束年晚于今年_也只到今天()
        => Assert.Equal(Today.Date, Narrow(null, 2030).End);

    // ── 空窗口是正常结果，不是错误 ──

    /// <summary>要的区间整段早于任务自己的起点 → 空窗口，调用方按 start &gt; end 跳过。</summary>
    [Fact]
    public void 区间整段早于整段起点_给出空窗口()
    {
        var floor = new DateTime(2026, 4, 1);
        var (s, e) = Narrow(2016, 2022, from: floor);

        Assert.True(s > e, $"该是空窗口，实际 {s:yyyy-MM-dd}~{e:yyyy-MM-dd}");
    }

    /// <summary>要的区间整段晚于任务自己的终点 → 同样是空窗口。</summary>
    [Fact]
    public void 区间整段晚于整段终点_给出空窗口()
    {
        var ceiling = new DateTime(2012, 6, 30);
        var (s, e) = Narrow(2016, 2022, to: ceiling);

        Assert.True(s > e, $"该是空窗口，实际 {s:yyyy-MM-dd}~{e:yyyy-MM-dd}");
    }

    /// <summary>起止同一年＝只补那一年。</summary>
    [Fact]
    public void 起止同年_就补那一年()
    {
        var (s, e) = Narrow(2018, 2018);

        Assert.Equal(new DateTime(2018, 1, 1), s);
        Assert.Equal(new DateTime(2018, 12, 31), e);
    }
}
