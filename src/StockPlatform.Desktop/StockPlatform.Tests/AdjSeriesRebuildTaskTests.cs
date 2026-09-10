using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【重算回测序列】迁到新任务框架之后的行为（2026-09-10，见
/// <see cref="AdjSeriesRebuildTask"/> 的类注释）。
///
/// 盯的是迁移里**最容易写错、而且写错很安静**的三件事：
/// ① **增量 vs 整段重算的分岔**。选错了不报错：该整段的走了增量，接缝处凭空多一个假跳空；
///    该增量的走了整段，只是慢——1400 万行每天读写一遍。判据在
///    <c>AdjSeriesRebuildTask.TryIncremental</c>，三个条件缺一个就得整段重来。
/// ② **整段重算必须先删后写**。只 UPSERT 的话，"新序列比旧序列短"那一段的旧行会留在库里
///    （前面补过历史、起点变了就是这种情况），复权序列上多出一截用旧因子算的价格。
/// ③ **算不出来的票要产出空批**。框架跳过空批的 <c>SaveBatchAsync</c>，于是它们不占
///    <see cref="TaskRunArgs.MaxItems"/> 额度——这条 <c>FullAuditTask</c> 当初栽过两次
///    （反过来：以为空批会落账）。
///
/// 判据本身（哪些票要重算）在 <see cref="AdjSeriesRebuildCriteriaTests"/>，
/// 收益率自检在 <see cref="ReturnSelfCheckTests"/>，两边都没动。
/// </summary>
public class AdjSeriesRebuildTaskTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteBarRepository _bars;
    private readonly SqliteDividendRepository _divs;

    /// <summary>连续 5 个交易日，价格给成好认的数。</summary>
    private static readonly DateTime[] Days =
    [
        new(2026, 9, 1), new(2026, 9, 2), new(2026, 9, 3), new(2026, 9, 4), new(2026, 9, 7),
    ];

    public AdjSeriesRebuildTaskTests()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"adjTask_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        _dbPath = Path.Combine(tmp, "current.sqlite");
        _bars = new SqliteBarRepository(_dbPath);
        _bars.EnsureSchema();
        _divs = new SqliteDividendRepository(_dbPath);
        _divs.EnsureSchema();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true); } catch { /* 临时目录 */ }
    }

    // ─────────────────── 造数据 ───────────────────

    private void Put(string code, string gran, DateTime day, double close, DateTime? fetchedAt = null) =>
        _bars.InsertOrRefreshUnconfirmed([new Bar
        {
            Code = code, Granularity = gran, PeriodStart = day,
            Open = close, Close = close, High = close, Low = close,
            Volume = 100, Amount = 100 * close * 100, Turnover = 1.5,
            FetchedAt = fetchedAt ?? day.AddHours(20),
        }]);

    /// <summary>一段不复权日线（收盘价一律 10，方便看复权因子）。</summary>
    private void PutRaw(string code, params DateTime[] days)
    {
        foreach (var d in days) Put(code, Granularity.DayRaw, d, 10);
    }

    /// <summary>一段已算好的 day_adj。<paramref name="stamp"/> 是"上次重算的时刻"。</summary>
    private void PutAdj(string code, DateTime stamp, params DateTime[] days)
    {
        foreach (var d in days) Put(code, Granularity.DayAdj, d, 10, stamp);
    }

    private void PutDividend(string code, DateTime exDate, double per10Cash, DateTime? fetchedAt = null) =>
        _divs.ReplaceByCode(code,
        [
            new DividendRow
            {
                Code = code, AnnounceDate = exDate.AddDays(-10),
                BonusShares = 0, TransferShares = 0, DividendYuan = per10Cash,
                Progress = "实施", RecordDate = exDate.AddDays(-1), ExDate = exDate,
                FetchedAt = fetchedAt ?? exDate.AddHours(20),
            },
        ]);

    private List<Bar> Adj(string code) => _bars.Query(code, Granularity.DayAdj);

    private static AdjSeriesRebuildTask NewTask(string db) => new(db);

    private async Task<(TaskRunResult Result, List<string> Log)> RunAsync(
        FetchMode mode = FetchMode.Incremental, int? maxItems = null, DateTime? deadline = null)
    {
        var task = NewTask(_dbPath);
        var log = new List<string>();
        task.OnProgress += p => log.Add(p.Text);
        var result = await task.RunAsync(new TaskRunArgs(mode, MaxItems: maxItems, Deadline: deadline),
                                        CancellationToken.None);
        return (result, log);
    }

    // ─────────────────── ① 增量 vs 整段 ───────────────────

    [Fact]
    public async Task 压根没算过_整段重算并写满全历史()
    {
        PutRaw("600000", Days);

        var (result, log) = await RunAsync();

        Assert.Equal(TaskState.Completed, result.State);
        Assert.Equal(5, Adj("600000").Count);
        Assert.Contains(log, l => l.Contains("1 只整段重算"));
    }

    [Fact]
    public async Task 只是尾巴长出来了_走增量只追加新的那几根()
    {
        PutRaw("600000", Days);
        // 前 4 天已经算过（上次重算时刻比K线晚，判据才不会因为"值被改过"而整段重来）
        PutAdj("600000", new DateTime(2026, 9, 8, 2, 0, 0), Days[..4]);

        var (_, log) = await RunAsync();

        // 收尾那一行的措辞（"增量 N 只"只出现在按时间节流的中途进度里，秒级的测试里不触发）
        Assert.Contains(log, l => l.Contains("1 只只追加了新K线、0 只整段重算"));
        Assert.Equal(5, Adj("600000").Count);
    }

    /// <summary>
    /// 新增的日子里有除权 ⇒ 必须整段重来。因子是从最早那天累乘上来的，
    /// 只追加尾巴会让新旧两段落在不同基准上。
    /// </summary>
    [Fact]
    public async Task 新增的日子里有除权_不许走增量()
    {
        // 9-7 除权：前收 10、每10股派 10 元（每股 1 元），除权参考价 9
        PutRaw("600000", Days[..4]);
        Put("600000", Granularity.DayRaw, Days[4], 9);
        PutAdj("600000", new DateTime(2026, 9, 8, 2, 0, 0), Days[..4]);
        PutDividend("600000", Days[4], per10Cash: 10);

        var (_, log) = await RunAsync();

        Assert.Contains(log, l => l.Contains("1 只整段重算"));
        Assert.Contains(log, l => l.Contains("应用除权 1 次"));
        // 因子从除权日起往后乘（10/9）：除权前那几天保持原价，除权当天 9 元被抬回 10 元。
        // 这正是"非除权日收益率不变、除权日的跳空被抵掉"——复权序列该有的样子。
        var adj = Adj("600000");
        Assert.Equal(10.0, adj[0].Close, precision: 6);
        Assert.Equal(10.0, adj[^1].Close, precision: 6);   // 不复权是 9
    }

    /// <summary>
    /// 不复权**在前面补长了**（补历史）⇒ 因子基准变了，只追加尾巴会在接缝处多出假跳空。
    /// 这一条最阴：日期的尾巴是对齐的，光看尾巴会以为"没什么要做"。
    /// </summary>
    [Fact]
    public async Task 不复权往前补了历史_不许走增量()
    {
        PutRaw("600000", Days);
        // 已算的那段从第二天才开始——开头对不上
        PutAdj("600000", new DateTime(2026, 9, 8, 2, 0, 0), Days[1..]);

        var (_, log) = await RunAsync();

        Assert.Contains(log, l => l.Contains("1 只整段重算"));
        Assert.Equal(5, Adj("600000").Count);
        Assert.Equal(Days[0], Adj("600000")[0].PeriodStart);
    }

    // ─────────────────── ② 整段重算要先删后写 ───────────────────

    /// <summary>
    /// 旧序列比新序列**长**时，多出来的旧行必须被删掉。只 UPSERT 的话它们会留在库里，
    /// 而它们是用旧因子算的——序列上多出一截对不上的价格，没有任何地方会报错。
    /// </summary>
    [Fact]
    public async Task 整段重算会删掉旧序列多出来的那一截()
    {
        PutRaw("600000", Days[..3]);                    // 不复权只有 3 天
        // 旧的 day_adj 有 5 天，且时间戳比不复权旧（判据：不复权的值比它新 ⇒ 要重算）
        PutAdj("600000", new DateTime(2026, 8, 20, 2, 0, 0), Days);
        Assert.Equal(5, Adj("600000").Count);

        await RunAsync();

        var after = Adj("600000");
        Assert.Equal(3, after.Count);
        Assert.DoesNotContain(after, b => b.PeriodStart == Days[4]);
    }

    // ─────────────────── ③ 空批 / 额度 / 截止 ───────────────────

    /// <summary>
    /// 不复权不足两根的票算不出收益率、也算不出因子，产出**空批**。框架跳过空批的落账，
    /// 所以它既不写库、也不占 <see cref="TaskRunArgs.MaxItems"/> 额度。
    /// </summary>
    [Fact]
    public async Task 不复权只有一根的票_不落账也不占额度()
    {
        Put("000001", Granularity.DayRaw, Days[0], 10);   // 只有一根
        PutRaw("600000", Days);                           // 正常的一只

        var (_, log) = await RunAsync(maxItems: 1);

        Assert.Empty(Adj("000001"));
        // 额度是 1 批，而那只单根的票没占额度，所以正常那只仍然算到了
        Assert.Equal(5, Adj("600000").Count);
        Assert.Contains(log, l => l.Contains("1 只整段重算"));
    }

    [Fact]
    public async Task MaxItems到了就收尾_剩下的报成没轮到()
    {
        PutRaw("600000", Days);
        PutRaw("600001", Days);
        PutRaw("600002", Days);

        var (_, log) = await RunAsync(maxItems: 2);

        Assert.Equal(2, new[] { "600000", "600001", "600002" }.Count(c => Adj(c).Count > 0));
        Assert.Contains(log, l => l.Contains("本轮已做满 2 批"));
        Assert.Contains(log, l => l.Contains("还有 1 只没轮到"));
    }

    /// <summary>
    /// 截止时刻已经过了：第一批存完就收尾。这正是迁移换来的东西——
    /// 老调用点是按"0.2 秒/只"**预估**只数，而一只从几毫秒到几百毫秒都有。
    /// </summary>
    [Fact]
    public async Task 过了截止时刻就收尾()
    {
        PutRaw("600000", Days);
        PutRaw("600001", Days);

        var (_, log) = await RunAsync(deadline: DateTime.Now.AddSeconds(-1));

        Assert.Equal(1, new[] { "600000", "600001" }.Count(c => Adj(c).Count > 0));
        Assert.Contains(log, l => l.Contains("收尾"));
    }

    // ─────────────────── ④ 全量重算（首次整段回补）───────────────────

    /// <summary>
    /// 【首次整段回补】在这一项里的意思是**忽略五条判据、全部整段重来**。
    /// 改过复权算法之后需要它——原来只能手工删掉 day_adj 逼判据重新认出来。
    /// </summary>
    [Fact]
    public async Task 全量重算_连已经算齐的票也重来()
    {
        PutRaw("600000", Days);
        // 已经算齐了：日期一致、时间戳更新 ⇒ 五条判据一条都不成立
        PutAdj("600000", new DateTime(2026, 9, 8, 2, 0, 0), Days);

        // 增量模式：没什么可做
        var (incr, incrLog) = await RunAsync();
        Assert.True(incr.NothingToDo);
        Assert.Contains(incrLog, l => l.Contains("已经跟不复权一样新"));

        // 全量模式：照样重算
        var (full, fullLog) = await RunAsync(FetchMode.FirstBackfill);
        Assert.False(full.NothingToDo);
        Assert.Contains(fullLog, l => l.Contains("全量重算"));
        Assert.Contains(fullLog, l => l.Contains("1 只整段重算"));
    }

    /// <summary>全量重算不看判据，所以也不该走增量那条路——哪怕日期只差一天。</summary>
    [Fact]
    public async Task 全量重算不走增量()
    {
        PutRaw("600000", Days);
        PutAdj("600000", new DateTime(2026, 9, 8, 2, 0, 0), Days[..4]);

        var (_, log) = await RunAsync(FetchMode.FirstBackfill);

        Assert.Contains(log, l => l.Contains("1 只整段重算"));
        Assert.Contains(log, l => l.Contains("0 只只追加了新K线"));
    }

    // ─────────────────── ⑤ 收尾报告 ───────────────────

    [Fact]
    public async Task 没什么可做时报NothingToDo()
    {
        var (result, log) = await RunAsync();

        Assert.True(result.NothingToDo);
        Assert.Contains(log, l => l.Contains("已经跟不复权一样新"));
    }

    /// <summary>
    /// 「还剩几只」是用减法算的，**不再重扫一遍全库**（老代码为了这个数字又对 1300 万行
    /// GROUP BY 一遍，几十秒）。全算完就不该出现这句话。
    /// </summary>
    [Fact]
    public async Task 全算完了就不提还剩几只()
    {
        PutRaw("600000", Days);

        var (_, log) = await RunAsync();

        Assert.Contains(log, l => l.Contains("回测序列更新完成"));
        Assert.DoesNotContain(log, l => l.Contains("没轮到"));
    }

    /// <summary>
    /// 收益率自检那一句：没有除权时复权序列就等于原价，两边收益率分毫不差，
    /// 所以该报"全部通过"而不是任何警告。措辞见 doc/bar-value-audit-design.md §15。
    /// </summary>
    [Fact]
    public async Task 没有除权时收益率自检全部通过()
    {
        PutRaw("600000", Days);

        var (_, log) = await RunAsync();

        Assert.Contains(log, l => l.Contains("收益率自检全部通过"));
        Assert.DoesNotContain(log, l => l.Contains("⚠"));
    }
}
