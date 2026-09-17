using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【当日完整性体检】迁到新任务框架之后的行为（2026-09-17）。
///
/// 判据本身在 <see cref="DayCompletenessTests"/>（那边直接测 auditor），这一组只盯迁移带来的三件事：
/// ① **一段一批落账**——K线那份名单先写进 manifest，后面两段扫得再久也不影响它；
/// ② **全齐时要报 NothingToDo**，别让计划以为每天都有活干；
/// ③ **不齐不算这一项失败**——体检干成了它该干的活（查出来、记下来），
///    该红的是【重新拉取失败】和日志里点名的那几项，不是体检自己。
/// </summary>
public class DayCompletenessTaskTests : IDisposable
{
    private readonly string _dir;
    private readonly FetchPaths _paths;
    private readonly SqliteBarRepository _bars;
    private readonly JsonManifestStore _manifest;

    private const string Anchor = MarketIndexCatalog.ShanghaiCompositeSymbol;

    /// <summary>用"今天往前数"的日子：体检的交易日锚只看最近 60 天，写死日期过一阵就失效了。</summary>
    private static readonly DateTime[] Days =
    [
        DateTime.Today.AddDays(-4), DateTime.Today.AddDays(-3),
        DateTime.Today.AddDays(-2), DateTime.Today.AddDays(-1),
    ];

    private static DateTime Latest => Days[^1];
    private static DateTime Previous => Days[^2];

    public DayCompletenessTaskTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"dayTask_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        _bars = new SqliteBarRepository(_paths.CurrentDb);
        _bars.EnsureSchema();
        _manifest = new JsonManifestStore(_paths.ManifestPath);

        // 交易日锚：上证指数，每天都有
        InsertBars(Anchor, Days);
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, [(Anchor, "上证指数")], SqliteStockMetaUpsert.TypeIndex);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录 */ }
    }

    private void InsertBars(string code, IEnumerable<DateTime> days, string gran = Granularity.Day) =>
        _bars.InsertOrRefreshUnconfirmed(days.Select(d => new Bar
        {
            Code = code, Granularity = gran, PeriodStart = d,
            Open = 1, Close = 1, High = 1, Low = 1, Volume = 1, Amount = 1,
        }));

    /// <summary>登记一只标的，并按 <paramref name="hasLatest"/> 决定给不给它最新交易日那根。</summary>
    private void Seed(string code, string type, bool hasLatest)
    {
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, [(code, code)], type);
        var days = hasLatest ? Days : Days[..^1];
        InsertBars(code, days);
        if (type == SqliteStockMetaUpsert.TypeStock)
        {
            InsertBars(code, days, Granularity.DayHfq);
            InsertBars(code, days, Granularity.DayRaw);
        }
    }

    private async Task<TaskRunResult> RunAsync()
    {
        var task = new DayCompletenessTask(_paths, _manifest);
        return await task.RunAsync(new TaskRunArgs(), CancellationToken.None);
    }

    [Fact]
    public async Task 全齐时报NothingToDo()
    {
        Seed("600000", SqliteStockMetaUpsert.TypeStock, hasLatest: true);
        Seed("sh510300", SqliteStockMetaUpsert.TypeEtf, hasLatest: true);

        var result = await RunAsync();

        Assert.Equal(TaskState.Completed, result.State);
        Assert.True(result.NothingToDo);
        Assert.Contains("全齐", result.Progress);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task 缺了就记进各自任务的待办_而且不算失败()
    {
        Seed("600000", SqliteStockMetaUpsert.TypeStock, hasLatest: false);
        Seed("sh510300", SqliteStockMetaUpsert.TypeEtf, hasLatest: false);

        var result = await RunAsync();

        // 体检查出来了就是干完了活——红在这一行只会让人去重跑一个本来没错的任务
        Assert.Equal(TaskState.Completed, result.State);
        Assert.Empty(result.Errors);
        Assert.False(result.NothingToDo);
        Assert.Contains("不齐", result.Progress);

        var m = _manifest.Load();
        var stock = m.Todo(RetryTaskIds.StockDayBars, RetryTodoKind.MissingDay);
        var etf = m.Todo(RetryTaskIds.EtfBars, RetryTodoKind.MissingDay);
        Assert.Equal("600000", Assert.Single(stock!.Targets).Code);
        Assert.Equal("sh510300", Assert.Single(etf!.Targets).Code);
        Assert.Equal(Latest, stock.Day);
    }

    [Fact]
    public async Task K线那段先落账_不等后面两段扫完()
    {
        // 一批＝一段的意义就在这儿：前一段的待办先落盘，后面两段扫得再久（日更表要读几张大表）
        // 也不影响它。这条用"跑完后 manifest 里有 K线名单"来间接钉住——
        // 要是改成扫完三段再一次性写，中途停下来就什么都没记下。
        Seed("600000", SqliteStockMetaUpsert.TypeStock, hasLatest: false);

        var task = new DayCompletenessTask(_paths, _manifest);
        var batches = new List<int>();
        task.OnProgress += p => { if (p.Text.Contains("当日完整性体检：查")) batches.Add(1); };
        await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.NotNull(_manifest.Load().Todo(RetryTaskIds.StockDayBars, RetryTodoKind.MissingDay));
    }

    [Fact]
    public async Task 补齐之后再跑_名单要清空()
    {
        // 名单是**重建**不是累加：今天补上的票，下一轮体检就该从名单里消失。
        Seed("600000", SqliteStockMetaUpsert.TypeStock, hasLatest: false);
        await RunAsync();
        Assert.NotEmpty(_manifest.Load().Todo(RetryTaskIds.StockDayBars, RetryTodoKind.MissingDay)!.Targets);

        InsertBars("600000", [Latest]);
        InsertBars("600000", [Latest], Granularity.DayHfq);
        InsertBars("600000", [Latest], Granularity.DayRaw);
        var result = await RunAsync();

        Assert.True(result.NothingToDo);
        var todo = _manifest.Load().Todo(RetryTaskIds.StockDayBars, RetryTodoKind.MissingDay);
        Assert.True(todo == null || todo.Targets.Count == 0);
    }

    [Fact]
    public async Task 没有交易日锚时不下结论()
    {
        // 本地上证指数日线不足两根＝判不了。这时候"当天全齐"和"当天全缺"都是瞎说。
        var dir = Path.Combine(Path.GetTempPath(), $"dayTask_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "local"));
        var paths = new FetchPaths(dir);
        new SqliteBarRepository(paths.CurrentDb).EnsureSchema();

        var task = new DayCompletenessTask(paths, new JsonManifestStore(paths.ManifestPath));
        var seen = new List<string>();
        task.OnProgress += p => seen.Add(p.Text);
        var result = await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, result.State);
        Assert.True(result.NothingToDo);
        Assert.Contains(seen, s => s.Contains("没有交易日锚"));
        try { Directory.Delete(dir, recursive: true); } catch { /* 临时目录 */ }
    }

    [Fact]
    public void 这一项已经登记进新框架()
    {
        // 迁移的意义在这条：它得在 registry 里，MainViewModel 那个 switch 才走不到。
        // FetchTaskCatalogTests 守的是"目录里有"，这里守的是"任务类认这个 id"。
        Assert.Equal(FetchActionId.StepDayCoverage,
            new DayCompletenessTask(_paths, _manifest).Id);
    }
}
