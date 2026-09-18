using Microsoft.Data.Sqlite;
using StockPlatform.Data.Orchestration;
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
/// 【大宗交易】任务的编排（2026-09-17 从【拉取市场事件】拆出来，见 doc/block-trade-task-design.md）。
///
/// 落库行为在 <see cref="BlockTradeDayWriterTests"/>，这一组只盯任务这一层：
/// ① 按**交易日历**排期，非交易日一个请求都不发；
/// ② 抓不全的那一天**不落库**、记成残缺日待办——这是这张表能犯的最坏的错，库里和体检都看不出来；
/// ③ <c>MaxItems</c> / <c>Deadline</c> 能分批收尾，水位线跟着走，下轮接得上。
/// </summary>
public class BlockTradeTaskTests : IDisposable
{
    private readonly string _dir;
    private readonly FetchPaths _paths;
    private readonly SqliteMarketEventRepository _repo;
    private readonly SqliteTradingDayRepository _cal;
    private readonly JsonManifestStore _manifest;

    /// <summary>五个连续交易日（周一到周五），中间夹的周末故意不进日历。</summary>
    private static readonly DateTime[] Days =
    [
        new(2026, 9, 7), new(2026, 9, 8), new(2026, 9, 9), new(2026, 9, 10), new(2026, 9, 11),
    ];

