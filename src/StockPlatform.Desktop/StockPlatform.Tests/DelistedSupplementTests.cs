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
/// 【补全退市名单】（2026-09-17）——见 <see cref="DelistedSupplementTask"/>。
///
/// 两所官网的终止上市名单漏两类：**科创板退市股整类缺失**（上交所 stockType=5 里 68 开头是 0 只，
/// 688086/688555/688287 在它家所有 stockType 里都查不到）、**已换代码的老号**。
/// 巨潮的全市场名单都有，但**不能直接并进在市名单**——它含大批已退市股，而 Upsert 默认写
/// type='stock' 且是 INSERT OR REPLACE，会把几百行 delisted 冲成 stock。
///
/// 钉死三件事：
///   ① 候选 = 巨潮 − 在市 − 已知退市（别把在市股或已知退市股再算一遍）
///   ② **数据源给得出日K才算交易过**——候选里混着从未上市的（蚂蚁集团那类过会后撤回的），
///      写进退市表会污染分红抓取和 FactorLab 选池
///   ③ 补进去的同时要把 StockMeta 标成 delisted，否则它们不在任何名单里
/// </summary>
public class DelistedSupplementTests : IDisposable
{
    private readonly string _dir;
    private readonly FetchPaths _paths;
    private readonly string _dbPath;

    public DelistedSupplementTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"delisted_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _paths = new FetchPaths(_dir);
        _dbPath = _paths.CurrentDb;
        new SqliteBarRepository(_dbPath).EnsureSchema();
        new SqliteDelistedRepository(_dbPath).EnsureSchema();
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

    /// <summary>
    /// ⭐ 科创板退市股补进来（这正是两所名单给不出的那类），同时 StockMeta 标成 delisted。
    /// </summary>
    [Fact]
    public async Task 补进科创板退市股并标成delisted()
    {
        PutLive("600000", "浦发银行");
        var list = new FakeList(
            new StockListEntry("600000", "浦发银行"),      // 在市，不是候选
            new StockListEntry("688086", "退市紫晶"),      // ⭐ 两所名单给不出的科创板退市股
            new StockListEntry("688555", "退市泽达"));
        var fetcher = FakeFetcher.ByHasBars(_ => true);

        var (result, _) = await RunAsync(list, fetcher);

        Assert.Equal(TaskState.Completed, result.State);
        var rows = new SqliteDelistedRepository(_dbPath).GetAll();
        Assert.Equal(new[] { "688086", "688555" }, rows.Select(r => r.Code).OrderBy(x => x));
        Assert.All(rows, r => Assert.Equal("sse", r.Exchange));
        Assert.All(rows, r => Assert.Null(r.DelistDate));               // 巨潮不给终止日，留空
        // StockMeta 也要标上，否则分红抓取（GetByTypes(stock+delisted)）看不到它们
        var meta = SqliteStockMetaUpsert.GetByTypes(_dbPath, SqliteStockMetaUpsert.TypeDelisted);
        Assert.Equal(2, meta.Count);
        Assert.DoesNotContain(SqliteStockMetaUpsert.GetAll(_dbPath), x => x.Code == "688086");
    }

    /// <summary>
    /// ⭐ 数据源一根日K都给不出 ⇒ 从未上市（过会后撤回/暂缓），**不写进退市表**。
    /// 写进去会污染分红抓取和选池——退市表的语义是"交易过、现在没了"。
    /// </summary>
    [Fact]
    public async Task 从未交易过的不写进退市表()
    {
        var list = new FakeList(
            new StockListEntry("688086", "退市紫晶"),      // 交易过
            new StockListEntry("688688", "蚂蚁集团"),      // 从未上市
            new StockListEntry("603361", "浙江国祥"));     // 过会后撤回
        var fetcher = FakeFetcher.ByHasBars(code => code == "688086");

        var (_, log) = await RunAsync(list, fetcher);

        var rows = new SqliteDelistedRepository(_dbPath).GetAll();
        Assert.Single(rows);
        Assert.Equal("688086", rows[0].Code);
        Assert.Contains(log, m => m.Contains("蚂蚁集团") || m.Contains("从未上市"));
    }

