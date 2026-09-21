using Microsoft.Data.Sqlite;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【拉取龙虎榜席位】任务的编排（2026-09-17 迁到新框架 + 改按日整日替换，
/// 见 doc/lhb-seat-task-design.md）。
///
/// 跟【大宗交易】那一组的差别全在"一天是**两个接口**"上：
/// ① 只有买方抓全、卖方对不上 → **整天**不落库（只写回买方＝把卖方整天删掉）；
/// ② 同一张榜里席位和金额完全相同的两行是**真实存在**的（接口自己就会返回），
///    整日替换必须原样落库，不能自作主张去重。
/// </summary>
public class LhbSeatTaskTests : IDisposable
{
    private readonly string _dir;
    private readonly FetchPaths _paths;
    private readonly SqliteLhbSeatRepository _repo;
    private readonly SqliteTradingDayRepository _cal;
    private readonly JsonManifestStore _manifest;

    /// <summary>五个连续交易日（周一到周五），中间夹的周末故意不进日历。</summary>
    private static readonly DateTime[] Days =
    [
        new(2026, 9, 7), new(2026, 9, 8), new(2026, 9, 9), new(2026, 9, 10), new(2026, 9, 11),
    ];

    public LhbSeatTaskTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"lsTask_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        _repo = new SqliteLhbSeatRepository(_paths.CurrentDb);
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
    /// 离线的"抓一天"。每天一张买方榜 + 一张卖方榜，各 <see cref="SeatsPerSide"/> 个席位。
    /// <see cref="ShortfallBuy"/> / <see cref="ShortfallSell"/> 里的日子会少给一行、
    /// 但那一侧的 count 照报全数——正是"某页被限流截断"的样子。
    /// </summary>
    private sealed class FakeFetcher : ILhbSeatDayFetcher
    {
        public readonly List<DateTime> Asked = [];
        public HashSet<DateTime> Empty = [];
        public HashSet<DateTime> ShortfallBuy = [];
        public HashSet<DateTime> ShortfallSell = [];
        public HashSet<DateTime> Throws = [];
        public int SeatsPerSide = 5;
        /// <summary>再加一行跟第一行席位和金额完全相同的——接口真的会这么返回（2016-03-25 实测 6 行）。</summary>
        public bool WithTwins;
        /// <summary>抓到第 N 天时触发取消——用来验中断收尾。</summary>
        public (int AfterDays, CancellationTokenSource Cts)? CancelAt;

        public Task<LhbSeatDay> FetchLhbSeatsOfDayAsync(DateTime day, CancellationToken ct = default)
        {
            Asked.Add(day.Date);
            if (CancelAt is { } ca && Asked.Count >= ca.AfterDays) ca.Cts.Cancel();
            if (Throws.Contains(day.Date)) throw new HttpRequestException("连不上");
            if (Empty.Contains(day.Date)) return Task.FromResult(new LhbSeatDay(day.Date, [], 0, 0));

            var rows = new List<LhbSeat>();
            foreach (var isBuy in new[] { true, false })
            {
                for (int i = 0; i < SeatsPerSide; i++) rows.Add(Row(day, isBuy, i));
                if (WithTwins) rows.Add(Row(day, isBuy, 0));    // 跟第 0 行完全相同
            }
            int perSide = SeatsPerSide + (WithTwins ? 1 : 0);

            if (ShortfallBuy.Contains(day.Date)) rows.Remove(rows.Last(r => r.IsBuy));
            if (ShortfallSell.Contains(day.Date)) rows.Remove(rows.Last(r => !r.IsBuy));

            EastMoneyLhbSeatProvider.AssignSeq(rows);
            return Task.FromResult(new LhbSeatDay(day.Date, rows, perSide, perSide));
        }

        private static LhbSeat Row(DateTime day, bool isBuy, int i) => new()
        {
            Code = "600108", Name = "亚盛集团", TradeDate = day.Date, IsBuy = isBuy,
            SeatCode = i == 0 ? "0" : $"100{i}", SeatName = i == 0 ? "机构专用" : $"某某营业部{i}",
            Buy = isBuy ? 1_000_000 - i : null,
            Sell = isBuy ? null : 900_000 - i,
            Net = (isBuy ? 1 : -1) * (1_000_000 - i),
            Explanation = "日涨幅偏离值达到7%的前5只证券",
            FetchedAt = DateTime.Now,
        };
    }

    private LhbSeatTask NewTask(FakeFetcher f) => new(_repo, f, _cal, _manifest, _paths);

    private static TaskRunArgs Backfill(int? maxItems = null, DateTime? deadline = null) =>
        new(FetchMode.FirstBackfill, MaxItems: maxItems, Deadline: deadline);

