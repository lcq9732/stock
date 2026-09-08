using System.Text.Json;
using Microsoft.Data.Sqlite;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 行业景气指标的解析与落库（2026-09-07）。
///
/// JSON 全部是从线上原样抄的，不联网。
///
/// 盯的两件事：
/// 1. <b>filter 拼错不会报错、只会静默返回 0 条</b>——少个 IS_POSED、日期格式不对、走错接口，
///    表现全都一样："这个指标没数据"。这种错不测就发现不了。
/// 2. 两种写入语义别搞混：目录是快照（空集合绝不清库），序列是累积（不删旧）。
/// </summary>
public class IndustryIndicatorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteIndustryIndicatorRepository _repo;

    public IndustryIndicatorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"indicator_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteIndustryIndicatorRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    // ── 线上原样抄来的三行 ──

    private const string CatalogRow = """
        {"CHART_TYPE":"折线图","INDICATOR_GRANULARITY":"002","INDICATOR_ID":"EMI00662712",
         "INDICATOR_NAME":"钛白粉商品指数","INDICATOR_ORDER":1,"ORIG_NAME":"钛白粉(硫.金.厂)指数",
         "SECUCODE":"002136.SZ","SOURCE":"生意社","UNIT":null,
         "UPDATE_DATE":"2026-09-06 00:00:00","UPDATE_FREQUENCY":"日","VALUE":66.75}
        """;

    private const string LineRow = """
        {"CLOSE_PRICE":42.9714299104,"INDICATOR_ID":"EMI00139010","INDICATOR_NAME":"全国猪粮比价",
         "IS_POSED":"1","SECUCODE":"002714.SZ","UNIT":null,"UPDATE_DATE":"2024-04-03 00:00:00",
         "UPDATE_FREQUENCY":"周","VALUE":6.25}
        """;

    private const string BarRow = """
        {"INDICATOR_ID":"EMI01642671","INDICATOR_NAME":"当月商品猪价格",
         "ORIG_NAME":"牧原股份:销售均价:生猪:商品猪价格:当月值","SECUCODE":"002714.SZ",
         "SOURCE":"上市公司公告","UNIT":"元/公斤","UPDATE_DATE":"2024-03-01 00:00:00",
         "UPDATE_FREQUENCY":"月","VALUE":14.24,"YOY_VALUE":"-5.07%","YOY_VALUE1":-5.07}
        """;

    // ════════ 解析 ════════

    [Fact]
    public void 解析目录行_UNIT为null不能变成字符串null()
    {
        var ind = EastMoneyIndustryIndicatorProvider.ParseIndicator(J(CatalogRow), new DateTime(2026, 9, 7));

        Assert.Equal("EMI00662712", ind.IndicatorId);
        Assert.Equal("钛白粉商品指数", ind.Name);
        Assert.Equal("钛白粉(硫.金.厂)指数", ind.OrigName);
        Assert.Equal("", ind.Unit);              // JSON 里是 null，不是 "null"
        Assert.Equal("日", ind.Frequency);
        Assert.Equal("002", ind.Granularity);
        Assert.Equal("折线图", ind.ChartType);
        Assert.Equal("生意社", ind.Source);
    }

    [Fact]
    public void 折线图行没有同比_留null而不是零()
    {
        // 0 和"没有"是两回事：同比 0% 是真的持平，null 是这个指标压根不给同比。
        // 混了的话分析侧会把 72 个折线图指标全当成"同比持平"。
        var p = EastMoneyIndustryIndicatorProvider.ParsePoint(J(LineRow));

        Assert.NotNull(p);
        Assert.Equal("EMI00139010", p!.Value.IndicatorId);
        Assert.Equal(new DateTime(2024, 4, 3), p.Value.TradeDate);
        Assert.Equal(6.25, p.Value.Value);
        Assert.Null(p.Value.YoyPct);
    }

    [Fact]
    public void 柱状图行取数值同比而不是带百分号的字符串()
    {
        var p = EastMoneyIndustryIndicatorProvider.ParsePoint(J(BarRow));

        Assert.NotNull(p);
        Assert.Equal(14.24, p!.Value.Value);
        Assert.Equal(-5.07, p.Value.YoyPct);     // YOY_VALUE1，不是 "-5.07%"
    }

    [Theory]
    [InlineData("""{"INDICATOR_ID":"EMI1","UPDATE_DATE":"2024-04-03 00:00:00"}""")]           // 缺 VALUE
    [InlineData("""{"INDICATOR_ID":"EMI1","VALUE":1.0}""")]                                   // 缺日期
    [InlineData("""{"UPDATE_DATE":"2024-04-03 00:00:00","VALUE":1.0}""")]                     // 缺指标号
    public void 缺关键字段的行丢弃而不是当成零(string json)
        => Assert.Null(EastMoneyIndustryIndicatorProvider.ParsePoint(J(json)));

    // ════════ filter 与接口选择 ════════

    [Fact]
    public void 折线图带IS_POSED_柱状图不带()
    {
        // ★ 走错接口/漏掉 IS_POSED 都是**静默返回 0 条**，不会报错。
        var line = new IndustryIndicator { IndicatorId = "EMI00139010", ChartType = "折线图" };
        var bar = new IndustryIndicator { IndicatorId = "EMI01642671", ChartType = "柱状图" };

        Assert.Equal("""(SECUCODE="002714.SZ")(IS_POSED="1")(INDICATOR_ID="EMI00139010")""",
            EastMoneyIndustryIndicatorProvider.BuildSeriesFilter(line, "002714.SZ", null));
        Assert.Equal("""(SECUCODE="002714.SZ")(INDICATOR_ID="EMI01642671")""",
            EastMoneyIndustryIndicatorProvider.BuildSeriesFilter(bar, "002714.SZ", null));

        Assert.Equal("RPTA_DATA_IF_LINECHART", EastMoneyIndustryIndicatorProvider.ReportFor(line));
        Assert.Equal("RPTA_DATA_IF_BARCHART", EastMoneyIndustryIndicatorProvider.ReportFor(bar));
    }

    [Fact]
    public void 增量水位线拼成东财认的日期格式()
    {
        var ind = new IndustryIndicator { IndicatorId = "EMI1", ChartType = "柱状图" };
        var f = EastMoneyIndustryIndicatorProvider.BuildSeriesFilter(
            ind, "002714.SZ", new DateTime(2026, 8, 1, 13, 45, 0));

        // 单引号、只到日、大于号（不是 >=，否则每轮重复拉最后一天）
        Assert.EndsWith("(UPDATE_DATE>'2026-08-01')", f);
    }

    [Theory]
    [InlineData("002714.SZ", "002714")]
    [InlineData("600519.SH", "600519")]
    [InlineData("920819.BJ", "920819")]   // 北交所，920 开头
    [InlineData("00700.HK", null)]        // 港股
    [InlineData("AAPL.O", null)]          // 美股
    [InlineData("002714", null)]          // 没后缀
    [InlineData("", null)]
    public void 代码去后缀只收A股(string secu, string? expected)
        => Assert.Equal(expected, EastMoneyIndustryIndicatorProvider.ToLocalCode(secu));

    // ════════ 落库语义 ════════

    private static IndustryIndicator Ind(string id, string chart = "折线图") => new()
    { IndicatorId = id, Name = "指标" + id, ChartType = chart, Frequency = "日", FetchedAt = DateTime.Now };

    [Fact]
    public void 空批次一律是空操作_绝不清库()
    {
        // 抓取失败时保留库里上一次的，跟板块快照同一条铁律
        _repo.UpsertIndicators([Ind("EMI1")]);
        _repo.ReplaceLinks([new StockIndicatorLink("002714", "EMI1", 1)]);
        _repo.UpsertPoints([new IndicatorPoint("EMI1", new DateTime(2026, 9, 1), 1.5, null)]);

        Assert.Equal(0, _repo.UpsertIndicators([]));
        Assert.Equal(0, _repo.ReplaceLinks([]));
        Assert.Equal(0, _repo.UpsertPoints([]));

        Assert.Equal((1, 1, 1), _repo.GetCounts());
    }

    [Fact]
    public void 序列是累积_第二批不删第一批()
    {
        _repo.UpsertIndicators([Ind("EMI1")]);
        _repo.UpsertPoints([new IndicatorPoint("EMI1", new DateTime(2026, 9, 1), 1.0, null)]);
        _repo.UpsertPoints([new IndicatorPoint("EMI1", new DateTime(2026, 9, 2), 2.0, null)]);

        Assert.Equal(2, _repo.GetCounts().Points);
        Assert.Equal(new DateTime(2026, 9, 2), _repo.GetLatestDates()["EMI1"]);
    }

    [Fact]
    public void 同一天重抓是覆盖不是重复()
    {
        // 增量用的是 >（不是 >=），正常不会重抓同一天；但首轮全量跟增量重叠时会，
        // 那时必须覆盖而不是插出两行——主键 (indicator_id, trade_date) 保证这一点。
        _repo.UpsertIndicators([Ind("EMI1")]);
        _repo.UpsertPoints([new IndicatorPoint("EMI1", new DateTime(2026, 9, 1), 1.0, null)]);
        _repo.UpsertPoints([new IndicatorPoint("EMI1", new DateTime(2026, 9, 1), 9.9, -3.2)]);

        Assert.Equal(1, _repo.GetCounts().Points);
    }

    [Fact]
    public void 映射是快照_下架的旧关联会被清掉()
    {
        _repo.UpsertIndicators([Ind("EMI1"), Ind("EMI2")]);
        _repo.ReplaceLinks([
            new StockIndicatorLink("002714", "EMI1", 1),
            new StockIndicatorLink("000876", "EMI1", 1),
            new StockIndicatorLink("002714", "EMI2", 2)]);
        Assert.Equal(3, _repo.GetCounts().Links);

        // 东财把 EMI2 摘了，000876 也不再关联 EMI1
        _repo.ReplaceLinks([new StockIndicatorLink("002714", "EMI1", 1)]);

        Assert.Equal(1, _repo.GetCounts().Links);
        Assert.Equal(2, _repo.GetCounts().Indicators);   // 字典不受影响，只清映射
    }

    [Fact]
    public void 代表股按代码排序取第一只_必须稳定()
    {
        // ★ 选谁都行（同一指标各股票的值完全一样），但**每轮必须选到同一只**——
        //   换来换去的话日志对不上、出了问题没法复现。
        _repo.UpsertIndicators([Ind("EMI1")]);
        _repo.ReplaceLinks([
            new StockIndicatorLink("600519", "EMI1", 3),
            new StockIndicatorLink("000876", "EMI1", 1),
            new StockIndicatorLink("002714", "EMI1", 2)]);

        Assert.Equal("000876", _repo.GetRepresentativeStocks()["EMI1"]);

        // 换个插入顺序，结果必须一样
        _repo.ReplaceLinks([
            new StockIndicatorLink("002714", "EMI1", 2),
            new StockIndicatorLink("600519", "EMI1", 3),
            new StockIndicatorLink("000876", "EMI1", 1)]);
        Assert.Equal("000876", _repo.GetRepresentativeStocks()["EMI1"]);
    }

    [Fact]
    public void 截断在指标边界_水位线粒度对得上()
    {
        // ★★ 这一条钉的是"这个任务为什么不需要完成度表"（2026-09-08 迁新架构时想清楚的）。
        //
        // 骨架会在 Deadline / MaxItems 到点时**从批中间收尾**，且那算正常完成。所以关键是
        // **批的粒度必须细于或等于水位线的粒度**：这里一批＝一个指标的完整序列，
        // 水位线是每个指标自己的 MAX(trade_date)，截断只可能落在指标边界上——
        // 被切掉的指标水位线还是旧的（或压根没有），下轮自然重抓。
        //
        // 对比【客户与供应商】：那边一批 2000 行、水位线却是"年"，粗了两个数量级，
        // 所以必须额外记 CustSuppYearState。满足了就不用加表，不满足就必须加。
        _repo.UpsertIndicators([Ind("EMI1"), Ind("EMI2"), Ind("EMI3")]);

        // 模拟：抓完 EMI1，正要抓 EMI2 时被截断
        _repo.UpsertPoints([
            new IndicatorPoint("EMI1", new DateTime(2026, 9, 1), 1.0, null),
            new IndicatorPoint("EMI1", new DateTime(2026, 9, 2), 2.0, null)]);

        var w = _repo.GetLatestDates();
        Assert.Equal(new DateTime(2026, 9, 2), w["EMI1"]);   // 抓完的：水位线前进了
        Assert.False(w.ContainsKey("EMI2"));                  // 被截断的：没有水位线 → 下轮全量
        Assert.False(w.ContainsKey("EMI3"));                  // 没轮到的：同上
    }

    [Fact]
    public void 半截序列的指标下轮从水位线续抓_不会重复()
    {
        // 一个指标的序列在落库中途没写完（进程被杀之类）也不会错乱：
        // 主键是 (indicator_id, trade_date)，续抓时重叠的那几天是覆盖不是插重复。
        _repo.UpsertIndicators([Ind("EMI1")]);
        _repo.UpsertPoints([new IndicatorPoint("EMI1", new DateTime(2026, 9, 1), 1.0, null)]);

        var since = _repo.GetLatestDates()["EMI1"];
        Assert.Equal(new DateTime(2026, 9, 1), since);

        // 下轮从 9-1 之后续，但即使数据源把 9-1 也带回来了也不会重复
        _repo.UpsertPoints([
            new IndicatorPoint("EMI1", new DateTime(2026, 9, 1), 1.5, null),
            new IndicatorPoint("EMI1", new DateTime(2026, 9, 2), 2.0, null)]);

        Assert.Equal(2, _repo.GetCounts().Points);
    }

    [Theory]
    [InlineData("002714", "002714.SZ")]
    [InlineData("600519", "600519.SH")]
    [InlineData("300750", "300750.SZ")]   // 创业板
    [InlineData("688981", "688981.SH")]   // 科创板
    [InlineData("920819", "920819.BJ")]   // ★ 北交所 920 开头
    public void 代表股转成东财要的带后缀形式(string code, string expected)
        => Assert.Equal(expected, EastMoneyIndustryIndicatorProvider.ToSecuCode(code));

    [Fact]
    public void 代表股转换和反向转换是一对()
    {
        // ToSecuCode / ToLocalCode 必须互逆，否则"用代表股查回来的数据存到哪个 code"会错位
        foreach (var code in new[] { "002714", "600519", "920819" })
        {
            var secu = EastMoneyIndustryIndicatorProvider.ToSecuCode(code);
            Assert.Equal(code, EastMoneyIndustryIndicatorProvider.ToLocalCode(secu!));
        }
    }

    [Fact]
    public void 没抓过的指标不在水位线里_该走全量()
    {
        _repo.UpsertIndicators([Ind("EMI1"), Ind("EMI2")]);
        _repo.UpsertPoints([new IndicatorPoint("EMI1", new DateTime(2026, 9, 1), 1.0, null)]);

        var w = _repo.GetLatestDates();
        Assert.True(w.ContainsKey("EMI1"));
        Assert.False(w.ContainsKey("EMI2"));    // 不在字典里＝首轮，拉全量
    }

    [Fact]
    public void 指标字典重抓是更新而不是插重复()
    {
        _repo.UpsertIndicators([Ind("EMI1")]);
        var changed = Ind("EMI1");
        changed.Name = "改了名";
        changed.ChartType = "柱状图";
        _repo.UpsertIndicators([changed]);

        var all = _repo.GetIndicators();
        Assert.Single(all);
        Assert.Equal("改了名", all[0].Name);
        Assert.Equal("柱状图", all[0].ChartType);
    }
}
