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
/// 【退市股收尾】的「整段缺失」那一类（2026-10-16）：本地一根K线都没有的退市股三个口径从头补齐。
///
/// 起因：【补全退市名单】从巨潮/档案补进来的 19 只（688287 观典防务等）没有终止日、
/// 也不在两所名单里，补尾巴和整段回补都走不到，一根K线都没有。
///
/// 真 SQLite + 离线模拟源（<see cref="MockBarFetcher"/>）：一个请求都不发，走的是产品代码的落库路径。
/// </summary>
public class DelistedTailTaskTests : IDisposable
{
    private readonly string _tmp;
    private readonly FetchPaths _paths;
    private readonly SqliteBarRepository _bars;
    private readonly IManifestStore _manifest;

    public DelistedTailTaskTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"delistedTail_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
        _paths = new FetchPaths(_tmp);
        _bars = new SqliteBarRepository(_paths.CurrentDb);
        _bars.EnsureSchema();
        _manifest = new JsonManifestStore(_paths.ManifestPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmp, recursive: true); } catch { /* 临时目录 */ }
    }

    /// <summary>两所名单：这批票本来就不在里面，给空的。</summary>
    private sealed class EmptyExchangeList : IDelistedListProvider
    {
        public event Action<string>? OnStatus { add { } remove { } }
        public Task<List<DelistedStockRow>> GetAllAsync(CancellationToken ct = default) => Task.FromResult(new List<DelistedStockRow>());
    }

    /// <summary>像【补全退市名单】那样写进退市名单：没有终止日。</summary>
    private void SupplementedDelisted(string code)
    {
        new SqliteDelistedRepository(_paths.CurrentDb).Upsert(
            [new DelistedStockRow { Code = code, Name = "退市" + code, Exchange = "sse" }]);
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, [(code, "退市" + code)], SqliteStockMetaUpsert.TypeDelisted);
    }

    private async Task<(TaskRunResult Result, List<string> Log)> RunAsync()
    {
        var source = new NamedBarSource("Mock", new MockBarFetcher(), new MockStockListProvider(() => []));
        var task = new DelistedTailTask(_paths, new BarSourceHolder(source), _manifest, new EmptyExchangeList());
        var log = new List<string>();
        task.OnProgress += p => log.Add(p.Text);
        var result = await task.RunAsync(new TaskRunArgs(Mode: FetchMode.Incremental), CancellationToken.None);
        return (result, log);
    }

    /// <summary>⭐ 三个口径都从 A股开市首日补到今天，写进真库；补完打已尝试标记。</summary>
    [Fact]
    public async Task 整段缺失_三个口径都从头补齐()
    {
        SupplementedDelisted("688287");

        var (result, log) = await RunAsync();

        Assert.Equal(TaskState.Completed, result.State);
        foreach (var gran in new[] { Granularity.Day, Granularity.DayHfq, Granularity.DayRaw })
        {
            var stored = _bars.Query("688287", gran);
            Assert.NotEmpty(stored);
            // 模拟源按请求窗口逐个工作日造数据：首根就是窗口起点那个工作日
            Assert.True(stored.Min(b => b.PeriodStart) < new DateTime(1991, 1, 1), $"{gran} 没从开市首日起");
            Assert.Equal(DateTime.Today, stored.Max(b => b.PeriodStart).Date);
        }
        Assert.Empty(new SqliteDelistedRepository(_paths.CurrentDb).GetTailPendingCodes());
        Assert.Contains(log, l => l.Contains("1 只本地一根K线都没有"));
    }

    /// <summary>每只只抓一次：第二轮没有活可干，一个请求都不发。</summary>
    [Fact]
    public async Task 整段缺失_补过一次就不再碰()
    {
        SupplementedDelisted("688287");
        await RunAsync();

        var (result, _) = await RunAsync();

        Assert.True(result.NothingToDo);
    }

    /// <summary>抓失败的不打标记，下轮还会被挑出来（代码含 999 的模拟源一律抛错）。</summary>
    [Fact]
    public async Task 整段缺失_失败的下轮重试()
    {
        SupplementedDelisted("600999");

        await RunAsync();

        Assert.Contains("600999", new SqliteDelistedRepository(_paths.CurrentDb).GetTailPendingCodes());
    }
}
