using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 陈峰法（<see cref="ProfitableSmallCapAnalysisEngine"/> + <see cref="VolumeExitDiscipline"/>，2026-11-16）。
/// 钉住用户逐条改过的口径，免得后来人"顺手"改回回测里的样子：
///   · 缩量/放量按**成交额**算，不按换手率；
///   · 缩量是"近5个交易日（含当天）任一天"，不是只看当天；备注列要写缩量天数和日期；
///   · 上市满1年用名册上市日，没有才退回第一根日K；
///   · 股价≥MinPrice（默认10元，可调）、250日位置&lt;30% 是两条独立条件。
/// </summary>
public class ProfitableSmallCapTests
{
    private const string Code = "600000";
    private static readonly DateTime D0 = new(2025, 1, 1);

    /// <summary>280 根日K：收盘从 30 一路跌到 16.05（一年低位、股价≥15），成交额恒定 1 亿；
    /// <paramref name="shrinkAt"/> 那几根成交额改成 0.4 亿（缩量）。</summary>
    private static List<Bar> Bars(int dayStep = 1, double priceShift = 0, params int[] shrinkAt)
    {
        var bars = new List<Bar>();
        for (int i = 0; i < 280; i++)
        {
            double c = 30 - i * 0.05 + priceShift;
            bars.Add(new Bar
            {
                Code = Code, Granularity = Granularity.Day, PeriodStart = D0.AddDays(i * dayStep),
                Open = c, Close = c, High = c, Low = c, Volume = 1e6,
                Amount = shrinkAt.Contains(i) ? 0.4e8 : 1e8,
            });
        }
        return bars;
    }

    private static FinancialSnapshot Snap(int y, int m, double np, double ocf) => new()
    {
        ReportDate = new DateTime(y, m, DateTime.DaysInMonth(y, m)),
        Values = new() { [FinancialKeys.NetProfitParent] = np, [FinancialKeys.Ocf] = ocf },
    };

    /// <summary>最新在前：26Q3 累计 +3/+2，25年报 +4/+3，25Q3 +3/+2 → TTM 净利 +4、现金流 +3。</summary>
    private static List<FinancialSnapshot> GoodFins() => new()
    {
        Snap(2026, 9, 3e8, 2e8), Snap(2025, 12, 4e8, 3e8), Snap(2025, 9, 3e8, 2e8),
    };

    private static StockScreenResult Run(List<Bar> bars, double? cap = 20e8, DateTime? listDate = null, string name = "测试股份",
        Func<List<FinancialSnapshot>>? fins = null, bool noListDate = false, double? minPrice = null)
    {
        var engine = new ProfitableSmallCapAnalysisEngine(null!, null!, null!, new Dictionary<string, DateTime>())
        {
            MinPrice = minPrice ?? ProfitableSmallCapAnalysisEngine.DefaultMinPrice,
        };
        return engine.Evaluate(Code, name, cap, noListDate ? null : listDate ?? new DateTime(2015, 1, 1),
            () => bars, fins ?? GoodFins);
    }

    [Fact]
    public void 八条都满足_通过_备注写缩量天数和日期()
    {
        var bars = Bars(shrinkAt: [276, 278]);
        var r = Run(bars);
        Assert.True(r.Passed, string.Join("\n", r.Criteria.Select(c => $"{c.Name} {c.Satisfied} {c.Basis}")));
        Assert.Equal(8, r.Criteria.Count);
        Assert.Equal(Enumerable.Range(1, 8).Select(n => n.ToString()), r.Criteria.Select(c => c.Name[..1]));
        Assert.Equal(0, r.SortScore!.Value, 3);
        Assert.Contains("缩量2天", r.PatternNote);
        Assert.Contains(bars[276].PeriodStart.ToString("MM-dd"), r.PatternNote);
        Assert.DoesNotContain("今天缩量", r.PatternNote);
    }

    [Fact]
    public void 当天缩量_备注标今天缩量()
    {
        var r = Run(Bars(shrinkAt: [279]));
        Assert.True(r.Passed);
        Assert.Contains("今天缩量", r.PatternNote);
    }

    [Fact]
    public void 缩量在5日窗口之外_不通过()
    {
        var r = Run(Bars(shrinkAt: [270]));
        Assert.False(r.Passed);
        Assert.Contains("缩量0天", r.PatternNote);
    }

    [Fact]
    public void 成交额全缺_缩量条跳过不判负()
    {
        var bars = Bars();
        foreach (var b in bars) b.Amount = 0;
        var r = Run(bars);
        Assert.True(r.Passed);
        Assert.True(r.Criteria.Single(c => c.Name.StartsWith("8")).DataMissing);
    }

