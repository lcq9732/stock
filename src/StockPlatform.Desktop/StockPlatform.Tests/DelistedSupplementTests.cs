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

    /// <summary>
    /// ⭐ **很久以前退市的老股不许被判成"从未上市"**（2026-09-19 加）。
    ///
    /// 探测起点原来是"今天往前 3650 天"的滚动窗口，于是 2016 年以前退市的票在探测区间里
    /// 必然一根K线都没有，整类落进"给不出日K ⇒ 从未上市"被跳过。实机跑时
    /// <c>600087 退市长油</c>（2014 退）和 <c>600849 上海医药</c>老号（2010 换代码）双双中招——
    /// 后者恰恰是这一项存在的理由之一（两所终止上市名单给不出"已换代码的老号"）。
    /// 日志里它们混在"多半是过会后撤回/暂缓上市"那句里，读起来完全正常，**没有任何地方会报**。
    /// </summary>
    [Fact]
    public async Task 很久以前退市的老股不能被判成从未上市()
    {
        var list = new FakeList(
            new StockListEntry("600087", "退市长油"),      // 2014 退，十年滚动窗口刚好够不着
            new StockListEntry("600849", "上海医药"));     // 2010 换代码，更早
        var fetcher = new FakeFetcher(code => code == "600087"
            ? new DateTime(2014, 4, 11)
            : new DateTime(2010, 3, 1));

        var (_, log) = await RunAsync(list, fetcher);

        var rows = new SqliteDelistedRepository(_dbPath).GetAll();
        Assert.Equal(["600087", "600849"], rows.Select(r => r.Code).OrderBy(x => x));
        Assert.Contains(log, m => m.Contains("跳过 0 只（从未上市）"));
        // 钉死起点是固定日期，不是滚动窗口——否则这个测试会随时间推移悄悄失效
        Assert.All(fetcher.AskedStart, s => Assert.True(s <= new DateTime(1990, 12, 19),
                                                       $"探测起点 {s:yyyy-MM-dd} 晚于上交所开市日"));
    }

    /// <summary>
    /// ⭐ **存量里写错的 exchange 要回头改对**（2026-09-19 加）。
    ///
    /// 光修 <c>ExchangeTag</c> 不够：候选第一步就减掉"已知退市"，已经在名单里的行永远不会再
    /// 被走一遍，错值不会自愈。实测 <c>920680 广道退</c> 09-17 被旧规则写成 <c>szse</c>，
    /// 09-19 修完判据再跑也纹丝不动。自检是纯本地的，不许因此多发一次探测请求。
    /// </summary>
    [Fact]
    public async Task 存量里写错的exchange会被改对()
    {
        new SqliteDelistedRepository(_dbPath).Upsert([
            new DelistedStockRow { Code = "920680", Name = "广道退", Exchange = "szse" },   // 旧规则写的，错
            new DelistedStockRow { Code = "600000", Name = "退市浦发", Exchange = "sse" }]); // 本来就对
        var fetcher = FakeFetcher.ByHasBars(_ => true);

        var (_, log) = await RunAsync(new FakeList(), fetcher);

        var rows = new SqliteDelistedRepository(_dbPath).GetAll().ToDictionary(r => r.Code);
        Assert.Equal("bse", rows["920680"].Exchange);
        Assert.Equal("sse", rows["600000"].Exchange);   // 对的那行不动
        Assert.Empty(fetcher.Asked);                    // 纯本地，零请求
        Assert.Contains(log, m => m.Contains("920680") && m.Contains("szse→bse"));
    }

    /// <summary>
    /// 自检只动 <c>MarketClassifier</c> 认得出交易所的行。<c>ExchangeTag</c> 的兜底分支把未知号段
    /// 也写成 szse，那是"写新行时总得给个值"的将就；拿它覆盖存量就成了把没根据的猜测写进库。
    /// </summary>
    [Fact]
    public async Task 自检不碰认不出交易所的号段()
    {
        new SqliteDelistedRepository(_dbPath).Upsert([
            new DelistedStockRow { Code = "123456", Name = "认不出的号段", Exchange = "sse" }]);

        var (_, log) = await RunAsync(new FakeList(), FakeFetcher.ByHasBars(_ => true));

        Assert.Equal("sse", new SqliteDelistedRepository(_dbPath).GetAll().Single().Exchange);
        Assert.DoesNotContain(log, m => m.Contains("存量 exchange 自检"));
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
    /// <summary>
    /// ⭐ **差集的盲区**：还挂在在市名册里的已退市票，永远不会是巨潮差集的候选
    /// （候选要减去在市名册），于是名册说在市、档案说退市，谁也纠正不了谁。
    /// 实测 920305 云创退就这么卡了半年：K线停在 2026-07-29、名字都带"退"，
    /// <c>type</c> 还是 <c>'stock'</c>，每天的K线/资金流/名册轮询都在白抓它。
    /// 档案那一路**不减在市名册**，补的就是这个。
    /// </summary>
    [Fact]
    public async Task 档案说退市_名册却还当它在市_照样补进来()
    {
        PutLive("920305", "云创退");                       // 名册还当它在市
        var list = new FakeList(new StockListEntry("920305", "云创退"));   // 巨潮也当它在市 ⇒ 差集里没有它
        var fetcher = FakeFetcher.ByHasBars(_ => true);    // 最后一根K线在很久以前

        var (result, log) = await RunAsync(list, fetcher, new FakeProfiles(("920305", "云创退")));

        Assert.Equal(TaskState.Completed, result.State);
        var row = Assert.Single(new SqliteDelistedRepository(_dbPath).GetAll());
        Assert.Equal("920305", row.Code);
        // ⚠ 920 是北交所——原来写死 "Shanghai ? sse : szse"，会把它记成深市
        Assert.Equal("bse", row.Exchange);
        Assert.Null(row.DelistDate);                       // 档案不给终止日，也不许拿K线去推
        // 标成 delisted 才会退出日常轮询
        Assert.DoesNotContain(SqliteStockMetaUpsert.GetAll(_dbPath), x => x.Code == "920305");
        Assert.Contains(log, m => m.Contains("名册还当它在市"));
    }

    /// <summary>
    /// 档案那一路要**自己过号段白名单**：巨潮那份在 provider 里过过了，这份没有。
    /// 实测库里 41 只"档案说退市、名单没有"的票，40 只是 B股(200/900)和老三板(83x)，
    /// 放进来会污染分红抓取和选池。
    /// </summary>
    [Fact]
    public async Task 档案来源也要过号段白名单()
    {
        var fetcher = FakeFetcher.ByHasBars(_ => true);
        var profiles = new FakeProfiles(
            ("200002", "万科B"),        // B股
            ("900951", "退市大化"),     // B股
            ("832317", "观典防务"),     // 老三板（MarketClassifier 会把 83x 判成北交所，但那是抓K线用的近似）
            ("688086", "退市紫晶"));    // 这只才该进来

        await RunAsync(new FakeList(), fetcher, profiles);

        var rows = new SqliteDelistedRepository(_dbPath).GetAll();
        Assert.Equal(["688086"], rows.Select(r => r.Code));
    }

    /// <summary>
    /// 档案来源同样要走"还在交易就不算退市"那道确认。实测东财的 <c>listing_state</c> 只在
    /// 真摘牌后才置 2（319 只有K线的退市票，最后一根全都在 30 天以前），但万一哪天它把
    /// 退市整理期也标成 2，挡住的就是这一道——标错的代价是那只票被踢出日常轮询、
    /// K线从此不再更新，而且没有任何地方会报。
    /// </summary>
    [Fact]
    public async Task 档案说退市但还在交易的_不补()
    {
        var fetcher = new FakeFetcher(code => code == "600000" ? DateTime.Today : DateTime.Today.AddYears(-1));
        var profiles = new FakeProfiles(("600000", "浦发银行"), ("688086", "退市紫晶"));

        var (_, log) = await RunAsync(new FakeList(), fetcher, profiles);

        var rows = new SqliteDelistedRepository(_dbPath).GetAll();
        Assert.Equal(["688086"], rows.Select(r => r.Code));
        Assert.Contains(log, m => m.Contains("还在交易"));
    }

    /// <summary>两个来源都指向同一只票时只处理一次（否则会多发一次探测请求）。</summary>
    [Fact]
    public async Task 两个来源撞上同一只票_不重复处理()
    {
        var list = new FakeList(new StockListEntry("688086", "退市紫晶"));
        var fetcher = FakeFetcher.ByHasBars(_ => true);

        var (_, log) = await RunAsync(list, fetcher, new FakeProfiles(("688086", "退市紫晶")));

        Assert.Single(new SqliteDelistedRepository(_dbPath).GetAll());
        Assert.Contains(log, m => m.Contains("候选 1 只"));
    }

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
        IStockListProvider list, IBarDataFetcher fetcher, FakeProfiles? profiles = null)
    {
        var task = new DelistedSupplementTask(_paths, list, profiles ?? new FakeProfiles(), fetcher);
        var log = new List<string>();
        task.OnProgress += p => log.Add(p.Text);
        var result = await task.RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);
        return (result, log);
    }

    /// <summary>本地公司档案——只有"档案说已终止上市"这一份名单是这一项要读的。</summary>
    private sealed class FakeProfiles(params (string Code, string Name)[] delisted) : ICompanyProfileRepository
    {
        public void EnsureSchema() { }
        public int Upsert(IEnumerable<(CompanyProfile Profile, CompanyNarrative Narrative)> items) => 0;
        public List<(string Code, string FullName, string? Abbr)> GetAllNames() => [];
        public List<(string Code, string Name)> GetDelistedCodes() => [.. delisted];
        public (int Profiles, int Narratives) GetCounts() => (0, 0);
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
    ///
    /// ⚠ **只返回落在请求区间里的**——真实数据源就是这样，探测起点定得太晚就会拿回空数组。
    /// 这正是 <c>600087 退市长油</c> 被十年滚动窗口误判成"从未上市"的机制，不模拟出来就测不到。
    /// </summary>
    private sealed class FakeFetcher(Func<string, DateTime?> lastBar) : IBarDataFetcher
    {
        /// <summary>老写法的简便构造：只说给不给得出，给得出就当是很久以前的（＝已退市）。</summary>
        public static FakeFetcher ByHasBars(Func<string, bool> hasBars) =>
            new(code => hasBars(code) ? new DateTime(2023, 8, 1) : null);

        public List<string> Asked { get; } = new();
        /// <summary>每次探测请求的起点——用来钉死"起点是固定日期，不是滚动窗口"。</summary>
        public List<DateTime> AskedStart { get; } = new();
        public bool SupportsHfq => true;
        public event Action<string>? OnStatus { add { } remove { } }

        public Task<(string Name, List<Bar> Bars)> FetchAsync(
            string code, string granularity, DateTime? start, DateTime? end, CancellationToken ct = default)
        {
            Asked.Add(code);
            if (start is not null) AskedStart.Add(start.Value);
            var d = lastBar(code);
            var inRange = d is not null && (start is null || d >= start) && (end is null || d <= end);
            var bars = !inRange
                ? new List<Bar>()
                : new List<Bar> { new() { Code = code, Granularity = granularity, PeriodStart = d!.Value, Close = 1.0 } };
            return Task.FromResult((code, bars));
        }
    }
}
