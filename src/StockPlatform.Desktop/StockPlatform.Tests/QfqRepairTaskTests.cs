using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
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
/// 【重取前复权】迁到新任务框架之后的行为（2026-09-22，见 <see cref="QfqRepairTask"/> 的类注释）。
///
/// 盯的是这次迁移**唯一要修的那件事**和它的两条边：
/// ① <b>每批存完就划账</b>。老实现的划账在 <c>Task.WhenAll</c> 之后、取消直接冒泡，
///    于是被停止时 <c>PendingQfqRepairCodes</c> 一个都不更新——**已经按新基准重写完的票
///    下一轮全部重抓一遍**。这条用 <see cref="TaskRunArgs.MaxItems"/> 截断来验证：
///    做完的必须从名单上消失，没做的必须还在。
/// ② <b>失败的票留在名单上</b>。这一项不写失败名单（名单本身就是待办），所以"失败"的表达
///    方式只有一个：不划掉。划掉了就是静默丢账——那只票的接缝再也没人修。
/// ③ <b>整段覆盖</b>。走默认那条路的话，库里已有的行会被判成"值也对得上"而原样跳过，
///    等于白抓一轮——而这一项的全部意义就是抹掉旧基准留下的接缝。
///
/// 用真 SQLite + 离线模拟源（<see cref="MockBarFetcher"/>）跑整条链：一个请求都不发，
/// 但走的是产品代码的落库路径（<c>BarWritePlanner</c> → <c>SqliteBarUpsert</c>）。
/// 窗口判据本身在 <see cref="QfqRepairPlannerTests"/>。
/// </summary>
public class QfqRepairTaskTests : IDisposable
{
    private readonly string _tmp;
    private readonly FetchPaths _paths;
    private readonly SqliteBarRepository _bars;
    private readonly IManifestStore _manifest;

    /// <summary>旧基准下的收盘价。模拟源一律给 <see cref="MockBarFetcher.Price"/>，所以两者必须不同。</summary>
    private const double StaleClose = 7.77;