    [Fact]
    public void 默认门槛10元_14元的票通过()
    {
        var r = Run(Bars(priceShift: -2, shrinkAt: [279]));   // 最新收盘 14.05
        Assert.True(r.Passed);
        Assert.Equal("6 股价 ≥ 10 元", r.Criteria.Single(c => c.Name.StartsWith("6")).Name);
    }

    [Fact]
    public void 门槛调到15元_14元的票不通过()
    {
        var r = Run(Bars(priceShift: -2, shrinkAt: [279]), minPrice: 15);   // 最新收盘 14.05
        Assert.False(r.Passed);
        Assert.False(r.Criteria.Single(c => c.Name.StartsWith("6")).Satisfied);
    }

    [Fact]
    public void 不在一年低位_不通过()
    {
        var bars = Bars(shrinkAt: [279]);
        bars[^1].Close = 27;   // 回到区间上部
        var r = Run(bars);
        Assert.False(r.Passed);
        Assert.False(r.Criteria.Single(c => c.Name.StartsWith("7")).Satisfied);
    }

    [Theory]
    [InlineData(60e8)]
    [InlineData(4e8)]
    public void 市值不在5到50亿_直接返回_不读K线和财报(double cap)
    {
        var engine = new ProfitableSmallCapAnalysisEngine(null!, null!, null!, new Dictionary<string, DateTime>());
        var r = engine.Evaluate(Code, "测试", cap, null,
            () => throw new InvalidOperationException("不该读K线"), () => throw new InvalidOperationException("不该读财报"));
        Assert.False(r.Passed);
    }

    [Fact]
    public void ST和北交所_不通过()
    {
        Assert.False(Run(Bars(shrinkAt: [279]), name: "*ST测试").Passed);
        var engine = new ProfitableSmallCapAnalysisEngine(null!, null!, null!, new Dictionary<string, DateTime>());
        Assert.False(engine.Evaluate("920001", "北交测试", 20e8, null, () => Bars(), GoodFins).Passed);
    }

    [Fact]
    public void 上市日用名册_没有才退回第一根K线()
    {
        // 名册说上市才半年 → 不通过（K线本身跨度够长也不行）
        var r = Run(Bars(shrinkAt: [279]), listDate: D0.AddDays(279 - 180));
        Assert.False(r.Criteria.Single(c => c.Name.StartsWith("3")).Satisfied);

        // 名册没有：K线只有 280 天（不满1年）→ 不通过，依据里写明退回第一根
        r = Run(Bars(shrinkAt: [279]), noListDate: true);
        var c3 = r.Criteria.Single(c => c.Name.StartsWith("3"));
        Assert.False(c3.Satisfied);
        Assert.Contains("第一根日K", c3.Basis);

        // 名册没有：K线隔天一根、跨 560 天 → 通过
        Assert.True(Run(Bars(dayStep: 2, shrinkAt: [279]), noListDate: true).Passed);
    }

    [Fact]
    public void TTM净利为负_即使最新一期累计为正也不通过()
    {
        // 26Q3 累计 +1，25年报 −10，25Q3 +5 → TTM = −10 − 5 + 1 = −14
        var fins = new List<FinancialSnapshot> { Snap(2026, 9, 1e8, 2e8), Snap(2025, 12, -10e8, 3e8), Snap(2025, 9, 5e8, 2e8) };
        var r = Run(Bars(shrinkAt: [279]), fins: () => fins);
        Assert.False(r.Passed);
        var c4 = r.Criteria.Single(c => c.Name.StartsWith("4"));
        Assert.False(c4.Satisfied);
        Assert.Contains("-14.00", c4.Basis);
    }

    [Fact]
    public void TTM缺上年同期_退回最新累计并写明()
    {
        var (v, basis) = ProfitableSmallCapAnalysisEngine.Ttm(new() { Snap(2026, 9, 3e8, 2e8) }, FinancialKeys.NetProfitParent);
        Assert.Equal(3e8, v);
        Assert.Contains("退回最新一期累计", basis);
    }

    // ───────── VolumeExitDiscipline ─────────

    private static List<Bar> Flat(int n, double close, double amount = 1e8, double earlyHigh = 0)
    {
        var bars = new List<Bar>();
        for (int i = 0; i < n; i++)
            bars.Add(new Bar { Code = Code, PeriodStart = D0.AddDays(i), Close = i < 5 && earlyHigh > 0 ? earlyHigh : close, Amount = amount });
        return bars;
    }

