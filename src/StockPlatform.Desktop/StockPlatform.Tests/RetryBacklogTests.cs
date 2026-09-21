using StockPlatform.Data.Orchestration;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【重新拉取失败】待办清单的测试（2026-09-13）。
///
/// 这些测试守的是一个具体事故：体检写进 manifest 的两类待办
/// （MissingBars / MissingNetInflowDays）从 2026-09-02 起就没进过界面上那行字，
/// 界面显示"09-11日线 1 只"而实际要跑 1909 段、20.8 万个交易日，几个小时。
/// 更要命的是"这一轮有没有活"的判定也漏了它们——别的名单一清零，手动点会被重取入口的
/// early-return 回绝、自动重试也认为名单已清零不再排，那 1909 段就永远补不上、而且一声不吭。
/// 所以每一条"体检类待办要出现在清单里"的断言都不是形式主义。
/// </summary>
public class RetryBacklogTests
{
    /// <summary>IsValueIssue 是由 Reason 派生的（Reason != "gap" 即值问题），所以只设 Reason。</summary>
    private static MissingBarRange Gap(string code, string gran, int days = 5,
                                       string reason = AuditFindingKind.Gap) =>
        new()
        {
            Code = code, Granularity = gran,
            From = new DateTime(2026, 9, 4), To = new DateTime(2026, 9, 10),
            Days = days, Reason = reason,
        };

    /// <summary>
    /// 走一遍"老格式 → 统一待办"的迁移再派生——<c>JsonManifestStore.Load</c> 里就是这么做的。
    /// 这些用例因此同时覆盖了迁移路径：老 manifest 升上来，一件待办都不能丢。
    /// </summary>
    private static RetryBacklog Backlog(Manifest m)
    {
        m.MigrateLegacyTodos();
        return RetryBacklog.From(m);
    }

    [Fact]
    public void 空名单_没有待办也不会自称有()
    {
        var b = Backlog(new Manifest());
        Assert.Empty(b.Items);
        Assert.False(b.Any);
        Assert.Equal(0, b.ActionableCount);
        Assert.Equal("无失败", b.Describe());
    }

    [Fact]
    public void 体检查出的历史空洞_必须进清单且让这一轮有活可干()
    {
        // 这就是 2026-09-13 那份真实 manifest 的形状：别的名单全空，只有体检写的空洞。
        var m = new Manifest();
        for (int i = 0; i < 1907; i++) m.MissingBars.Add(Gap($"{600000 + i}", Granularity.DayRaw, days: 109));

        var b = Backlog(m);

        // ⚠ 这三条是事故的正面回归。老的 FailedRetrySummary 在这个输入下 Any=false、
        //    Describe()="无失败"，于是手动点会被重取入口的 early-return 回绝、
        //    自动重试也认为"名单已清零"不再排——而执行链其实照样会去跑那 1907 段。
        Assert.True(b.Any);
        Assert.Equal(1907, b.ActionableCount);
        Assert.Contains("不复权空洞 1907 段", b.Describe());
    }

    [Fact]
    public void 段数和交易日数都要显示_只给段数看不出这是个几小时的活()
    {
        var m = new Manifest();
        for (int i = 0; i < 1907; i++) m.MissingBars.Add(Gap($"{600000 + i}", Granularity.DayRaw, days: 109));

        Assert.Contains("20.8万交易日", Backlog(m).Describe());
    }

    [Fact]
    public void 三个口径各算各的_不合成一条()
    {
        var m = new Manifest();
        m.MissingBars.Add(Gap("000001", Granularity.Day));
        m.MissingBars.Add(Gap("000001", Granularity.DayHfq));
        m.MissingBars.Add(Gap("000001", Granularity.DayRaw));

        var b = Backlog(m);

        // 三个口径的水位线、历史起点、能不能抓全都不一样，各补各的
        Assert.Equal(3, b.Items.Count);
        Assert.Equal(
            new[] { "前复权空洞", "后复权空洞", "不复权空洞" }.OrderBy(x => x),
            b.Items.Select(i => i.Label).OrderBy(x => x));
    }

