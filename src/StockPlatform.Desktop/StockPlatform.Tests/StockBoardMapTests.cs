using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 东财三级行业/题材归属（2026-09-03）。
///
/// 盯的是两件在这个项目上反复出事的事：
///   1. **空集合绝不能清空已有数据**——板块那边踩过，行业分类被清空的后果更严重
///      （所有依赖行业的分析当场失效，而且不报错、只是结果悄悄变错）。
///   2. **取最细一级**要真的取到最细——行业中性化用错层级，等于分组分错，
///      因子检验的结论会整体失真。
/// </summary>
public class StockBoardMapTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"bmap_{Guid.NewGuid():N}.sqlite");

    private SqliteStockBoardMapRepository NewRepo()
    {
        var r = new SqliteStockBoardMapRepository(_db);
        r.EnsureSchema();
        return r;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch { }
    }

    private static StockIndustryEm Ind(string code, string board, string name, int level)
        => new() { Code = code, BoardCode = board, BoardName = name, BoardLevel = level, FetchedAt = DateTime.Now };

    [Fact]
    public void 写入空集合不动库里已有的行业()
    {
        var repo = NewRepo();
        repo.UpsertIndustries([Ind("600519", "BK0438", "酿酒行业", 2)]);

        // 接口返回空（限流/改版）时的写入路径——库里那条必须原样还在
        Assert.Equal(0, repo.UpsertIndustries([]));
        Assert.Equal(0, repo.UpsertThemes([]));
        Assert.Equal(1, repo.CountIndustries());
    }

    [Fact]
    public void 同股同板块重复写入不会翻倍()
    {
        var repo = NewRepo();
        // 快照表每季度全量重取，跑两轮不能变两份
        repo.UpsertIndustries([Ind("600519", "BK0438", "酿酒行业", 2)]);
        repo.UpsertIndustries([Ind("600519", "BK0438", "白酒", 2)]);

        Assert.Equal(1, repo.CountIndustries());
        Assert.Equal("白酒", repo.GetFinestIndustryByStock()["600519"].BoardName);
    }

    [Fact]
    public void 最细行业取层级最大的那条()
    {
        var repo = NewRepo();
        // 一只股票同时挂在三级行业树的三层上，中性化要用最细那层（三级）
        repo.UpsertIndustries([
            Ind("600519", "BK1041", "食品饮料", 1),
            Ind("600519", "BK0438", "酿酒行业", 2),
            Ind("600519", "BK1123", "白酒", 3),
        ]);

        var (code, name) = repo.GetFinestIndustryByStock()["600519"];
        Assert.Equal("BK1123", code);
        Assert.Equal("白酒", name);
    }

    [Fact]
    public void 题材带入选理由和精确匹配标记()
    {
        var repo = NewRepo();
        // 理由原文 + 精确匹配是判断「实质业务 vs 蹭概念」的判据，不能丢
        repo.UpsertThemes([new StockThemeEm
        {
            Code = "300499", BoardCode = "BK1137", BoardName = "液冷服务器",
            IsPrecise = true, BoardRank = 3,
            Reason = "公司在互动平台表示，已有液冷相关产品供货。",
            FetchedAt = DateTime.Now,
        }]);
        Assert.Equal(1, repo.CountThemes());
    }

    // ─────────── 按票整只替换（2026-09-22，见 IStockBoardMapRepository.ReplaceForStocks）───────────
    // 这几条钉的是那次改动的理由：先清全表再逐批写的话，抓了一半停下来就只剩一部分票的归属，
    // 其余票静默退回证监会粗分类。改成按票替换之后，库里每只票永远是自洽的一代。

    private static StockThemeEm Theme(string code, string board, string name)
        => new() { Code = code, BoardCode = board, BoardName = name, IsPrecise = true,
                   Reason = "测试", FetchedAt = DateTime.Now };

    [Fact]
    public void 整只替换_只动这一批的票别人一行不碰()
    {
        var repo = NewRepo();
        repo.UpsertIndustries([Ind("600519", "BK0438", "酿酒行业", 2), Ind("600000", "BK0475", "银行", 2)]);

        repo.ReplaceForStocks([Ind("600519", "BK0477", "白酒", 3)], []);

        // 600519 换成了新的那一条（旧的 BK0438 没了），600000 原样
        Assert.Equal("白酒", repo.GetFinestIndustryByStock()["600519"].BoardName);
        Assert.Equal("银行", repo.GetFinestIndustryByStock()["600000"].BoardName);
        Assert.Equal(2, repo.CountIndustries());
    }

    [Fact]
    public void 整只替换_旧的三级板块不会留下来压掉新值()
    {
        // 这是"按行代际清理"那个方案被否掉的理由：GetFinestIndustryByStock 取 board_level
        // 最大那条，残留一条旧的三级会**压掉**本轮的正确值——静默错值，比没有数据更糟。
        var repo = NewRepo();
        repo.UpsertIndustries([Ind("300750", "BK1033", "电池", 2), Ind("300750", "BK1303", "锂电池", 3)]);

        // 本轮东财只给到二级了
        repo.ReplaceForStocks([Ind("300750", "BK1033", "电池", 2)], []);

        Assert.Equal("电池", repo.GetFinestIndustryByStock()["300750"].BoardName);
        Assert.Equal(1, repo.CountIndustries());
    }

    [Fact]
    public void 整只替换_只有题材的票也会清掉它的旧行业()
    {
        // 「本轮它没有行业归属」也是事实。所以要替换的票是两份的并集，不是各管各的。
        var repo = NewRepo();
        repo.UpsertIndustries([Ind("600519", "BK0438", "酿酒行业", 2)]);

        repo.ReplaceForStocks([], [Theme("600519", "BK0500", "白酒概念")]);

        Assert.Equal(0, repo.CountIndustries());
        Assert.Equal(1, repo.CountThemes());
    }

    [Fact]
    public void 整只替换_两份都空是空操作()
    {
        var repo = NewRepo();
        repo.UpsertIndustries([Ind("600519", "BK0438", "酿酒行业", 2)]);

        Assert.Equal((0, 0), repo.ReplaceForStocks([], []));
        Assert.Equal(1, repo.CountIndustries());
    }

    [Fact]
    public void 清孤儿票_只删本轮之前落盘的行()
    {
        // 整轮抓完且对账通过之后的那一步：剩下的旧时间戳就是"本轮一行都没出现"的票
        // （被东财摘掉全部归属的那种）。
        var repo = NewRepo();
        var runAt = new DateTime(2026, 9, 22, 10, 0, 0);
        repo.UpsertIndustries([
            new StockIndustryEm { Code = "600519", BoardCode = "BK0477", BoardName = "白酒",
                                  BoardLevel = 3, FetchedAt = runAt },
            new StockIndustryEm { Code = "000001", BoardCode = "BK0475", BoardName = "银行",
                                  BoardLevel = 2, FetchedAt = runAt.AddDays(-90) },
        ]);
        repo.UpsertThemes([new StockThemeEm { Code = "000001", BoardCode = "BK0500", BoardName = "旧题材",
                                             IsPrecise = true, Reason = "测试", FetchedAt = runAt.AddDays(-90) }]);

        Assert.Equal(2, repo.PurgeOlderThan(runAt));
        Assert.Equal(1, repo.CountIndustries());
        Assert.Equal(0, repo.CountThemes());
        Assert.True(repo.GetFinestIndustryByStock().ContainsKey("600519"));
    }

    [Fact]
    public void 清孤儿票_同一时刻落盘的不算旧()
    {
        // 边界：provider 盖的是任务给的那个 runAt，一模一样的时刻不能被自己清掉。
        var repo = NewRepo();
        var runAt = new DateTime(2026, 9, 22, 10, 0, 0);
        repo.UpsertIndustries([new StockIndustryEm { Code = "600519", BoardCode = "BK0477",
                                                     BoardName = "白酒", BoardLevel = 3, FetchedAt = runAt }]);

        Assert.Equal(0, repo.PurgeOlderThan(runAt));
        Assert.Equal(1, repo.CountIndustries());
    }

    [Fact]
    public void ClearAll之后两张表都空()
    {
        var repo = NewRepo();
        repo.UpsertIndustries([Ind("600519", "BK0438", "酿酒行业", 2)]);
        repo.UpsertThemes([new StockThemeEm
        {
            Code = "600519", BoardCode = "BK0500", BoardName = "白酒概念",
            IsPrecise = true, Reason = "主营白酒", FetchedAt = DateTime.Now,
        }]);

        repo.ClearAll();
        Assert.Equal(0, repo.CountIndustries());
        Assert.Equal(0, repo.CountThemes());
    }

    [Fact]
    public void 计划项归在季度定期组且排在证监会分类之后()
    {
        // 归日更会每晚白跑 188 页——行业归属根本不按天变
        Assert.Equal(PlanGroupKind.Periodic,
            FetchTaskCatalog.DefaultGroupOf(FetchActionId.FetchStockBoardMap));

        var order = FetchTaskCatalog.PeriodicOrder;
        Assert.Contains(FetchActionId.FetchStockBoardMap, order);
        // 两份行业数据要挨着更新，免得出现"一份新一份旧"的错配
        Assert.True(order.ToList().IndexOf(FetchActionId.FetchStockBoardMap)
                  > order.ToList().IndexOf(FetchActionId.FetchIndustry));
    }

    [Fact]
    public void 计划项说明里写明了不替换证监会分类()
    {
        // 说明写错会让人以为老表可以删——那 109 只没覆盖的票会直接失去行业
        var note = FetchTaskCatalog.Info(FetchActionId.FetchStockBoardMap).Note;
        Assert.Contains("不替换", note);
        Assert.Contains("push2", note);
    }

    [Theory]
    // 东财同一批接口里布尔就有三种写法：业绩预告 IS_LATEST 给 "T"、题材 IS_PRECISE 给数字 1。
    // 原来写死 == "1" 的那处，实测让 14.5 万行 is_latest 全变成了 0——不报错、不缺行，
    // 只有这一列悄悄全错。这类"结构对、值域错"的问题体检最难发现，所以固定住。
    [InlineData("\"T\"", true)]
    [InlineData("\"t\"", true)]
    [InlineData("1", true)]
    [InlineData("\"1\"", true)]
    [InlineData("true", true)]
    [InlineData("\"true\"", true)]
    [InlineData("\"F\"", false)]
    [InlineData("0", false)]
    [InlineData("\"0\"", false)]
    [InlineData("null", false)]
    public void 东财布尔字段三种写法都认(string json, bool expected)
    {
        using var doc = System.Text.Json.JsonDocument.Parse($"{{\"X\":{json}}}");
        Assert.Equal(expected, StockPlatform.Data.Remote.EastMoneyJson.Bool(doc.RootElement, "X"));
    }

    [Fact]
    public void 东财布尔字段缺列时算false()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("{\"Y\":1}");
        Assert.False(StockPlatform.Data.Remote.EastMoneyJson.Bool(doc.RootElement, "X"));
    }
}

