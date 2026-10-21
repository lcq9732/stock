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
/// 【拉取财务报表】挪进日更、去掉每轮上限后的报错口径（2026-10-21 用户定）：
/// 抓到的立即入库；没抓到的只报只数、不挡后面的任务；手动【执行】或等明天日更都会再取。
///
/// 钉三件事：
///   ① 单只失败不影响前后——前后的照常入库，失败的**不能**记成"问过了"（否则进冷却、明天不取）；
///   ② 连续 10 只失败（数据源不通）就收工、记失败——没上限之后不收工会在限速器熔断里一等几小时；
///   ③ 已披露、数据源还没有这一期：不算错，明天再取；拖过 3 天才报错。
/// </summary>
public class FinancialTaskErrorTests : IDisposable
{
    private readonly string _dir;
    private readonly FetchPaths _paths;
    private readonly List<string> _codes = Enumerable.Range(1, 25).Select(i => $"{600000 + i:000000}").ToList();
    private static readonly DateTime H1 = new(2026, 6, 30);
    private static readonly DateTime Q3 = new(2026, 9, 30);

    public FinancialTaskErrorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"finErr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        new SqliteFinancialRepository(_paths.CurrentDb).EnsureSchema();
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, _codes.Select(c => (c, c)), "stock");
        var bars = new SqliteBarRepository(_paths.CurrentDb);
        bars.EnsureSchema();
        bars.InsertOrRefreshUnconfirmed([new Bar
        {
            Code = "sh000001", Granularity = Granularity.Day, PeriodStart = DateTime.Today,
            Open = 10, Close = 10, High = 10, Low = 10, Volume = 1, Amount = 10, FetchedAt = DateTime.Now,
        }]);
    }

    public void Dispose()
    {
        // 不调 SqliteConnection.ClearAllPools()——那是进程级的，会崩掉别的测试类
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录 */ }
    }

    /// <summary>按规则失败/少给一期的假数据源。</summary>
    private sealed class FakeProvider(Func<string, bool> fails, DateTime latest) : IFinancialProvider
    {
        public event Action<string>? OnStatus;
        public int Calls;

        public Task<List<FinancialValue>> GetAllAsync(string code, CancellationToken ct = default)
        {
            Calls++;
            OnStatus?.Invoke("");
            if (fails(code)) throw new HttpRequestException($"假的：{code} 取不到");
            return Task.FromResult<List<FinancialValue>>(
                [new FinancialValue { Code = code, ReportDate = latest, Key = FinancialKeys.NetProfitParent, Value = 1 }]);
        }
    }

    private Task<TaskRunResult> Run(FakeProvider f)
        => new FinancialTask(_paths, f).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

    private Dictionary<string, FinancialFetchState> States()
        => new SqliteFinancialRepository(_paths.CurrentDb).GetFetchStateByCode();

    [Fact]
    public async Task 单只失败_前后照常入库_只报只数_下一轮还会再取()
    {
        var bad = _codes[1];
        var r = await Run(new FakeProvider(c => c == bad, H1));

        Assert.Equal(TaskState.Completed, r.State);          // 不是整项失败：后面的任务照跑
        Assert.Single(r.Errors);
        Assert.Contains("1 只没取到", r.Progress);
        Assert.DoesNotContain(bad, r.Progress);                // 汇总只报只数，不列代码

        var st = States();
        Assert.Equal(24, st.Count);
        Assert.False(st.ContainsKey(bad));                     // 没记"问过了"
        Assert.Contains(bad, new FinancialFetchPlanner(_paths).Plan().AllPending);
    }

    [Fact]
    public async Task 连续十只失败_提前收工记失败_报剩余只数()
    {
        var f = new FakeProvider(_ => true, H1);
        var r = await Run(f);

        Assert.Equal(TaskState.Failed, r.State);
        Assert.Equal(FinancialTask.AbortAfterConsecutiveFailures, f.Calls);
        Assert.Contains("还剩 15 只", r.Errors[0]);
    }

    [Fact]
    public async Task 中间夹着成功的_不算连续失败()
    {
        // 每隔一只失败一次：一共 13 只失败，但从没连着 10 只
        var f = new FakeProvider(c => int.Parse(c) % 2 == 1, H1);
        var r = await Run(f);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.Equal(25, f.Calls);
        Assert.Contains("13 只没取到", r.Progress);
    }

    private void Disclosed(string code, DateTime period, DateTime actual)
    {
        var repo = new SqliteEarningsScheduleRepository(_paths.CurrentDb);
        repo.EnsureSchema();
        repo.Upsert([new EarningsScheduleRow(code, period, actual, null, null, null, actual)]);
    }

    [Fact]
    public async Task 已披露数据源还没更新_不算错_拖过三天才报错()
    {
        Disclosed(_codes[0], Q3, DateTime.Today);             // 今天刚披露
        Disclosed(_codes[1], Q3, DateTime.Today.AddDays(-5)); // 五天前披露的
        var r = await Run(new FakeProvider(_ => false, H1)); // 数据源只给到中报

        Assert.Equal(TaskState.Completed, r.State);
        Assert.Contains("1 只已披露、数据源还没有这一期（明天再取）", r.Progress);
        Assert.Contains(r.Errors, e => e.Contains("1 只披露已超过 3 天"));
        // 照常入库（老期次可能有修订），状态里记着冲的是哪期，今天不再问
        Assert.Equal(Q3, States()[_codes[0]].TargetDate);
        Assert.DoesNotContain(_codes[0], new FinancialFetchPlanner(_paths).Plan().AllPending);
    }
}
