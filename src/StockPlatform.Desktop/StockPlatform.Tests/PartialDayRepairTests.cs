using Microsoft.Data.Sqlite;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 残缺日补齐的**编排**（2026-09-17 从 <c>FetchOrchestrator.FillPartialDaysAsync</c> 抽成
/// <see cref="PartialDayRepair"/>，为的是让新框架的任务共用同一份）。
///
/// 判据本身在 <see cref="DailyTableAuditTests"/>，这一组只盯编排：
/// 待办怎么增减、Tries 怎么涨、什么时候进"确认就这些"名单、抓不动的时候动不动名单。
///
/// ⚠ 这里面最该锁住的是两条，都是重构里最容易被"简化"掉的：
/// ① **整批都失败＝连不上**，名单和 Tries 必须原样留着，不能白耗一轮；
/// ② **复查不是 <c>COUNT &gt; 0</c>**——残缺日本来就有行，补了一半仍算没补上。
/// </summary>
public class PartialDayRepairTests : IDisposable
{
    private readonly string _dir;
    private readonly FetchPaths _paths;
    private readonly JsonManifestStore _manifest;
    private readonly PartialDayRepair _repair;

    private const string Anchor = MarketIndexCatalog.ShanghaiCompositeSymbol;
    private const string TaskId = RetryTaskIds.Margin;

    /// <summary>被测表用融资余额——跟判据那组测试同一张表，ThinRatio 0.7。</summary>
    private static readonly DateTime[] Cal =
    [
        new(2026, 8, 24), new(2026, 8, 25), new(2026, 8, 26), new(2026, 8, 27), new(2026, 8, 28),
    ];

    /// <summary>被判残缺的那一天（前两天各 100 行当基准，它只有 5 行）。</summary>
    private static DateTime BadDay => Cal[2];

    public PartialDayRepairTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"pdr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);

        var bars = new SqliteBarRepository(_paths.CurrentDb);
        bars.EnsureSchema();
        bars.InsertOrRefreshUnconfirmed(Cal.Select(d => new Bar
        {
            Code = Anchor, Granularity = Granularity.Day, PeriodStart = d,
            Open = 1, Close = 1, High = 1, Low = 1, Volume = 1, Amount = 1,
        }));

        foreach (var d in Cal) InsertMargin(d, d == BadDay ? 5 : 100);

        _manifest = new JsonManifestStore(_paths.ManifestPath);
        _repair = new PartialDayRepair(_paths.CurrentDb, _manifest);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录 */ }
    }

    /// <summary>往 MarginDetail 塞某一天的若干行（代码随便编，体检只数行数）。</summary>
    private void InsertMargin(DateTime day, int rows)
    {
        using var conn = new SqliteConnection($"Data Source={_paths.CurrentDb}");
        conn.Open();
        using var tx = conn.BeginTransaction();
        for (int i = 0; i < rows; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT OR IGNORE INTO MarginDetail (trade_date, code, margin_balance) VALUES ($d, $c, 1);";
            cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$c", $"60{i:D4}");
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>把 BadDay 挂进待办，Tries 从 <paramref name="tries"/> 起。</summary>
    private void Pend(int tries = 0)
    {
        var m = _manifest.Load();
        m.SetTodo(TaskId, RetryTodoKind.PartialDay, [new RetryTarget { Day = BadDay, Tries = tries }]);
        _manifest.Save(m);
    }

    private List<RetryTarget> Pending() =>
        _manifest.Load().Todo(TaskId, RetryTodoKind.PartialDay)?.Targets.ToList() ?? [];

    private List<DateTime> Confirmed() =>
        _manifest.Load().ConfirmedPartialDays.TryGetValue(TaskId, out var d) ? d : [];

    [Fact]
    public async Task 补齐了_从待办里划掉()
    {
        Pend();

        var r = await _repair.RunAsync(TaskId, d =>
        {
            InsertMargin(d.ToDateTime(TimeOnly.MinValue), 100);
            return Task.FromResult(95);
        }, null, CancellationToken.None);

        Assert.NotNull(r);
        Assert.Equal(1, r!.Days);
        Assert.Equal(1, r.Fixed);
        Assert.Equal(95, r.Rows);
        Assert.Empty(Pending());
        Assert.Empty(Confirmed());
    }

    /// <summary>
    /// **复查不是 COUNT &gt; 0**：重抓确实写进去了行、那天也一直有行，但仍不到基准的 70%，
    /// 就还是残缺——不能被当成已补齐静默划掉。
    /// </summary>
    [Fact]
    public async Task 补了一半仍不齐_留在待办并且Tries加一()
    {
        Pend();

        var r = await _repair.RunAsync(TaskId, d =>
        {
            InsertMargin(d.ToDateTime(TimeOnly.MinValue), 50);   // 50 < 100 × 0.7
            return Task.FromResult(45);
        }, null, CancellationToken.None);

        Assert.Equal(0, r!.Fixed);
        var t = Assert.Single(Pending());
        Assert.Equal(BadDay, t.Day);
        Assert.Equal(1, t.Tries);
        Assert.Empty(Confirmed());
    }

    [Fact]
    public async Task 补满MaxTries仍不齐_判定数据源就这些()
    {
        Pend(tries: PartialDayRepair.MaxTries - 1);

        var r = await _repair.RunAsync(TaskId, _ => Task.FromResult(0), null, CancellationToken.None);

        Assert.Equal(1, r!.ConfirmedNow);
        Assert.Empty(Pending());
        Assert.Equal(BadDay, Assert.Single(Confirmed()));
    }

    /// <summary>
    /// 整批都失败多半是被限流/断网，不是"数据源没有"。这时候**名单和 Tries 一动都不能动**，
    /// 否则连不上网跑两轮就能把一天误判成"数据源确实没有"、以后再也不报。
    /// </summary>
    [Fact]
    public async Task 全部重抓失败_名单和Tries原样不动()
    {
        Pend(tries: 1);

        var r = await _repair.RunAsync(
            TaskId, _ => throw new HttpRequestException("连不上"), null, CancellationToken.None);

        Assert.Equal(1, r!.Failed);
        Assert.Equal(0, r.Fixed);
        var t = Assert.Single(Pending());
        Assert.Equal(BadDay, t.Day);
        Assert.Equal(1, t.Tries);           // 没有涨
        Assert.Empty(Confirmed());
    }

    [Fact]
    public async Task 没有按天重抓的入口_什么都不改()
    {
        Pend(tries: 1);

        var r = await _repair.RunAsync(TaskId, refetch: null, null, CancellationToken.None);

        Assert.True(r!.NoRefetcher);
        Assert.Equal(1, Assert.Single(Pending()).Tries);
        Assert.Empty(Confirmed());
    }

    [Fact]
    public async Task 没有待办_不干活()
    {
        Assert.Null(await _repair.RunAsync(TaskId, _ => Task.FromResult(1), null, CancellationToken.None));
    }

    /// <summary>这一项压根没进日频体检（没有 spec）——跟"没有待办"一样，安静返回。</summary>
    [Fact]
    public async Task 不在日频体检名单里的任务_不干活()
    {
        Assert.Null(await _repair.RunAsync(
            "StepStockDayBars", _ => Task.FromResult(1), null, CancellationToken.None));
    }

    [Fact]
    public async Task DaysOf_读出待办里的天()
    {
        Pend();
        Assert.Equal(DateOnly.FromDateTime(BadDay), Assert.Single(_repair.DaysOf(TaskId)));
    }
}
