using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【指数日K】的「首次整段回补」模式（2026-09-10 加）。
///
/// 病根：水位线增量**只往后走**。往 <see cref="MarketIndexCatalog"/> 里加一条新指数后，
/// 它第一次被日更抓到的只有回看年数那几年（默认 3），之后水位线就钉在最新一根上，
/// 前面的历史再也补不回来——2026-09-09 加深证综指等三条时踩到过：各只抓了 726 行
/// （正好 3 年），而龙虎榜的偏离值要拿深证综指当基准回溯到 2004。
///
/// 这里守的是两件事：目录里真的声明了这个模式（否则界面上根本选不到），
/// 以及那个"本地最早一根"的查询（换源重抓的防护靠它判断指数够不够早）。
/// </summary>
public class IndexBarsBackfillTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteBarRepository _repo;

    public IndexBarsBackfillTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"idxbf_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteBarRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!,
                                             Path.GetFileNameWithoutExtension(_dbPath) + "*"))
            try { File.Delete(f); } catch { /* 临时文件 */ }
    }

    /// <summary>目录里没声明的话，界面上那个下拉框里就没有这一项，功能等于不存在。</summary>
    [Fact]
    public void 指数日K声明了首次整段回补模式()
    {
        var info = FetchTaskCatalog.All.Single(x => x.Id == FetchActionId.StepIndexBars);
        Assert.True(info.SupportedModes.HasFlag(FetchMode.FirstBackfill));
        Assert.True(info.SupportedModes.HasFlag(FetchMode.Incremental));   // 日常那条不能丢
    }

    /// <summary>
    /// 龙虎榜换源重抓的防护靠这个查询判断"基准指数够不够早"。
    /// ⚠ 判据必须是**最早**一根，不是"有没有数据"——2026-09-09 那次就是只判非空，
    /// 结果三条只有 3 年历史的指数照样放行。
    /// </summary>
    [Fact]
    public void 能查出单条指数本地最早的那一根()
    {
        _repo.InsertOrRefreshUnconfirmed([
            Bar("sz399106", new DateTime(2023, 9, 11)),
            Bar("sz399106", new DateTime(2026, 9, 9)),
            Bar("sh000001", new DateTime(1990, 12, 19)),
        ]);

        Assert.Equal(new DateTime(2023, 9, 11), _repo.GetEarliestPeriodStart("sz399106", Granularity.Day));
        Assert.Equal(new DateTime(1990, 12, 19), _repo.GetEarliestPeriodStart("sh000001", Granularity.Day));
    }

    /// <summary>没抓过的返回 null，别让调用方拿 default(DateTime)（0001-01-01）当成"早得很"放行。</summary>
    [Fact]
    public void 没抓过的指数返回null()
    {
        Assert.Null(_repo.GetEarliestPeriodStart("bj899050", Granularity.Day));
    }

    /// <summary>按粒度分开算——指数只有 day，别让别的口径串进来。</summary>
    [Fact]
    public void 按粒度分别统计()
    {
        _repo.InsertOrRefreshUnconfirmed([
            Bar("sz399106", new DateTime(2023, 9, 11)),
            Bar("sz399106", new DateTime(2010, 1, 4), Granularity.Week),
        ]);

        Assert.Equal(new DateTime(2023, 9, 11), _repo.GetEarliestPeriodStart("sz399106", Granularity.Day));
        Assert.Equal(new DateTime(2010, 1, 4), _repo.GetEarliestPeriodStart("sz399106", Granularity.Week));
    }

    /// <summary>算龙虎榜偏离值要用的那几条基准指数，必须都在抓取清单里——
    /// 少一条，对应板块的对应值就永远是空的，而且不会报错。</summary>
    [Fact]
    public void 偏离值基准指数都在抓取清单里()
    {
        var fetched = MarketIndexCatalog.All.Select(i => i.Symbol).ToHashSet();
        foreach (var code in new[] { "600000", "000001", "300750", "688001", "920371" })
        {
            var benchmark = MarketIndexCatalog.DeviationBenchmarkFor(code);
            Assert.NotNull(benchmark);
            Assert.Contains(benchmark!, fetched);
        }
    }

    private static Bar Bar(string code, DateTime day, string gran = Granularity.Day) =>
        new()
        {
            Code = code, Granularity = gran, PeriodStart = day,
            Open = 1, Close = 1, High = 1, Low = 1, Volume = 1, Amount = 1,
            FetchedAt = day.AddHours(20),
        };
}