    /// <summary>
    /// ⭐ **最后一根K线还很新 ⇒ 它还在交易，不是退市**。第一版漏了这条，实机跑时
    /// <c>601091 沈鼓集团</c>（当天刚上市的新股，只有 1 根K线）被判成退市，
    /// 那批在**退市整理期**照常交易的"退市XX"和 *ST 也一样。
    /// 标错的代价是它们被踢出日常轮询、K线从此不再更新，而且**没有任何地方会报**。
    /// </summary>
    [Fact]
    public async Task 最后一根K线很新的不算退市()
    {
        var today = DateTime.Today;
        var list = new FakeList(
            new StockListEntry("601091", "沈鼓集团"),      // 当天刚上市的新股：只有 1 根，就是今天
            new StockListEntry("600355", "*ST精伦"),       // 退市整理期，仍在交易
            new StockListEntry("688086", "退市紫晶"));     // 真退市：K线停在 2023
        var fetcher = new FakeFetcher(code => code switch
        {
            "601091" => today,
            "600355" => today.AddDays(-1),
            _ => new DateTime(2023, 8, 1),
        });

        var (_, log) = await RunAsync(list, fetcher);

        var rows = new SqliteDelistedRepository(_dbPath).GetAll();
        Assert.Equal(new[] { "688086" }, rows.Select(r => r.Code));     // ⭐ 只补真退市的那只
        Assert.Contains(log, m => m.Contains("还在交易"));
        // 被挡下的不能留在 StockMeta 里当 delisted
        Assert.DoesNotContain(SqliteStockMetaUpsert.GetByTypes(_dbPath, SqliteStockMetaUpsert.TypeDelisted),
                              x => x.Code is "601091" or "600355");
    }

    /// <summary>刚好卡在 30 天边界外的算退市——判据是"距今 &gt; 30 天"。</summary>
    [Fact]
    public async Task 距今超过三十天算已退市()
    {
        var list = new FakeList(
            new StockListEntry("600001", "刚好29天"),
            new StockListEntry("600002", "已过31天"));
        var fetcher = new FakeFetcher(code => code == "600001"
            ? DateTime.Today.AddDays(-29)
            : DateTime.Today.AddDays(-31));

        await RunAsync(list, fetcher);

        Assert.Equal(new[] { "600002" }, new SqliteDelistedRepository(_dbPath).GetAll().Select(r => r.Code));
    }

    /// <summary>已经在退市表里的不重复处理——候选要先减掉它们，省下探测请求。</summary>
    [Fact]
    public async Task 已知退市股不再探测()
    {
        new SqliteDelistedRepository(_dbPath).Upsert([
            new DelistedStockRow { Code = "600001", Name = "已知退市", Exchange = "sse" }]);
        var list = new FakeList(
            new StockListEntry("600001", "已知退市"),
            new StockListEntry("688086", "退市紫晶"));
        var fetcher = FakeFetcher.ByHasBars(_ => true);

        await RunAsync(list, fetcher);

        Assert.Equal(new[] { "688086" }, fetcher.Asked);                // 只探了新的那只
        Assert.Equal(2, new SqliteDelistedRepository(_dbPath).GetAll().Count);
    }

    /// <summary>在市股一只都不许碰——它们的 type 必须还是 stock。</summary>
    [Fact]
    public async Task 在市股不受影响()
    {
        PutLive("600000", "浦发银行");
        PutLive("000001", "平安银行");
        var list = new FakeList(
            new StockListEntry("600000", "浦发银行"),
            new StockListEntry("000001", "平安银行"),
            new StockListEntry("688086", "退市紫晶"));

        await RunAsync(list, FakeFetcher.ByHasBars(_ => true));

        var live = SqliteStockMetaUpsert.GetAll(_dbPath).Select(x => x.Code).ToList();
        Assert.Contains("600000", live);
        Assert.Contains("000001", live);
        Assert.Equal(new[] { "688086" }, new SqliteDelistedRepository(_dbPath).GetAll().Select(r => r.Code));
    }

    /// <summary>名单源挂了就整项跳过，不算失败，也不动库。</summary>
    [Fact]
    public async Task 名单源失败时跳过且不动库()
    {
        PutLive("600000", "浦发银行");

        var (result, _) = await RunAsync(new ThrowingList(), FakeFetcher.ByHasBars(_ => true));

        Assert.Equal(TaskState.Completed, result.State);
        Assert.True(result.NothingToDo);
        Assert.Empty(new SqliteDelistedRepository(_dbPath).GetAll());
    }

    /// <summary>深市代码要标成 szse——退市表按交易所分，分红/公告那几条路会用到。</summary>
    [Fact]
    public async Task 深市代码标成szse()
    {
        var list = new FakeList(new StockListEntry("300114", "中航电测"));

        await RunAsync(list, FakeFetcher.ByHasBars(_ => true));

        Assert.Equal("szse", new SqliteDelistedRepository(_dbPath).GetAll().Single().Exchange);
    }

