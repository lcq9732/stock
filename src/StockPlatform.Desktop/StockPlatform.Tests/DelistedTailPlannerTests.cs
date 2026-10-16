using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 「哪些退市股缺最后几天、各补哪一段」（2026-09-21 随【退市股收尾】迁移抽出来，
/// 见 <see cref="DelistedTailPlanner"/>）。
///
/// ⚠ 判错的两个方向都很贵：漏判＝那几个交易日（退市整理期，回测最关键那段）**永久缺失**；
/// 多判＝停牌到退市的票每天被徒劳重抓。
/// </summary>
public class DelistedTailPlannerTests
{
    private static readonly DateTime Delist = new(2026, 8, 20);

    private static DelistedStockRow Row(string code, DateTime? delist, string name = "退市X")
        => new() { Code = code, Name = name, DelistDate = delist };

    private static List<DelistedTailPlanner.Target> Select(
        IEnumerable<DelistedStockRow> all, string[] tailPending,
        params (string Code, DateTime Latest)[] latestDay)
        => DelistedTailPlanner.SelectPending(
            all, tailPending.ToHashSet(StringComparer.Ordinal),
            latestDay.ToDictionary(x => x.Code, x => x.Latest));

    [Fact]
    public void 本地最后一根早于终止日_要补()
    {
        var got = Select([Row("600000", Delist)], ["600000"], ("600000", new DateTime(2026, 8, 10)));

        var t = Assert.Single(got);
        Assert.Equal(Delist, t.End);
        Assert.False(t.WholeHistory);
        Assert.Equal(new DateTime(2026, 8, 11), t.DayStart);   // 从本地最后一根的**次日**续
    }

    [Fact]
    public void 已经补到终止日_不补()
        => Assert.Empty(Select([Row("600000", Delist)], ["600000"], ("600000", Delist)));

    /// <summary>已经尝试过的不再碰——停牌到退市的票 K线永远早于终止日，没这道闸会天天重抓。</summary>
    [Fact]
    public void 已尝试过_不补()
        => Assert.Empty(Select([Row("600000", Delist)], tailPending: [], ("600000", new DateTime(2026, 8, 10))));

    /// <summary>上交所转板/吸收合并那些行没有终止日，算不出补到哪天。</summary>
    [Fact]
    public void 没有终止日_不补()
        => Assert.Empty(Select([Row("600000", null)], ["600000"], ("600000", new DateTime(2026, 8, 10))));

    /// <summary>本地一根都没有的不算补尾巴——归下面的整段缺失。</summary>
    [Fact]
    public void 本地没有历史_不补()
        => Assert.Empty(Select([Row("600000", Delist)], ["600000"]));

    // ── 整段缺失（本地一根前复权都没有，2026-10-16）──

    private static readonly DateTime Today = new(2026, 10, 16);

    private static List<DelistedTailPlanner.Target> Whole(
        IEnumerable<DelistedStockRow> all, string[] tailPending, params (string Code, DateTime Latest)[] latestDay)
        => DelistedTailPlanner.SelectWholeHistory(
            all, tailPending.ToHashSet(StringComparer.Ordinal),
            latestDay.ToDictionary(x => x.Code, x => x.Latest), Today);

    /// <summary>巨潮补进来的那批没有终止日——照样补，补到今天（数据源只给到它最后交易那天）。</summary>
    [Fact]
    public void 整段缺失_没有终止日_从开市首日补到今天()
    {
        var t = Assert.Single(Whole([Row("688287", null)], ["688287"]));

        Assert.True(t.WholeHistory);
        Assert.Equal(IncrementalWindowCalculator.AShareMarketOpen, t.DayStart);
        Assert.Equal(Today, t.End);
    }

    [Fact]
    public void 整段缺失_有终止日_补到终止日()
        => Assert.Equal(Delist, Assert.Single(Whole([Row("600087", Delist)], ["600087"])).End);

    /// <summary>每只只抓一次：抓过（成功或数据源确实没有）就打了标记，不再碰。</summary>
    [Fact]
    public void 整段缺失_已尝试过_不补()
        => Assert.Empty(Whole([Row("688287", null)], tailPending: []));

    /// <summary>本地有前复权的归补尾巴那一类，两类不重叠。</summary>
    [Fact]
    public void 本地有前复权_不算整段缺失()
        => Assert.Empty(Whole([Row("600000", Delist)], ["600000"], ("600000", new DateTime(2026, 8, 10))));

    /// <summary>后复权/不复权：整段缺失的票该口径没有历史时也从开市首日起，不是只回看几年。</summary>
    [Fact]
    public void 整段缺失_后复权窗口从开市首日起()
    {
        var t = new DelistedTailPlanner.Target("688287", "退市观典", Today,
                                               IncrementalWindowCalculator.AShareMarketOpen, WholeHistory: true);

        Assert.Equal((IncrementalWindowCalculator.AShareMarketOpen, Today), DelistedTailPlanner.WindowFor(t, null, 3));
        Assert.Equal((new DateTime(2026, 6, 9), Today),
                     DelistedTailPlanner.WindowFor(t, new DateTime(2026, 6, 8), 3));   // 有历史照旧从次日续
    }

    [Fact]
    public void 补尾巴_后复权窗口照旧回看几年()
    {
        var t = new DelistedTailPlanner.Target("600000", "退市X", Delist, new DateTime(2026, 8, 11));

        Assert.Equal((Delist.AddYears(-3), Delist), DelistedTailPlanner.WindowFor(t, null, 3));
    }

    // ── 后复权/不复权的窗口（水位线跟前复权各自独立）──

    [Fact]
    public void 有该口径历史_从次日续()
    {
        var (start, end) = DelistedTailPlanner.WindowFor(Delist, new DateTime(2026, 8, 1), 3);

        Assert.Equal(new DateTime(2026, 8, 2), start);
        Assert.Equal(Delist, end);
    }

    /// <summary>完全没有该口径历史：从终止日往前回看几年，一次抓够（回测吃的就是这两条线）。</summary>
    [Fact]
    public void 没有该口径历史_从终止日往前回看()
    {
        var (start, end) = DelistedTailPlanner.WindowFor(Delist, null, 3);

        Assert.Equal(Delist.AddYears(-3), start);
        Assert.Equal(Delist, end);
    }
}
