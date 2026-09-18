using StockPlatform.Logic.Models;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【拉取分红送配】迁到新任务框架之后的两条判据（2026-09-18，见 doc/dividend-task-design.md）。
///
/// 测的是抽出来的两个静态方法——真跑一轮要发几千个请求，测不了；而这两条恰恰是这次迁移的
/// 全部意义所在：**中断之后不重抓**（SelectDue）和**限流别傻跑完**（IsDeadBatch）。
/// </summary>
public class DividendTaskTests
{
    private static DividendFetchState Ok(string code, DateTime okAt) =>
        new() { Code = code, LastOkAt = okAt, DividendRows = 3 };

    [Fact]
    public void 新鲜的不抓_过期的和没抓过的才抓()
    {
        var now = new DateTime(2026, 9, 18, 10, 0, 0);
        var cutoff = now.AddDays(-25);
        var states = new Dictionary<string, DividendFetchState>
        {
            ["600000"] = Ok("600000", now.AddDays(-1)),    // 昨天刚抓，跳过
            ["600001"] = Ok("600001", now.AddDays(-30)),   // 30 天前，过期
        };

        var due = DividendTask.SelectDue(["600000", "600001", "600002"], states, cutoff);

        // 600002 从没抓过 → 必抓；600000 新鲜 → 不抓
        Assert.Equal(["600002", "600001"], due);
    }

    [Fact]
    public void 失败过但成功过的仍按成功时刻算新鲜()
    {
        // 失败只写 LastFailAt，LastOkAt 要原样留着——否则一次失败就把"抓过"抹掉，
        // 下一轮它又成了没抓过的，正是这次要修的那个病的反面。
        var now = new DateTime(2026, 9, 18);
        var states = new Dictionary<string, DividendFetchState>
        {
            ["600000"] = new()
            {
                Code = "600000",
                LastOkAt = now.AddDays(-2),
                LastFailAt = now,
                FailReason = "限流",
            },
        };

        Assert.Empty(DividendTask.SelectDue(["600000"], states, now.AddDays(-25)));
    }

    [Fact]
    public void 只抓成功过一次但没有分红的票也算抓过()
    {
        // DividendRows = 0 是"那次确实没有"的事实，不是"没抓过"。
        // 这条不成立的话，全市场约三千只无分红的票每轮都会被重抓一遍。
        var now = new DateTime(2026, 9, 18);
        var states = new Dictionary<string, DividendFetchState>
        {
            ["300001"] = new() { Code = "300001", LastOkAt = now.AddDays(-3), DividendRows = 0 },
        };

        Assert.Empty(DividendTask.SelectDue(["300001"], states, now.AddDays(-25)));
    }

    [Fact]
    public void 顺序是先没抓过的再最旧的()
    {
        var now = new DateTime(2026, 9, 18);
        var states = new Dictionary<string, DividendFetchState>
        {
            ["000002"] = Ok("000002", now.AddDays(-40)),
            ["000003"] = Ok("000003", now.AddDays(-60)),
        };

        var due = DividendTask.SelectDue(["000001", "000002", "000003", "000004"], states, now.AddDays(-25));

        // 没抓过的两只排最前（彼此按代码定序），然后是最旧的 000003、再 000002
        Assert.Equal(["000001", "000004", "000003", "000002"], due);
    }

    [Fact]
    public void 整批因限流失败才算全军覆没()
    {
        Assert.True(DividendTask.IsDeadBatch([Fail("1", limited: true), Fail("2", limited: false)]));
    }

    [Fact]
    public void 有一只成功就不算全军覆没()
    {
        Assert.False(DividendTask.IsDeadBatch([Fail("1", limited: true), Good("2")]));
    }

    [Fact]
    public void 整批失败但跟限流无关的不算()
    {
        // 比如页面改版把解析全打挂了：那种该老老实实报失败，收工只会把问题藏起来。
        Assert.False(DividendTask.IsDeadBatch([Fail("1", limited: false), Fail("2", limited: false)]));
    }

    private static DividendTask.DividendOutcome Good(string code) =>
        new(code, [new DividendRow { Code = code }], [], true, null, false);

    private static DividendTask.DividendOutcome Fail(string code, bool limited) =>
        new(code, [], [], false, "boom", limited);
}
