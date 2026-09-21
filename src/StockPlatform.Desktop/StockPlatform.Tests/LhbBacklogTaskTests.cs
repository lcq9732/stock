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
/// 【龙虎榜】主表的【只补待办】那一路（2026-09-18 收口：编排从 <c>FetchOrchestrator</c>
/// 搬进任务，见 doc/fill-backlog-to-tasks-design.md）。
///
/// 编排本体是共用的 <see cref="PartialDayRepair"/>（复查判据、Tries、确认名单都在那儿，
/// 由 <see cref="PartialDayRepairTests"/> 盯着），这一组只盯**接线**：
/// 收到 <see cref="FetchMode.FillBacklog"/> 之后，抓的是不是待办里那一天、抓回来写没写回去。
///
/// ⚠ 这一条的价值在于"不走水位线"：目标从待办清单来，跑一整轮增量是补不上历史残缺日的。
/// </summary>
public class LhbBacklogTaskTests : IDisposable
{
    private readonly string _dir;
    private readonly FetchPaths _paths;
    private readonly SqliteLhbRepository _repo;
    private readonly SqliteTradingDayRepository _cal;
    private readonly JsonManifestStore _manifest;

    private static readonly DateTime[] Days =
    [
        new(2026, 9, 7), new(2026, 9, 8), new(2026, 9, 9), new(2026, 9, 10), new(2026, 9, 11),
    ];

    /// <summary>每天几行——削掉之后行数要看得出差别。</summary>
    private const int RowsPerDay = 4;

    public LhbBacklogTaskTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"lhbBacklog_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);

        _repo = new SqliteLhbRepository(_paths.CurrentDb);
        _repo.EnsureSchema();
        // 落库时 LhbDayWriter 要查日K派生对应值，表得在（没有K线就派生不出，但不报错）
        new SqliteBarRepository(_paths.CurrentDb).EnsureSchema();
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

    /// <summary>离线的"抓一天"。只实现 <see cref="ILhbProvider"/>——**不实现**按月切片那个接口，
    /// 于是任务走逐日路径，跟补待办时按天重抓是同一个动作。</summary>
    private sealed class FakeProvider : ILhbProvider
    {
        public event Action<string>? OnStatus;
        public readonly List<DateTime> Asked = [];
        public HashSet<DateTime> Throws = [];

        public DateOnly EarliestAvailable => DateOnly.FromDateTime(Days[0]);

        public Task<List<LhbRow>> GetDailyAsync(DateOnly date, CancellationToken ct = default)
        {
            OnStatus?.Invoke("");
            var day = date.ToDateTime(TimeOnly.MinValue);
            Asked.Add(day);
            if (Throws.Contains(day)) throw new HttpRequestException("连不上");

            return Task.FromResult(Enumerable.Range(0, RowsPerDay).Select(i => new LhbRow
            {
                TradeDate = day,
                StockCode = $"60010{i}",
                StockName = $"某某{i}",
                ClosePrice = 10 + i,
                Reason = "日涨幅偏离值达到7%的前5只证券",
                FetchedAt = DateTime.Now,
            }).ToList());
        }
    }

    private LhbTask NewTask(FakeProvider p) =>
        new(_paths.CurrentDb, _repo, p, _cal, _manifest);

    private int RowsOn(DateTime day)
    {
        using var conn = new SqliteConnection($"Data Source={_paths.CurrentDb}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Lhb WHERE trade_date = $d;";
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private List<DateTime> PendingDays() =>
        (_manifest.Load().Todo(RetryTaskIds.Lhb, RetryTodoKind.PartialDay)?.Targets ?? [])
            .Where(t => t.Day.HasValue).Select(t => t.Day!.Value.Date).OrderBy(d => d).ToList();

    private void Todo(DateTime day)
    {
        var m = _manifest.Load();
        m.SetTodo(RetryTaskIds.Lhb, RetryTodoKind.PartialDay,
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
            "DELETE FROM Lhb WHERE trade_date = $d AND rowid NOT IN "
            + "(SELECT rowid FROM Lhb WHERE trade_date = $d LIMIT $k);";
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$k", keep);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task 只补待办_只抓待办里的那一天()
    {
        await NewTask(new FakeProvider()).RunAsync(
            new TaskRunArgs(FetchMode.FirstBackfill), CancellationToken.None);
        DeleteRows(Days[2], keep: 1);
        Todo(Days[2]);

        var p = new FakeProvider();
        var r = await NewTask(p).RunAsync(
            new TaskRunArgs(FetchMode.FillBacklog), CancellationToken.None);

        Assert.Equal([Days[2]], p.Asked);            // ← 不是五天：没走水位线那条路
        Assert.Equal(RowsPerDay, RowsOn(Days[2]));   // 整天替换把那天补回来了
        Assert.Equal(TaskState.Completed, r.State);
    }

    /// <summary>没有欠着的天就别开工：报跳过、一个请求都不发。</summary>
    [Fact]
    public async Task 只补待办_没有待办就跳过()
    {
        var p = new FakeProvider();
        var r = await NewTask(p).RunAsync(
            new TaskRunArgs(FetchMode.FillBacklog), CancellationToken.None);

        Assert.Empty(p.Asked);
        // 「没有待办」是**没活可干**，不是「没开工」：2026-09-21 起返回 NothingToDo。
        // 写成 Skipped 的话计划引擎会立刻再排一次，而条件根本不会变，空转到被护栏拦下。
        Assert.True(r.NothingToDo);
        Assert.Null(r.SkippedReason);
    }

    /// <summary>抓不动的时候整项判失败——名单和 Tries 由 PartialDayRepair 原样留着。</summary>
    [Fact]
    public async Task 只补待办_那天抓不动_整项失败()
    {
        await NewTask(new FakeProvider()).RunAsync(
            new TaskRunArgs(FetchMode.FirstBackfill), CancellationToken.None);
        DeleteRows(Days[2], keep: 1);
        Todo(Days[2]);

        var p = new FakeProvider { Throws = [Days[2]] };
        var r = await NewTask(p).RunAsync(
            new TaskRunArgs(FetchMode.FillBacklog), CancellationToken.None);

        Assert.Equal(TaskState.Failed, r.State);
        Assert.Equal([Days[2]], PendingDays());      // 名单原样留着，下轮还来
    }
}
