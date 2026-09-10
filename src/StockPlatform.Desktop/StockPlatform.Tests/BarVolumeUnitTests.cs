using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 成交量单位归一化（2026-09-10）。
///
/// 病根：三个数据源口径不同——腾讯主板给手/**科创板给股**、新浪**一律给股**、东财都给手——
/// 而两个 fetcher 原来把数字原样入库。2026-09-08 全市场日线实测：主板 3203 只、创业板 1404 只、
/// 北交所 342 只是手，科创板 688/689 的 613 只是股，差 100 倍。
///
/// 这个错不会报警：单票内部的分析用的都是比值，单位约掉了；只有跨股票累加（板块指数把成分股
/// 成交量加总）才露馅。所以这里钉死两件事——**解析出口的换算**和**历史修正的判据**。
/// </summary>
public class BarVolumeUnitTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteBarRepository _repo;

    public BarVolumeUnitTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"volunit_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteBarRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!,
                                             Path.GetFileNameWithoutExtension(_dbPath) + "*"))
            try { File.Delete(f); } catch { /* 临时文件 */ }
    }

    // ─────────────────── 解析出口的换算 ───────────────────

    /// <summary>
    /// 腾讯：只有科创板要除。数字取自 2026-09-09 实抓——
    /// 688001 腾讯给 19,050,931（股），而东财同一天给 190,509（手）。
    /// </summary>
    [Theory]
    [InlineData("688001", 19050931, 190509.31)]   // 科创板 → 除
    [InlineData("689009", 1000000, 10000)]        // 科创板 CDR → 除
    [InlineData("600000", 505325, 505325)]        // 沪主板 → 原样
    [InlineData("000001", 123456, 123456)]        // 深主板 → 原样
    [InlineData("300750", 390240, 390240)]        // 创业板 → 原样
    [InlineData("920371", 237344, 237344)]        // 北交所 → 原样
    public void TencentOnlyDividesTheStarBoard(string code, double raw, double expected)
    {
        Assert.Equal(expected, BarVolumeUnit.ToLots(code, raw, BarVolumeUnit.Source.Tencent), 4);
    }

    /// <summary>
    /// 新浪：**所有板块**都给股，全要除。实抓佐证：2026-09-09 新浪给 600000 的是 50,532,458，
    /// 而当天真实成交是 505,325 手——正好 100 倍。
    /// </summary>
    [Theory]
    [InlineData("600000", 50532458, 505324.58)]
    [InlineData("000001", 10000000, 100000)]
    [InlineData("688001", 19050931, 190509.31)]
    public void SinaDividesEveryBoard(string code, double raw, double expected)
    {
        Assert.Equal(expected, BarVolumeUnit.ToLots(code, raw, BarVolumeUnit.Source.Sina), 4);
    }

    /// <summary>东财三个板块都给手，一律不动。</summary>
    [Theory]
    [InlineData("600000")]
    [InlineData("688001")]
    [InlineData("920371")]
    public void EastMoneyNeedsNoConversion(string code)
    {
        Assert.Equal(505325, BarVolumeUnit.ToLots(code, 505325, BarVolumeUnit.Source.EastMoney));
    }

    /// <summary>
    /// 指数走的是带前缀的 8 位符号，<see cref="MarketClassifier"/> 判不出板块——正好，
    /// 指数的"成交量"是成分股汇总，手/股这套判据对它没有意义，不该被换算。
    /// </summary>
    [Fact]
    public void PrefixedIndexSymbolsAreLeftAlone()
    {
        Assert.Equal(12345, BarVolumeUnit.ToLots("sh000001", 12345, BarVolumeUnit.Source.Tencent));
        Assert.Equal(12345, BarVolumeUnit.ToLots("sz399106", 12345, BarVolumeUnit.Source.Tencent));
    }

    // ─────────────────── 历史修正的判据 ───────────────────

    /// <summary>
    /// 判据是量额比而不是代码前缀。600000 那一行的数字是 2026-09-09 实抓的：
    /// 505,325 手 × 9.23 元 ≈ 4.67 亿（比值 ≈100，是手）；换成 50,532,458 股则比值 ≈1。
    /// </summary>
    [Theory]
    [InlineData(505325, 467549947, 9.23, false)]     // 手
    [InlineData(50532458, 467549947, 9.23, true)]    // 股
    [InlineData(19050931, 1244048584, 66.31, true)]  // 688001 实抓，股
    [InlineData(190509, 1244048584, 66.31, false)]   // 同一天东财给的，手
    public void LooksLikeSharesUsesTheAmountRatio(double vol, double amt, double close, bool expected)
    {
        Assert.Equal(expected, BarVolumeUnit.LooksLikeShares(vol, amt, close));
    }

    /// <summary>缺 amount 或 close 就判不出来——这时**不许猜**，宁可漏改也不能错改。</summary>
    [Theory]
    [InlineData(1000, 0, 10)]      // 没有成交额（新浪抓的行就是这样）
    [InlineData(1000, 10000, 0)]   // 没有收盘价
    [InlineData(0, 10000, 10)]     // 没有成交量
    public void LooksLikeSharesRefusesToGuessWithoutAmountOrClose(double vol, double amt, double close)
    {
        Assert.False(BarVolumeUnit.LooksLikeShares(vol, amt, close));
    }

    // ─────────────────── 历史修正的实际行为 ───────────────────

    private static Bar Bar(string code, string gran, DateTime day, double close, double vol, double amt) =>
        new()
        {
            Code = code, Granularity = gran, PeriodStart = day,
            Open = close, Close = close, High = close, Low = close,
            Volume = vol, Amount = amt, FetchedAt = day.AddHours(20),
        };

    private double VolumeOf(string code, string gran)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT volume FROM Bar WHERE code=$c AND granularity=$g;";
        cmd.Parameters.AddWithValue("$c", code);
        cmd.Parameters.AddWithValue("$g", gran);
        return Convert.ToDouble(cmd.ExecuteScalar());
    }

    [Fact]
    public void FixerConvertsShareRowsAndLeavesLotRowsAlone()
    {
        var d = new DateTime(2026, 9, 9);
        _repo.InsertOrRefreshUnconfirmed([
            Bar("688001", "day_raw", d, 66.31, 19050931, 1244048584),   // 股 → 要改
            Bar("600000", "day_raw", d, 9.23, 505325, 467549947),       // 手 → 不动
        ]);

        var n = new SqliteBarVolumeUnitFixer(_dbPath).FixAsync();

        Assert.Equal(190509.31, VolumeOf("688001", "day_raw"), 2);
        Assert.Equal(505325, VolumeOf("600000", "day_raw"));
        Assert.Equal(1, n["day_raw"]);
    }

    /// <summary>
    /// **从"修了一半"的状态重跑，必须能补齐**（2026-09-10 第二次生产事故）。
    ///
    /// 真实经过：第一轮跑的是旧版 fixer，把 day_raw 全改对了、day 漏了 199,860 行。
    /// 修好代码重跑，结果日志写着"涉及 0 只标的"、六个口径各"修正 0 行"——因为找票的判据是
    /// "day_raw 的量额比 ≈1"，而 day_raw 已经被上一轮改成手了，一条都找不到。
    /// **拿"还没修"当前提去找要修的东西，跑过一轮之后这个前提就没了。**
    ///
    /// 所以这条测试的初始状态**故意是半修状态**：day_raw 已是手、day 还是股。
    /// 之前那些测试全从"全是股"开始，一条都拦不住这个 bug。
    /// </summary>
    [Fact]
    public void FixesTheRestWhenDayRawWasAlreadyCorrectedInAnEarlierRun()
    {
        var d = new DateTime(2020, 6, 15);
        _repo.InsertOrRefreshUnconfirmed([
            Bar("688004", "day_raw", d, 131.07, 69647.59, 913013253),   // 上一轮已改对 → 手
            Bar("688004", "day", d, 92.72, 6964759, 913013253),         // 上一轮漏了 → 还是股
            Bar("688004", "week", d, 92.72, 6964759, 913013253),        // 同上
        ]);

        // 前提：day_raw 已经不像"股"了，第①条判据找不到这只票
        Assert.False(BarVolumeUnit.LooksLikeShares(69647.59, 913013253, 131.07));

        new SqliteBarVolumeUnitFixer(_dbPath).FixAsync();

        Assert.Equal(69647.59, VolumeOf("688004", "day_raw"), 2);   // 不许再除一次
        Assert.Equal(69647.59, VolumeOf("688004", "day"), 2);       // 补上
        Assert.Equal(69647.59, VolumeOf("688004", "week"), 2);      // 补上
    }

    /// <summary>反复跑不会越除越小——改完的行比值变成 ≈100，第二遍不会再命中。</summary>
    [Fact]
    public void FixerIsIdempotent()
    {
        var d = new DateTime(2026, 9, 9);
        _repo.InsertOrRefreshUnconfirmed([Bar("688001", "day_raw", d, 66.31, 19050931, 1244048584)]);

        var fixer = new SqliteBarVolumeUnitFixer(_dbPath);
        fixer.FixAsync();
        var second = fixer.FixAsync();

        Assert.Equal(0, second["day_raw"]);
        Assert.Equal(190509.31, VolumeOf("688001", "day_raw"), 2);
    }

    /// <summary>
    /// day_hfq / day_adj 的 close 是复权价，量额比失真（实测 600 开头是 23.76），自己判不了。
    /// 靠的是"复权不动量"这个硬事实：它们的 volume 本该等于 day_raw，改完 day_raw 之后
    /// 还差 100 倍的就要跟上。
    /// </summary>
    [Fact]
    public void FollowsDayRawForAdjustedGranularities()
    {
        var d = new DateTime(2026, 9, 9);
        _repo.InsertOrRefreshUnconfirmed([
            Bar("688001", "day_raw", d, 66.31, 19050931, 1244048584),
            Bar("688001", "day_hfq", d, 98.01, 19050931, 1244048584),   // 复权价，量跟 raw 一样
            Bar("600000", "day_raw", d, 9.23, 505325, 467549947),       // 手
            Bar("600000", "day_hfq", d, 18.60, 505325, 467549947),      // 跟着不动
        ]);

        new SqliteBarVolumeUnitFixer(_dbPath).FixAsync();

        Assert.Equal(190509.31, VolumeOf("688001", "day_hfq"), 2);
        Assert.Equal(505325, VolumeOf("600000", "day_hfq"));
    }

    /// <summary>
    /// **这条是第一版漏掉的那 199,860 行**（2026-09-10 生产实测才发现）。
    ///
    /// <c>day</c> 也是复权口径（前复权），第一版却把它归进"能自己判量额比"那一组。
    /// 数字取自真实漏改的 688004 在 2020-06-15：前复权价 92.72、真实价 131.07，
    /// 量额比因此是 <c>913,013,253 / (6,964,759 × 92.72) ≈ 1.41</c>——落在 0.8~1.25 之外，
    /// 判据就放过了它，于是 day 还是股、day_raw 已经是手，同一只票两个口径差 100 倍。
    ///
    /// 现在 day 跟 day_hfq/day_adj 一样走"比对 day_raw 同一天"。
    /// </summary>
    [Fact]
    public void FixesDayEvenWhenItsAdjustedCloseSkewsTheRatio()
    {
        var d = new DateTime(2020, 6, 15);
        _repo.InsertOrRefreshUnconfirmed([
            Bar("688004", "day_raw", d, 131.07, 6964759, 913013253),  // 真实价，比值 ≈1 → 自己判得出
            Bar("688004", "day", d, 92.72, 6964759, 913013253),       // 前复权价，比值 ≈1.41 → 判不出
        ]);

        // 前提：这一行确实骗过了量额比判据，否则这条测试就没在测该测的东西
        Assert.False(BarVolumeUnit.LooksLikeShares(6964759, 913013253, 92.72));

        new SqliteBarVolumeUnitFixer(_dbPath).FixAsync();

        Assert.Equal(69647.59, VolumeOf("688004", "day_raw"), 2);
        Assert.Equal(69647.59, VolumeOf("688004", "day"), 2);
    }

    /// <summary>
    /// 周/月线是本地聚合的（volume 求和、period_start 取该期第一个交易日），
    /// 所以拿**同期 day_raw 之和**比，不是拿单日比。第一版跟 day 一样漏了。
    /// </summary>
    [Fact]
    public void FixesWeeklyAndMonthlyAgainstTheSumOfDayRaw()
    {
        var mon = new DateTime(2026, 9, 7);
        _repo.InsertOrRefreshUnconfirmed([
            // 一周三天的 day_raw：股口径，会被第一步改成手（1000/2000/3000 手）
            Bar("688001", "day_raw", mon, 10, 100000, 1000000),
            Bar("688001", "day_raw", mon.AddDays(1), 10, 200000, 2000000),
            Bar("688001", "day_raw", mon.AddDays(2), 10, 300000, 3000000),
            // 周线：三天求和 600,000（股），改完应是 6,000 手
            Bar("688001", "week", mon, 10, 600000, 6000000),
            Bar("688001", "month", new DateTime(2026, 9, 1), 10, 600000, 6000000),
        ]);

        new SqliteBarVolumeUnitFixer(_dbPath).FixAsync();

        Assert.Equal(6000, VolumeOf("688001", "week"), 2);
        Assert.Equal(6000, VolumeOf("688001", "month"), 2);
    }

    /// <summary>
    /// 修完之后所有口径的关系必须自洽：day / day_hfq / day_adj 逐行等于 day_raw，
    /// 周线等于同期之和。这条是"整体不变式"，比逐个口径的断言更难被绕过去。
    /// </summary>
    [Fact]
    public void AllGranularitiesAgreeAfterTheFix()
    {
        var mon = new DateTime(2026, 9, 7);
        _repo.InsertOrRefreshUnconfirmed([
            Bar("688001", "day_raw", mon, 10, 100000, 1000000),
            Bar("688001", "day", mon, 7.2, 100000, 1000000),        // 前复权
            Bar("688001", "day_hfq", mon, 14.5, 100000, 1000000),   // 后复权
            Bar("688001", "day_adj", mon, 15.1, 100000, 1000000),
            Bar("688001", "week", mon, 7.2, 100000, 1000000),
        ]);

        new SqliteBarVolumeUnitFixer(_dbPath).FixAsync();

        var raw = VolumeOf("688001", "day_raw");
        Assert.Equal(1000, raw, 2);
        foreach (var gran in new[] { "day", "day_hfq", "day_adj", "week" })
            Assert.Equal(raw, VolumeOf("688001", gran), 2);
    }

    /// <summary>
    /// 指数、板块合成、ETF 一律不动：它们的"成交量"是汇总值或按份计，量额比没有物理意义
    /// （实测上证指数 7890 行落在两个区间之外、沪深 ETF 各有 10%~13% 在区间外）。
    /// 这几行的比值刻意造成 ≈1，就是要证明**光看比值不够、还得看是什么标的**。
    /// </summary>
    [Fact]
    public void LeavesIndexesBoardsAndEtfsAlone()
    {
        var d = new DateTime(2026, 9, 9);
        _repo.InsertOrRefreshUnconfirmed([
            Bar("sh000001", "day", d, 3940.55, 1000, 3940550),   // 指数，比值 ≈1
            Bar("BK0475", "day", d, 100, 1000, 100000),          // 板块合成，比值 ≈1
            Bar("510300", "day", d, 4.5, 1000, 4500),            // 沪 ETF，比值 ≈1
            Bar("159901", "day", d, 4.5, 1000, 4500),            // 深 ETF，比值 ≈1
        ]);

        var n = new SqliteBarVolumeUnitFixer(_dbPath).FixAsync();

        Assert.Equal(0, n["day"]);
        Assert.Equal(1000, VolumeOf("sh000001", "day"));
        Assert.Equal(1000, VolumeOf("BK0475", "day"));
        Assert.Equal(1000, VolumeOf("510300", "day"));
        Assert.Equal(1000, VolumeOf("159901", "day"));
    }

    /// <summary>Preview 跟 Fix 用同一个判据，报出来的数就该是实际会改的数。</summary>
    [Fact]
    public void PreviewMatchesWhatFixActuallyChanges()
    {
        var d = new DateTime(2026, 9, 9);
        _repo.InsertOrRefreshUnconfirmed([
            Bar("688001", "day_raw", d, 66.31, 19050931, 1244048584),
            Bar("688002", "day_raw", d, 10, 1000000, 10000000),
            Bar("600000", "day_raw", d, 9.23, 505325, 467549947),
        ]);

        var fixer = new SqliteBarVolumeUnitFixer(_dbPath);
        var preview = fixer.Preview();
        var fixedRows = fixer.FixAsync();

        Assert.Equal(2, preview["day_raw"]);
        Assert.Equal(preview["day_raw"], fixedRows["day_raw"]);
    }
}
