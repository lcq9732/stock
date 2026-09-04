using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 板块仓储的替换语义（2026-09-03，随板块数据源换成东财一起加）。
///
/// 盯的是同一件事：<b>什么情况下不该动库里已有的数据</b>。板块是快照，抓取失败时"库里留着
/// 上一次的"才是对的行为——空列表把库清空是这类代码最容易犯也最难发现的错（界面上看起来
/// 就是"板块页突然空了"，但排查时接口早就恢复了，复现不出来）。
/// </summary>
public class BoardRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteBoardRepository _repo;

    public BoardRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"board_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteBoardRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    private static Board Make(string code, BoardType type = BoardType.Concept,
                              params string[] members) => new()
    {
        BoardCode = code,
        Type = type,
        Name = "板块" + code,
        MemberCount = members.Length,
        ChangePct = 1.5,
        AsOf = new DateTime(2026, 9, 3, 10, 0, 0),
        MemberCodes = members.ToList(),
    };

    [Fact]
    public void 空批次是空操作_绝不清空已有数据()
    {
        // 这是最要紧的一条：接口改版/被限流返回空列表时，库里的板块必须原样留着
        _repo.ReplaceAll(new[] { Make("BK1137", BoardType.Concept, "688825", "002049") });

        _repo.ReplaceAll(Array.Empty<Board>());

        Assert.Single(_repo.QueryBoards());
        Assert.Equal(2, _repo.QueryMembers("BK1137").Count);
    }

    [Fact]
    public void 重复写入是整体覆盖_旧板块和它的成分股一起清掉()
    {
        _repo.ReplaceAll(new[]
        {
            Make("BK1137", BoardType.Concept, "688825", "002049"),
            Make("BK9999", BoardType.Concept, "000001"),
        });

        // 第二轮只返回一个板块（比如东财下架了 BK9999）
        _repo.ReplaceAll(new[] { Make("BK1137", BoardType.Concept, "688825") });

        var all = _repo.QueryBoards();
        Assert.Single(all);
        Assert.Equal("BK1137", all[0].BoardCode);
        Assert.Single(_repo.QueryMembers("BK1137"));   // 成分股跟着缩到 1 只
        Assert.Empty(_repo.QueryMembers("BK9999"));    // 旧板块的成分股不能留成孤儿行
    }

    [Fact]
    public void 概念和行业能分别查询()
    {
        _repo.ReplaceAll(new[]
        {
            Make("BK1137", BoardType.Concept, "688825"),
            Make("BK1325", BoardType.Industry, "600875"),
        });

        Assert.Single(_repo.QueryBoards(BoardType.Concept));
        Assert.Single(_repo.QueryBoards(BoardType.Industry));
        Assert.Equal(2, _repo.QueryBoards().Count);
    }

    [Fact]
    public void 反查个股所属概念板块()
    {
        _repo.ReplaceAll(new[]
        {
            Make("BK1137", BoardType.Concept, "688825", "002049"),
            Make("BK1134", BoardType.Concept, "688825"),
            Make("BK1325", BoardType.Industry, "688825"),   // 行业板块不该出现在概念反查里
        });

        var map = _repo.GetConceptBoardsByStock();
        Assert.Equal(2, map["688825"].Count);
        Assert.Single(map["002049"]);
    }

    [Fact]
    public void 抓取时刻能读回来_界面用它显示数据截至()
    {
        _repo.ReplaceAll(new[] { Make("BK1137", BoardType.Concept, "688825") });
        Assert.Equal(new DateTime(2026, 9, 3, 10, 0, 0), _repo.GetLatestAsOf());
    }
}

/// <summary>
/// 龙虎榜席位的位次编号（2026-09-03）。
///
/// 这组用例守的是一个已经真实发生过的数据丢失：龙虎榜的机构席位是**匿名**的，
/// seat_code 一律 "0"、名称一律"机构专用"，但同一张榜里可能有 2~3 个不同机构；
/// "深股通投资者/机构投资者/自然人/中小投资者"这类类别统计也共用同一个营业部代码。
/// 早先的主键不带 seq，这些行互相覆盖——实测一天 415 行会丢 31 行(7.5%)，
/// 联网冒烟里表现为"抓到 5760 行、库里只有 4408 行"。
/// </summary>
public class LhbSeatSeqTests
{
    private static LhbSeat Row(string code, bool isBuy, string seat, string expl, double net) => new()
    {
        TradeDate = new DateTime(2026, 9, 2),
        Code = code,
        IsBuy = isBuy,
        SeatCode = seat,
        SeatName = seat == "0" ? "机构专用" : "某营业部",
        Explanation = expl,
        Net = net,
    };