    [Fact]
    public void 成交额比_零额不进均值_有效天数不足返回空()
    {
        var bars = Flat(30, 10);
        bars[25].Amount = 0;
        bars[^1].Amount = 3e8;
        Assert.Equal(3.0, VolumeExitDiscipline.AmountRatio(bars, 29)!.Value, 6);
        Assert.Null(VolumeExitDiscipline.AmountRatio(bars, 10));   // 前面只有10天
        bars[^1].Amount = 0;
        Assert.Null(VolumeExitDiscipline.AmountRatio(bars, 29));
    }

    private static VolumeExitDiscipline.HoldingState State(double avg = 10, double lastBuy = 10, int lots = 1, bool sold = false,
        double? np = 1e8, double? ocf = 1e8) => new(avg, lastBuy, lots, sold, np, ocf);

    [Fact]
    public void 放量收涨_没减过减半_减过清仓_收跌不算()
    {
        var bars = Flat(200, 10, earlyHigh: 20);
        bars[^1].Close = 10.3; bars[^1].Amount = 2.5e8;
        Assert.Contains(VolumeExitDiscipline.EvaluateHolding(bars, State()), a => a.Text.Contains("先减半"));
        Assert.Contains(VolumeExitDiscipline.EvaluateHolding(bars, State(sold: true)), a => a.Text.Contains("已减过一次 → 清仓"));

        bars[^1].Close = 9.7;
        Assert.DoesNotContain(VolumeExitDiscipline.EvaluateHolding(bars, State()), a => a.Text.Contains("放量"));
    }

    [Fact]
    public void 价格纪律_25减半_50清仓_高位清仓()
    {
        var bars = Flat(200, 10, earlyHigh: 20);   // 早期高点 20，位置不会误触 80%
        bars[^1].Close = 13;
        Assert.Contains(VolumeExitDiscipline.EvaluateHolding(bars, State()), a => a.Text.Contains("→ 减半"));
        Assert.Empty(VolumeExitDiscipline.EvaluateHolding(bars, State(sold: true)));   // 减过就不再提减半

        bars[^1].Close = 15.5;
        Assert.Contains(VolumeExitDiscipline.EvaluateHolding(bars, State()), a => a.Text.Contains("清仓"));

        var high = Flat(200, 10);
        high[^1].Close = 11;   // 只涨10%，但在250日区间最高处
        Assert.Contains(VolumeExitDiscipline.EvaluateHolding(high, State()), a => a.Text.Contains("250日位置"));
    }

    [Fact]
    public void 跌15加仓_满3笔不再提示()
    {
        var bars = Flat(200, 10, earlyHigh: 20);
        bars[^1].Close = 8.4;
        Assert.Contains(VolumeExitDiscipline.EvaluateHolding(bars, State()), a => a.Text.Contains("可加仓") && a.Severity == 3);
        Assert.DoesNotContain(VolumeExitDiscipline.EvaluateHolding(bars, State(lots: 3)), a => a.Text.Contains("可加仓"));
    }

    [Fact]
    public void 基本面转亏_清仓且最高严重度()
    {
        var bars = Flat(200, 10, earlyHigh: 20);
        var a = VolumeExitDiscipline.EvaluateHolding(bars, State(np: -1e8));
        Assert.Contains(a, x => x.Severity == 0 && x.Text.Contains("转亏"));
        a = VolumeExitDiscipline.EvaluateHolding(bars, State(ocf: -1e8));
        Assert.Contains(a, x => x.Severity == 0 && x.Text.Contains("现金流"));
        Assert.Empty(VolumeExitDiscipline.EvaluateHolding(bars, State(np: null, ocf: null)));   // 没财报不判
    }

    // ───────── 名册上市日（真 SQLite）─────────

    [Fact]
    public void GetListDates_读名册上市日_空值和空串跳过_带时间只取日期()
    {
        var db = Path.Combine(Path.GetTempPath(), $"listdate_{Guid.NewGuid():N}.sqlite");
        try
        {
            SqliteStockMetaUpsert.Upsert(db, [("600001", "甲"), ("600002", "乙"), ("600003", "丙"), ("600004", "丁")]);
            using (var conn = new SqliteConnection($"Data Source={db}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE StockMeta SET list_date='2020-03-05' WHERE code='600001';" +
                                  "UPDATE StockMeta SET list_date='2021-07-08 00:00:00' WHERE code='600002';" +
                                  "UPDATE StockMeta SET list_date='' WHERE code='600003';";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            var d = SqliteStockMetaUpsert.GetListDates(db);
            Assert.Equal(2, d.Count);
            Assert.Equal(new DateTime(2020, 3, 5), d["600001"]);
            Assert.Equal(new DateTime(2021, 7, 8), d["600002"]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(db); } catch { }
        }
    }
}
