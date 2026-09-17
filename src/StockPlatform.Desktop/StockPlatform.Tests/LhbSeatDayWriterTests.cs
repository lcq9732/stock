using Microsoft.Data.Sqlite;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【龙虎榜席位】"抓一天并落库"这一个动作（2026-09-17）。任务侧和
/// <c>FetchOrchestrator.DailyRefetcherFor</c>（补残缺日）共用它，所以它的行为要单独锁住。
///
/// 盯的是三条：
/// ① 抓不全（买卖任一侧对不上 count）**抛异常、绝不落库**——整日替换时写回半天，
///    库里和体检都看不出来；
/// ② 接口自报 0 行时**不删**已有的行（接口抽风那一次不能抹掉一整天）；
/// ③ 整日替换是幂等的：同一天抓多少次，库里都是那么多行。
/// </summary>
public class LhbSeatDayWriterTests : IDisposable
{
    private readonly string _db;
    private readonly SqliteLhbSeatRepository _repo;
    private static readonly DateOnly Day = new(2026, 9, 10);

    public LhbSeatDayWriterTests()
    {
        _db = Path.Combine(Path.GetTempPath(), $"lsWriter_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteLhbSeatRepository(_db);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_db)) File.Delete(_db); } catch { /* 临时文件 */ }
    }

    private sealed class FakeFetcher(Func<DateTime, LhbSeatDay> make) : ILhbSeatDayFetcher
    {
        public Task<LhbSeatDay> FetchLhbSeatsOfDayAsync(DateTime day, CancellationToken ct = default)
            => Task.FromResult(make(day));
    }

    private static LhbSeat Row(DateTime day, bool isBuy, int i) => new()
    {
        Code = "600108", Name = "亚盛集团", TradeDate = day.Date, IsBuy = isBuy,
        SeatCode = $"100{i}", SeatName = $"某某营业部{i}",
        Buy = isBuy ? 1_000_000 - i : null,
        Sell = isBuy ? null : 900_000 - i,
        Net = (isBuy ? 1 : -1) * (1_000_000 - i),
        Explanation = "日涨幅偏离值达到7%的前5只证券",
        FetchedAt = DateTime.Now,
    };

    /// <summary>每侧 <paramref name="seats"/> 个席位、两侧 count 都报 <paramref name="reported"/>。</summary>
    private static LhbSeatDay Make(DateTime day, int seats, int reported)
    {
        var rows = new List<LhbSeat>();
        foreach (var isBuy in new[] { true, false })
            for (int i = 0; i < seats; i++) rows.Add(Row(day, isBuy, i));
        EastMoneyLhbSeatProvider.AssignSeq(rows);
        return new LhbSeatDay(day.Date, rows, reported, reported);
    }

    private int Rows()
    {
        using var conn = new SqliteConnection($"Data Source={_db}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM LhbSeat;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task 抓全了_整日替换写入()
    {
        var w = new LhbSeatDayWriter(new FakeFetcher(d => Make(d, 5, 5)), _repo);
        Assert.Equal(10, await w.RefetchAsync(Day));
        Assert.Equal(10, Rows());
    }

    /// <summary>反复跑无害——这正是改整日替换的意义。</summary>
    [Fact]
    public async Task 同一天抓三次_行数不变()
    {
        var w = new LhbSeatDayWriter(new FakeFetcher(d => Make(d, 5, 5)), _repo);
        await w.RefetchAsync(Day);
        await w.RefetchAsync(Day);
        await w.RefetchAsync(Day);
        Assert.Equal(10, Rows());
    }

    /// <summary>
    /// **最要紧的一条**：某一侧被截断了就抛，绝不落库。
    /// 落了库就是拿半天的数据盖掉完整的一天，事后完全看不出来。
    /// </summary>
    [Fact]
    public async Task 卖方对不上count_抛异常且一行都不写()
    {
        var w = new LhbSeatDayWriter(new FakeFetcher(d =>
        {
            var one = Make(d, 5, 5);
            one.Rows.Remove(one.Rows.Last(r => !r.IsBuy));     // 卖方少一行，count 仍报 5
            return one;
        }), _repo);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => w.RefetchAsync(Day));
        Assert.Contains("没抓全", ex.Message);
        Assert.Equal(0, Rows());
    }

    /// <summary>已有完整数据时抓到残缺的一次，原数据必须原封不动。</summary>
    [Fact]
    public async Task 抓残缺时_已有的那天不被删()
    {
        await new LhbSeatDayWriter(new FakeFetcher(d => Make(d, 5, 5)), _repo).RefetchAsync(Day);
        Assert.Equal(10, Rows());

        var bad = new LhbSeatDayWriter(new FakeFetcher(d =>
        {
            var one = Make(d, 5, 5);
            one.Rows.Remove(one.Rows.Last(r => r.IsBuy));
            return one;
        }), _repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() => bad.RefetchAsync(Day));
        Assert.Equal(10, Rows());
    }

    /// <summary>
    /// 安全阀：接口自报 0 行时**不删**已有的行。
    /// 否则接口抽风返回空的那一次会把一整天的数据抹掉。
    /// </summary>
    [Fact]
    public async Task 接口返回空_不删已有的行()
    {
        await new LhbSeatDayWriter(new FakeFetcher(d => Make(d, 5, 5)), _repo).RefetchAsync(Day);

        var empty = new LhbSeatDayWriter(
            new FakeFetcher(d => new LhbSeatDay(d.Date, [], 0, 0)), _repo);
        Assert.Equal(0, await empty.RefetchAsync(Day));

        Assert.Equal(10, Rows());
    }

    /// <summary>编排器那边传的是它自己的 _dbLock，传了也要照样工作。</summary>
    [Fact]
    public async Task 带锁调用_行为不变()
    {
        var w = new LhbSeatDayWriter(new FakeFetcher(d => Make(d, 5, 5)), _repo, new object());
        Assert.Equal(10, await w.RefetchAsync(Day));
        Assert.Equal(10, Rows());
    }
}