    [Fact]
    public void 值问题按原因分类显示_而且它是补得了的()
    {
        var m = new Manifest();
        m.MissingBars.Add(Gap("000001", Granularity.Day, reason: AuditFindingKind.Intraday));

        var b = Backlog(m);

        // ⚠ 值问题**是可执行的**：BarFetchTaskBase.FillValueAsync 真的会去抓去改。
        //   FillAuditedGapsAsync 里"值类记录这一轮先原样留着"那句说的是缺行那个循环里先不动，
        //   等缺行跑完再单独处理——不是整轮不补。
        Assert.Single(b.Items);
        Assert.Single(b.Actionable);
        Assert.True(b.Any);
        Assert.Contains("盘中固化 1 段", b.Describe());
    }

    [Fact]
    public void 值问题和缺行分开计数_补法和复查方式不一样()
    {
        var m = new Manifest();
        m.MissingBars.Add(Gap("000001", Granularity.Day));
        m.MissingBars.Add(Gap("000002", Granularity.Day, reason: AuditFindingKind.Intraday));

        var b = Backlog(m);

        // 分成两项而不是合成"前复权 2 段"：复查方式根本不同（缺行看"行在不在"，
        // 值错要按 Reason 重查对应判据），合并显示会让人以为它们是一回事。
        Assert.Equal(2, b.Items.Count);
        Assert.Equal(2, b.ActionableCount);
        Assert.Contains("前复权空洞 1 段", b.Describe());
        Assert.Contains("盘中固化 1 段", b.Describe());
    }

    // 注：`Actionable` 那条分流（不可执行的不进重取那行、也不让 Any 为真）**现在没有测试**，
    // 因为当前每一类待办都是补得了的，构造不出反例。机制本身留着：以后真挂进"只报不补"的
    // 待办时，在 RetryBacklog.Describe 里标一下 actionable:false，连同测试一起补。

    [Fact]
    public void 老记录没有口径字段_按前复权算不会丢()
    {
        var m = new Manifest();
        m.MissingBars.Add(new MissingBarRange
        {
            Code = "000001", Granularity = "",      // 2026-09-04 之前的记录
            From = new DateTime(2020, 1, 1), To = new DateTime(2020, 1, 10), Days = 7,
        });

        var b = Backlog(m);

        Assert.Single(b.Items);
        Assert.Equal("前复权空洞", b.Items[0].Label);
        Assert.True(b.Any);
    }

    [Fact]
    public void 资金流缺失日也要进清单()
    {
        var m = new Manifest();
        m.MissingNetInflowDays.Add(new MissingDayRetry { Day = new DateTime(2026, 9, 9) });

        var b = Backlog(m);

        Assert.True(b.Any);
        Assert.Contains("资金流缺失日 1 天", b.Describe());
    }

    [Fact]
    public void 市值按轮不按只_整轮扫描的代码数没有多少只票缺数据的含义()
    {
        var m = new Manifest();
        for (int i = 0; i < 5544; i++) m.FailedMarketCapCodes.Add($"{600000 + i}");

        var b = Backlog(m);

        // 加总成 5544 会被读成"5544 只票的数据丢了"，实际是一次快照没取到
        Assert.Equal("市值 1 轮", b.Describe());
        Assert.Equal(1, b.ActionableCount);
    }

    [Fact]
    public void 当天日线带上日期_当天日线四个字含义太模糊()
    {
        var m = new Manifest { MissingDayDate = new DateTime(2026, 9, 11) };
        m.MissingDayCodes.Add("000001");

        Assert.Contains("09-11日线 1 只", Backlog(m).Describe());
    }

