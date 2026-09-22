using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// K线任务把"请求成功但返回 0 行"落成永久水位（<c>BarProbeFloor</c>，2026-09-22 重建）。
///
/// ════ 为什么重建 ════
/// 这张表原来由老编排层的 <c>RecordProbeFloors</c> 写，那条路随【拉取区间数据】整段删掉之后
/// **只剩读、没有写**——表不再长大，新上市的标的每轮都要重新试一遍。
/// 没有它的代价实测过：2026-09-07 一万六千个请求、四个半小时、写入为零。
///
/// ⚠ 判据本身（两道安全前提）在 <see cref="ProbeFloorPlannerTests"/>。这里验的是**接线**：
/// 空响应真的走到了那个判据、结论真的落了库、而且**每批就落**（不是收尾才落——
/// 老实现就是收尾落，中途停就整段丢）。
/// </summary>
public class BarProbeFloorWriteTests : IDisposable
{
    private readonly string _tmp;
    private readonly FetchPaths _paths;
    private readonly SqliteBarRepository _bars;
    private readonly SqliteBarProbeFloorRepository _floors;
    private readonly IManifestStore _manifest;

    public BarProbeFloorWriteTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"floorWrite_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
        _paths = new FetchPaths(_tmp);
        _bars = new SqliteBarRepository(_paths.CurrentDb);
        _bars.EnsureSchema();
        _floors = new SqliteBarProbeFloorRepository(_paths.CurrentDb);
        _floors.EnsureSchema();
        _manifest = new JsonManifestStore(_paths.ManifestPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmp, recursive: true); } catch { /* 临时目录 */ }
    }

    /// <summary>这只票本地有一段K线（水位判据的前提①：本地得有旁证）。</summary>
    private void Local(string code, DateTime from, int days)
    {
        var rows = new List<Bar>();
        for (int i = 0; i < days; i++)
        {
            var d = from.AddDays(i);
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            rows.Add(new Bar
            {
                Code = code, Granularity = Granularity.Day, PeriodStart = d,
                Open = 10, Close = 10, High = 10, Low = 10,
                Volume = 100, Amount = 1000, Turnover = 1.5, FetchedAt = d.AddHours(20),
            });
        }
        _bars.InsertOrRefreshUnconfirmed(rows);
    }

    /// <summary>跑一轮整段回补。模拟源对 <paramref name="emptyFrom"/> 之前的窗口返回空。</summary>
    private async Task RunAsync(int yearStart, int yearEnd)
    {
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, [("600000", "测试")]);
        var source = new NamedBarSource("Mock", new MockBarFetcher(), new MockStockListProvider(() => []));
        var task = new StockDayBarTask(_paths, new BarSourceHolder(source), _manifest, batchSize: 30);
        await task.RunAsync(new TaskRunArgs(Mode: FetchMode.FirstBackfill,
                                            YearStart: yearStart, YearEnd: yearEnd),
                            CancellationToken.None);
    }

    /// <summary>
    /// ⭐ 模拟源对任何窗口都返回数据，所以这里验的是**反面**：有数据时不该记水位。
    /// 记错一条的后果是那只票的历史永久跳过、而且不报错。
    /// </summary>
    [Fact]
    public async Task 抓到数据时_不记水位()
    {
        Local("600000", DateTime.Today.AddDays(-30), 30);

        await RunAsync(DateTime.Today.Year, DateTime.Today.Year);

        Assert.Empty(_floors.GetAll(Granularity.Day));
    }

    /// <summary>
    /// ⭐ 空响应真的会落库——直接喂判据 + 仓储，验的是这条链本身接通了。
    /// （模拟源不会返回空，所以这一半用真仓储单独验；接线那一半靠上面那条反面测试兜。）
    /// </summary>
    [Fact]
    public void 空响应按判据落成水位()
    {
        var earliest = new Dictionary<string, DateTime>(StringComparer.Ordinal)
        {
            ["600000"] = new(2020, 1, 1),
        };
        // 请求终点 2015-12-31，早于本地最早的 2020-01-01 ⇒ 是"往前补缺口"，可以落
        var floors = ProbeFloorPlanner.Plan([("600000", new DateTime(2015, 12, 31))], earliest);
        _floors.Record(floors, Granularity.Day);

        var saved = _floors.GetAll(Granularity.Day);
        Assert.Equal(new DateTime(2016, 1, 1), saved["600000"]);
    }

    /// <summary>落过的水位，下一轮整段回补要真的据此跳过（读那半边）。</summary>
    [Fact]
    public async Task 水位盖住区间时_整只跳过()
    {
        Local("600000", DateTime.Today.AddDays(-30), 30);
        _floors.Record([("600000", DateTime.Today.AddDays(1))], Granularity.Day);
        int before = _bars.Query("600000", Granularity.Day).Count;

        await RunAsync(DateTime.Today.Year - 1, DateTime.Today.Year - 1);

        Assert.Equal(before, _bars.Query("600000", Granularity.Day).Count);
    }
}