    [Fact]
    public void 同一张榜里的多个匿名机构席位_各拿到不同位次()
    {
        var rows = new List<LhbSeat>
        {
            Row("000505", true, "0", "日振幅值达到15%的前5只证券", -4639629.96),
            Row("000505", true, "0", "日振幅值达到15%的前5只证券", -6644886),
            Row("000505", true, "0", "日振幅值达到15%的前5只证券", 1200000),
        };
        EastMoneyLhbSeatProvider.AssignSeq(rows);

        Assert.Equal(new[] { 0, 1, 2 }, rows.OrderBy(r => r.Seq).Select(r => r.Seq));
        // 按净额降序：1200000 → -4639629.96 → -6644886
        var bySeq = rows.OrderBy(r => r.Seq).ToList();
        Assert.Equal(1200000, bySeq[0].Net);
        Assert.Equal(-6644886, bySeq[2].Net);
    }

    [Fact]
    public void 不同榜单各自从0开始编号()
    {
        var rows = new List<LhbSeat>
        {
            Row("000505", true, "0", "日振幅值达到15%的前5只证券", 100),
            Row("000505", true, "0", "换手率达20%的证券", 200),
            Row("000505", false, "0", "日振幅值达到15%的前5只证券", 300),
            Row("000628", true, "0", "日振幅值达到15%的前5只证券", 400),
        };
        EastMoneyLhbSeatProvider.AssignSeq(rows);
        Assert.All(rows, r => Assert.Equal(0, r.Seq));   // 四行分属四张不同的榜
    }

    [Fact]
    public void 编号只看净额不看输入顺序_保证重抓幂等()
    {
        // 接口返回顺序不保证稳定；跟着它编号的话重抓一次就会产生一批 seq 不同的新行，
        // 同一天的数据在库里翻倍。所以必须按净额这种数据本身的属性来定序。
        var a = new List<LhbSeat>
        {
            Row("000505", true, "0", "榜A", 100),
            Row("000505", true, "0", "榜A", 300),
            Row("000505", true, "0", "榜A", 200),
        };
        var b = new List<LhbSeat>   // 同样三行，顺序打乱
        {
            Row("000505", true, "0", "榜A", 200),
            Row("000505", true, "0", "榜A", 100),
            Row("000505", true, "0", "榜A", 300),
        };
        EastMoneyLhbSeatProvider.AssignSeq(a);
        EastMoneyLhbSeatProvider.AssignSeq(b);

        double SeqOf(List<LhbSeat> l, int seq) => l.Single(r => r.Seq == seq).Net!.Value;
        for (int i = 0; i < 3; i++)
            Assert.Equal(SeqOf(a, i), SeqOf(b, i));
    }

    [Fact]
    public void 净额为空也能编号不抛异常()
    {
        var rows = new List<LhbSeat>
        {
            Row("000505", true, "0", "榜A", 100),
            new() { TradeDate = new DateTime(2026,9,2), Code="000505", IsBuy=true,
                    SeatCode="0", Explanation="榜A", Net=null },
        };
        EastMoneyLhbSeatProvider.AssignSeq(rows);
        Assert.Equal(new[] { 0, 1 }, rows.OrderBy(r => r.Seq).Select(r => r.Seq));
        Assert.Null(rows.Single(r => r.Seq == 1).Net);   // null 排最后
    }
}

