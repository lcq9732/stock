using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 板块指数合成读哪个口径（2026-09-11）。
///
/// 为什么要钉死：<see cref="BoardIndexSynthesizer"/> 算的是**收益率**，而源给的前复权是减法式
/// （原价减去此后的累计分红）——非除权日两天减的是同一个常数、分母却变小了，所以连非除权日的
/// 收益率都是错的，分红越重放大越狠。而这个错**完全静默**：板块指数照样画得出一条曲线。
///
/// 拿东财本地那 1032 只官方板块指数当标尺实测（比日收益率序列）：
///   银行 BK0475 全历史  前复权 相关 0.454 / 平均差 5.88%   day_adj 相关 0.945 / 0.30%
///            2000-2010 前复权 相关 0.510 / 平均差 14.95%（81% 的天差&gt;2%）
///   煤炭 BK0437 2016 后 前复权 相关 0.593（26% 的天差&gt;2%）  day_adj 0.994（0 天差&gt;2%）
///   白酒 BK0896 2016 后 两者持平 0.965 / 0.961  ← 股息率低、股价高，减法式扣掉的占比小
/// 白酒那一行是反证：分红轻的板块两者差不多，所以失真确实来自分红。
/// </summary>
public class BoardIndexGranularityTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteBarRepository _bars;

    private static readonly DateTime[] Days =
    [
        new(2026, 9, 1), new(2026, 9, 2), new(2026, 9, 3), new(2026, 9, 4), new(2026, 9, 7),
    ];

    public BoardIndexGranularityTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"boardgran_{Guid.NewGuid():N}.sqlite");
        _bars = new SqliteBarRepository(_dbPath);
        _bars.EnsureSchema();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    private void Put(string code, string gran, DateTime day, double close) =>
        _bars.InsertOrRefreshUnconfirmed([new Bar
        {
            Code = code, Granularity = gran, PeriodStart = day,
            Open = close, Close = close, High = close, Low = close,
            Volume = 100, Amount = 100 * close * 100, Turnover = 1.5,
            FetchedAt = day.AddHours(20),
        }]);

    /// <summary>把一条收盘价序列写进某个口径。</summary>
    private void PutSeries(string code, string gran, params double[] closes)
    {
        for (int i = 0; i < closes.Length; i++) Put(code, gran, Days[i], closes[i]);
    }

    /// <summary>
    /// 合成读的是 <c>day_adj</c>，不是 <c>day</c>。
    ///
    /// 造法：两个口径给**完全不同**的价格序列——`day` 一路涨、`day_adj` 一路跌。
    /// 读错口径的话指数会朝反方向走，一眼就能看出来。
    /// </summary>
    [Fact]
    public void 合成读的是回测口径_不是前复权()
    {
        var members = new List<string>();
        for (int k = 0; k < 5; k++)               // MinMembersPerDay = 5，至少要 5 只
        {
            var code = $"60000{k}";
            members.Add(code);
            PutSeries(code, Granularity.Day,    10, 11, 12, 13, 14);   // 前复权：一路涨
            PutSeries(code, Granularity.DayAdj, 10, 9, 8, 7, 6);       // 回测口径：一路跌
        }

        var bars = BoardIndexSynthesizer.Synthesize("BK0001", members, _bars, DateTime.Now);

        Assert.NotEmpty(bars);
        // 读对了口径 ⇒ 指数跟着 day_adj 往下走，收在 1000 以下
        Assert.True(bars[^1].Close < 1000,
            $"指数收在 {bars[^1].Close:F1}，>1000 说明读的是前复权那条一路涨的序列");
    }

    /// <summary>
    /// 减法式前复权的失真复现：同一只票，真实收益率是 +10%/天，而前复权序列（每天都减掉
    /// 同一个累计分红 9 元）算出来的收益率被放大到 +100%/天。
    ///
    /// 这不是造的极端值——`D` 越接近老股价放大越狠，万科 1997 年的前复权价干脆是 −8.17。
    /// </summary>
    [Fact]
    public void 减法式前复权会放大收益率()
    {
        var members = new List<string>();
        for (int k = 0; k < 5; k++)
        {
            var code = $"60010{k}";
            members.Add(code);
            //  真实价 10 → 11（+10%）；减掉累计分红 9 之后是 1 → 2（+100%）
            PutSeries(code, Granularity.Day,    1, 2);
            PutSeries(code, Granularity.DayAdj, 10, 11);
        }

        var bars = BoardIndexSynthesizer.Synthesize("BK0002", members, _bars, DateTime.Now);

        Assert.Single(bars);
        // 读 day_adj ⇒ +10%，指数 1100；读 day 会是 +100%、指数 2000
        Assert.Equal(1100.0, bars[0].Close, precision: 6);
    }

    /// <summary>
    /// 只有前复权、没有回测口径的成分股会被跳过（<c>bars.Count &lt; 2</c> 那条）。
    ///
    /// 实测这不影响生产：板块成分股 5651 只里，`day` 和 `day_adj` 都是 5559 只有 ≥2 根
    /// （缺的 92 只是 B 股 200xxx，两个口径都没有），换口径后**没有任何板块的可用成分股
    /// 掉到 <see cref="BoardIndexSynthesizer.MinMembersPerDay"/> 以下**。
    /// </summary>
    [Fact]
    public void 只有前复权的成分股被跳过()
    {
        var members = new List<string>();
        for (int k = 0; k < 5; k++)
        {
            var code = $"60020{k}";
            members.Add(code);
            PutSeries(code, Granularity.Day, 10, 11);      // 只有前复权
        }

        var bars = BoardIndexSynthesizer.Synthesize("BK0003", members, _bars, DateTime.Now);
        Assert.Empty(bars);
    }

    /// <summary>量额加总不受口径影响——四个日线口径的 volume/amount 是同一个值。</summary>
    [Fact]
    public void 量额加总不受口径影响()
    {
        var members = new List<string>();
        for (int k = 0; k < 5; k++)
        {
            var code = $"60030{k}";
            members.Add(code);
            PutSeries(code, Granularity.DayAdj, 10, 11);
        }

        var bars = BoardIndexSynthesizer.Synthesize("BK0004", members, _bars, DateTime.Now);
        Assert.Single(bars);
        Assert.Equal(5 * 100, bars[0].Volume);                  // 5 只票 × 100 手
        Assert.Equal(5 * 100 * 11 * 100, bars[0].Amount);       // Put 里 amount = volume×close×100
    }
}