    [Fact]
    public void 项目太多时收成等N项_一行字不能无限长()
    {
        var m = new Manifest { MissingDayDate = new DateTime(2026, 9, 11) };
        m.MissingDayCodes.Add("000001");
        m.FailedCodes.AddRange(new[] { "1", "2" });
        m.FailedNetInflowCodes.AddRange(new[] { "1", "2", "3" });
        m.FailedDividendCodes.AddRange(new[] { "1", "2", "3", "4" });
        m.FailedShareholderCodes.AddRange(new[] { "1", "2", "3", "4", "5" });

        var text = Backlog(m).Describe();

        Assert.Equal(RetryBacklog.MaxDescribeParts, text.Split(" · ").Length - 1);
        Assert.Contains("等 2 项", text);
        // 大头排前面：股东 5 只最多
        Assert.StartsWith("股东 5 只", text);
    }

    [Fact]
    public void 每个待办的归属任务都要是真实存在的任务()
    {
        // TaskId 是字符串（Data 层不引用 Scheduling），拼错了编译期发现不了——这条兜着。
        var m = new Manifest { MissingDayDate = new DateTime(2026, 9, 11) };
        m.MissingDayCodes.Add("000001");
        m.FailedCodes.Add("000002");
        m.FailedMarketCapCodes.Add("000003");
        m.FailedNetInflowCodes.Add("000004");
        m.FailedIndexConsCodes.Add("000300");
        m.FailedIndexWeightCodes.Add("000300");
        m.FailedShareholderCodes.Add("000005");
        m.FailedDividendCodes.Add("000006");
        m.MissingNetInflowDays.Add(new MissingDayRetry { Day = new DateTime(2026, 9, 9) });
        m.MissingBars.Add(Gap("000007", Granularity.Day));
        m.MissingBars.Add(Gap("000008", Granularity.DayHfq));
        m.MissingBars.Add(Gap("000009", Granularity.DayRaw));

        var b = Backlog(m);

        Assert.NotEmpty(b.Items);
        foreach (var item in b.Items)
            Assert.True(Enum.TryParse<FetchActionId>(item.TaskId, out _),
                $"{item.Label} 的归属任务 \"{item.TaskId}\" 不是一个有效的 FetchActionId");
    }
    /// <summary>
    /// K线类待办**按任务叫名字**（2026-09-21）。原来只分前/后/不复权三档、其余一律"前复权"，
    /// 于是同一句提示里会出现两遍「前复权K线失败 N 只」——ETF 和指数顶着前复权的名字。
    /// </summary>
    [Theory]
    [InlineData(RetryTaskIds.StockDayBars, "前复权K线失败")]
    [InlineData(RetryTaskIds.StockHfqBars, "后复权K线失败")]
    [InlineData(RetryTaskIds.StockRawBars, "不复权K线失败")]
    [InlineData(RetryTaskIds.EtfBars, "ETFK线失败")]
    [InlineData(RetryTaskIds.EtfRawBars, "ETF不复权K线失败")]
    [InlineData(RetryTaskIds.IndexBars, "指数K线失败")]
    [InlineData(RetryTaskIds.DelistedTails, "退市股K线失败")]
    public void 失败名单按任务叫名字(string taskId, string expected)
    {
        var m = new Manifest();
        m.SetTodo(taskId, RetryTodoKind.Failed, [new RetryTarget { Code = "600000" }]);

        Assert.Equal(expected, RetryBacklog.From(m).Items.Single().Label);
    }

    /// <summary>空洞那一类同样按任务叫。</summary>
    [Fact]
    public void 空洞也按任务叫名字()
    {
        var m = new Manifest();
        m.SetTodo(RetryTaskIds.EtfBars, RetryTodoKind.Gap,
            [new RetryTarget { Code = "sh510300", Gran = Granularity.Day, Days = 3 }]);

        Assert.Equal("ETF空洞", RetryBacklog.From(m).Items.Single().Label);
    }

}
