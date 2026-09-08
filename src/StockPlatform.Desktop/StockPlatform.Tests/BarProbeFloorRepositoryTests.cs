using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// BarProbeFloor 表的存取（"数据源没有更早数据"的水位，2026-09-07）。
///
/// 判定逻辑在 ProbeFloorPlannerTests 那边；这里盯的是 SQL 本身，两条最容易写错的：
/// <list type="bullet">
/// <item><b>只抬不降</b>——ON CONFLICT 里那个 MAX 是标量两参数版，不是聚合版。写错的话，
/// 一次窄区间回补（比如只补 2016 那一年）就会把已有的更高水位改矮，白省的请求又都回来了。</item>
/// <item><b>按粒度分行</b>——三条线的水位各自独立，混了会让 day_raw 借用 day 的结论。</item>
/// </list>
/// </summary>
public class BarProbeFloorRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteBarProbeFloorRepository _repo;

    public BarProbeFloorRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"probefloor_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteBarProbeFloorRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    [Fact]
    public void RoundTripsOneFloor()
    {
        _repo.Record([("300750", new DateTime(2017, 1, 1))], Granularity.Day);

        var all = _repo.GetAll(Granularity.Day);
        Assert.Equal(new DateTime(2017, 1, 1), all["300750"]);
        Assert.Equal(1, _repo.Count());
    }

    [Fact]
    public void RaisesFloorButNeverLowersIt()
    {
        _repo.Record([("300750", new DateTime(2017, 1, 1))], Granularity.Day);

        // 更晚的水位 → 抬上去
        _repo.Record([("300750", new DateTime(2020, 6, 1))], Granularity.Day);
        Assert.Equal(new DateTime(2020, 6, 1), _repo.GetAll(Granularity.Day)["300750"]);

        // 更早的水位（窄区间那一轮探到的）→ 不许把已有结论改矮
        _repo.Record([("300750", new DateTime(2005, 1, 1))], Granularity.Day);
        Assert.Equal(new DateTime(2020, 6, 1), _repo.GetAll(Granularity.Day)["300750"]);

        Assert.Equal(1, _repo.Count());   // 始终一行，没有写成多行
    }

    [Fact]
    public void KeepsGranularitiesApart()
    {
        _repo.Record([("300750", new DateTime(2017, 1, 1))], Granularity.Day);
        _repo.Record([("300750", new DateTime(2019, 1, 1))], Granularity.DayHfq);

        Assert.Equal(new DateTime(2017, 1, 1), _repo.GetAll(Granularity.Day)["300750"]);
        Assert.Equal(new DateTime(2019, 1, 1), _repo.GetAll(Granularity.DayHfq)["300750"]);
        Assert.Empty(_repo.GetAll(Granularity.DayRaw));
        Assert.Equal(2, _repo.Count());
    }

    [Fact]
    public void RecordsManyInOneTransaction()
    {
        var rows = Enumerable.Range(0, 500)
            .Select(i => ($"{600000 + i}", new DateTime(2017, 1, 1))).ToList();

        Assert.Equal(500, _repo.Record(rows, Granularity.Day));
        Assert.Equal(500, _repo.GetAll(Granularity.Day).Count);
    }

    [Fact]
    public void ClearWipesEverything()
    {
        _repo.Record([("300750", new DateTime(2017, 1, 1))], Granularity.Day);
        _repo.Record([("300750", new DateTime(2019, 1, 1))], Granularity.DayHfq);

        _repo.Clear();

        Assert.Equal(0, _repo.Count());
        Assert.Empty(_repo.GetAll(Granularity.Day));
    }

    [Fact]
    public void EmptyBatchIsANoOp()
    {
        Assert.Equal(0, _repo.Record([], Granularity.Day));
        Assert.Equal(0, _repo.Count());
    }

    [Fact]
    public void GetAllOnAnUntouchedDbReturnsEmpty()
    {
        // 老库升级上来时这张表还不存在 → 必须当作"一条水位都没有"，行为回到改造前，不能抛
        var fresh = Path.Combine(Path.GetTempPath(), $"probefloor_none_{Guid.NewGuid():N}.sqlite");
        try
        {
            var repo = new SqliteBarProbeFloorRepository(fresh);
            Assert.Empty(repo.GetAll(Granularity.Day));
            Assert.Equal(0, repo.Count());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(fresh)) File.Delete(fresh); } catch { /* 临时文件 */ }
        }
    }
}
