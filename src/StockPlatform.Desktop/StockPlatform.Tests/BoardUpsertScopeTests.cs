using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// <c>UpsertBoards</c> 的删除范围（2026-09-04）。
///
/// 这张表的语义是**快照**：这一轮没返回的板块＝已下架，要连同成分股一起清掉，
/// 否则库里会攒一堆查不到也不再更新的僵尸板块。
///
/// 但概念和行业是**分两次抓、分两次写**的（一类抓完就落库，免得第二类失败把第一类的成果
/// 也带走）。所以删除必须限定在同一个 board_type 内——不限定的话，写概念板块那一下就会把
/// 行业板块全删光（它们当然不在概念这批名单里），跨轮攒了好几天的成分股跟着一起没，
/// 而且全程不报错。这个坑是在写这批测试之前差一点发布出去的。
/// </summary>
public class BoardUpsertScopeTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"bd_{Guid.NewGuid():N}.sqlite");

    private SqliteBoardRepository NewRepo()
    {
        var r = new SqliteBoardRepository(_db);
        r.EnsureSchema();
        return r;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch { }
    }

    private static Board B(string code, BoardType type, string name) => new()
    {
        BoardCode = code,
        Type = type,
        Name = name,
        AsOf = DateTime.Now,
    };

    [Fact]
    public void 写概念板块不会删掉行业板块()
    {
        var repo = NewRepo();
        repo.UpsertBoards([B("BK9001", BoardType.Industry, "银行"),
                           B("BK9002", BoardType.Industry, "白酒")]);
        repo.UpsertBoards([B("BK1001", BoardType.Concept, "液冷服务器"),
                           B("BK1002", BoardType.Concept, "算力租赁")]);

        var all = repo.QueryBoards();
        Assert.Equal(4, all.Count);
        Assert.Equal(2, all.Count(b => b.Type == BoardType.Industry));   // ← 行业那两个必须还在
        Assert.Equal(2, all.Count(b => b.Type == BoardType.Concept));
    }

    [Fact]
    public void 写概念板块不会删掉行业板块的成分股()
    {
        // 成分股是逐板块抓的、约 2500 个请求、跨好几轮才攒得齐。
        // 被误删的话要重抓好几天，代价比板块名单本身大得多。
        var repo = NewRepo();
        repo.UpsertBoards([B("BK9001", BoardType.Industry, "银行")]);
        repo.ReplaceMembers("BK9001", ["600000", "601398", "601939"]);

        repo.UpsertBoards([B("BK1001", BoardType.Concept, "液冷服务器")]);

        Assert.Equal(3, repo.QueryMembers("BK9001").Count);
    }

    [Fact]
    public void 同类型里这轮没返回的板块要被清掉()
    {
        // 删除本身是必要的：下架的板块留着就成了僵尸，查不到也不会再更新
        var repo = NewRepo();
        repo.UpsertBoards([B("BK1001", BoardType.Concept, "液冷服务器"),
                           B("BK1002", BoardType.Concept, "已下架的概念")]);
        repo.ReplaceMembers("BK1002", ["000001"]);

        // 第二轮只返回 BK1001 —— BK1002 下架了
        repo.UpsertBoards([B("BK1001", BoardType.Concept, "液冷服务器")]);

        var all = repo.QueryBoards();
        Assert.Single(all);
        Assert.Equal("BK1001", all[0].BoardCode);
        Assert.Empty(repo.QueryMembers("BK1002"));       // 成分股也跟着清掉
    }

    [Fact]
    public void 一批里混了两种类型要直接拒绝()
    {
        // 删除是按类型限定的，混着写必然误删其中一类。与其悄悄删错，不如当场报错。
        var repo = NewRepo();
        var ex = Assert.Throws<ArgumentException>(() =>
            repo.UpsertBoards([B("BK1001", BoardType.Concept, "液冷"),
                               B("BK9001", BoardType.Industry, "银行")]));
        Assert.Contains("board_type", ex.Message);
    }

    [Fact]
    public void 传空集合是空操作()
    {
        // 抓取失败时保留库里上一次的数据才是对的，不能因为这轮拿回来个空的就把库清空
        var repo = NewRepo();
        repo.UpsertBoards([B("BK1001", BoardType.Concept, "液冷服务器")]);
        repo.UpsertBoards([]);

        Assert.Single(repo.QueryBoards());
    }
}
