using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【拉取行业分类】换东财 + 迁新框架（2026-09-10）。用真 SQLite 临时库跑，因为这一项最要紧的
/// 语义就在落库那一步：<b>整表替换</b>。
///
/// 盯的是三件错了不报错的事：
///   ① 半截名单进库——一批股票的行业无声消失，界面上只是"这只票恰好没有行业"；
///   ② 逐条覆盖留下旧版命名——两个源的大类名分属证监会分类的不同修订版
///      （"开采辅助活动" vs "开采专业及辅助性活动"），并存会把同一个行业裂成两个中性化分组；
///   ③ 门类字母丢失——东财不给字母，它只能来自两所，被覆盖掉就再也算不出证监会门类。
/// </summary>
public class IndustryTaskTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteIndustryRepository _repo;

    public IndustryTaskTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"industry_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteIndustryRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    private sealed class FakeProvider(string source, params StockIndustry[] rows) : IIndustryProvider
    {
        public event Action<string>? OnStatus { add { } remove { } }
        public string SourceName => source;
        public int Calls;

        public Task<List<StockIndustry>> GetAllAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(rows.ToList());
        }
    }

    private static StockIndustry Row(string code, string cls = "C", string clsName = "制造业", string major = "")
        => new() { Code = code, ClassCode = cls, ClassName = clsName, MajorName = major };

    private static StockIndustry[] Many(int n, string major)
        => Enumerable.Range(0, n).Select(i => Row($"{600000 + i}", major: major)).ToArray();

    private async Task<TaskRunResult> RunAsync(IIndustryProvider provider)
        => await new IndustryTask(_repo, provider).RunAsync(new TaskRunArgs(), CancellationToken.None);

    // ─────────────────── 落库语义 ───────────────────

    [Fact]
    public async Task 首次抓取_空库_全量写入并记来源()
    {
        var result = await RunAsync(new FakeProvider(IndustrySources.EastMoney,
            Row("600519", major: "酒、饮料和精制茶制造业"), Row("000001", "J", "金融业", "货币金融服务")));

        Assert.Equal(TaskState.Completed, result.State);
        var best = _repo.GetBestByCode();
        Assert.Equal("酒、饮料和精制茶制造业", best["600519"]);
        Assert.Equal("货币金融服务", best["000001"]);
        Assert.Equal(2, _repo.Count());
    }

    [Fact]
    public async Task 换源_旧版命名不会残留在库里()
    {
        // 旧版（新浪）：两只票，其中 002828 用的是老名字
        _repo.ReplaceAll([Row("600519", major: "酒精及饮料制造业"), Row("002828", major: "开采辅助活动")],
                         IndustrySources.Sina);

        // 新版（东财）：只提到 600519，002828 换了名字
        await RunAsync(new FakeProvider(IndustrySources.EastMoney,
            Row("600519", major: "酒、饮料和精制茶制造业"),
            Row("002828", major: "开采专业及辅助性活动")));

        var majors = _repo.GetBestByCode().Values.ToHashSet();
        // 关键断言：老名字一个都不能留下——留下就等于同一个行业裂成两个分组
        Assert.DoesNotContain("酒精及饮料制造业", majors);
        Assert.DoesNotContain("开采辅助活动", majors);
        Assert.Equal(2, _repo.Count());
    }

    [Fact]
    public async Task 换源_新源没提到的票不会留着旧行()
    {
        // 规模要够大，否则先撞上"少 5% 就放弃"那道护栏（100 → 99 只属正常波动）。
        var old = Many(99, "老名字").Append(Row("999999", major: "已退市的票")).ToArray();
        _repo.ReplaceAll(old, IndustrySources.Sina);

        await RunAsync(new FakeProvider(IndustrySources.EastMoney, Many(99, "新名字")));

        // ReplaceAll 是整表替换：上一版里有、这一版没有的票不该留在库里
        Assert.Equal(99, _repo.Count());
        Assert.False(_repo.GetBestByCode().ContainsKey("999999"));
        Assert.DoesNotContain("老名字", _repo.GetBestByCode().Values);
    }

    // ─────────────────── 护栏 ───────────────────

    [Fact]
    public async Task 返回空_不写库_报失败()
    {
        _repo.ReplaceAll([Row("600519", major: "原有数据")], IndustrySources.Sina);

        var result = await RunAsync(new FakeProvider(IndustrySources.EastMoney));

        Assert.Equal(TaskState.Failed, result.State);
        // 库里上一版必须原样留着——空结果覆盖上去就是"行业页突然空了"
        Assert.Equal(1, _repo.Count());
        Assert.Equal("原有数据", _repo.GetBestByCode()["600519"]);
    }

    [Fact]
    public async Task 半截名单_少于库里九成五_不写库_报失败()
    {
        _repo.ReplaceAll(Many(100, "原有"), IndustrySources.Sina);

        var result = await RunAsync(new FakeProvider(IndustrySources.EastMoney, Many(90, "半截")));

        Assert.Equal(TaskState.Failed, result.State);
        Assert.Contains("半截名单", string.Join("；", result.Errors));
        Assert.Equal(100, _repo.Count());
        Assert.Equal("原有", _repo.GetBestByCode()["600000"]);
    }

    [Fact]
    public async Task 略少于库里但在容差内_照常写入()
    {
        _repo.ReplaceAll(Many(100, "原有"), IndustrySources.Sina);

        // 96 只 > 100×0.95，属正常波动（退市/停牌名单变动），不该拦
        var result = await RunAsync(new FakeProvider(IndustrySources.EastMoney, Many(96, "新的")));

        Assert.Equal(TaskState.Completed, result.State);
        Assert.Equal(96, _repo.Count());
    }

    [Fact]
    public async Task 空库首次抓取_不受少于九成五的护栏影响()
    {
        var result = await RunAsync(new FakeProvider(IndustrySources.EastMoney, Many(3, "新的")));

        Assert.Equal(TaskState.Completed, result.State);
        Assert.Equal(3, _repo.Count());
    }

    // ─────────────────── 东财解析 ───────────────────

    [Theory]
    [InlineData("制造业-酒、饮料和精制茶制造业", "制造业", "酒、饮料和精制茶制造业")]
    [InlineData("金融业-货币金融服务", "金融业", "货币金融服务")]
    // 大类名里带顿号是常态，不能被当成分隔符
    [InlineData("水利、环境和公共设施管理业-生态保护和环境治理业", "水利、环境和公共设施管理业", "生态保护和环境治理业")]
    // 只有一段：当作只有门类，不能把整串塞进大类
    [InlineData("综合", "综合", "")]
    [InlineData("", "", "")]
    public void 拆两级行业名(string raw, string expectClass, string expectMajor)
    {
        var (cls, major) = ExchangeEastMoneyIndustryProvider.SplitCsrcName(raw);
        Assert.Equal(expectClass, cls);
        Assert.Equal(expectMajor, major);
    }

    // ─────────────────── A股过滤（2026-09-10 实机验证抓到的 bug）───────────────────
    //
    // 这张报表 count=24810 行，里头大量不是 A 股。只判"6 位数字 + MarketClassifier 认得"
    // 会放进港股之类，实测抓回 21018 只（真实约 6000 只）而且一个错都不报——
    // 决定性的判据是 SECUCODE 后缀。

    [Theory]
    [InlineData("600519.SH", "600519", true)]
    [InlineData("000001.SZ", "000001", true)]
    [InlineData("920002.BJ", "920002", true)]   // 920 是北交所，别再自写前缀规则
    [InlineData("600519.sh", "600519", true)]   // 后缀大小写不该影响
    public void A股行被收下(string secucode, string code, bool expect)
        => Assert.Equal(expect, ExchangeEastMoneyIndustryProvider.IsAShare(secucode, code, out _));

    [Theory]
    [InlineData("00700.HK", "00700")]           // 港股
    [InlineData("600519.HK", "600519")]         // 形状像 A 股、后缀不是 —— 正是漏掉的那一类
    [InlineData("AAPL.O", "AAPL")]              // 美股
    [InlineData("", "600519")]                  // 没有 SECUCODE 就不能认
    [InlineData("12345.SH", "12345")]           // 位数不对
    public void 非A股行被挡掉(string secucode, string code)
        => Assert.False(ExchangeEastMoneyIndustryProvider.IsAShare(secucode, code, out _));

    [Fact]
    public void 拆两级行业名_只按第一个连字符拆()
    {
        // 真出现带连字符的大类名时，后半段要完整保留，不能被截断
        var (cls, major) = ExchangeEastMoneyIndustryProvider.SplitCsrcName("制造业-A-B制造业");
        Assert.Equal("制造业", cls);
        Assert.Equal("A-B制造业", major);
    }
}
