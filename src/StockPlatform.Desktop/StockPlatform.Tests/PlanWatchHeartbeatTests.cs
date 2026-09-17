using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【回购公告进展】取正文那一段的**心跳密度**和**分段落库**（2026-09-14）。
///
/// ════ 这组测试是为一个真实事故写的 ════
/// 09-14 08:25 这一项被判定卡死掐断，最后一句停在「2026-09-02 命中 262 条，
/// 真回购公告 241 条，开始取正文…」。它其实一直在正常前进——只是内层那个 foreach
/// 整段一句话不说：单条正文两个请求、限流 1 秒一个，实测 1.3 秒/条，**241 条＝5 分 13 秒**，
/// 刚好越过 <c>QuietWatchdog</c> 的 5 分钟线。
///
/// ⚠ 而且这是**每月都会来一次**的结构性高峰，不是偶发：法定披露节奏是「回购期间每月
///   前三个交易日披露上月进展」，月初那几天的量是平日十几倍（09-02 有 241 条，09-04 只有 2 条）。
///
/// 还有第二层伤：<c>yield return</c> 当时在内层循环之外，掐断时这一天抓到的全部丢弃、
/// 一行不落库，水位线原地不动 → 下一轮算出同样的起点 → 跑到同一天再死一次。
/// 这就是 <c>QuietWatchdog</c> 类注释里龙虎榜那种**自锁死**换了个地方。
///
/// 所以这两条都得被判据钉住：**慢的那一段必须持续出声**，**已抓到的必须先落库**。
/// </summary>
public class PlanWatchHeartbeatTests
{
    /// <summary>跟 <c>PlanWatchTask.ProgressEvery</c> 对齐（那边是 private）。</summary>
    private const int ProgressEvery = 20;

    /// <summary>跟 <c>PlanWatchTask.YieldChunk</c> 对齐。</summary>
    private const int YieldChunk = 60;

    /// <summary>就是出事那天的条数。</summary>
    private const int BusyDayHits = 241;

    /// <summary>
    /// ★ 事故场景：单天 241 条，取正文期间必须每 20 条报一次。
    /// 判据是"相邻两次上报之间隔了多少条"——这正是看门狗看到的静默长度。
    /// </summary>
    [Fact]
    public async Task 单天命中很多时_取正文期间持续报进度()
    {
        var (task, _, detail) = Build(BusyDayHits);

        // 每次上报时记下"已经取了多少条正文"，上报之间的差就是静默跨度。
        var beats = new List<int>();
        task.OnProgress += _ => beats.Add(detail.Calls);

        var result = await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, result.State);
        Assert.Equal(BusyDayHits, detail.Calls);