/// <summary>
/// 抓取时的批边界（<see cref="StockBoardMapBatchRule"/>，2026-09-22）。
///
/// 这不是性能取舍而是**正确性前提**：落库那侧按票整只替换，一只票的行被切成两批的话，
/// 第二批的 DELETE 会把第一批刚写进去的那几行删掉——表现出来只是"这只票的归属少了几条"，
/// 一个错都不报。所以这条规则要有用例钉着。
/// </summary>
public class StockBoardMapBatchRuleTests
{
    [Fact]
    public void 没攒够就不提交()
        => Assert.False(StockBoardMapBatchRule.ShouldFlush(4999, "600000", "600519"));

    [Fact]
    public void 攒够了但还是同一只票_不能提交()
        => Assert.False(StockBoardMapBatchRule.ShouldFlush(
            StockBoardMapBatchRule.BatchRows, "600519", "600519"));

    [Fact]
    public void 攒够了且换票了_提交()
        => Assert.True(StockBoardMapBatchRule.ShouldFlush(
            StockBoardMapBatchRule.BatchRows, "600000", "600519"));

    [Fact]
    public void 批里还什么都没有_不提交()
        => Assert.False(StockBoardMapBatchRule.ShouldFlush(
            StockBoardMapBatchRule.BatchRows, null, "600519"));

