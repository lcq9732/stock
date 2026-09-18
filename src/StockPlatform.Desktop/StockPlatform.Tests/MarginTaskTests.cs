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
/// 【融资余额】任务的编排（2026-09-18 从编排器迁到新框架，见 doc/margin-task-design.md）。
///
/// 这一组盯的是**两条最容易在重构里被"简化"掉的规矩**，其余（四道闸本身、残缺日编排）
/// 已经分别由 <see cref="DailyBackfillGateTests"/> 和 <see cref="PartialDayRepairTests"/> 盯着：
///
/// ① **最近 5 个交易日无条件重抓**——它看起来像多余的重复劳动。丢了它，两所分批发布的
///    残缺会被永久固化（"有行就跳过"，那半天再也补不回来）。
/// ② **空日定案要等 3 天**——两所 T+1，当天拿到 0 行多半只是还没发。写成"0 行就定案"
///    的话那天会被永久钉死，往后一个请求都不再发。
/// </summary>
public class MarginTaskTests : IDisposable
{
    private readonly string _dir;
    private readonly FetchPaths _paths;
    private readonly SqliteMarginRepository _repo;
    private readonly SqliteTradingDayRepository _cal;
    private readonly SqliteDailyFetchNoDataRepository _noData;
    private readonly JsonManifestStore _manifest;

    /// <summary>今天往前数的交易日（周一~周五），最后一个就是"今天"。</summary>
    private static readonly DateOnly[] Days = MakeDays();

