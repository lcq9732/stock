using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【回购公告进展】两次被看门狗判卡死之后的修补（2026-10-22）。
///
/// 10-21 17:24、10-22 02:26 两次都停在"取正文"中途（第 80、160 条），10 分钟没进展被掐断。
/// 原因是回看 14 天每轮都把窗口里约 550 条公告**全部重取正文**（1100 个东财请求），
/// 跑到三百来条被限流拖慢，而进度 20 条才报一次。这里钉三件事：
///   ① 已经取到正文的不再重取——请求量从"整个窗口"降到"新公告 + 空壳"；
///   ② 每取完一条都喂看门狗，但日志仍是 20 条一行；
///   ③ 连续 10 条取正文抛异常（东财不通）就收工记「跳过」，已取到的照常落库。
/// </summary>
public class PlanWatchSkipAndAbortTests : IDisposable
{
    private static readonly DateOnly Busy = DateOnly.FromDateTime(DateTime.Today).AddDays(-5);
    private const string Title = "关于回购公司股份的进展公告";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"planWatch_{Guid.NewGuid():N}");

    public void Dispose()
    {
        // 不调 SqliteConnection.ClearAllPools()——那是进程级的，会崩掉别的测试类
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录 */ }
    }

    // ── ① 已取到正文的不重取 ─────────────────────────────────────

    [Fact]
    public async Task 已有正文的公告跳过_只取新的()
    {
        var repo = new FakeRepo { Watermark = DateTime.Today };
        // 50 条里前 40 条库里已有正文
        for (int i = 0; i < 40; i++) repo.Fetched.Add(($"{600000 + i}", Busy.ToDateTime(TimeOnly.MinValue), Title));
        var detail = new FakeDetail();
        var task = new PlanWatchTask(repo, new FakeSearch(50), detail);

        var r = await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.Equal(10, detail.Calls);                  // 只取了没正文的那 10 条
        Assert.Equal(10, repo.Saved.Count);
        Assert.Contains("40 条已有正文、跳过", r.Progress);
    }

    [Fact]
    public async Task 全部已有正文_一条请求都不发()
    {
        var repo = new FakeRepo { Watermark = DateTime.Today };
        for (int i = 0; i < 30; i++) repo.Fetched.Add(($"{600000 + i}", Busy.ToDateTime(TimeOnly.MinValue), Title));
        var detail = new FakeDetail();

        var r = await new PlanWatchTask(repo, new FakeSearch(30), detail).RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(0, detail.Calls);
        Assert.True(r.NothingToDo);
    }

    // ── ② 喂狗按每条，日志按时间 ───────────────────────────────────

    /// <summary>
    /// 喂狗按每条、日志按时间——这两件事分开（2026-10-24）。
    /// 跑得快的时候：45 条每条都喂狗，但 30 秒的日志间隔一次都到不了点，所以不刷屏。
    /// </summary>
    [Fact]
    public async Task 跑得快时_每条都喂狗_日志不刷屏()
    {
        var repo = new FakeRepo { Watermark = DateTime.Today };
        var detail = new FakeDetail();
        var task = new PlanWatchTask(repo, new FakeSearch(45), detail);
        var fed = new List<string>();          // 所有 OnProgress＝都算心跳，不分 Quiet
        var logged = new List<string>();       // 只有非 Quiet 的才进日志
        task.OnProgress += p => { fed.Add(p.Text); if (!p.Quiet) logged.Add(p.Text); };

        await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(45, fed.Count(t => t.Contains("取正文 ")));
        Assert.DoesNotContain(logged, t => t.Contains("取正文 "));
    }

    /// <summary>
    /// 反过来，跑得慢的时候日志照样出来。间隔设 0 ＝ 每条都到点，模拟"一条几十秒"。
    /// 旧写法是"每 20 条一行"：批量只有几条时一次都不打，于是整段静默、被看门狗掐断。
    /// </summary>
    [Fact]
    public async Task 跑得慢时_日志照样出来()
    {
        var repo = new FakeRepo { Watermark = DateTime.Today };
        var task = new PlanWatchTask(repo, new FakeSearch(45), new FakeDetail())
        {
            LogInterval = TimeSpan.Zero,
        };
        var logged = new List<string>();
        task.OnProgress += p => { if (!p.Quiet) logged.Add(p.Text); };

        await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        // 最后一条走的是 else 分支（doneToday < group.Count 为假），所以是 44 不是 45
        Assert.Equal(44, logged.Count(t => t.Contains("取正文 ")));
    }

    /// <summary>
    /// **回补那一段也要每条喂狗**——这是 2026-10-24 真正修的那个 bug：
    /// 那段原来只有 Report、没有 ReportQuiet，日志稀疏＝喂狗也稀疏；
    /// 而一轮只回补 6 条（比当时 20 条的日志门限还少），整段一次都没出声，
    /// 东财掐连接时一条要走满两个请求的超时+重试，10 分钟后就被看门狗掐断了。
    /// </summary>
    [Fact]
    public async Task 回补段也是每条喂狗()
    {
        var repo = new FakeRepo { Watermark = DateTime.Today, Missing = Stale(6) };
        var task = new PlanWatchTask(repo, new FakeSearch(0), new FakeDetail());
        var fed = new List<string>();
        task.OnProgress += p => fed.Add(p.Text);

        await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(6, fed.Count(t => t.StartsWith("回补 ") && t.Contains("/6…")));
    }

    /// <summary>回补段也要能提前收工——原来它只往 _warnings 里塞，一路耗到被掐断。</summary>
    [Fact]
    public async Task 回补连续十条取不到_也提前收工()
    {
        var repo = new FakeRepo { Watermark = DateTime.Today, Missing = Stale(60) };
        var detail = new FakeDetail { FailFrom = 1 };               // 一上来就全失败
        var task = new PlanWatchTask(repo, new FakeSearch(0), detail);

        var r = await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(PlanWatchTask.AbortAfterConsecutiveFailures, detail.Calls);   // 没一路撞到 60 条
        Assert.NotNull(r.SkippedReason);
        Assert.NotEqual(TaskState.Failed, r.State);
    }

    private static List<PlanAnnouncement> Stale(int n) => Enumerable.Range(0, n)
        .Select(i => new PlanAnnouncement
        {
            Code = $"{600100 + i}",
            AnnounceDate = DateTime.Today.AddDays(-3),
            Stage = PlanStage.Progress,
            Title = Title,
        }).ToList();

    // ── ③ 连续失败就收工 ──────────────────────────────────────────

    [Fact]
    public async Task 东财连续十条取不到_提前收工记跳过_已取到的已落库()
    {
        var repo = new FakeRepo { Watermark = DateTime.Today };
        var detail = new FakeDetail { FailFrom = 26 };              // 前 25 条正常，之后一律抛异常
        var task = new PlanWatchTask(repo, new FakeSearch(200), detail);

        var r = await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(25 + PlanWatchTask.AbortAfterConsecutiveFailures, detail.Calls);   // 没有一路撞到 200 条
        Assert.NotNull(r.SkippedReason);                            // 跳过：计划隔一阵回头再试，不算失败
        Assert.NotEqual(TaskState.Failed, r.State);
        Assert.Equal(25 + PlanWatchTask.AbortAfterConsecutiveFailures, repo.Saved.Count);  // 失败的也按标题留档
        Assert.Equal(25, repo.Saved.Count(x => x.ArtCode != ""));
    }

    [Fact]
    public async Task 零星失败不算连续_照常跑完()
    {
        var repo = new FakeRepo { Watermark = DateTime.Today };
        var detail = new FakeDetail { FailEvery = 3 };              // 每三条失败一条
        var r = await new PlanWatchTask(repo, new FakeSearch(60), detail).RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(60, detail.Calls);
        Assert.Null(r.SkippedReason);
    }

    // ── 仓储：打在真 SQLite 上 ─────────────────────────────────────

    [Fact]
    public void 已取到正文的键_只含有art_code的且在窗口内()
    {
        Directory.CreateDirectory(_dir);
        var repo = new SqlitePlanAnnouncementRepository(Path.Combine(_dir, "t.sqlite"));
        repo.EnsureSchema();
        var day = new DateTime(2026, 10, 9);
        repo.Upsert(
        [
            new PlanAnnouncement { Code = "600000", AnnounceDate = day, Stage = PlanStage.Progress, Title = Title, ArtCode = "AN1" },
            new PlanAnnouncement { Code = "600001", AnnounceDate = day, Stage = PlanStage.Progress, Title = Title, ArtCode = "" },
            new PlanAnnouncement { Code = "600002", AnnounceDate = day.AddDays(-30), Stage = PlanStage.Progress, Title = Title, ArtCode = "AN2" },
        ]);

        var keys = repo.GetFetchedKeys(PlanKind.Buyback, day.AddDays(-14));

        Assert.Equal([("600000", day, Title)], keys.ToList());
    }

    // ─────────────────── 脚手架 ───────────────────

    private sealed class FakeRepo : IPlanAnnouncementRepository
    {
        public List<PlanAnnouncement> GetStaleExtraction(string kind, int version, int fetchLimit) => [];
        public int CountStaleExtraction(string kind, int version) => 0;
        public DateTime? Watermark;
        public readonly List<PlanAnnouncement> Saved = [];
        public HashSet<(string Code, DateTime AnnounceDate, string Title)> Fetched = [];

        public void EnsureSchema() { }
        public int Upsert(IEnumerable<PlanAnnouncement> items) { var l = items.ToList(); Saved.AddRange(l); return l.Count; }
        public List<PlanAnnouncement> GetByCode(string code, string kind) => [];
        /// <summary>要回补的空壳；默认没有，测回补那几条用例自己塞。</summary>
        public List<PlanAnnouncement> Missing = [];
        public List<PlanAnnouncement> GetMissingDetail(string kind, DateTime since, int limit)
            => Missing.Take(limit).ToList();
        public Dictionary<string, PlanAnnouncement> GetOpenPlans(string kind) => [];
        public DateTime? GetLatestAnnounceDate(string kind) => Watermark;
        public HashSet<(string Code, DateTime AnnounceDate, string Title)> GetFetchedKeys(string kind, DateTime since) => Fetched;
        public (int Rows, int Stocks, int OpenPlans) GetCounts(string kind) => (Saved.Count, 1, 0);
    }

    /// <summary>只有 <see cref="Busy"/> 那天有 <paramref name="hits"/> 条命中。</summary>
    private sealed class FakeSearch(int hits) : IAnnouncementSearchProvider
    {
        public Task<List<AnnouncementSearchHit>> SearchAsync(
            string keyword, DateOnly start, DateOnly end, IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromResult(start != Busy ? [] : Enumerable.Range(0, hits)
                .Select(i => new AnnouncementSearchHit($"{600000 + i}", $"票{i}", Title, Busy.ToDateTime(TimeOnly.MinValue), null))
                .ToList());
    }

    private sealed class FakeDetail : IAnnouncementDetailFetcher
    {
        public event Action<string>? OnStatus { add { } remove { } }
        public Task<string?> FetchContentAsync(string artCode, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public int Calls;
        /// <summary>从第几次调用起一律抛异常（模拟东财限流不通）。</summary>
        public int? FailFrom;
        /// <summary>每隔几次失败一次（零星失败）。</summary>
        public int? FailEvery;

        public Task<(string ArtCode, string Content)?> FetchDetailAsync(
            string code, string title, DateOnly approxDate, CancellationToken ct = default)
        {
            Calls++;
            if (FailFrom is { } f && Calls >= f) throw new HttpRequestException("假的：东财限流");
            if (FailEvery is { } e && Calls % e == 0) throw new HttpRequestException("假的：偶发超时");
            return Task.FromResult<(string, string)?>(($"art{code}", "本次回购股份 100 股，成交金额 1000 元。"));
        }
    }
}