    /// <summary>
    /// ⭐ **已标成 delisted 的，不许被在市名单写回 'stock'**。
    ///
    /// 在市名单源的口径不一致：上交所 stockType=10（沪市全量）里就含 18 只**已经退市**的票
    /// （600193 退市创兴那批，K线停在 80~600 天前）。没这道保护的话，日更的【刷新名册】把它们
    /// 写成 stock、周期组的【补全退市名单】再写回 delisted，两边来回翻——而退市股一变回 stock
    /// 就重新进入日常轮询，每天几百个必然落空的请求。
    /// </summary>
    [Fact]
    public void 退市状态不被在市名单覆盖()
    {
        SqliteStockMetaUpsert.Upsert(_dbPath, [("600193", "退市创兴")], SqliteStockMetaUpsert.TypeDelisted);

        // 在市名册刷新时又把它当成在市股写了一遍（上交所名单里确实有它）
        SqliteStockMetaUpsert.Upsert(_dbPath, [("600193", "退市创兴")], SqliteStockMetaUpsert.TypeStock);

        Assert.DoesNotContain(SqliteStockMetaUpsert.GetAll(_dbPath), x => x.Code == "600193");
        Assert.Contains(SqliteStockMetaUpsert.GetByTypes(_dbPath, SqliteStockMetaUpsert.TypeDelisted),
                        x => x.Code == "600193");
    }

    /// <summary>反过来要能生效：真判成退市时，'stock' → 'delisted' 必须写得进去。</summary>
    [Fact]
    public void 在市股可以被标成退市()
    {
        SqliteStockMetaUpsert.Upsert(_dbPath, [("688086", "紫晶存储")], SqliteStockMetaUpsert.TypeStock);

        SqliteStockMetaUpsert.Upsert(_dbPath, [("688086", "退市紫晶")], SqliteStockMetaUpsert.TypeDelisted);

        Assert.DoesNotContain(SqliteStockMetaUpsert.GetAll(_dbPath), x => x.Code == "688086");
        var d = SqliteStockMetaUpsert.GetByTypes(_dbPath, SqliteStockMetaUpsert.TypeDelisted).Single();
        Assert.Equal("退市紫晶", d.Name);          // 名字要跟着更新
    }

    /// <summary>ETF/指数那几类不受影响——保护只针对 delisted ↔ stock。</summary>
    [Fact]
    public void 其它类型照常覆盖()
    {
        SqliteStockMetaUpsert.Upsert(_dbPath, [("sh510300", "沪深300ETF")], SqliteStockMetaUpsert.TypeEtf);
        SqliteStockMetaUpsert.Upsert(_dbPath, [("sh510300", "沪深300ETF改名")], SqliteStockMetaUpsert.TypeEtf);

        var etf = SqliteStockMetaUpsert.GetByTypes(_dbPath, SqliteStockMetaUpsert.TypeEtf).Single();
        Assert.Equal("沪深300ETF改名", etf.Name);
    }

    // ─────────────────── 造数据 ───────────────────

    private void PutLive(string code, string name) =>
        SqliteStockMetaUpsert.Upsert(_dbPath, [(code, name)], SqliteStockMetaUpsert.TypeStock);

    private async Task<(TaskRunResult Result, List<string> Log)> RunAsync(
        IStockListProvider list, IBarDataFetcher fetcher)
    {
        var task = new DelistedSupplementTask(_paths, list, fetcher);
        var log = new List<string>();
        task.OnProgress += p => log.Add(p.Text);
        var result = await task.RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);
        return (result, log);
    }

    private sealed class FakeList(params StockListEntry[] rows) : IStockListProvider
    {
        public Task<List<StockListEntry>> GetAllStocksAsync(
            IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromResult(rows.ToList());
    }

    private sealed class ThrowingList : IStockListProvider
    {
        public Task<List<StockListEntry>> GetAllStocksAsync(
            IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromException<List<StockListEntry>>(new HttpRequestException("巨潮连不上"));
    }

    /// <summary>
    /// 按代码决定"最后一根K线在哪天"（null＝一根都给不出），并记下被探测过哪些。
    /// 候选筛选和两道判据对不对，全看这个。
    /// </summary>
    private sealed class FakeFetcher(Func<string, DateTime?> lastBar) : IBarDataFetcher
    {
        /// <summary>老写法的简便构造：只说给不给得出，给得出就当是很久以前的（＝已退市）。</summary>
        public static FakeFetcher ByHasBars(Func<string, bool> hasBars) =>
            new(code => hasBars(code) ? new DateTime(2023, 8, 1) : null);

        public List<string> Asked { get; } = new();
        public bool SupportsHfq => true;
        public event Action<string>? OnStatus { add { } remove { } }

        public Task<(string Name, List<Bar> Bars)> FetchAsync(
            string code, string granularity, DateTime? start, DateTime? end, CancellationToken ct = default)
        {
            Asked.Add(code);
            var d = lastBar(code);
            var bars = d is null
                ? new List<Bar>()
                : new List<Bar> { new() { Code = code, Granularity = granularity, PeriodStart = d.Value, Close = 1.0 } };
            return Task.FromResult((code, bars));
        }
    }
}
