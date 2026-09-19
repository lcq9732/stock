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
/// 【拉取财务报表】的心跳密度（2026-09-19）。
///
/// 这一项跟【资金净流入】踩的是同一个坑：日志每 20 只一行，而单只约 12~18 秒（接口降到
/// 约 10 请求/分钟、每只 3 个请求），20 只就是 4~6 分钟——正顶着静默看门狗的 5 分钟上限。
/// 至今没被掐过只是因为最近每轮只有 13 只（不足 20，只在收尾报一次）；哪天一轮抓满
/// 300 只就会在"一路正常抓"的状态下被判成卡死。
///
/// 所以这里钉的是：**每只票都要报一条进展**（喂看门狗），而日志密度不变（每 20 只 + 末只）。
/// 见 <see cref="QuietWatchdog.IBeatOnlySink"/>。
/// </summary>
public class FinancialTaskHeartbeatTests : IDisposable
{
    private readonly string _dir;
    private readonly FetchPaths _paths;

    public FinancialTaskHeartbeatTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"finBeat_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        new SqliteFinancialRepository(_paths.CurrentDb).EnsureSchema();

        // 25 只在市票，本地一期都没有 ⇒ 全部待抓
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb,
            Enumerable.Range(1, 25).Select(i => ($"{600000 + i:000000}", $"票{i}")), "stock");
        // 市场锚：判"一年没成交"要拿上证指数当基准，没有它会拿 Today 算（同 FinancialFetchPlannerTests）
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

    /// <summary>每只票都返回一行的假数据源。</summary>
    private sealed class FakeProvider : IFinancialProvider
    {
        public event Action<string>? OnStatus;
        public int Calls;

        public Task<List<FinancialValue>> GetAllAsync(string code, CancellationToken ct = default)
        {
            Calls++;
            OnStatus?.Invoke("");    // 真源也会偶尔吐一句，确认它不影响进展计数
            return Task.FromResult<List<FinancialValue>>(
            [
                new FinancialValue
                {
                    Code = code, ReportDate = new DateTime(2026, 6, 30),
                    Key = FinancialKeys.NetProfitParent, Value = 1,
                }
            ]);
        }
    }

    [Fact]
    public async Task 每只票都报进展_只有每二十只和末只写日志()
    {
        var f = new FakeProvider();
        var task = new FinancialTask(_paths, f);
        var seen = new List<TaskProgress>();
        task.OnProgress += p => { lock (seen) seen.Add(p); };

        var r = await task.RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.Equal(25, f.Calls);

        // 带进度数字的那些就是"抓完一只"的进展：25 只一条不落
        var steps = seen.Where(p => p.Done.HasValue && p.Total == 25).ToList();
        Assert.Equal(Enumerable.Range(1, 25), steps.Select(p => p.Done!.Value));

        // 日志密度不变：第 20 只、第 25 只（末只）落日志，其余只喂狗
        var logged = steps.Where(p => !p.Quiet).Select(p => p.Done!.Value).ToList();
        Assert.Equal([20, 25], logged);
    }
}
