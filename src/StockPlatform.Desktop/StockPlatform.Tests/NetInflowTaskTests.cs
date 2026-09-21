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

        /// <summary>每只票磨这么久——给"一批很快、整轮很慢"那类看门狗场景用。</summary>
        public TimeSpan Delay;

        public async Task<List<NetInflow>> FetchAsync(
            string code, DateTime? start, DateTime? end, CancellationToken ct = default)
        {
            OnStatus?.Invoke("");
            lock (Asked) Asked.Add((code, start ?? DateTime.MinValue, end ?? DateTime.MaxValue));
            if (Throws.Contains(code)) throw new HttpRequestException("连不上");
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);

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
            return rows;
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

    /// <summary>
    /// 水位线早于今天 ⇒ 从**水位线的下一天**续抓，已有的那几天不重抓。
    ///
    /// ⚠ 前提是**直接塞进库的**，不靠"先跑一轮"来造（2026-09-19 改）：假源跟真源一样不给
    ///   周末的行（A股周末没行情），所以周末跑的时候"先跑一轮"根本抓不到今天那一行，
    ///   水位线会停在上周五——旧写法把这个当成"水位线就是今天"来断言，于是**每个周末红一次**。
    ///   这两条（续抓 / 跳过）现在都只依赖库里的行，跟今天是周几无关。
    /// </summary>
    [Fact]
    public async Task 增量_第二轮从水位线的下一天续抓()
    {
        var mark = Today.AddDays(-3);
        _repo.Upsert([new NetInflow
        {
            Code = "600000", PeriodStart = mark, MainNetInflow = 1, FetchedAt = mark.AddHours(20),
        }]);

        var f = new FakeFetcher();
        await NewTask(f).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        Assert.Equal(mark.AddDays(1), f.Asked.Single(a => a.Code == "600000").Start);
        // 从没抓过的那几只仍然是回看 60 天——水位线是**逐只**算的
        Assert.All(f.Asked.Where(a => a.Code != "600000"),
                   a => Assert.Equal(Today.AddDays(-NetInflowTask.InitialLookbackDays), a.Start));
    }

    /// <summary>
    /// 水位线就是今天、而且那一行是**收盘后**抓的 ⇒ 整只跳过，一个请求都不发。
    /// 跟下面"盘中抓的要重抓"那条合起来才是完整判据（见类注释 ①）。
    /// </summary>
    [Fact]
    public async Task 增量_水位线是今天且收盘后抓的_整只跳过()
    {
        _repo.Upsert([new NetInflow
        {
            Code = "600000", PeriodStart = Today, MainNetInflow = 1,
            FetchedAt = Today.AddHours(20),      // 收盘后
        }]);

        var f = new FakeFetcher();
        await NewTask(f).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        Assert.DoesNotContain("600000", f.Asked.Select(a => a.Code));   // 整只跳过
        Assert.Equal(Codes.Length - 1, f.Asked.Count);                  // 别的票照抓
        Assert.Equal(1, RowsOf("600000"));                              // 那一行没被重写、也没多出行
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

    // ── 心跳密度（2026-09-19）──────────────────────────────────────
    //  这一项 09-18、09-19 连着两轮被静默看门狗判成"卡死"掐断，实际它一路在正常抓：
    //  日志每 300 只才一行，而 300 只要 5 分半，比默认静默上限（5 分钟）还长。
    //  修法是心跳和日志分开：每批都喂狗（Quiet 的进展），日志仍每 10 批一行。

    /// <summary>每一批都要报一条进展，中间那些标成 Quiet（只喂狗、不写日志）。</summary>
    [Fact]
    public async Task 每批都报进展_中间批只喂心跳不写日志()
    {
        var seen = new List<TaskProgress>();
        var task = NewTask(new FakeFetcher(), batchSize: 1);
        task.OnProgress += p => { lock (seen) seen.Add(p); };

        await task.RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        // 四只票、一批一只＝四批，每批都有一条带进度数字的进展
        var steps = seen.Where(p => p.Done.HasValue).ToList();
        Assert.Equal([1, 2, 3, 4], steps.Select(p => p.Done!.Value));
        // 前三批只喂狗；末批（做完了）照常落一行日志
        Assert.All(steps.Take(3), p => Assert.True(p.Quiet));
        Assert.False(steps[^1].Quiet);
    }

    /// <summary>
    /// 端到端：任务 + registry 的桥接 + 真的看门狗。整轮跑得比静默上限久、每批远快于上限，
    /// 就该一路活着——修之前这种形状必被掐（而且日志里连"掐在哪"都看不出来）。
    /// </summary>
    [Fact]
    public async Task 整轮比静默上限还久_但每批都喂了狗_所以不被掐()
    {
        // 12 只票、一批一只、每只磨 40 毫秒 ⇒ 整轮约 0.5 秒，是静默上限（0.3 秒）的一倍半，
        // 而单批只占上限的 1/7——正是【资金净流入】全市场那一轮的缩小版。
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb,
            Enumerable.Range(1, 8).Select(i => ($"60010{i}", $"票60010{i}")));
        var f = new FakeFetcher { Delay = TimeSpan.FromMilliseconds(40) };

        var reg = new FetchTaskRegistry();
        reg.Register(FetchActionId.StepNetInflow, () => NewTask(f, batchSize: 1));

        var logs = new List<string>();
        using var dog = new QuietWatchdog(
            TimeSpan.FromMilliseconds(300), CancellationToken.None,
            checkInterval: TimeSpan.FromMilliseconds(20));

        var r = await reg.RunAsync(FetchActionId.StepNetInflow,
            new TaskRunArgs(FetchMode.Incremental),
            dog.Wrap(s => { lock (logs) logs.Add(s); }), dog.Token);

        Assert.False(dog.Starved);                 // 没被判成卡死
        Assert.False(r.Failed);
        Assert.Equal(12, f.Asked.Count);           // 12 只全抓完了，没在中途被掐断

        await Task.Delay(300);                     // 日志走 Progress<T>，异步派发
        lock (logs)
        {
            // 日志密度不变：每 10 批一行 + 末批那一行，中间的批不该出现
            Assert.DoesNotContain(logs, l => l.Contains("已抓 3 只"));
            Assert.Contains(logs, l => l.Contains("已抓 10 只"));
            Assert.Contains(logs, l => l.Contains("已抓 12 只"));
        }
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
        // 「没有待办」是**没活可干**，不是「没开工」：2026-09-21 起返回 NothingToDo。
        // 写成 Skipped 的话计划引擎会立刻再排一次，而条件根本不会变，空转到被护栏拦下。
        Assert.True(r.NothingToDo);
        Assert.Null(r.SkippedReason);
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
