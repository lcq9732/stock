using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 大宗交易改成**按日整日替换**之后的落库行为（2026-09-17，见 doc/block-trade-task-design.md）。
///
/// 这一组锁的是改造要解决的那个 bug 本身：原来主键第三列存东财的 <c>DAILY_RANK</c>，
/// 而那个值跨抓取会变，于是 UPSERT 认不出"同一笔"、每次重抓都 INSERT 一份副本
/// （全表曾多出 3087 行，最近一个月的笔数金额普遍虚高一倍）。
///
/// 所以第一条测试是**重抓两次行数不变**——它一旦红了，那个 bug 就回来了。
/// </summary>
public class BlockTradeDayWriterTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteMarketEventRepository _repo;

    private static readonly DateTime Day = new(2026, 9, 15);

    public BlockTradeDayWriterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bt_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteMarketEventRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    /// <summary>造一笔。<paramref name="rank"/> 模拟东财给的 DAILY_RANK——它**不该**影响落库结果。</summary>
    private static BlockTrade Row(string code, double amount, int rank = 0, DateTime? day = null) => new()
    {
        Code = code,
        Name = code,
        TradeDate = day ?? Day,
        DailyRank = rank,
        DealPrice = 10,
        DealVolume = amount / 10,
        DealAmount = amount,
        PremiumRatio = 0,
        BuyerName = "机构专用",
        SellerName = "机构专用",
        FetchedAt = DateTime.Now,
    };

    private (int Rows, double Amount) Stat(DateTime? day = null)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), IFNULL(SUM(deal_amount),0) FROM BlockTrade WHERE trade_date = $d;";
        cmd.Parameters.AddWithValue("$d", (day ?? Day).ToString("yyyy-MM-dd"));
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt32(0), r.GetDouble(1));
    }

    private List<(string Code, int Rank)> Ranks()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, daily_rank FROM BlockTrade WHERE trade_date = $d ORDER BY code, daily_rank;";
        cmd.Parameters.AddWithValue("$d", Day.ToString("yyyy-MM-dd"));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, int)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1)));
        return list;
    }

    /// <summary>
    /// **本次改造的核心**：同一天抓两次，第二次东财给的 DAILY_RANK 全变了，
    /// 库里的行数和金额也必须一模一样。原来的 UPSERT 会在这里翻倍。
    /// </summary>
    [Fact]
    public void 同一天抓两次_DailyRank全变了_行数和金额都不变()
    {
        List<BlockTrade> first = [Row("300750", 3_163_600, rank: 22), Row("300750", 4_745_400, rank: 27)];
        _repo.ReplaceBlockTradesForDay(Day, first);
        Assert.Equal((2, 7_909_000d), Stat());

        // 第二次：同样两笔，但东财这回给的是 rank 1/2（实测就是这么变的）
        List<BlockTrade> second = [Row("300750", 3_163_600, rank: 1), Row("300750", 4_745_400, rank: 2)];
        _repo.ReplaceBlockTradesForDay(Day, second);

        Assert.Equal((2, 7_909_000d), Stat());
    }

    /// <summary>序号由落库时按 code 分组、按返回顺序自赋 1..N——不再是东财那个会变的值。</summary>
    [Fact]
    public void 序号按股票分组自赋_从1连续()
    {
        _repo.ReplaceBlockTradesForDay(Day, [
            Row("300750", 100, rank: 99),
            Row("600000", 200, rank: 7),
            Row("300750", 300, rank: 3),
            Row("300750", 400, rank: 51),
        ]);

        Assert.Equal([("300750", 1), ("300750", 2), ("300750", 3), ("600000", 1)], Ranks());
    }

    /// <summary>
    /// 同一天同价同量同席位的两笔是**真实拆单**（301380 在 2026-09-04 实测就有），
    /// 不能被当成重复合并掉——这正是"按内容去重"那条路走不通的原因。
    /// </summary>
    [Fact]
    public void 同价同量同席位的两笔_都留着()
    {
        _repo.ReplaceBlockTradesForDay(Day, [Row("301380", 2_010_000), Row("301380", 2_010_000)]);
        Assert.Equal(2, Stat().Rows);
    }

    /// <summary>接口抽风返回空的那一次，不能把一整天抹掉。</summary>
    [Fact]
    public void 空批次_不删已有的那天()
    {
        _repo.ReplaceBlockTradesForDay(Day, [Row("300750", 100), Row("300750", 200)]);
        Assert.Equal(0, _repo.ReplaceBlockTradesForDay(Day, []));
        Assert.Equal(2, Stat().Rows);
    }

    /// <summary>整日替换只动那一天，别的日子不受影响。</summary>
    [Fact]
    public void 只替换指定那一天()
    {
        var other = Day.AddDays(-1);
        _repo.ReplaceBlockTradesForDay(other, [Row("600000", 500, day: other)]);
        _repo.ReplaceBlockTradesForDay(Day, [Row("300750", 100)]);
        _repo.ReplaceBlockTradesForDay(Day, [Row("300750", 100), Row("300750", 200)]);

        Assert.Equal(1, Stat(other).Rows);
        Assert.Equal(2, Stat().Rows);
    }

    /// <summary>那天原来有 5 笔、这次只剩 2 笔（东财撤了几条），替换后就该只剩 2 笔。</summary>
    [Fact]
    public void 数据源变少了_多余的行被删掉()
    {
        _repo.ReplaceBlockTradesForDay(Day, [
            Row("300750", 100), Row("300750", 200), Row("300750", 300),
            Row("600000", 400), Row("600000", 500),
        ]);
        Assert.Equal(5, Stat().Rows);

        _repo.ReplaceBlockTradesForDay(Day, [Row("300750", 100), Row("600000", 400)]);
        Assert.Equal((2, 500d), Stat());
    }
}