    public QfqRepairTaskTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"qfqTask_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
        _paths = new FetchPaths(_tmp);
        _bars = new SqliteBarRepository(_paths.CurrentDb);
        _bars.EnsureSchema();
        _manifest = new JsonManifestStore(_paths.ManifestPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmp, recursive: true); } catch { /* 临时目录 */ }
    }

    // ─────────────────── 造数据 ───────────────────

    /// <summary>
    /// 给这只票铺一段**旧基准**的前复权日线，并把它排进待重取名单。
    /// 起点定在 10 天前——只要早于"今天"，窗口就必然覆盖到已有的这几根。
    /// </summary>
    private void Pending(string code, int days = 10)
    {
        var rows = new List<Bar>();
        for (int i = days; i >= 1; i--)
        {
            var d = DateTime.Today.AddDays(-i);
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            rows.Add(new Bar
            {
                Code = code, Granularity = Granularity.Day, PeriodStart = d,
                Open = StaleClose, Close = StaleClose, High = StaleClose, Low = StaleClose,
                Volume = 100, Amount = 100 * StaleClose * 100, Turnover = 1.5,
                FetchedAt = d.AddHours(20),
            });
        }
        _bars.InsertOrRefreshUnconfirmed(rows);

        var m = _manifest.Load();
        m.PendingQfqRepairCodes = m.PendingQfqRepairCodes.Append(code)
            .Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        _manifest.Save(m);
    }

    private List<string> PendingNow() => _manifest.Load().PendingQfqRepairCodes;

    private List<Bar> Bars(string code) => _bars.Query(code, Granularity.Day);

    private async Task<(TaskRunResult Result, List<string> Log)> RunAsync(
        int batchSize = 1, int? maxItems = null)
    {
        var source = new NamedBarSource(
            "Mock", new MockBarFetcher(), new MockStockListProvider(() => []));
        var task = new QfqRepairTask(_paths, new BarSourceHolder(source), _manifest, batchSize);
        var log = new List<string>();
        task.OnProgress += p => log.Add(p.Text);
        var result = await task.RunAsync(new TaskRunArgs(MaxItems: maxItems), CancellationToken.None);
        return (result, log);
    }

    // ─────────────────── ① 分批划账 ───────────────────

    [Fact]
    public async Task 名单空_一个请求都不发也不算失败()
    {
        var (result, log) = await RunAsync();

        Assert.Equal(TaskState.Completed, result.State);
        Assert.True(result.NothingToDo);
        Assert.Contains(log, l => l.Contains("名单是空的"));
    }

    [Fact]
    public async Task 全部跑完_名单清空()
    {
        Pending("600000");
        Pending("600519");

        var (result, _) = await RunAsync();

        Assert.Equal(TaskState.Completed, result.State);
        Assert.Empty(PendingNow());
    }

    [Fact]
    public async Task 做满批数就收尾_做完的划掉没做的留着()
    {
        // 这是这次迁移的核心用例：老实现在这种"只做一部分"的轮次里名单一个都不更新，
        // 于是 600000 下一轮会再被整段重抓一遍（每只十年多页、约 4 秒）。
        Pending("600000");
        Pending("600519");
        Pending("601318");

        var (result, log) = await RunAsync(batchSize: 1, maxItems: 1);

        Assert.Equal(TaskState.Completed, result.State);
        Assert.Contains(log, l => l.Contains("本轮已做满"));

        // 名单按代码排序，所以做掉的必然是第一只
        Assert.Equal(["600519", "601318"], PendingNow());
        Assert.All(Bars("600000"), b => Assert.Equal(MockBarFetcher.Price, b.Close));
        // 没轮到的一根都没动——还是旧基准的值
        Assert.All(Bars("601318"), b => Assert.Equal(StaleClose, b.Close));
    }

    [Fact]
    public async Task 一批里做了两只_两只一起划掉()
    {
        Pending("600000");
        Pending("600519");
        Pending("601318");

        // 一批 2 只、只做 1 批 ⇒ 划掉 2 只
        await RunAsync(batchSize: 2, maxItems: 1);

        Assert.Equal(["601318"], PendingNow());
    }

    // ─────────────────── ② 失败的票留在名单上 ───────────────────

    [Fact]
    public async Task 抓失败的票不划掉_下轮还能重来()
    {
        // 模拟源约定：代码里含 999 就抛异常。
        Pending("600000");
        Pending("999001");

        var (result, _) = await RunAsync();

        Assert.Equal(TaskState.Completed, result.State);      // 一只成了就不算整项失败
        Assert.Equal(["999001"], PendingNow());
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task 全军覆没_判失败且名单一只不少()
    {
        Pending("999001");
        Pending("999002");

        var (result, _) = await RunAsync();

        Assert.Equal(TaskState.Failed, result.State);
        Assert.Equal(["999001", "999002"], PendingNow());
    }

    // ─────────────────── ③ 整段覆盖 ───────────────────

    [Fact]
    public async Task 已有的行按新基准整段覆盖_不是原样跳过()
    {
        Pending("600000");
        var before = Bars("600000");
        Assert.NotEmpty(before);
        Assert.All(before, b => Assert.Equal(StaleClose, b.Close));

        await RunAsync();

        var after = Bars("600000");
        Assert.NotEmpty(after);
        // 每一根都换成了数据源当前基准的值——这正是"抹平接缝"的定义。
        // 走默认那条路（overwrite:false）的话它们会被判成"库里已有、值也对得上"而原样留着。
        Assert.All(after, b => Assert.Equal(MockBarFetcher.Price, b.Close));
    }

    [Fact]
    public async Task 被停止_已落库的批算数且名单已更新()
    {
        Pending("600000");
        Pending("600519");
        Pending("601318");

        var source = new NamedBarSource(
            "Mock", new MockBarFetcher(), new MockStockListProvider(() => []));
        var task = new QfqRepairTask(_paths, new BarSourceHolder(source), _manifest, batchSize: 1);
        using var cts = new CancellationTokenSource();
        var log = new List<string>();
        // 在**第二批抓完**时取消：那会儿第一批已经落库并划过账了，而第二批会被骨架
        // 下一次 ct 检查拦在落库之前。第一次"抓取中"就取消的话第一批还没存
        // （骨架是先 yield、再 SaveBatchAsync），测的就不是"已落库的批算不算数"了。
        int seen = 0;
        task.OnProgress += p =>
        {
            log.Add(p.Text);
            if (p.Text.Contains("抓取中") && ++seen == 2) cts.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => task.RunAsync(new TaskRunArgs(), cts.Token));

        // 取消也不许丢账：第一只已经重写完，名单上不能再有它
        Assert.Equal(["600519", "601318"], PendingNow());
        Assert.All(Bars("600000"), b => Assert.Equal(MockBarFetcher.Price, b.Close));
        // 抓回来了但没来得及落库的那只，库里仍是旧基准——不丢也不半写
        Assert.All(Bars("600519"), b => Assert.Equal(StaleClose, b.Close));
        Assert.Contains(log, l => l.Contains("中断"));
    }
}
