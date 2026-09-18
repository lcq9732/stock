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
/// 让 ETF 能回测的那三件事（2026-09-17）——见 <see cref="FundExDividendImportTask"/> /
/// <see cref="EtfRawBarTask"/> / <c>doc/etf-backtest-granularity-design.md</c>。
///
/// ETF 至今只有源给的**减法式前复权**，非除权日的收益率也是错的 ⇒ 完全不能回测。
/// 补齐要三样：事件（从东财终端本地文件导）+ <c>day_raw</c>（抓或复制）+ 因子（【重算回测序列】，零改动）。
///
/// 这里钉死最容易错、而且错了很安静的四件事：
///   ① **份额合并的送转比例是负数**，不能被当成空事件跳过（复权算法为此从 <c>&lt;=0</c> 改成 <c>==0</c>）
///   ② 导进来的 ETF 分红行**必须带市场前缀**——写成 6 位裸码会漏进个股选股全集
///   ③ 没除权过的 ETF 从 <c>day</c> 复制 <c>day_raw</c> 之前**必须过闸门**，
///      否则事件源漏记一条就静默抄错一整条序列
///   ④ 日常增量的闸门是**纯本地**的（比两条序列在同一天的收盘价），不能每天再抓一遍
/// </summary>
public class EtfBacktestSeriesTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly FetchPaths _paths;
    private readonly SqliteBarRepository _bars;
    private readonly SqliteDividendRepository _divs;

    public EtfBacktestSeriesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"etfraw_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _paths = new FetchPaths(_dir);
        _dbPath = _paths.CurrentDb;
        _bars = new SqliteBarRepository(_dbPath);
        _bars.EnsureSchema();
        _divs = new SqliteDividendRepository(_dbPath);
        _divs.EnsureSchema();
    }

    /// <summary>
    /// 不调 <c>SqliteConnection.ClearAllPools()</c>——那是**进程级**的全局操作，这里没必要用。
    /// 临时目录删不掉就留着：反正在系统 temp 里、名字带 Guid 不会撞。
    ///
    /// ⚠ 2026-09-17 我一度把当天的 testhost 崩溃（先崩在第 970 个、再崩在第 720 个）归因成
    /// "ClearAllPools 撞上 xunit 的并行"，**那个因果是错的**：本仓库 2026-09-07 就在
    /// <c>AssemblyInfo.cs</c> 里设了 <c>DisableTestParallelization = true</c>，测试类之间根本不并行。
    /// 当时同时做了两件事（杀掉残留 testhost + 去掉这行调用），崩溃消失该记在前者头上。
    /// 真要排查这类随机崩，先查残留进程数、跑 <c>dotnet build-server shutdown</c>
    /// （见 project_stale_dotnet_crashes_tests）。
    /// </summary>
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 还被连接池占着，留给系统清理 */ }
    }

    // ═══════════════ ① 份额合并：负的送转比例是合法输入 ═══════════════

    /// <summary>
    /// ⭐ 负 <c>ShareRatio</c> **不是**空事件。改之前判据写的是 <c>ShareRatio &lt;= 0</c>，
    /// 份额合并（基金把 10 份合成 N 份、价格上调）会被当空事件**静默跳过**，
    /// ETF 的复权序列在折算日直接断掉。
    /// </summary>
    [Fact]
    public void 份额合并的负送转比例不是空事件()
    {
        // 510500 中证500ETF 2015-04-15 那次：Diviratiob=2.80325 ⇒ ShareRatio = 2.80325/10 − 1
        var merge = new AdjustFactorCalculator.ExDividend(new DateTime(2015, 4, 15), 2.80325 / 10 - 1, 0);

        Assert.False(merge.IsEmpty);                                     // ⭐ 改之前这里是 true
        Assert.True(merge.ShareRatio < 0);
        Assert.True(new AdjustFactorCalculator.ExDividend(new DateTime(2015, 4, 15), 0, 0).IsEmpty);
    }

    /// <summary>
    /// 份额合并真的被应用进因子：价格乘数 = <c>10 / Diviratiob</c> = 3.567，
    /// 实测那天跳空 +261.1%（理论 +256.7%，差 4 个点落在价格校验容差内）。
    /// </summary>
    [Fact]
    public void 份额合并被应用进复权因子()
    {
        const double ratio = 2.80325;
        List<Bar> raw =
        [
            RawBar("sh510500", new DateTime(2015, 4, 14), 1.000, 1.000),
            RawBar("sh510500", new DateTime(2015, 4, 15), 3.611, 3.611),   // 实际跳空 +261.1%
        ];
        var ev = new AdjustFactorCalculator.ExDividend(new DateTime(2015, 4, 15), ratio / 10 - 1, 0);

        var adj = AdjustFactorCalculator.BuildAdjusted("sh510500", raw, [ev], out var report);

        Assert.Equal(1, report.Applied);                                 // 通过了价格校验
        Assert.Equal(0, report.Skipped);
        // 还原之后那天不该再有 +261% 的假暴涨——复权序列上是个正常的小涨幅
        double gap = adj[1].Close / adj[0].Close - 1;
        Assert.True(Math.Abs(gap) < 0.10, $"还原后跳空应被抹平，实际 {gap:P2}");
    }

    // ═══════════════ ② 导入：映射与带前缀 ═══════════════

    /// <summary>
    /// ⭐ 现金分红进 <c>DividendYuan</c>、份额折算进 <c>TransferShares</c>（<c>Diviratiob − 10</c>，
    /// 合并时为负），<c>code</c> **带市场前缀**（裸码会让 ETF 漏进个股选股全集）。
    /// </summary>
    [Fact]
    public async Task 导入基金除权_映射正确且代码带前缀()
    {
        PutEtfMeta("sh510500", "中证500ETF");
        PutEtfMeta("sz159915", "创业板ETF");
        var file = WriteFundCqcx(
            ("510500", 1, 20150410, 20150415, 2, 0.0, 2.80325),     // 份额合并
            ("510500", 1, 20240105, 20240110, 1, 0.2110, 0.0),      // 现金分红
            ("159915", 0, 20230101, 20230105, 1, 0.0500, 0.0),
            ("999999", 1, 20230101, 20230105, 1, 1.0000, 0.0));     // 不是我们库里的 ETF

        var result = await RunImportAsync(file);

        Assert.Equal(TaskState.Completed, result.State);
        var merge = _divs.GetByCode("sh510500").Single(d => d.ExDate == new DateTime(2015, 4, 15));
        Assert.Equal(2.80325 - 10, merge.TransferShares, 5);             // ⭐ 负数＝份额合并
        Assert.Equal(0, merge.DividendYuan);
        var cash = _divs.GetByCode("sh510500").Single(d => d.ExDate == new DateTime(2024, 1, 10));
        Assert.Equal(0.2110, cash.DividendYuan, 4);
        Assert.Single(_divs.GetByCode("sz159915"));
        Assert.Empty(_divs.GetByCode("999999"));                         // 库里没有的基金不导
        Assert.Empty(_divs.GetByCode("510500"));                         // ⭐ 裸码下一行都没有
    }

    /// <summary>映射之后算出来的 ShareRatio 必须还原成价格乘数 10/Diviratiob——这是整条链的关键等式。</summary>
    [Fact]
    public async Task 导入的折算比例能还原成价格乘数()
    {
        PutEtfMeta("sh510500", "中证500ETF");
        var file = WriteFundCqcx(("510500", 1, 20150410, 20150415, 2, 0.0, 2.80325));

        await RunImportAsync(file);

        var row = _divs.GetByCode("sh510500").Single();
        double shareRatio = (row.BonusShares + row.TransferShares) / 10.0;   // AdjSeriesRebuildTask 的算法
        Assert.Equal(10.0 / 2.80325, 1 / (1 + shareRatio), 6);               // 价格乘数 = 3.567
    }

    /// <summary>没装东财终端就整项跳过，不算失败。</summary>
    [Fact]
    public async Task 没有本地文件时跳过不算失败()
    {
        var result = await RunImportAsync(Path.Combine(_dir, "不存在.db"));

        Assert.Equal(TaskState.Completed, result.State);
        Assert.True(result.NothingToDo);
    }

    // ═══════════════ ③④ ETF day_raw：闸门与复制 ═══════════════

    /// <summary>
    /// ⭐ 没除权过的 ETF：闸门抓一页真 <c>day_raw</c> 比对通过后，整段从 <c>day</c> 复制。
    /// 只发**一个**请求（闸门那页），不是逐页抓全历史。
    /// </summary>
    [Fact]
    public async Task 没除权过的ETF_过闸门后从day复制()
    {
        PutEtfMeta("sz159915", "创业板ETF");
        foreach (var (d, c) in Series()) Put("sz159915", Granularity.Day, d, c);
        var fetcher = new FakeFetcher(Series());          // 源给的 day_raw 跟库里的 day 一样

        var (result, _) = await RunEtfRawAsync(fetcher);

        Assert.Equal(TaskState.Completed, result.State);
        Assert.Equal(1, fetcher.Calls);                                   // ⭐ 只发了闸门那一个请求
        var raw = _bars.Query("sz159915", Granularity.DayRaw);
        Assert.Equal(Series().Count, raw.Count);                          // 整段都复制过来了
        Assert.Equal(_bars.Query("sz159915", Granularity.Day).Select(b => b.Close), raw.Select(b => b.Close));
    }

    /// <summary>
    /// ⭐ 闸门不过（事件源漏记了这只的除权）⇒ **不复制**，改为老实抓全历史，并报一条。
    /// 这一条是整个"省请求"设计的安全网：没有它，漏记一条事件就静默抄错一整条序列。
    /// </summary>
    [Fact]
    public async Task 闸门不过时不复制_改为抓全历史并报出来()
    {
        PutEtfMeta("sh510500", "中证500ETF");
        foreach (var (d, c) in Series()) Put("sh510500", Granularity.Day, d, c);
        // 源给的 day_raw 跟库里的 day 对不上（说明这只其实除过权，只是事件源没记）
        var real = Series().Select(x => (x.Day, Close: x.Close * 2)).ToList();
        var fetcher = new FakeFetcher(real);

        var (result, log) = await RunEtfRawAsync(fetcher);

        Assert.Equal(TaskState.Completed, result.State);
        Assert.Equal(2, fetcher.Calls);                                   // 闸门 1 次 + 老实抓 1 次
        Assert.Contains(log, m => m.Contains("不一致") || m.Contains("事件源"));
        var raw = _bars.Query("sh510500", Granularity.DayRaw);
        Assert.All(raw, b => Assert.Equal(b.Close, Series().Single(x => x.Day == b.PeriodStart).Close * 2, 4));
    }

    /// <summary>有除权事件的 ETF 一律抓，不走复制那条路（它的 day 跟 day_raw 本来就不一样）。</summary>
    [Fact]
    public async Task 有除权事件的ETF直接抓()
    {
        PutEtfMeta("sh510500", "中证500ETF");
        foreach (var (d, c) in Series()) Put("sh510500", Granularity.Day, d, c);
        _divs.ReplaceByCode("sh510500",
        [
            new DividendRow
            {
                Code = "sh510500", AnnounceDate = new DateTime(2026, 8, 30),
                TransferShares = 2.80325 - 10, Progress = "实施",
                ExDate = new DateTime(2026, 9, 2), FetchedAt = DateTime.Now,
            },
        ]);
        var fetcher = new FakeFetcher(Series().Select(x => (x.Day, Close: x.Close * 3)).ToList());

        await RunEtfRawAsync(fetcher);

        Assert.Equal(1, fetcher.Calls);                                   // 直接抓，没有闸门那一次
        Assert.NotEmpty(_bars.Query("sh510500", Granularity.DayRaw));
    }

    /// <summary>
    /// ⭐ 日常增量：库里已有 <c>day_raw</c> 时，判据是**纯本地**的——比两条序列在同一天的收盘价。
    /// 相等就说明此后没除过权（前复权基准一变整条 day 都会变），直接复制新增那几根，**零请求**。
    /// </summary>
    [Fact]
    public async Task 增量时用本地判据复制_不发请求()
    {
        PutEtfMeta("sz159915", "创业板ETF");
        var all = Series();
        foreach (var (d, c) in all) Put("sz159915", Granularity.Day, d, c);
        // 已有的 day_raw 只到倒数第二天，且那天跟 day 相等 ⇒ 没除过权
        foreach (var (d, c) in all.Take(all.Count - 1)) Put("sz159915", Granularity.DayRaw, d, c);
        var fetcher = new FakeFetcher([]);

        var (result, _) = await RunEtfRawAsync(fetcher);

        Assert.Equal(TaskState.Completed, result.State);
        Assert.Equal(0, fetcher.Calls);                                   // ⭐ 一个请求都没发
        Assert.Equal(all.Count, _bars.Query("sz159915", Granularity.DayRaw).Count);
    }

    /// <summary>增量时两条序列在老日子上已经不等 ⇒ 期间除权了 ⇒ 整只重抓，不能接着复制。</summary>
    [Fact]
    public async Task 增量时发现已除权_整只重抓()
    {
        PutEtfMeta("sh510500", "中证500ETF");
        var all = Series();
        foreach (var (d, c) in all) Put("sh510500", Granularity.Day, d, c);
        // day_raw 的老行跟 day 对不上 ⇒ 期间除过权
        foreach (var (d, c) in all.Take(all.Count - 1)) Put("sh510500", Granularity.DayRaw, d, c * 2);
        var fetcher = new FakeFetcher(all.Select(x => (x.Day, Close: x.Close * 2)).ToList());

        var (_, log) = await RunEtfRawAsync(fetcher);

        Assert.Equal(1, fetcher.Calls);
        Assert.Contains(log, m => m.Contains("除权") || m.Contains("重抓"));
    }

    // ─────────────────── 造数据 ───────────────────

    private static List<(DateTime Day, double Close)> Series() =>
    [
        (new DateTime(2026, 9, 1), 1.10), (new DateTime(2026, 9, 2), 1.20),
        (new DateTime(2026, 9, 3), 1.30), (new DateTime(2026, 9, 4), 1.40),
    ];

    private static Bar RawBar(string code, DateTime day, double open, double close) => new()
    {
        Code = code, Granularity = Granularity.DayRaw, PeriodStart = day,
        Open = open, Close = close, High = Math.Max(open, close), Low = Math.Min(open, close),
        FetchedAt = DateTime.Now,
    };

    private void Put(string code, string gran, DateTime day, double close) =>
        _bars.InsertOrRefreshUnconfirmed([new Bar
        {
            Code = code, Granularity = gran, PeriodStart = day,
            Open = close, Close = close, High = close, Low = close,
            Volume = 100, Amount = 100 * close, Turnover = 1.0, FetchedAt = day.AddHours(20),
        }]);

    private void PutEtfMeta(string code, string name) =>
        SqliteStockMetaUpsert.Upsert(_dbPath, [(code, name)], SqliteStockMetaUpsert.TypeEtf);

    /// <summary>造一个跟东财终端同构的 fund_cqcx.db。</summary>
    private string WriteFundCqcx(params (string Code, int Market, int Notice, int Nvcvt, int Type, double A, double B)[] rows)
    {
        var path = Path.Combine(_dir, "fund_cqcx.db");
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE fund_cqcx (RecordID INT64 PRIMARY KEY, Code TEXT, Market int,
                  UpdateDate int, UpdateTime int, ExDivType int, NoticeDate int, NvcvtDate int,
                  Diviratioa Real, Diviratiob Real, Remark Text, IsValid int);
                """;
            cmd.ExecuteNonQuery();
        }
        int id = 1;
        foreach (var r in rows)
        {
            using var ins = conn.CreateCommand();
            ins.CommandText = """
                INSERT INTO fund_cqcx (RecordID, Code, Market, UpdateDate, UpdateTime, ExDivType,
                    NoticeDate, NvcvtDate, Diviratioa, Diviratiob, Remark, IsValid)
                VALUES ($id, $c, $m, 0, 0, $t, $n, $v, $a, $b, '', 1);
                """;
            ins.Parameters.AddWithValue("$id", id++);
            ins.Parameters.AddWithValue("$c", r.Code);
            ins.Parameters.AddWithValue("$m", r.Market);
            ins.Parameters.AddWithValue("$t", r.Type);
            ins.Parameters.AddWithValue("$n", r.Notice);
            ins.Parameters.AddWithValue("$v", r.Nvcvt);
            ins.Parameters.AddWithValue("$a", r.A);
            ins.Parameters.AddWithValue("$b", r.B);
            ins.ExecuteNonQuery();
        }
        return path;
    }

    private async Task<TaskRunResult> RunImportAsync(string fundCqcxPath)
    {
        var task = new FundExDividendImportTask(_paths, _divs, fundCqcxPath);
        return await task.RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);
    }

    private async Task<(TaskRunResult Result, List<string> Log)> RunEtfRawAsync(
        FakeFetcher fetcher, FetchMode mode = FetchMode.Incremental)
    {
        var task = new EtfRawBarTask(_paths, _bars, _divs, fetcher);
        var log = new List<string>();
        task.OnProgress += p => log.Add(p.Text);
        var result = await task.RunAsync(new TaskRunArgs(mode), CancellationToken.None);
        return (result, log);
    }

    /// <summary>按固定序列回 day_raw，并数发了几次请求——"省了多少请求"正是这套设计的重点。</summary>
    private sealed class FakeFetcher(IReadOnlyList<(DateTime Day, double Close)> rows) : IBarDataFetcher
    {
        public int Calls { get; private set; }
        public bool SupportsHfq => true;
        public event Action<string>? OnStatus { add { } remove { } }

        public Task<(string Name, List<Bar> Bars)> FetchAsync(
            string code, string granularity, DateTime? start, DateTime? end, CancellationToken ct = default)
        {
            Calls++;
            var bars = rows
                .Where(r => (start is null || r.Day >= start.Value.Date) && (end is null || r.Day <= end.Value.Date))
                .Select(r => RawBar(code, r.Day, r.Close, r.Close))
                .ToList();
            return Task.FromResult((code, bars));
        }
    }
}
