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
        Assert.Equal(Delist, t.DelistDate);
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

    /// <summary>本地一根都没有的不在这里补——那是【拉取区间数据】的活。</summary>
    [Fact]
    public void 本地没有历史_不补()
        => Assert.Empty(Select([Row("600000", Delist)], ["600000"]));

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
