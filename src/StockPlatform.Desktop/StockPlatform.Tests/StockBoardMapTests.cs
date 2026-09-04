using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
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
