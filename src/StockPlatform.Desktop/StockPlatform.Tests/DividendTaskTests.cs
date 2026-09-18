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

    // ── 公告索引（2026-09-18）────────────────────────────────────

    [Fact]
    public void 索引命中的无视水位线也要抓()
    {
        // 命中＝出了新方案或进度变了，昨天刚抓过也得再抓——这是索引法的全部意义。
        var now = new DateTime(2026, 9, 18);
        var states = new Dictionary<string, DividendFetchState> { ["600000"] = Ok("600000", now.AddDays(-1)) };

        // 公告日比上次抓取晚 ⇒ 抓
        var due = DividendTask.SelectDue(["600000"], states, now.AddDays(-90),
                                         hits: Notice(("600000", now)));

        Assert.Equal(["600000"], due);
    }

    [Fact]
    public void 上次抓取晚于那条公告就不再抓()
    {
        // 这条是"每天重抓 900 只"那个毛病的判据（2026-09-18 当天修）：
        // 45 天窗口里同一只票天天在索引里，但只有"上次抓取早于那条公告"才真要抓。
        var now = new DateTime(2026, 9, 18);
        var states = new Dictionary<string, DividendFetchState> { ["600000"] = Ok("600000", now) };

        // 公告是 10 天前的，而今天抓过了 ⇒ 已经覆盖，不抓
        Assert.Empty(DividendTask.SelectDue(["600000"], states, now.AddDays(-90),
                                            hits: Notice(("600000", now.AddDays(-10)))));
    }

    [Fact]
    public void 当天出的公告_当天抓过之后第二天还会再抓一次()
    {
        // 公告只有日期没有时刻，所以"当天抓过"不能算覆盖当天的公告——
        // 判据写成 notice > lastOk 的话，这条公告**永远抓不到**（第二天比较仍然相等）。
        // 取"公告日的次日零点"，代价是多抓一次，换的是绝不漏。
        var noticeDay = new DateTime(2026, 9, 18);
        var states = new Dictionary<string, DividendFetchState>
        {
            ["600000"] = Ok("600000", noticeDay.AddHours(9)),   // 当天上午抓的
        };

        Assert.Equal(["600000"],
            DividendTask.SelectDue(["600000"], states, noticeDay.AddDays(-90),
                                   hits: Notice(("600000", noticeDay))));
    }

    [Fact]
    public void 索引命中的排在到期的前面()
    {
        // 被每轮上限或 Deadline 截断时，先保住真有新数据的那些。
        var now = new DateTime(2026, 9, 18);
        var states = new Dictionary<string, DividendFetchState>
        {
            ["000001"] = Ok("000001", now.AddDays(-200)),   // 很旧，到期
            ["600000"] = Ok("600000", now.AddDays(-1)),     // 很新，但索引命中
        };

        var due = DividendTask.SelectDue(["000001", "600000"], states, now.AddDays(-90),
                                         hits: Notice(("600000", now)));

        Assert.Equal(["600000", "000001"], due);
    }

    [Fact]
    public void 索引为空时仍按水位线兜底()
    {
        // 索引拿不到 ⇒ hits 是空集合。这时**绝不能**变成"本轮没有要抓的"，那是静默漏抓。
        var now = new DateTime(2026, 9, 18);
        var states = new Dictionary<string, DividendFetchState> { ["000001"] = Ok("000001", now.AddDays(-200)) };

        var due = DividendTask.SelectDue(["000001", "600000"], states, now.AddDays(-90),
                                         hits: Notice());

        Assert.Equal(["600000", "000001"], due);   // 没抓过的 + 到期的，一个不少
    }

    [Fact]
    public void 退市股走更长的那一档()
    {
        // 退市股不在索引里，而分红是静态历史——200 天前抓过的在市股该抓，退市股不该。
        var now = new DateTime(2026, 9, 18);
        var states = new Dictionary<string, DividendFetchState>
        {
            ["000001"] = Ok("000001", now.AddDays(-200)),
            ["600001"] = Ok("600001", now.AddDays(-200)),
        };

        var due = DividendTask.SelectDue(
            ["000001", "600001"], states, now.AddDays(-90),
            hits: Notice(),
            delisted: new HashSet<string> { "600001" },
            delistedCutoff: now.AddDays(-365));

        Assert.Equal(["000001"], due);
    }

    [Fact]
    public void 退市股过了一年也要重抓()
    {
        var now = new DateTime(2026, 9, 18);
        var states = new Dictionary<string, DividendFetchState> { ["600001"] = Ok("600001", now.AddDays(-400)) };

        var due = DividendTask.SelectDue(
            ["600001"], states, now.AddDays(-90),
            hits: Notice(),
            delisted: new HashSet<string> { "600001" },
            delistedCutoff: now.AddDays(-365));

        Assert.Equal(["600001"], due);
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

    /// <summary>造一份"代码 → 最新公告日"的索引结果。</summary>
    private static Dictionary<string, DateTime> Notice(params (string Code, DateTime Day)[] items)
        => items.ToDictionary(x => x.Code, x => x.Day, StringComparer.Ordinal);

    private static DividendTask.DividendOutcome Good(string code) =>
        new(code, [new DividendRow { Code = code }], [], true, null, false);

    private static DividendTask.DividendOutcome Fail(string code, bool limited) =>
        new(code, [], [], false, "boom", limited);
}
