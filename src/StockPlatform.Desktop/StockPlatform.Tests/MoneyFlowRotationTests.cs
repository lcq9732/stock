using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 分档资金流的排队顺序（2026-09-04）。
///
/// 修的是一个跑了两天才暴露出来的问题：原来排队是「今天没抓过的，按代码顺序抓」，
/// 可每天零点一过，昨天抓过的又全变成"今天没抓过"——于是每天都从 000001 重新开始，
/// 代码靠后的票**永远轮不到**。实测库里只有 000001~000509 这 89 只（1.5%），
/// 而且再跑多久都不会变多。
///
/// 接口是 120 天滚动窗口，所以目标不是"每天抓全市场"（那要 17 小时，做不到），
/// 而是**保证每只票 120 天内被轮到一次**。前提就是这里的排序：最久没抓的先抓。
/// </summary>
public class MoneyFlowRotationTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"mf_{Guid.NewGuid():N}.sqlite");

    private SqliteNetInflowDetailRepository NewRepo()
    {
        var r = new SqliteNetInflowDetailRepository(_db);
        r.EnsureSchema();
        return r;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch { }
    }

    private static NetInflowDetail Row(string code, DateTime fetchedAt) => new()
    {
        Code = code,
        TradeDate = new DateTime(2026, 9, 3),
        MainNet = 1, SuperNet = 1, BigNet = 0, MidNet = 0, SmallNet = -1,
        FetchedAt = fetchedAt,
    };

    [Fact]
    public void 能查出每只票上次抓取的时刻()
    {
        var repo = NewRepo();
        repo.Upsert([Row("000001", new DateTime(2026, 9, 1, 10, 0, 0)),
                     Row("600519", new DateTime(2026, 9, 3, 10, 0, 0))]);

        var map = repo.GetLastFetchedAt();
        Assert.Equal(new DateTime(2026, 9, 1, 10, 0, 0), map["000001"]);
        Assert.Equal(new DateTime(2026, 9, 3, 10, 0, 0), map["600519"]);
    }

    [Fact]
    public void 从没抓过的票不在结果里_排队时要排最前()
    {
        var repo = NewRepo();
        repo.Upsert([Row("000001", DateTime.Now)]);

        var map = repo.GetLastFetchedAt();
        Assert.False(map.ContainsKey("300750"));   // 没抓过 → 查不到 → 排序时用 MinValue 排最前
    }

    [Fact]
    public void 按最久没抓排序时从没抓过的在最前_其余由旧到新()
    {
        var repo = NewRepo();
        repo.Upsert([Row("000001", new DateTime(2026, 9, 3, 10, 0, 0)),   // 最近抓的
                     Row("000002", new DateTime(2026, 6, 1, 10, 0, 0))]); // 很久没抓
        var last = repo.GetLastFetchedAt();

        // 跟 FetchOrchestrator 里的排序同一套判据
        var codes = new[] { "000001", "000002", "300750", "600519" };
        var order = codes
            .OrderBy(c => last.TryGetValue(c, out var t) ? t : DateTime.MinValue)
            .ToList();

        // 两只从没抓过的排最前（它们之间保持原顺序），然后是 6 月抓的，最后才是今天抓的
        Assert.Equal(new[] { "300750", "600519", "000002", "000001" }, order);
    }

    [Fact]
    public void 重复抓同一只票取最新那次的时刻()
    {
        var repo = NewRepo();
        repo.Upsert([Row("000001", new DateTime(2026, 6, 1, 10, 0, 0))]);
        repo.Upsert([Row("000001", new DateTime(2026, 9, 4, 10, 0, 0))]);

        // 取 MAX 而不是任意一条——否则一只票被重抓过就会一直排在队首，占着别人的名额
        Assert.Equal(new DateTime(2026, 9, 4, 10, 0, 0), repo.GetLastFetchedAt()["000001"]);
    }
}