        var gaps = beats.Zip(beats.Skip(1), (a, b) => b - a).ToList();
        Assert.NotEmpty(gaps);
        Assert.All(gaps, g => Assert.True(
            g <= ProgressEvery,
            $"两次上报之间静默了 {g} 条（约 {g * 1.3:F0} 秒），超过 {ProgressEvery} 条就有被看门狗误杀的风险。"));
    }

    /// <summary>量小的日子（平日就几条）不该被进度刷屏——一天一句就够。</summary>
    [Fact]
    public async Task 单天只有几条时_不刷屏()
    {
        var (task, _, _) = Build(3);

        var texts = new List<string>();
        task.OnProgress += p => texts.Add(p.Text);

        await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        // 「开始取正文…」那句照报，中间那种「取正文 20/241…」一条都不该有。
        Assert.DoesNotContain(texts, t => t.Contains("取正文 "));
    }

    /// <summary>一天的命中拆成多批交出去，每批不超过 YieldChunk，合起来一条不少。</summary>
    [Fact]
    public async Task 单天命中很多时_分段落库()
    {
        var (task, repo, _) = Build(BusyDayHits);

        await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.True(repo.BatchSizes.Count >= BusyDayHits / YieldChunk,
            $"241 条只交了 {repo.BatchSizes.Count} 批，没有分段。");
        Assert.All(repo.BatchSizes, n => Assert.True(n <= YieldChunk, $"有一批 {n} 条，超过 {YieldChunk}。"));
        Assert.Equal(BusyDayHits, repo.BatchSizes.Sum());
    }

    /// <summary>
    /// ★ 自锁死那一半：跑到一半被掐（看门狗判卡死、或人按停止），
    /// 已经抓到的那几十条必须已经在库里——否则水位线不动，下一轮会在同一天再死一次。
    /// </summary>
    [Fact]
    public async Task 中途被取消_已抓的那几段仍已落库()
    {
        var (task, repo, detail) = Build(BusyDayHits);

        var states = new List<TaskStateChanged>();
        task.OnStateChanged += s => states.Add(s);

        using var cts = new CancellationTokenSource();
        detail.OnCall = n => { if (n == 100) cts.Cancel(); };

        // 取消照旧往上抛——骨架靠这个跟"失败"区分，见 FetchTaskBase.RunAsync。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => task.RunAsync(new TaskRunArgs(), cts.Token));

        Assert.Contains(states, s => s.State == TaskState.Stopped);
        Assert.True(repo.BatchSizes.Sum() >= YieldChunk,
            $"被取消时只落库了 {repo.BatchSizes.Sum()} 条——修复前是 0，这一天白跑。");
    }

    // ─────────────────── 脚手架 ───────────────────

    /// <summary>
    /// 造一个"只有某一天有命中"的场景。那天选 today−5：任务内部按 <c>DateTime.Today</c> 算窗口
    /// （水位线设成今天 → 起点是今天−14），落在窗口里。
    /// </summary>
    private static (PlanWatchTask Task, FakeRepo Repo, FakeDetail Detail) Build(int hits)
    {
        var busy = DateOnly.FromDateTime(DateTime.Today).AddDays(-5);
        var repo = new FakeRepo { Watermark = DateTime.Today };
        var detail = new FakeDetail();
        return (new PlanWatchTask(repo, new FakeSearch(busy, hits), detail), repo, detail);
    }

    private sealed class FakeRepo : IPlanAnnouncementRepository
    {
        public DateTime? Watermark;

        /// <summary>每次 Upsert 的条数——批的形状就看它。</summary>
        public readonly List<int> BatchSizes = [];

        public void EnsureSchema() { }
        public int Upsert(IEnumerable<PlanAnnouncement> items)
        {
            var n = items.Count();
            BatchSizes.Add(n);
            return n;
        }
        /// <summary>回补段要取的空壳行；默认空＝这组测试只看新抓那一段。</summary>
        public List<PlanAnnouncement> Missing = [];

        public List<PlanAnnouncement> GetByCode(string code, string kind) => [];
        public List<PlanAnnouncement> GetMissingDetail(string kind, DateTime since, int limit)
            => Missing.Take(limit).ToList();
        public Dictionary<string, PlanAnnouncement> GetOpenPlans(string kind) => [];
        public DateTime? GetLatestAnnounceDate(string kind) => Watermark;
        public (int Rows, int Stocks, int OpenPlans) GetCounts(string kind) => (BatchSizes.Sum(), 1, 0);
    }

    private sealed class FakeSearch(DateOnly busyDay, int hits) : IAnnouncementSearchProvider
    {
        public Task<List<AnnouncementSearchHit>> SearchAsync(
            string keyword, DateOnly start, DateOnly end,
            IProgress<string>? progress = null, CancellationToken ct = default)
        {
            if (start != busyDay) return Task.FromResult(new List<AnnouncementSearchHit>());
            var list = Enumerable.Range(0, hits)
                .Select(i => new AnnouncementSearchHit(
                    (600000 + i).ToString(), $"票{i}",
                    "关于回购公司股份的进展公告",
                    busyDay.ToDateTime(TimeOnly.MinValue), null))
                .ToList();
            return Task.FromResult(list);
        }
    }

    private sealed class FakeDetail : IAnnouncementDetailFetcher
    {
        /// <summary>取了多少条正文——测试拿它当"慢时钟"，一条≈1.3 秒。</summary>
        public int Calls;

        /// <summary>第 N 条时插一脚（用来在中途触发取消）。</summary>
        public Action<int>? OnCall;

        public Task<(string ArtCode, string Content)?> FetchDetailAsync(
            string code, string title, DateOnly approxDate, CancellationToken ct = default)
        {
            Calls++;
            OnCall?.Invoke(Calls);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<(string, string)?>(($"art{code}", "本次回购股份 100 股，成交金额 1000 元。"));
        }
    }
}
