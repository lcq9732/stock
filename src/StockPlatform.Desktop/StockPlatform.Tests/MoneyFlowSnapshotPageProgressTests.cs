using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 快照的**页进度表**（2026-09-21）——打在真 SQLite 上，不是假仓储。
///
/// 为什么要有这张表：东财的配额实测一轮只放过约 16 页，而全市场约 60 页。原来
/// "一批＝一整天、任一页失败就整轮不落库"的做法在这个配额下永远攒不满——每轮抓 16 页、
/// 每轮全扔掉。记下已抓的页、下一轮只补缺的，四五轮才补得齐。
///
/// 为什么不能从 <c>NetInflowDetail</c> 的行反推页号：停牌股整行不写库，页边界对不齐。
///
/// 这张表要是读写不对，表现出来只有两种，都不报错：跳过了其实没抓到的页（那些票当天
/// 的数据从此没人再补），或者每轮重抓已有的页（配额白扔、永远补不完）。所以钉在真库上。
/// </summary>
public class MoneyFlowSnapshotPageProgressTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"mfpage_{Guid.NewGuid():N}.sqlite");

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

    private static readonly DateTime Day = new(2026, 9, 21);

    [Fact]
    public void 记下的页下次读得回来()
    {
        var repo = NewRepo();
        Assert.Empty(repo.GetSnapshotPages(Day));            // 一轮都没跑过时是空的，不是报错

        repo.MarkSnapshotPages(Day, new Dictionary<int, int> { [1] = 100, [2] = 97, [5] = 100 },
                               new DateTime(2026, 9, 21, 15, 34, 57));

        Assert.Equal([1, 2, 5], repo.GetSnapshotPages(Day).OrderBy(p => p));
    }

    [Fact]
    public void 分几轮记_累加而不是覆盖()
    {
        // 这是跨轮续抓的全部意义所在：第二轮补的页要**加**到第一轮的进度上。
        var repo = NewRepo();
        var at = new DateTime(2026, 9, 21, 15, 34, 57);

        repo.MarkSnapshotPages(Day, new Dictionary<int, int> { [1] = 100, [2] = 100 }, at);
        repo.MarkSnapshotPages(Day, new Dictionary<int, int> { [3] = 100, [4] = 88 }, at);

        Assert.Equal([1, 2, 3, 4], repo.GetSnapshotPages(Day).OrderBy(p => p));
    }

    [Fact]
    public void 同一页重记不会变成两行()
    {
        // 重抓同一页是正常的（上一轮记完页号之前程序被停掉）。主键要能吃住，
        // 否则进度表会越滚越大、而且 rows_got 到底算哪一次说不清。
        var repo = NewRepo();
        var at = new DateTime(2026, 9, 21, 15, 34, 57);

        repo.MarkSnapshotPages(Day, new Dictionary<int, int> { [7] = 3 }, at);       // 被截断的那次
        repo.MarkSnapshotPages(Day, new Dictionary<int, int> { [7] = 100 }, at);     // 重抓补全

        Assert.Equal([7], repo.GetSnapshotPages(Day));
        using var conn = new SqliteConnection($"Data Source={_db}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), MAX(rows_got) FROM MoneyFlowSnapshotPage WHERE trade_date='2026-09-21';";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(1, r.GetInt32(0));          // 一行，不是两行
        Assert.Equal(100, r.GetInt32(1));        // 留下的是后写的那次
    }

    [Fact]
    public void 按交易日分开记()
    {
        // 周五抓了一半、周末接着补，问的必须是周五那天的进度。按天分不开的话，
        // 周六那一轮会把周五攒下的页当成自己的、于是一页都不补。
        var repo = NewRepo();
        var at = new DateTime(2026, 9, 21, 15, 34, 57);

        repo.MarkSnapshotPages(new DateTime(2026, 9, 18), new Dictionary<int, int> { [1] = 100 }, at);
        repo.MarkSnapshotPages(Day, new Dictionary<int, int> { [9] = 100 }, at);

        Assert.Equal([1], repo.GetSnapshotPages(new DateTime(2026, 9, 18)));
        Assert.Equal([9], repo.GetSnapshotPages(Day));
        Assert.Empty(repo.GetSnapshotPages(new DateTime(2026, 9, 17)));
    }

    [Fact]
    public void 空的一批不写也不炸()
    {
        // 一轮一页都没抓到时会走到这里（整条出口被切）。不该留下任何痕迹——
        // 记了空进度反而会让人以为抓过。
        var repo = NewRepo();
        repo.MarkSnapshotPages(Day, new Dictionary<int, int>(), DateTime.Now);
        Assert.Empty(repo.GetSnapshotPages(Day));
    }
}