    private int RowsOn(DateTime day, bool? isBuy = null)
    {
        using var conn = new SqliteConnection($"Data Source={_paths.CurrentDb}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM LhbSeat WHERE trade_date = $d"
                        + (isBuy is { } b ? $" AND is_buy = {(b ? 1 : 0)}" : "") + ";";
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private List<DateTime> PendingDays() =>
        (_manifest.Load().Todo(RetryTaskIds.LhbSeat, RetryTodoKind.PartialDay)?.Targets ?? [])
            .Where(t => t.Day.HasValue).Select(t => t.Day!.Value.Date).OrderBy(d => d).ToList();

    // ── 排期 ──────────────────────────────────────────────────────

    [Fact]
    public async Task 整段回补_只问交易日历里的日子()
    {
        var f = new FakeFetcher();
        var r = await NewTask(f).RunAsync(Backfill(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.All(f.Asked, d => Assert.Contains(d, Days));
        Assert.Equal(Days.Length, f.Asked.Count);
        Assert.All(Days, d => Assert.Equal(10, RowsOn(d)));      // 买 5 + 卖 5
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

        Assert.All(Days, d => Assert.Equal(10, RowsOn(d)));
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

    // ── count 校验：两侧都要对上 ──────────────────────────────────

    /// <summary>
    /// **这张表最要紧的一条**：卖方那半被截断了，买方那半也不能落库——
    /// 整日替换会先 DELETE 掉一整天，只写回买方就等于把卖方永久删掉，
    /// 而且事后完全看不出来（行数判据只会觉得"那天本来就少"）。
    /// </summary>
    [Fact]
    public async Task 只有卖方对不上count_整天都不落库()
    {
        var f = new FakeFetcher { ShortfallSell = [Days[2]] };
        var r = await NewTask(f).RunAsync(Backfill(), CancellationToken.None);

        Assert.Equal(0, RowsOn(Days[2]));                 // ← 买方那半也没写
        Assert.Equal(0, RowsOn(Days[2], isBuy: true));
        Assert.Equal(10, RowsOn(Days[1]));                // 别的天照常
        Assert.Equal([Days[2]], PendingDays());
        Assert.Contains(r.Errors, e => e.Contains("2026-09-09"));
    }

    [Fact]
    public async Task 只有买方对不上count_整天都不落库()
    {
        var f = new FakeFetcher { ShortfallBuy = [Days[1]] };
        await NewTask(f).RunAsync(Backfill(), CancellationToken.None);

        Assert.Equal(0, RowsOn(Days[1]));
        Assert.Equal([Days[1]], PendingDays());
    }

    /// <summary>已经有完整数据的那天，后来抓到残缺的一次，**不能**把它覆盖掉。</summary>
    [Fact]
    public async Task 抓残缺时_已有的完整数据不被覆盖()
    {
        await NewTask(new FakeFetcher()).RunAsync(Backfill(), CancellationToken.None);
        Assert.Equal(10, RowsOn(Days[2]));

        var bad = new FakeFetcher { ShortfallSell = [Days[2]] };
        await NewTask(bad).RunAsync(new TaskRunArgs(FetchMode.SpecificDay,
            Day: DateOnly.FromDateTime(Days[2])), CancellationToken.None);

        Assert.Equal(10, RowsOn(Days[2]));
    }

    [Fact]
    public async Task 单天抓取失败_不拖垮整轮_记成待办()
    {
        var f = new FakeFetcher { Throws = [Days[1]] };
        var r = await NewTask(f).RunAsync(Backfill(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.Equal(0, RowsOn(Days[1]));
        Assert.Equal(10, RowsOn(Days[4]));
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

    /// <summary>那天本来就没人上榜——0 行是正常结果，不是残缺，不该进待办。</summary>
    [Fact]
    public async Task 那天真的没人上榜_不记成待办()
    {
        var f = new FakeFetcher { Empty = [Days[0]] };
        var r = await NewTask(f).RunAsync(Backfill(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.Equal(0, RowsOn(Days[0]));
        Assert.Empty(PendingDays());
    }

    // ── 幂等 ──────────────────────────────────────────────────────

    /// <summary>
    /// 跑两轮，库里行数一模一样。这条红了就说明 2026-09-17 修的那个 bug 回来了
    /// （seq 是位次，靠主键 UPSERT 去重时行数一波动就留下孤儿行）。
    /// </summary>
    [Fact]
    public async Task 连跑两轮_行数不变()
    {
        await NewTask(new FakeFetcher()).RunAsync(Backfill(), CancellationToken.None);
        int before = _repo.Count();

        await NewTask(new FakeFetcher()).RunAsync(Backfill(), CancellationToken.None);

        Assert.Equal(before, _repo.Count());
        Assert.Equal(Days.Length * 10, before);
    }

    /// <summary>
    /// 席位和金额**完全相同**的两行是真实存在的（2016-03-25 单日实抓，接口自己返回了 6 行这种），
    /// 整日替换必须原样落库、不能自作主张去重；而且重抓之后还是那么多行。
    ///
    /// ⚠ 大宗那轮的判据"同批次内的同内容行是真实拆单"在这张表上**不成立**（同批次内的同内容行
    /// 多数是跨页副本），但反过来也不能一律去重——去重的判断该由接口返回什么来定，不是由我们定。
    /// </summary>
    [Fact]
    public async Task 同榜同席位同金额的两行_原样落库且重抓不变()
    {
        await NewTask(new FakeFetcher { WithTwins = true }).RunAsync(Backfill(), CancellationToken.None);
        Assert.Equal(12, RowsOn(Days[0]));               // (5+1) × 2 侧
        int before = _repo.Count();

        await NewTask(new FakeFetcher { WithTwins = true }).RunAsync(Backfill(), CancellationToken.None);

        Assert.Equal(before, _repo.Count());
    }

    /// <summary>
    /// 一天里席位数变少了（东财事后撤回一行），整天替换要跟着变少——
    /// 靠主键 UPSERT 的老写法在这里会把多出来的那行永久留下。
    /// </summary>
    [Fact]
    public async Task 席位数变少_旧行跟着消失()
    {
        await NewTask(new FakeFetcher { SeatsPerSide = 5 }).RunAsync(Backfill(), CancellationToken.None);
        Assert.Equal(10, RowsOn(Days[0]));

        await NewTask(new FakeFetcher { SeatsPerSide = 3 }).RunAsync(Backfill(), CancellationToken.None);

        Assert.Equal(6, RowsOn(Days[0]));
    }

    [Fact]
    public async Task 增量_从水位线往前回看()
    {
        await NewTask(new FakeFetcher()).RunAsync(Backfill(), CancellationToken.None);

        var f = new FakeFetcher();
        await NewTask(f).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        // 水位线是 9-11，往前回看 7 天 → 这五天全在窗口里，全部重抓（幂等，不会多行）
        Assert.Equal(Days.Length, f.Asked.Count);
        Assert.Equal(Days.Length * 10, _repo.Count());
    }

    /// <summary>seq 是同一张榜内的位次：同 (日期,代码,买卖,原因) 内必须 0..N-1 连续、不重复。</summary>
    [Fact]
    public async Task seq在同一张榜内连续且不重复()
    {
        await NewTask(new FakeFetcher { WithTwins = true }).RunAsync(Backfill(), CancellationToken.None);

        using var conn = new SqliteConnection($"Data Source={_paths.CurrentDb}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM (
              SELECT trade_date, code, is_buy, explanation,
                     COUNT(*) n, COUNT(DISTINCT seq) d, MIN(seq) mn, MAX(seq) mx
              FROM LhbSeat GROUP BY 1,2,3,4
              HAVING n <> d OR mn <> 0 OR mx <> n - 1);
            """;
        Assert.Equal(0, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    // ── 中断收尾的措辞 ────────────────────────────────────────────

    /// <summary>
    /// **「整段回补」中断时不能说"下次从水位线接着走"**——它压根不看水位线，每轮都从数据起点
    /// 重新排期，中断＝进度不保留；改用增量也接不上（只回看 7 天，够不着中间那段）。
    ///
    /// 这条是 2026-09-17 真把人误导过一次之后补的：用户看到那句话才放心停掉了跑到 459/2603 的回补。
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
        // 先把五天抓满，再把中间那天削掉一半——历史上"只抓到一半"的天就长这样
        await NewTask(new FakeFetcher()).RunAsync(Backfill(), CancellationToken.None);
        DeleteRows(Days[2], keep: 2);
        Todo(Days[2]);

        var f = new FakeFetcher();
        var r = await NewTask(f).RunAsync(
            new TaskRunArgs(FetchMode.FillBacklog), CancellationToken.None);

        Assert.Equal([Days[2]], f.Asked);            // ← 不是五天：没走水位线那条路
        Assert.Equal(10, RowsOn(Days[2]));           // 整日替换把那天补回来了
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
        // 「没有待办」是**没活可干**，不是「没开工」：2026-09-21 起返回 NothingToDo。
        // 写成 Skipped 的话计划引擎会立刻再排一次，而条件根本不会变，空转到被护栏拦下。
        Assert.True(r.NothingToDo);
        Assert.Null(r.SkippedReason);
    }

    /// <summary>抓不动的时候整项判失败——名单和 Tries 由 PartialDayRepair 原样留着。</summary>
    [Fact]
    public async Task 只补待办_那天抓不动_整项失败()
    {
        await NewTask(new FakeFetcher()).RunAsync(Backfill(), CancellationToken.None);
        DeleteRows(Days[2], keep: 2);
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
        m.SetTodo(RetryTaskIds.LhbSeat, RetryTodoKind.PartialDay,
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
            "DELETE FROM LhbSeat WHERE trade_date = $d AND rowid NOT IN "
            + "(SELECT rowid FROM LhbSeat WHERE trade_date = $d LIMIT $k);";
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$k", keep);
        cmd.ExecuteNonQuery();
    }
}