    /// <summary>
    /// 性质检验：照这条规则把一长串行切成批，**任何一只票的行都不能出现在两个批里**。
    /// 单点用例覆盖不到"连续几只票都超批长"这类组合，而那正是真数据的样子
    /// （一只票十几行，偶尔几十行）。
    /// </summary>
    [Fact]
    public void 照这条规则切批_没有一只票会跨批()
    {
        // 造 4000 只票、每只 1~40 行，总行数远超一批
        var rows = new List<string>();
        var rng = new Random(20260922);
        for (int i = 0; i < 4000; i++)
        {
            var code = $"60{i:D4}";
            int n = rng.Next(1, 41);
            for (int k = 0; k < n; k++) rows.Add(code);
        }

        var batches = new List<List<string>>();
        var current = new List<string>();
        string? last = null;
        foreach (var code in rows)
        {
            if (StockBoardMapBatchRule.ShouldFlush(current.Count, last, code))
            {
                batches.Add(current);
                current = [];
            }
            last = code;
            current.Add(code);
        }
        if (current.Count > 0) batches.Add(current);

        Assert.True(batches.Count > 1, "总行数该切出好几批，否则这个用例什么也没验证");

        // 每只票只能落在一个批里
        var batchOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int b = 0; b < batches.Count; b++)
            foreach (var code in batches[b].Distinct(StringComparer.Ordinal))
            {
                Assert.False(batchOf.TryGetValue(code, out var prev) && prev != b,
                             $"{code} 的行被切到了第 {prev} 批和第 {b} 批");
                batchOf[code] = b;
            }

        // 一行都不能丢
        Assert.Equal(rows.Count, batches.Sum(b => b.Count));
    }
}
