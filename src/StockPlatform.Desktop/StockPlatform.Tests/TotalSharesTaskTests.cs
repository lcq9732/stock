using Microsoft.Data.Sqlite;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【拉取总股本】任务（2026-09-14）。
///
/// ════ 为什么这一项值得一组测试 ════
/// 它修的是个**静默**的错：PE/PB 一直拿财报 <c>share_capital</c>（实收资本，金额）当股数用，
/// 只有面值 1.00 元才碰巧对。错的时候没有任何迹象——中国移动算出 PE 348.8（真值 16.1）、
/// 分众传媒 0.5（真值 20.1），都是格式正常的数字。
///
/// 所以护栏比数据本身更要紧：这份数据缺了会**静默回退**到那个错值，缺得越安静越危险。
/// 两道护栏各守一类缺法：
///   ① 半截名单——接口改版/翻页断在半路，少一大片；
///   ② 北交所整块丢——只占 6%，进不了 5% 的门槛，总数护栏根本挡不住。
///      这一条是照着真事写的：provider 自写前缀规则漏掉 920，342 只票静默抓不到、一个错都不报。
/// </summary>
public class TotalSharesTaskTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"totShares_{Guid.NewGuid():N}");
    private readonly FetchPaths _paths;
    private readonly SqliteFundamentalMetricRepository _repo;
    private readonly SqliteTradingDayRepository _days;

    public TotalSharesTaskTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        _repo = new SqliteFundamentalMetricRepository(_paths.CurrentDb);
        _repo.EnsureSchema();
        _days = new SqliteTradingDayRepository(_paths.CurrentDb);
        _days.EnsureSchema();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ───────────────────────── 假数据源 ─────────────────────────

    private sealed class FakeProvider(IEnumerable<(string Code, double Shares)> rows, DateTime? tradeDate)
        : ITotalSharesProvider
    {
        public string SourceName => "Fake";
        public event Action<string>? OnStatus { add { } remove { } }

        public Task<TotalSharesSnapshot> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult(new TotalSharesSnapshot(
                rows.Select(r => new TotalSharesEntry(r.Code, r.Shares)).ToList(), tradeDate));
    }

    /// <summary>往本地名册里塞 n 只沪市个股 + bjs 只北交所（920 开头）。</summary>
    private void SeedRoster(int n, int bjs = 0)
    {
        var list = new List<(string, string)>();
        for (int i = 0; i < n; i++) list.Add(($"60{i:D4}", $"票{i}"));
        for (int i = 0; i < bjs; i++) list.Add(($"92{i:D4}", $"北票{i}"));
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, list);
    }

    private async Task<TaskRunResult> RunAsync(
        IEnumerable<(string Code, double Shares)> rows, DateTime? tradeDate = null)
    {
        var task = new TotalSharesTask(_paths, _repo, new FakeProvider(rows, tradeDate), _days);
        return await task.RunAsync(new TaskRunArgs(), CancellationToken.None);
    }

    private static List<(string, double)> Rows(int n, int bjs = 0, double shares = 1e9)
    {
        var list = new List<(string, double)>();
        for (int i = 0; i < n; i++) list.Add(($"60{i:D4}", shares));
        for (int i = 0; i < bjs; i++) list.Add(($"92{i:D4}", shares));
        return list;
    }

    // ───────────────────────── 测试 ─────────────────────────

    [Fact]
    public async Task 全市场落库_单位是股()
    {
        SeedRoster(100);
        var day = new DateTime(2026, 9, 11);
        _days.Upsert([(DateOnly.FromDateTime(day), ITradingDayRepository.SzseSource)]);

        var result = await RunAsync(Rows(100, shares: 26_591_000_000d), day);

        Assert.Equal(TaskState.Completed, result.State);
        var row = Assert.Single(_repo.Query("600000", MetricKeys.TotalShares));
        // 265.91 亿股（紫金矿业量级）——存的是股数，不是"亿股"也不是金额。
        Assert.Equal(26_591_000_000d, row.Value);
        Assert.Equal(day, row.AsOfDate);
    }

    [Fact]
    public async Task 半截名单不写库()
    {
        // 接口改版/翻页断在半路时的样子。少的那一片会静默回退到报表实收资本——
        // 宁可这一轮什么都不写，保留上一版。
        SeedRoster(100);
        await RunAsync(Rows(100));                 // 先有完整的一版
        int before = _repo.Query("600050", MetricKeys.TotalShares).Count;

        var result = await RunAsync(Rows(80));     // 只回来 80%

        Assert.Equal(TaskState.Failed, result.State);
        Assert.Contains(result.Errors, e => e.Contains("半截名单"));
        Assert.Equal(before, _repo.Query("600050", MetricKeys.TotalShares).Count);
    }

    [Fact]
    public async Task 北交所整块丢要单独挡住()
    {
        // 总数护栏挡不住这一类：这里北交所只占 2.9%，全丢了也进不了 5% 的门槛。
        // 真事——provider 自写前缀规则漏掉 920，342 只票静默抓不到、一个错都不报。
        SeedRoster(100, bjs: 3);

        var result = await RunAsync(Rows(100, bjs: 0));   // 沪市齐全，北交所一只没有

        Assert.Equal(TaskState.Failed, result.State);
        Assert.Contains(result.Errors, e => e.Contains("北交所"));
        Assert.Empty(_repo.Query("600000", MetricKeys.TotalShares));
    }

    [Fact]
    public async Task 北交所整块丢时报的是市场范围不是半截名单()
    {
        // 真实占比 6.2%，整块丢会把两条护栏一起触发。先判具体的那条，
        // 否则用户看到的是"疑似半截名单"——方向完全指错。
        SeedRoster(100, bjs: 7);

        var result = await RunAsync(Rows(100, bjs: 0));

        Assert.Equal(TaskState.Failed, result.State);
        Assert.Contains(result.Errors, e => e.Contains("北交所"));
        Assert.DoesNotContain(result.Errors, e => e.Contains("半截名单"));
    }

    [Fact]
    public async Task 北交所在内就正常落库()
    {
        SeedRoster(100, bjs: 6);

        var result = await RunAsync(Rows(100, bjs: 6));

        Assert.Equal(TaskState.Completed, result.State);
        Assert.Single(_repo.Query("920000", MetricKeys.TotalShares));
    }

    [Fact]
    public async Task 空库首次抓取要放行()
    {
        // 护栏只在库里本来就有名册时才判，否则新库第一次跑永远过不去。
        var result = await RunAsync(Rows(50));

        Assert.Equal(TaskState.Completed, result.State);
        Assert.Single(_repo.Query("600000", MetricKeys.TotalShares));
    }

    [Fact]
    public async Task 一行都没拿到不写库()
    {
        SeedRoster(100);

        var result = await RunAsync([]);

        Assert.Equal(TaskState.Failed, result.State);
        Assert.Empty(_repo.Query("600000", MetricKeys.TotalShares));
    }

    [Fact]
    public async Task 接口自报的日期不是交易日就按日历归位()
    {
        // 记的是"值所属交易日"，不是"哪天跑的"——流通市值那一列正是在这里踩过坑
        // （早期一律写 DateTime.Today，周末跑到的值被记到周末名下，事后要按交易日归位）。
        SeedRoster(10);
        var friday = new DateTime(2026, 9, 11);
        var sunday = new DateTime(2026, 9, 13);
        _days.Upsert([(DateOnly.FromDateTime(friday), ITradingDayRepository.SzseSource)]);

        var result = await RunAsync(Rows(10), sunday);

        Assert.Equal(TaskState.Completed, result.State);
        Assert.Equal(friday, _repo.Query("600000", MetricKeys.TotalShares).Single().AsOfDate);
    }

    [Fact]
    public async Task 没有日历时采用接口自报的日期()
    {
        // 日历没跑过就不该硬把数据往回挪——直接用接口说的，并在日志里说明。
        SeedRoster(10);
        var day = new DateTime(2026, 9, 11);

        await RunAsync(Rows(10), day);

        Assert.Equal(day, _repo.Query("600000", MetricKeys.TotalShares).Single().AsOfDate);
    }

    [Fact]
    public async Task 同一天再抓一次是覆盖不是重复()
    {
        SeedRoster(10);
        var day = new DateTime(2026, 9, 11);

        await RunAsync(Rows(10, shares: 1e9), day);
        await RunAsync(Rows(10, shares: 2e9), day);

        var row = Assert.Single(_repo.Query("600000", MetricKeys.TotalShares));
        Assert.Equal(2e9, row.Value);
    }

    [Fact]
    public void 归日更_排在名册之后()
    {
        // 日更的理由：股本不常动，但一动就直接改 PE/PB，而这一项一两秒就完。
        // 排在名册之后的理由：覆盖护栏拿库里在市名册当基准，名册先刷新护栏才不会误报。
        Assert.Equal(PlanGroupKind.Daily, FetchTaskCatalog.DefaultGroupOf(FetchActionId.FetchTotalShares));

        var order = FetchTaskCatalog.DailyOrder;
        Assert.Contains(FetchActionId.FetchTotalShares, order);
        Assert.True(order.ToList().IndexOf(FetchActionId.StepRoster)
                    < order.ToList().IndexOf(FetchActionId.FetchTotalShares));
    }
}
