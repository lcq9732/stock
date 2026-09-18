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
/// 【分红对账】（2026-09-18，见 doc/dividend-reconcile-design.md）——离线跑产品代码本身：
/// 真 SQLite、真仓储、真任务，只把东财那一侧换成本地假货。
///
/// 盯的三件事，每一件错了都会让复权序列出错：
///   ① **判重按除权日、不按主键**——两边的"公告日"语义不同，按主键插会让同一个方案变成两行，
///      库里凭空多出一次除权，比原来缺一条还糟；
///   ② **只加不删**——库里有而东财没有的（退市股占一多半）绝不能动；
///   ③ **幂等**——再跑一次不能重复补。
/// </summary>
public class DividendReconcileTaskTests : IDisposable
{
    private readonly string _dir;
    private readonly FetchPaths _paths;
    private readonly SqliteDividendRepository _repo;

    public DividendReconcileTaskTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"divRec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        _repo = new SqliteDividendRepository(_paths.CurrentDb);
        _repo.EnsureSchema();
    }

    // 不调 SqliteConnection.ClearAllPools()：那是进程级的，会误伤别的测试类（2026-09-18 踩过）。
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 还被连接占着就留着 */ }
    }

    private sealed class FakeSource(params DividendRow[] rows) : IDividendCrossCheckSource
    {
        public event Action<string>? OnStatus { add { } remove { } }
        public string SourceName => "假东财";

        public async IAsyncEnumerable<IReadOnlyList<DividendRow>> StreamImplementedAsync(
            int fromYear, int toYear,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            for (int y = fromYear; y <= toYear; y++)      // 按年切片，跟真源一样
                yield return rows.Where(r => r.ExDate!.Value.Year == y).ToList();
        }
    }

    private static DividendRow Row(string code, string announce, string ex,
                                   double div = 1.0, double bonus = 0, double transfer = 0) => new()
    {
        Code = code,
        AnnounceDate = DateTime.Parse(announce),
        ExDate = DateTime.Parse(ex),
        DividendYuan = div,
        BonusShares = bonus,
        TransferShares = transfer,
        Progress = "实施",
        FetchedAt = DateTime.Now,
        Source = "eastmoney",
    };

    private void Seed(string code, string announce, string ex)
        => _repo.ReplaceByCode(code, [new DividendRow
        {
            Code = code,
            AnnounceDate = DateTime.Parse(announce),
            ExDate = DateTime.Parse(ex),
            DividendYuan = 1.0,
            Progress = "实施",
            FetchedAt = DateTime.Now,
        }]);

    private Task<TaskRunResult> Run(params DividendRow[] emRows)
        => new DividendReconcileTask(_paths, _repo, new FakeSource(emRows))
            .RunAsync(new TaskRunArgs(), CancellationToken.None);

    [Fact]
    public async Task 东财有库里没有的就补进来()
    {
        await Run(Row("600000", "2025-03-01", "2025-06-10", div: 2.5));

        var got = _repo.GetByCode("600000");
        Assert.Single(got);
        Assert.Equal(new DateTime(2025, 6, 10), got[0].ExDate);
        Assert.Equal("eastmoney", got[0].Source);      // 来源可追溯
    }

    [Fact]
    public async Task 除权日库里已有就不补_哪怕公告日对不上()
    {
        // 这是最要命的一条：新浪的"公告日期"和东财的"预案公告日"语义不同，
        // 按主键判重会让同一个方案变成两行 ⇒ 库里凭空多出一次除权。
        Seed("600000", "2025-04-20", "2025-06-10");     // 库里：公告日 4-20
        await Run(Row("600000", "2025-03-01", "2025-06-10"));   // 东财：同一次除权，公告日 3-01

        Assert.Single(_repo.GetByCode("600000"));       // 还是一条，没被插成两条
    }

    [Fact]
    public async Task 除权日差几天的算同一条()
    {
        // 两边对除权日的记法偶有一两天出入（首次对账实测 10 条）。
        Seed("600000", "2025-04-20", "2025-06-10");
        await Run(Row("600000", "2025-03-01", "2025-06-12"));

        Assert.Single(_repo.GetByCode("600000"));
    }

    [Fact]
    public async Task 库里有东财没有的一律不动()
    {
        // 退市股在东财那张表里全是空的（首次对账 2,516 条），要是"以东财为准"就全删了。
        Seed("600001", "2020-04-20", "2020-06-10");
        await Run(Row("600000", "2025-03-01", "2025-06-10"));

        Assert.Single(_repo.GetByCode("600001"));       // 原样还在
    }

    [Fact]
    public async Task 跑第二次不会重复补()
    {
        var em = Row("600000", "2025-03-01", "2025-06-10");
        await Run(em);
        await Run(em);

        Assert.Single(_repo.GetByCode("600000"));
    }

    [Fact]
    public async Task 同一年里除权日只差一两天的两条_只补一条()
    {
        // 东财自己也可能给出两条挨着的记录。不在本轮内累加"已补"的话，两条都会被插进去，
        // 等于自己造出一次重复除权。
        await Run(Row("600000", "2025-03-01", "2025-06-10"),
                  Row("600000", "2025-03-02", "2025-06-12"));

        Assert.Single(_repo.GetByCode("600000"));
    }

    [Fact]
    public async Task 公告日撞了主键_改用除权日插进去()
    {
        // 2026-09-18 实机踩到的：东财会给出"两条不同除权日、预案公告日相同"的记录。
        // 判重按除权日放行了，插入时主键 (code, announce_date) 撞上，
        // INSERT OR IGNORE 直接吞掉——于是这条缺口每轮被判成缺、每轮被吞、
        // 日志每轮报一次假的成功数，**永远收敛不了**（实机 139 条）。
        await Run(Row("600000", "2025-03-01", "2025-06-10"),
                  Row("600000", "2025-03-01", "2025-12-20"));   // 同一个公告日，两个除权日

        var got = _repo.GetByCode("600000");
        Assert.Equal(2, got.Count);                             // 两条都进来了
        Assert.Equal([new DateTime(2025, 6, 10), new DateTime(2025, 12, 20)],
                     got.Select(x => x.ExDate!.Value).OrderBy(d => d));
    }

    [Fact]
    public async Task 撞主键补进来的那条_下一轮不会再被判成缺()
    {
        // 收敛性：上一条要是只"补上了"却没真写进去，这一轮就会重来一遍。
        var rows = new[] { Row("600000", "2025-03-01", "2025-06-10"),
                           Row("600000", "2025-03-01", "2025-12-20") };
        await Run(rows);
        var second = await Run(rows);

        Assert.True(second.NothingToDo);
        Assert.Equal(2, _repo.GetByCode("600000").Count);
    }

    [Fact]
    public async Task 派息送转全为0的空记录不补()
    {
        // 东财标着"实施分配"但三项全 0，没有任何除权实质（理论跳空就是 0）。
        // 补进库只是噪音，还会把"有多少条落在 day_adj 区间内"抬高、让日志显得比实情严重
        // ——2026-09-18 正式实例首轮那句"36 条"里 29 条是这种，真有除权的只有 7 条。
        var r = await Run(Row("600000", "2025-03-01", "2025-06-10", div: 0, bonus: 0, transfer: 0));

        Assert.Empty(_repo.GetByCode("600000"));
        Assert.True(r.NothingToDo);
    }

    [Fact]
    public async Task 只要有一项不为0就照补()
    {
        await Run(Row("600000", "2025-03-01", "2025-06-10", div: 0, bonus: 0, transfer: 5));

        Assert.Single(_repo.GetByCode("600000"));
    }

    [Fact]
    public async Task 一条都不缺时报无事可做()
    {
        Seed("600000", "2025-04-20", "2025-06-10");
        var r = await Run(Row("600000", "2025-03-01", "2025-06-10"));

        Assert.True(r.NothingToDo);
    }

    [Fact]
    public async Task 补进来的行进得了股息率口径()
    {
        // 补的目的是让复权和股息率算对，所以补完必须被现有的读取口径认出来
        // （progress='实施' + 除权日在窗口内）。
        var ex = DateTime.Today.AddDays(-30);
        await Run(Row("600000", ex.AddMonths(-2).ToString("yyyy-MM-dd"),
                      ex.ToString("yyyy-MM-dd"), div: 5.0));

        var perShare = _repo.GetTrailingCashDividendPerShare("600000", DateTime.Today.AddYears(-1));
        Assert.Equal(0.5, perShare, 3);        // 每10股派 5 元 ⇒ 每股 0.5
    }
}