    public BlockTradeTaskTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"btTask_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        _repo = new SqliteMarketEventRepository(_paths.CurrentDb);
        _repo.EnsureSchema();
        _cal = new SqliteTradingDayRepository(_paths.CurrentDb);
        _cal.EnsureSchema();
        _cal.Upsert(Days.Select(d => (DateOnly.FromDateTime(d), ITradingDayRepository.SzseSource)));
        _manifest = new JsonManifestStore(_paths.ManifestPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录 */ }
    }

    // ── 假数据源 ──────────────────────────────────────────────────

    /// <summary>
    /// 离线的"抓一天"。<see cref="Shortfall"/> 里的日子会少给几行、但 count 照报全数——
    /// 正是"某页被限流截断"的样子。
    /// </summary>
    private sealed class FakeFetcher : IBlockTradeDayFetcher
    {
        public readonly List<DateTime> Asked = [];
        public HashSet<DateTime> Empty = [];
        public HashSet<DateTime> Shortfall = [];
        public HashSet<DateTime> Throws = [];
        public int RowsPerDay = 3;
        /// <summary>抓到第 N 天时触发取消——用来验中断收尾。</summary>
        public (int AfterDays, CancellationTokenSource Cts)? CancelAt;

        public Task<BlockTradeDay> FetchBlockTradesOfDayAsync(DateTime day, CancellationToken ct = default)
        {
            Asked.Add(day.Date);
            if (CancelAt is { } ca && Asked.Count >= ca.AfterDays) ca.Cts.Cancel();
            if (Throws.Contains(day.Date)) throw new HttpRequestException("连不上");
            if (Empty.Contains(day.Date)) return Task.FromResult(new BlockTradeDay(day.Date, [], 0));

            var rows = Enumerable.Range(0, RowsPerDay).Select(i => new BlockTrade
            {
                Code = "300750", Name = "宁德时代", TradeDate = day.Date,
                DealPrice = 300, DealVolume = 1000, DealAmount = 300_000 + i,
                PremiumRatio = 0, BuyerName = "机构专用", SellerName = "机构专用",
                FetchedAt = DateTime.Now,
            }).ToList();

            // 少给一行，但 count 仍报全数
            if (Shortfall.Contains(day.Date)) rows.RemoveAt(rows.Count - 1);
            return Task.FromResult(new BlockTradeDay(day.Date, rows, RowsPerDay));
        }
    }

    private BlockTradeTask NewTask(FakeFetcher f) => new(_repo, f, _cal, _manifest, _paths);

    private static TaskRunArgs Backfill(int? maxItems = null, DateTime? deadline = null) =>
        new(FetchMode.FirstBackfill, MaxItems: maxItems, Deadline: deadline);

    private int RowsOn(DateTime day)
    {
        using var conn = new SqliteConnection($"Data Source={_paths.CurrentDb}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM BlockTrade WHERE trade_date = $d;";
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private List<DateTime> PendingDays() =>
        (_manifest.Load().Todo(RetryTaskIds.BlockTrade, RetryTodoKind.PartialDay)?.Targets ?? [])
            .Where(t => t.Day.HasValue).Select(t => t.Day!.Value.Date).OrderBy(d => d).ToList();

    // ── 排期 ──────────────────────────────────────────────────────

    [Fact]
    public async Task 整段回补_只问交易日历里的日子()
    {
        var f = new FakeFetcher();
        var r = await NewTask(f).RunAsync(Backfill(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        // 9-7 ~ 9-11 五个交易日；中间的周末（9-12/13 之后才是，这段本身连续）不该出现
        Assert.All(f.Asked, d => Assert.Contains(d, Days));
        Assert.Equal(Days.Length, f.Asked.Count);
        Assert.All(Days, d => Assert.Equal(3, RowsOn(d)));
    }

    /// <summary>日历里没有的日子一个请求都不发——2600 个交易日混着周末就是 3900 天，全是空请求。</summary>
    [Fact]
    public async Task 非交易日_一个请求都不发()
    {
        var f = new FakeFetcher();
        await NewTask(f).RunAsync(Backfill(), CancellationToken.None);

        Assert.DoesNotContain(new DateTime(2026, 9, 12), f.Asked);   // 周六
        Assert.DoesNotContain(new DateTime(2026, 9, 13), f.Asked);   // 周日
    }

    [Fact]
    public async Task MaxItems_只抓那么多天()
    {
        var f = new FakeFetcher();
        await NewTask(f).RunAsync(Backfill(maxItems: 2), CancellationToken.None);

        Assert.Equal(2, f.Asked.Count);
        Assert.Equal([Days[0], Days[1]], f.Asked);
        Assert.Equal(0, RowsOn(Days[2]));
    }

    /// <summary>分批跑要接得上：第二轮从水位线往前回看，把剩下的抓完。</summary>
    [Fact]
    public async Task 分两轮跑_接得上()
    {
        var f = new FakeFetcher();
        await NewTask(f).RunAsync(Backfill(maxItems: 2), CancellationToken.None);
        await NewTask(f).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        Assert.All(Days, d => Assert.Equal(3, RowsOn(d)));
    }

    [Fact]
    public async Task 到了收尾时间_停在天与天之间()
    {
        var f = new FakeFetcher();
        var r = await NewTask(f).RunAsync(
            Backfill(deadline: DateTime.Now.AddMilliseconds(-1)), CancellationToken.None);

        Assert.Empty(f.Asked);
        Assert.Equal(TaskState.Completed, r.State);
    }

    // ── count 校验 ────────────────────────────────────────────────

    /// <summary>
    /// **本次改造最要紧的一条**：某页被限流截断时实收行数对不上接口自报的 count，
    /// 这一天绝不能落库——拿半天的数据盖掉完整的一天，库里看不出、体检也看不出。
    /// </summary>
    [Fact]
    public async Task 收到的行数对不上count_那天不落库_记成待办()
    {
        var f = new FakeFetcher { Shortfall = [Days[2]] };
        var r = await NewTask(f).RunAsync(Backfill(), CancellationToken.None);

        Assert.Equal(0, RowsOn(Days[2]));
        Assert.Equal(3, RowsOn(Days[1]));      // 别的天照常
        Assert.Equal(3, RowsOn(Days[3]));
        Assert.Equal([Days[2]], PendingDays());
        Assert.Contains(r.Errors, e => e.Contains("2026-09-09"));
    }

    /// <summary>已经有完整数据的那天，后来抓到残缺的一次，**不能**把它覆盖掉。</summary>
    [Fact]
    public async Task 抓残缺时_已有的完整数据不被覆盖()
    {
        await NewTask(new FakeFetcher()).RunAsync(Backfill(), CancellationToken.None);
        Assert.Equal(3, RowsOn(Days[2]));

        var bad = new FakeFetcher { Shortfall = [Days[2]] };
        await NewTask(bad).RunAsync(new TaskRunArgs(FetchMode.SpecificDay,
            Day: DateOnly.FromDateTime(Days[2])), CancellationToken.None);

        Assert.Equal(3, RowsOn(Days[2]));
    }

    [Fact]
    public async Task 单天抓取失败_不拖垮整轮_记成待办()
    {
        var f = new FakeFetcher { Throws = [Days[1]] };
        var r = await NewTask(f).RunAsync(Backfill(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.Equal(0, RowsOn(Days[1]));
        Assert.Equal(3, RowsOn(Days[4]));
        Assert.Equal([Days[1]], PendingDays());
    }

    /// <summary>全轮都抓不成多半是断网/限流，要整项判失败让人看见，不能静静地"完成"。</summary>
    [Fact]
    public async Task 整轮全失败_判失败()
    {
        var f = new FakeFetcher { Throws = [.. Days] };
        var r = await NewTask(f).RunAsync(Backfill(), CancellationToken.None);

        Assert.Equal(TaskState.Failed, r.State);
        Assert.Equal(Days.Length, PendingDays().Count);
    }

    /// <summary>那天数据源本来就没有大宗交易——0 行是正常结果，不是残缺，不该进待办。</summary>
    [Fact]
    public async Task 那天真的没有大宗_不记成待办()
    {
        var f = new FakeFetcher { Empty = [Days[0]] };
        var r = await NewTask(f).RunAsync(Backfill(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.Equal(0, RowsOn(Days[0]));
        Assert.Empty(PendingDays());
    }

    // ── 幂等 ──────────────────────────────────────────────────────

    /// <summary>
    /// 跑两轮，库里行数一模一样。原来靠东财的 DAILY_RANK 做主键时，这里会翻倍——
    /// 这条红了就说明那个 bug 回来了。
    /// </summary>
    [Fact]
    public async Task 连跑两轮_行数不变()
    {
        await NewTask(new FakeFetcher()).RunAsync(Backfill(), CancellationToken.None);
        int before = _repo.Count("BlockTrade");

        await NewTask(new FakeFetcher()).RunAsync(Backfill(), CancellationToken.None);

        Assert.Equal(before, _repo.Count("BlockTrade"));
        Assert.Equal(Days.Length * 3, before);
    }

    [Fact]
    public async Task 增量_从水位线往前回看()
    {
        await NewTask(new FakeFetcher()).RunAsync(Backfill(), CancellationToken.None);

        var f = new FakeFetcher();
        await NewTask(f).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        // 水位线是 9-11，往前回看 30 天 → 这五天全在窗口里，全部重抓（幂等，不会多行）
        Assert.Equal(Days.Length, f.Asked.Count);
        Assert.Equal(Days.Length * 3, _repo.Count("BlockTrade"));
    }

    // ── 中断收尾的措辞 ────────────────────────────────────────────

    /// <summary>
    /// **「整段回补」中断时不能说"下次从水位线接着走"**——它压根不看水位线，每轮都从数据起点
    /// 重新排期，中断＝进度不保留；改用增量也接不上（只回看 30 天，够不着中间那段）。
    ///
    /// 这条跟 <c>LhbSeatTaskTests</c> 里那两条是同一件事：2026-09-17 席位表整段回补跑到
    /// 459/2603 时被停掉，收尾打的正是这句错话，人看了才放心停的。两个任务同一份逻辑，
    /// 各写一遍就会各改错一遍，所以两边都锁住。
    /// </summary>
    [Fact]
    public async Task 整段回补中断_要说清进度不保留()
    {
        var cts = new CancellationTokenSource();
        var f = new FakeFetcher { CancelAt = (2, cts) };
        var task = NewTask(f);
        var lines = new List<string>();
        task.OnProgress += p => lines.Add(p.Text);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => task.RunAsync(Backfill(), cts.Token));

        var stop = Assert.Single(lines, l => l.Contains("中断"));
        Assert.Contains("进度不保留", stop);
        Assert.DoesNotContain("下次从水位线接着走", stop);
    }

    /// <summary>增量中断则确实是"从水位线接着走"——整日替换幂等，重跑无害。</summary>
    [Fact]
    public async Task 增量中断_说从水位线接着走()
    {
        await NewTask(new FakeFetcher()).RunAsync(Backfill(), CancellationToken.None);

        var cts = new CancellationTokenSource();
        var f = new FakeFetcher { CancelAt = (2, cts) };
        var task = NewTask(f);
        var lines = new List<string>();
        task.OnProgress += p => lines.Add(p.Text);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => task.RunAsync(new TaskRunArgs(FetchMode.Incremental), cts.Token));

        var stop = Assert.Single(lines, l => l.Contains("中断"));
        Assert.Contains("下次从水位线接着走", stop);
        Assert.DoesNotContain("进度不保留", stop);
    }

    // ── 只补待办（2026-09-18 收口：编排从 orchestrator 搬进任务）──────────

    /// <summary>
    /// 【只补待办】的目标**从待办清单来**，不从水位线来——这正是它跟增量的分界。
    ///
    /// 编排本体是共用的 <c>PartialDayRepair</c>（复查判据、Tries、确认名单都在那儿，
    /// 由 <see cref="PartialDayRepairTests"/> 盯着），这一条只盯**接线**：
    /// 收到这个模式之后抓的是不是待办里那一天、抓回来写没写回去。
    /// </summary>
    [Fact]
    public async Task 只补待办_只抓待办里的那一天()
    {
        await NewTask(new FakeFetcher()).RunAsync(Backfill(), CancellationToken.None);
        DeleteRows(Days[2], keep: 1);
        Todo(Days[2]);

        var f = new FakeFetcher();
        var r = await NewTask(f).RunAsync(
            new TaskRunArgs(FetchMode.FillBacklog), CancellationToken.None);

        Assert.Equal([Days[2]], f.Asked);            // ← 不是五天：没走水位线那条路
        Assert.Equal(3, RowsOn(Days[2]));            // 整日替换把那天补回来了
        Assert.Equal(TaskState.Completed, r.State);
    }

    /// <summary>没有欠着的天就别开工：报跳过、一个请求都不发。</summary>
    [Fact]
    public async Task 只补待办_没有待办就跳过()
    {
        var f = new FakeFetcher();
        var r = await NewTask(f).RunAsync(
            new TaskRunArgs(FetchMode.FillBacklog), CancellationToken.None);

        Assert.Empty(f.Asked);
        Assert.NotNull(r.SkippedReason);
    }

    /// <summary>抓不动的时候整项判失败——名单和 Tries 由 PartialDayRepair 原样留着。</summary>
    [Fact]
    public async Task 只补待办_那天抓不动_整项失败()
    {
        await NewTask(new FakeFetcher()).RunAsync(Backfill(), CancellationToken.None);
        DeleteRows(Days[2], keep: 1);
        Todo(Days[2]);

        var f = new FakeFetcher { Throws = [Days[2]] };
        var r = await NewTask(f).RunAsync(
            new TaskRunArgs(FetchMode.FillBacklog), CancellationToken.None);

        Assert.Equal(TaskState.Failed, r.State);
        Assert.Equal([Days[2]], PendingDays());      // 名单原样留着，下轮还来
    }

    /// <summary>往待办里记一天残缺日。</summary>
    private void Todo(DateTime day)
    {
        var m = _manifest.Load();
        m.SetTodo(RetryTaskIds.BlockTrade, RetryTodoKind.PartialDay,
                  [new RetryTarget { Day = day.Date, Tries = 0 }]);
        _manifest.Save(m);
    }

    /// <summary>把某天削成残缺：只留 <paramref name="keep"/> 行。</summary>
    private void DeleteRows(DateTime day, int keep)
    {
        using var conn = new SqliteConnection($"Data Source={_paths.CurrentDb}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "DELETE FROM BlockTrade WHERE trade_date = $d AND rowid NOT IN "
            + "(SELECT rowid FROM BlockTrade WHERE trade_date = $d LIMIT $k);";
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$k", keep);
        cmd.ExecuteNonQuery();
    }
}