/// <summary>
/// 板块成分股的逐板块落库与断点续传（2026-09-03）。
///
/// 背景：成分股改回 push2 的官方名单之后（datacenter 的 F10 报表会系统性漏股，
/// 液冷服务器 170 只漏 4 只），1031 个板块 ≈ 2500 个请求，而 push2 限流极敏感，
/// **一轮跑不完是常态**。所以这套逻辑的正确性直接决定"跑几轮能不能抓完"——
/// 错了的话要么每轮从头再来永远抓不完，要么把前几轮的成果清掉。
/// </summary>
public class BoardMemberResumeTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteBoardRepository _repo;

    public BoardMemberResumeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bmres_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteBoardRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private static Board B(string code, string name = "板块") => new()
    {
        BoardCode = code, Type = BoardType.Concept, Name = name,
        AsOf = new DateTime(2026, 9, 3, 10, 0, 0),
    };

    [Fact]
    public void 更新板块列表_不会清掉已抓到的成分股()
    {
        // 这是最要紧的一条：成分股跨轮累积，列表刷新绝不能把它冲掉
        _repo.UpsertBoards(new[] { B("BK1137"), B("BK1138") });
        _repo.ReplaceMembers("BK1137", new[] { "688825", "002049" });

        _repo.UpsertBoards(new[] { B("BK1137", "存储芯片"), B("BK1138") });   // 第二轮刷新列表

        Assert.Equal(2, _repo.QueryMembers("BK1137").Count);
        Assert.Equal("存储芯片", _repo.QueryBoards().Single(b => b.BoardCode == "BK1137").Name);
    }

    [Fact]
    public void 已成功抓过的板块_下一轮会被跳过()
    {
        _repo.UpsertBoards(new[] { B("BK1137"), B("BK1138"), B("BK1134") });
        _repo.ReplaceMembers("BK1137", new[] { "688825" });
        _repo.MarkMembersFailed("BK1138", "failed", "限流");
        // BK1134 从未抓过

        var fresh = _repo.GetBoardsWithFreshMembers(DateTime.Today.AddDays(-7));
        Assert.Contains("BK1137", fresh);
        Assert.DoesNotContain("BK1138", fresh);   // 失败的要重试
        Assert.DoesNotContain("BK1134", fresh);   // 没抓过的要抓
    }

    [Fact]
    public void 抓到空名单也要重试_不能当成功()
    {
        // 空名单可能是限流导致的，不能标成 ok 让下轮跳过
        _repo.UpsertBoards(new[] { B("BK9999") });
        _repo.ReplaceMembers("BK9999", Array.Empty<string>());

        Assert.DoesNotContain("BK9999", _repo.GetBoardsWithFreshMembers(DateTime.Today.AddDays(-7)));
    }

    [Fact]
    public void 过期的抓取记录会被重新抓()
    {
        _repo.UpsertBoards(new[] { B("BK1137") });
        _repo.ReplaceMembers("BK1137", new[] { "688825" });

        // 用一个"未来"的时间点当门槛，等价于本次记录已过期
        var fresh = _repo.GetBoardsWithFreshMembers(DateTime.Now.AddMinutes(5));
        Assert.Empty(fresh);
    }

    [Fact]
    public void 重抓同一板块_成分股是替换而不是累加()
    {
        _repo.UpsertBoards(new[] { B("BK1137") });
        _repo.ReplaceMembers("BK1137", new[] { "688825", "002049", "300475" });
        _repo.ReplaceMembers("BK1137", new[] { "688825" });      // 第二轮名单变短

        Assert.Single(_repo.QueryMembers("BK1137"));
        Assert.Equal(1, _repo.QueryBoards().Single().MemberCount);   // member_count 跟着实际名单走
    }

    [Fact]
    public void 板块下架后_它的成分股和进度一起清掉()
    {
        _repo.UpsertBoards(new[] { B("BK1137"), B("BK9999") });
        _repo.ReplaceMembers("BK1137", new[] { "688825" });
        _repo.ReplaceMembers("BK9999", new[] { "000001" });

        _repo.UpsertBoards(new[] { B("BK1137") });   // BK9999 不在新列表里了

        Assert.Single(_repo.QueryBoards());
        Assert.Empty(_repo.QueryMembers("BK9999"));                       // 不留孤儿成分股
        Assert.DoesNotContain("BK9999", _repo.GetBoardsWithFreshMembers(DateTime.Today.AddDays(-7)));
    }

    [Fact]
    public void 进度统计能区分已抓_待重试_从未抓过()
    {
        _repo.UpsertBoards(new[] { B("BK1"), B("BK2"), B("BK3"), B("BK4") });
        _repo.ReplaceMembers("BK1", new[] { "600000" });
        _repo.ReplaceMembers("BK2", new[] { "600001" });
        _repo.MarkMembersFailed("BK3", "failed", "限流");
        // BK4 从未抓过

        var (ok, failed, never) = _repo.GetMemberFetchProgress(DateTime.Today.AddDays(-7));
        Assert.Equal(2, ok);
        Assert.Equal(1, failed);
        Assert.Equal(1, never);
    }
}

/// <summary>
/// 龙虎榜席位的水位线回退（2026-09-03，实战中踩出来的）。
///
/// 这张表落库粒度是"月"，且**月内买卖分两次落库**（先买方后卖方）。在买方已落、卖方未落时
/// 被打断，MAX(trade_date) 就停在那个月的月中/月末——直接拿它当下一轮起点的话，
/// 该月月初到那天的卖方数据会永久漏掉。
///
/// 实测现场：中断时买方最大日 2019-05-31、卖方 2019-04-30，2019-05 整月只有买方 7635 行、
/// 卖方 0 行。所以起点必须回退到所在月的 1 号，整月重抓（主键 UPSERT 保证不重复）。
/// </summary>
public class LhbSeatWatermarkTests
{
    /// <summary>编排层用的那条推导：水位线 → 下一轮起点。</summary>
    private static DateTime StartFrom(DateTime? watermark) =>
        watermark.HasValue
            ? new DateTime(watermark.Value.Year, watermark.Value.Month, 1)
            : new DateTime(2016, 1, 1);

    [Fact]
    public void 水位线在月中时_回退到月初整月重抓()
    {
        // 正是踩到的那个现场
        Assert.Equal(new DateTime(2019, 5, 1), StartFrom(new DateTime(2019, 5, 31)));
        Assert.Equal(new DateTime(2019, 5, 1), StartFrom(new DateTime(2019, 5, 15)));
    }

    [Fact]
    public void 水位线正好是月初时_仍从该月初开始()
    {
        Assert.Equal(new DateTime(2019, 5, 1), StartFrom(new DateTime(2019, 5, 1)));
    }

    [Fact]
    public void 空库时从2016年起()
    {
        Assert.Equal(new DateTime(2016, 1, 1), StartFrom(null));
    }

    [Fact]
    public void 回退不会跳过任何已有数据的月份()
    {
        // 关键性质：回退后的起点 <= 水位线，所以不会漏掉水位线之前任何一天
        foreach (var wm in new[] { new DateTime(2019,5,31), new DateTime(2020,1,1),
                                   new DateTime(2026,9,3), new DateTime(2016,12,15) })
            Assert.True(StartFrom(wm) <= wm);
    }
}