    private static DateOnly[] MakeDays()
    {
        // 以今天为终点往前铺 12 个工作日——"最近 5 个交易日"和"3 天前才定案"都是相对今天算的，
        // 写死日期的话这组测试过几天就会自己红。
        var list = new List<DateOnly>();
        var d = DateOnly.FromDateTime(DateTime.Today);
        while (list.Count < 12)
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) list.Add(d);
            d = d.AddDays(-1);
        }
        list.Reverse();
        return [.. list];
    }

    private static DateOnly Today => Days[^1];

    public MarginTaskTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"marginTask_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        _repo = new SqliteMarginRepository(_paths.CurrentDb);
        _repo.EnsureSchema();
        _cal = new SqliteTradingDayRepository(_paths.CurrentDb);
        _cal.EnsureSchema();
        _cal.Upsert(Days.Select(d => (d, ITradingDayRepository.SzseSource)));
        _noData = new SqliteDailyFetchNoDataRepository(_paths.CurrentDb);
        _noData.EnsureSchema();
        _manifest = new JsonManifestStore(_paths.ManifestPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录 */ }
    }

    // ── 假数据源 ──────────────────────────────────────────────────

    private sealed class FakeProvider : IMarginProvider
    {
        public event Action<string>? OnStatus;
        public readonly List<DateOnly> Asked = [];
        /// <summary>这些天返回 0 行（数据源还没发/确实没有）。</summary>
        public HashSet<DateOnly> Empty = [];
        public HashSet<DateOnly> Throws = [];
        public int RowsPerDay = 3;

        public DateOnly EarliestAvailable => Days[0];

        public Task<List<MarginDetailRow>> GetDetailAsync(DateOnly date, CancellationToken ct = default)
        {
            OnStatus?.Invoke("");
            Asked.Add(date);
            if (Throws.Contains(date)) throw new HttpRequestException("连不上");
            if (Empty.Contains(date)) return Task.FromResult(new List<MarginDetailRow>());
            return Task.FromResult(Enumerable.Range(0, RowsPerDay).Select(i => new MarginDetailRow
            {
                TradeDate = date.ToDateTime(TimeOnly.MinValue),
                Code = $"60000{i}",
                MarginBalance = 1000 + i,
            }).ToList());
        }
    }

    private MarginTask NewTask(FakeProvider p) =>
        new(_repo, p, _cal, _manifest, _paths, _noData);

    private int RowsOn(DateOnly day)
    {
        using var conn = new SqliteConnection($"Data Source={_paths.CurrentDb}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM MarginDetail WHERE trade_date = $d;";
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static TaskRunArgs Incremental() => new(FetchMode.Incremental);

    // ── 排期 ──────────────────────────────────────────────────────

    [Fact]
    public async Task 增量_只问交易日历里的日子()
    {
        var p = new FakeProvider();
        var r = await NewTask(p).RunAsync(Incremental(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.All(p.Asked, d => Assert.Contains(d, Days));
        Assert.DoesNotContain(p.Asked, d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    /// <summary>
    /// **这一组最要紧的一条**：最近 5 个交易日哪怕本地已有也要重抓。
    ///
    /// 两所分批发布，早抓到的可能只是沪市那一半；"有行就跳过"会把这个残缺状态**永久固化**。
    /// </summary>
    [Fact]
    public async Task 最近五个交易日_本地已有也重抓()
    {
        // 先跑一轮，本地就都有了
        await NewTask(new FakeProvider()).RunAsync(Incremental(), CancellationToken.None);

        var p = new FakeProvider();
        await NewTask(p).RunAsync(Incremental(), CancellationToken.None);

        // 最近 5 个交易日全部被重抓
        foreach (var d in Days[^5..]) Assert.Contains(d, p.Asked);
        // 更早的那些被"本地已有"闸挡掉了
        Assert.DoesNotContain(Days[^6], p.Asked);
    }

    [Fact]
    public async Task 只抓某一天_绕开本地已有()
    {
        await NewTask(new FakeProvider()).RunAsync(Incremental(), CancellationToken.None);

        var day = Days[0];   // 已经抓过、且早得挡在回看窗口之外
        var p = new FakeProvider();
        await NewTask(p).RunAsync(new TaskRunArgs(FetchMode.SpecificDay, Day: day), CancellationToken.None);

        Assert.Equal([day], p.Asked);
    }

    [Fact]
    public async Task MaxItems_只抓那么多天()
    {
        var p = new FakeProvider();
        await NewTask(p).RunAsync(
            new TaskRunArgs(FetchMode.FirstBackfill, MaxItems: 2), CancellationToken.None);

        Assert.Equal(2, p.Asked.Count);
    }

    // ── 空日名单 ──────────────────────────────────────────────────

    /// <summary>够旧的空才定案。</summary>
    [Fact]
    public async Task 三天前的空_定案()
    {
        var old = Days[0];
        var p = new FakeProvider { Empty = [.. Days] };
        await NewTask(p).RunAsync(new TaskRunArgs(FetchMode.FirstBackfill), CancellationToken.None);

        Assert.Contains(old, _noData.GetConfirmed(IDailyFetchNoDataRepository.MarginDataset));
    }

    /// <summary>
    /// **今天的空不定案**——两所 T+1，当天拿到 0 行多半只是还没发布。
    /// 定了案那天就被永久钉死了（名单是一次定案的，闸③直接跳过）。
    /// </summary>
    [Fact]
    public async Task 今天的空_不定案()
    {
        var p = new FakeProvider { Empty = [.. Days] };
        await NewTask(p).RunAsync(new TaskRunArgs(FetchMode.FirstBackfill), CancellationToken.None);

        Assert.DoesNotContain(Today, _noData.GetConfirmed(IDailyFetchNoDataRepository.MarginDataset));
    }

    /// <summary>源后来把数据补上了 → 撤销之前的结论。</summary>
    [Fact]
    public async Task 后来有数据了_撤销结论()
    {
        var day = Days[0];
        _noData.Confirm(IDailyFetchNoDataRepository.MarginDataset, day);

        // 名单里的日子平时被闸③挡掉，人点名那天才会真去抓
        var p = new FakeProvider();
        await NewTask(p).RunAsync(new TaskRunArgs(FetchMode.SpecificDay, Day: day), CancellationToken.None);

        Assert.DoesNotContain(day, _noData.GetConfirmed(IDailyFetchNoDataRepository.MarginDataset));
        Assert.Equal(3, RowsOn(day));
    }

    // ── 落库与结局 ────────────────────────────────────────────────

    /// <summary>
    /// 合并落库、不是整日替换——跟龙虎榜/席位/大宗那三张表**正好相反**：
    /// 两所分批发布，抓到半天也该写，主键去重、下轮补另一半。
    /// </summary>
    [Fact]
    public async Task 反复跑_行数不翻倍()
    {
        await NewTask(new FakeProvider()).RunAsync(new TaskRunArgs(FetchMode.FirstBackfill), CancellationToken.None);
        int before = RowsOn(Days[0]);
        await NewTask(new FakeProvider()).RunAsync(new TaskRunArgs(FetchMode.FirstBackfill), CancellationToken.None);

        Assert.Equal(before, RowsOn(Days[0]));
    }

    [Fact]
    public async Task 整轮都抓不动_判失败()
    {
        var p = new FakeProvider { Throws = [.. Days] };
        var r = await NewTask(p).RunAsync(new TaskRunArgs(FetchMode.FirstBackfill), CancellationToken.None);

        Assert.Equal(TaskState.Failed, r.State);
    }

    /// <summary>一天失败不拖垮整轮：其余的照抓照落库。</summary>
    [Fact]
    public async Task 单天失败_不影响其余()
    {
        var p = new FakeProvider { Throws = [Days[1]] };
        var r = await NewTask(p).RunAsync(new TaskRunArgs(FetchMode.FirstBackfill), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.Equal(0, RowsOn(Days[1]));
        Assert.Equal(3, RowsOn(Days[2]));
    }

    // ── 只补待办 ──────────────────────────────────────────────────

    /// <summary>目标从待办清单来，不从水位线来。编排本体在共用的 PartialDayRepair。</summary>
    [Fact]
    public async Task 只补待办_只抓待办里的那一天()
    {
        await NewTask(new FakeProvider()).RunAsync(new TaskRunArgs(FetchMode.FirstBackfill), CancellationToken.None);
        var day = Days[2];
        var m = _manifest.Load();
        m.SetTodo(RetryTaskIds.Margin, RetryTodoKind.PartialDay,
                  [new RetryTarget { Day = day.ToDateTime(TimeOnly.MinValue), Tries = 0 }]);
        _manifest.Save(m);

        var p = new FakeProvider();
        var r = await NewTask(p).RunAsync(new TaskRunArgs(FetchMode.FillBacklog), CancellationToken.None);

        Assert.Equal([day], p.Asked);
        Assert.Equal(TaskState.Completed, r.State);
    }

    [Fact]
    public async Task 只补待办_没有待办就跳过()
    {
        var p = new FakeProvider();
        var r = await NewTask(p).RunAsync(new TaskRunArgs(FetchMode.FillBacklog), CancellationToken.None);

        Assert.Empty(p.Asked);
        Assert.NotNull(r.SkippedReason);
    }
}
