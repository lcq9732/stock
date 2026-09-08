using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 板块层级树写库（2026-09-07）。
///
/// 盯的是两件事：
/// 1. <b>层级列不能被板块列表的抓取抹掉</b>。Board.parent_code/board_level 由这一条路写，
///    而板块列表每天抓一次、走的是另一条路（CommitStaged / UpsertBoards）——那条路上
///    根本不知道父级是谁，只要它的 UPDATE 语句多带上这两列，每天都会把树抹成 NULL。
///    这跟 member_count 是同一个坑（那个由成分股抓取写），只是这次提前拿测试钉住。
/// 2. 空批次是空操作。读不到终端文件时该留着上一次的树，跟板块快照同理。
/// </summary>
public class BoardHierarchyRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteBoardRepository _repo;

    public BoardHierarchyRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"boardhier_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteBoardRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    private static Board Make(string code, BoardType type = BoardType.Industry) => new()
    {
        BoardCode = code,
        Type = type,
        Name = "板块" + code,
        ChangePct = 1.5,
        AsOf = new DateTime(2026, 9, 7, 10, 0, 0),
        MemberCodes = [],
    };

    private static BoardHierarchyEdge E(string code, string parent, int level)
        => new(code, parent, level, level - 1);

    /// <summary>直接读回这一行的层级列——测试要看的就是它有没有被谁抹掉。</summary>
    private (string? Parent, int? Level) Read(string code)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT parent_code, board_level FROM Board WHERE board_code = $c;";
        cmd.Parameters.AddWithValue("$c", code);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (null, null);
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetInt32(1));
    }

    private void SeedThreeLevelTree()
    {
        _repo.UpsertBoards([Make("BK0427"), Make("BK0428"), Make("BK1377")]);
        _repo.UpdateHierarchy([E("BK0428", "BK0427", 2), E("BK1377", "BK0428", 3)]);
    }

    [Fact]
    public void 一级板块拿到层级但父为空()
    {
        SeedThreeLevelTree();

        // 一级板块**从不作为子出现**，它的层级只藏在别人的 ParentLevel 里 ——
        // 漏了这一步的话 23 个一级行业的 board_level 会全是 NULL，往上卷就卷不到顶。
        Assert.Equal((null, 1), Read("BK0427"));
        Assert.Equal(("BK0427", 2), Read("BK0428"));
        Assert.Equal(("BK0428", 3), Read("BK1377"));
    }

    [Fact]
    public void 抓板块列表不会抹掉层级()
    {
        // ★ 本文件最要紧的一条。板块列表天天抓，层级一个季度才变一次。
        SeedThreeLevelTree();

        var refreshed = Make("BK1377");
        refreshed.Name = "改了名字";
        refreshed.ChangePct = 9.9;
        _repo.UpsertBoards([refreshed]);

        Assert.Equal(("BK0428", 3), Read("BK1377"));                       // 层级原样
        Assert.Equal("改了名字", _repo.QueryBoards().Single(b => b.BoardCode == "BK1377").Name);
    }

    [Fact]
    public void 走暂存区提交也不会抹掉层级()
    {
        // UpsertBoards 和 CommitStaged 是两条独立的写入路径，各有一条 INSERT…ON CONFLICT。
        // 只测一条的话，另一条改坏了照样没人知道。
        SeedThreeLevelTree();

        _repo.StageBoards([Make("BK0427"), Make("BK0428"), Make("BK1377")]);
        var (committed, _) = _repo.CommitStaged(BoardType.Industry);

        Assert.Equal(3, committed);
        Assert.Equal(("BK0428", 3), Read("BK1377"));
        Assert.Equal((null, 1), Read("BK0427"));
    }

    [Fact]
    public void 空批次是空操作_留着上一次的树()
    {
        SeedThreeLevelTree();

        var r = _repo.UpdateHierarchy([]);

        Assert.Equal((0, 0, 0), r);
        Assert.Equal(("BK0428", 3), Read("BK1377"));   // 一行没动
    }

    [Fact]
    public void 重新导入是整棵树替换_旧关系会被清掉()
    {
        SeedThreeLevelTree();

        // 行业改版：BK1377 改挂到 BK1028 下面，BK0428 整个不在树里了。
        // ⚠ 名单得一次写全 —— UpsertBoards 是快照语义，只传 BK1028 会把其余三个当下架板块删掉。
        _repo.UpsertBoards([Make("BK0427"), Make("BK0428"), Make("BK1377"), Make("BK1028")]);
        var (updated, unknown, cleared) = _repo.UpdateHierarchy([E("BK1028", "BK0427", 2), E("BK1377", "BK1028", 3)]);

        Assert.Equal(3, updated);          // BK0427 / BK1028 / BK1377
        Assert.Equal(0, unknown);
        Assert.Equal(3, cleared);          // 上一棵树那三行
        Assert.Equal(("BK1028", 3), Read("BK1377"));
        Assert.Equal((null, null), Read("BK0428"));   // 被摘掉的那个不能留着旧父
    }

    [Fact]
    public void 板块表里没有的代码只计数不报错()
    {
        // 终端认识的板块比我们抓到的多几个，这是常态，不该当成错误
        _repo.UpsertBoards([Make("BK0427")]);

        var (updated, unknown, _) = _repo.UpdateHierarchy([E("BK9999", "BK0427", 2)]);

        Assert.Equal(1, updated);    // 只有 BK0427 写进去了
        Assert.Equal(1, unknown);    // BK9999 我们没有
        Assert.Equal((null, 1), Read("BK0427"));
    }

    [Fact]
    public void 概念和地区板块不受影响_始终为空()
    {
        // 层级树只覆盖行业。概念/地区本来就是平的，误写进去会让"按一级行业卷"多出一堆假节点。
        // 分两次写：UpsertBoards 一批只收一种 board_type（删除按类型限定，混着写会误删）
        _repo.UpsertBoards([Make("BK0427")]);
        _repo.UpsertBoards([Make("BK1137", BoardType.Concept)]);
        _repo.UpdateHierarchy([E("BK0428", "BK0427", 2)]);

        Assert.Equal((null, null), Read("BK1137"));
    }
}
