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
/// 【资金净流入】任务的编排（2026-09-18 从编排器迁到新框架，见 doc/netinflow-task-design.md）。
///
/// 这一组盯的是搬家时最容易丢的几条：
/// ① **当天那一行要重抓**——盘中抓到的是不完整的，判成"已有"就永久固化了；
/// ② **失败名单是"这轮碰过、这次没失败的移出"**，不是清空重写；
/// ③ 【只补待办】里整天缺失和残缺日合并成一轮抓，但**复查判据各走各的**。
/// </summary>
public class NetInflowTaskTests : IDisposable
{
    private readonly string _dir;
    private readonly FetchPaths _paths;
    private readonly SqliteNetInflowRepository _repo;
    private readonly JsonManifestStore _manifest;

    private static readonly DateTime Today = DateTime.Today;
    private static readonly string[] Codes = ["600000", "600036", "000001", "000002"];

    public NetInflowTaskTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"niTask_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        _repo = new SqliteNetInflowRepository(_paths.CurrentDb);
        _repo.EnsureSchema();
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, Codes.Select(c => (c, $"票{c}")));
        _manifest = new JsonManifestStore(_paths.ManifestPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录 */ }
    }

    // ── 假数据源 ──────────────────────────────────────────────────

    private sealed class FakeFetcher : INetInflowFetcher
    {
        public event Action<string>? OnStatus;
        public readonly List<(string Code, DateTime Start, DateTime End)> Asked = [];
        public HashSet<string> Throws = [];
        public DateOnly EarliestAvailable => new(2010, 3, 1);

        public Task<List<NetInflow>> FetchAsync(
            string code, DateTime? start, DateTime? end, CancellationToken ct = default)
        {
            OnStatus?.Invoke("");
            lock (Asked) Asked.Add((code, start ?? DateTime.MinValue, end ?? DateTime.MaxValue));
            if (Throws.Contains(code)) throw new HttpRequestException("连不上");

            var rows = new List<NetInflow>();
            for (var d = (start ?? Today).Date; d <= (end ?? Today).Date; d = d.AddDays(1))
            {
                if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                rows.Add(new NetInflow
                {
                    Code = code, PeriodStart = d, MainNetInflow = 1_111_111,
                    FetchedAt = d.AddHours(20),
                });
            }
            return Task.FromResult(rows);
        }
    }

    private NetInflowTask NewTask(FakeFetcher f, int batchSize = 2) =>
        new(f, _manifest, _paths, batchSize);

    private int RowsOf(string code) => _repo.Query(code).Count;

    private List<string> FailedTodo() =>
        (_manifest.Load().Todo(RetryTaskIds.NetInflow, RetryTodoKind.Failed)?.Targets ?? [])
            .Select(t => t.Code).OrderBy(c => c, StringComparer.Ordinal).ToList();

    // ── 排期 ──────────────────────────────────────────────────────

    [Fact]
    public async Task 增量_没抓过的票回看六十天()
    {
        var f = new FakeFetcher();
        var r = await NewTask(f).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.Equal(Codes.Length, f.Asked.Count);
        Assert.All(f.Asked, a => Assert.Equal(Today.AddDays(-NetInflowTask.InitialLookbackDays), a.Start));
    }

    /// <summary>第二轮从水位线的下一天续抓，不重抓已有的。</summary>
    [Fact]
    public async Task 增量_第二轮从水位线续抓()
    {
        await NewTask(new FakeFetcher()).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);
        int before = RowsOf("600000");

        var f = new FakeFetcher();
        await NewTask(f).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        // 水位线就是今天：今天那行是 20:00 抓的（收盘后），所以整只跳过、一个请求都不发
        Assert.Empty(f.Asked);
        Assert.Equal(before, RowsOf("600000"));
    }

    /// <summary>
    /// **这一组最要紧的一条**：当天那行如果是**盘中**抓的，下一轮必须重抓。
    /// 判成"已有"的话，盘中那份不完整的数据就被永久固化了。
    /// </summary>
    [Fact]
    public async Task 当天那行是盘中抓的_下轮要重抓()
    {
        // 手工塞一行"今天 11:00 抓的"——盘中
        _repo.Upsert([new NetInflow
        {
            Code = "600000", PeriodStart = Today, MainNetInflow = 1,
            FetchedAt = Today.AddHours(11),
        }]);

        var f = new FakeFetcher();
        await NewTask(f).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        var asked = f.Asked.Single(a => a.Code == "600000");
        Assert.Equal(Today, asked.Start);      // ← 重抓了今天，不是跳过
    }

    [Fact]
    public async Task 只抓某一天_精确抓那天()
    {
        var day = Today.AddDays(-10);
        var f = new FakeFetcher();
        await NewTask(f).RunAsync(
            new TaskRunArgs(FetchMode.SpecificDay, Day: DateOnly.FromDateTime(day)), CancellationToken.None);

        Assert.All(f.Asked, a => { Assert.Equal(day, a.Start); Assert.Equal(day, a.End); });
    }

    /// <summary>1.75 小时的活要能分批跑——这正是迁移带来的。</summary>
    [Fact]
    public async Task MaxItems_只跑那么多批()
    {
        var f = new FakeFetcher();
        await NewTask(f, batchSize: 2).RunAsync(
            new TaskRunArgs(FetchMode.Incremental, MaxItems: 1), CancellationToken.None);

        Assert.Equal(2, f.Asked.Count);        // 一批 2 只，只跑 1 批
    }

    // ── 失败名单 ──────────────────────────────────────────────────

    [Fact]
    public async Task 失败的票进名单()
    {
        var f = new FakeFetcher { Throws = ["600036"] };
        await NewTask(f).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        Assert.Equal(["600036"], FailedTodo());
    }

    /// <summary>
    /// **这轮碰过、这次没失败的移出名单**——不是"清空重写"，也不是"只加不减"。
    /// 没被这轮碰到的代码要原样留着。
    /// </summary>
    [Fact]
    public async Task 名单语义_碰过且这次成功的移出_没碰的留着()
    {
        var m = _manifest.Load();
        m.SetTodo(RetryTaskIds.NetInflow, RetryTodoKind.Failed,
                  [new RetryTarget { Code = "600000" }, new RetryTarget { Code = "999999" }]);
        _manifest.Save(m);

        await NewTask(new FakeFetcher()).RunAsync(
            new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        // 600000 这轮碰过且成功 → 移出；999999 不在名册里、这轮没碰 → 原样留着
        Assert.Equal(["999999"], FailedTodo());
    }

    [Fact]
    public async Task 整轮都失败_判失败()
    {
        var f = new FakeFetcher { Throws = [.. Codes] };
        var r = await NewTask(f).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        Assert.Equal(TaskState.Failed, r.State);
    }

    // ── 只补待办 ──────────────────────────────────────────────────

    [Fact]
    public async Task 只补待办_没有待办就跳过()
    {
        var f = new FakeFetcher();
        var r = await NewTask(f).RunAsync(new TaskRunArgs(FetchMode.FillBacklog), CancellationToken.None);

        Assert.Empty(f.Asked);
        Assert.NotNull(r.SkippedReason);
    }

    /// <summary>失败名单那一类：只抓名单里的票，不是全市场。</summary>
    [Fact]
    public async Task 只补待办_失败名单只抓名单里的()
    {
        var m = _manifest.Load();
        m.SetTodo(RetryTaskIds.NetInflow, RetryTodoKind.Failed, [new RetryTarget { Code = "600036" }]);
        _manifest.Save(m);

        var f = new FakeFetcher();
        await NewTask(f).RunAsync(new TaskRunArgs(FetchMode.FillBacklog), CancellationToken.None);

        Assert.Equal(["600036"], f.Asked.Select(a => a.Code).Distinct().ToList());
        Assert.Empty(FailedTodo());            // 这次成功了，移出名单
    }

    /// <summary>
    /// 整天缺失那一类：**全市场**逐只跑一轮，只留待办里那几天。
    /// 补上了就把待办划掉（这一类的复查判据是"那天现在有没有行"）。
    /// </summary>
    [Fact]
    public async Task 只补待办_整天缺失走全市场一轮()
    {
        var day = Today.AddDays(-7);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(-1);
        var m = _manifest.Load();
        m.SetTodo(RetryTaskIds.NetInflow, RetryTodoKind.MissingDays, [new RetryTarget { Day = day }]);
        _manifest.Save(m);

        var f = new FakeFetcher();
        await NewTask(f).RunAsync(new TaskRunArgs(FetchMode.FillBacklog), CancellationToken.None);

        Assert.Equal(Codes.Length, f.Asked.Select(a => a.Code).Distinct().Count());   // 全市场
        Assert.True(_repo.CountRowsByDay([day])[day] > 0);
        Assert.Empty(_manifest.Load().Todo(RetryTaskIds.NetInflow, RetryTodoKind.MissingDays)?.Targets ?? []);
    }
}
