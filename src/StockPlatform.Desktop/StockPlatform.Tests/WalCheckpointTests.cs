using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 主动回收 WAL（<see cref="SqliteMaintenance.CheckpointWal"/>，2026-09-12）。
///
/// 为什么值得测：2026-09-10 C 盘被 **162GB** 的 WAL 撑爆（931G 用到 0 可用），而整个过程
/// **一句日志都没有** —— 任务本身"正常跑完"，是事后查磁盘才发现的。根因是一个残留进程握着库
/// 两个多小时，SQLite 的 autocheckpoint 是被动的、只要有读连接活着就跳过，于是【板块指数合成】
/// 那 468 万行全堆在 WAL 里回收不掉。
///
/// 所以这里盯两件事：① 写完能真的把 WAL 截回去 ② **`busy` 必须能报出来** —— 那是
/// "有人握着库、WAL 正在失控"的唯一早期信号，吞掉它就等于把那次事故的教训又丢了一遍。
/// </summary>
public class WalCheckpointTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteBarRepository _bars;
    private readonly SqliteMaintenance _maint;

    public WalCheckpointTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"wal_{Guid.NewGuid():N}.sqlite");
        _bars = new SqliteBarRepository(_dbPath);
        _bars.EnsureSchema();          // 这一步把 journal_mode 设成 WAL
        _maint = new SqliteMaintenance(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* 临时文件 */ }
    }

    private void WriteBars(int n)
    {
        var day = new DateTime(2026, 1, 1);
        _bars.InsertOrRefreshUnconfirmed(Enumerable.Range(0, n).Select(i => new Bar
        {
            Code = $"{600000 + i % 50}", Granularity = Granularity.Day,
            PeriodStart = day.AddDays(i / 50),
            Open = 10, Close = 10, High = 10, Low = 10,
            Volume = 100, Amount = 100000, Turnover = 1.5,
            FetchedAt = day.AddDays(i / 50).AddHours(20),
        }));
    }

    [Fact]
    public void 写完能把WAL截回去()
    {
        WriteBars(5000);
        var r = _maint.CheckpointWal();

        Assert.Equal(0, r.Busy);
        Assert.True(r.FullyReclaimed, $"没能完整回收：busy={r.Busy} log={r.LogPages} done={r.CheckpointedPages}");
        // TRUNCATE 之后 WAL 文件应该被截成 0（PASSIVE 只搬数据、不缩文件）
        var wal = new FileInfo(_dbPath + "-wal");
        Assert.True(!wal.Exists || wal.Length == 0, $"WAL 还有 {(wal.Exists ? wal.Length : 0)} 字节");
    }

    /// <summary>把 WAL 撑到指定大小（拿一张废表灌 4KB 的块，不污染 Bar）。</summary>
    private void InflateWal(int megabytes)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var tx = conn.BeginTransaction();
        using (var ddl = conn.CreateCommand())
        {
            ddl.Transaction = tx;
            ddl.CommandText = "CREATE TABLE IF NOT EXISTS _wal_filler(id INTEGER PRIMARY KEY, blob BLOB);";
            ddl.ExecuteNonQuery();
        }
        var chunk = new byte[4096];
        for (int i = 0; i < megabytes * 256; i++)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO _wal_filler(blob) VALUES($b);";
            ins.Parameters.AddWithValue("$b", chunk);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>开一条**正在读**的连接握着库——TRUNCATE 拿不到独占。</summary>
    private static (Microsoft.Data.Sqlite.SqliteConnection Conn,
                    Microsoft.Data.Sqlite.SqliteTransaction Tx) HoldRead(string dbPath)
    {
        var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var tx = conn.BeginTransaction();
        using var read = conn.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT COUNT(*) FROM Bar;";
        read.ExecuteScalar();
        return (conn, tx);
    }

    /// <summary>
    /// `busy` 本身必须如实返回——它是"有人握着库"的原始事实，不能被吞掉。
    /// （要不要**报警**是上一层的事，见下面两条。）
    /// </summary>
    [Fact]
    public void 有别的连接握着时_busy要如实报出来()
    {
        WriteBars(2000);
        var (conn, tx) = HoldRead(_dbPath);
        using (conn) using (tx)
        {
            var r = _maint.CheckpointWal();
            Assert.Equal(1, r.Busy);
            Assert.False(r.FullyReclaimed);
        }
    }

    /// <summary>
    /// ⭐ busy 但 WAL 还小 ⇒ **不报警**（2026-09-21 改）。
    ///
    /// 【板块指数合成】改成增量之后一轮只跑 19 秒，收尾 checkpoint 几乎必然撞上
    /// 界面自己那几个全表扫描（<c>MainViewModel.RefreshFailedCodeCount</c>，25GB 库上要好几分钟，
    /// 启动时和每项任务跑完后各一轮）。那种 busy 是常态，WAL 十来 MB，下一轮就收掉了。
    /// 按老判据会**每天报一次**"有别的连接握着这个库"，把用户支去关一堆根本没开的程序。
    /// </summary>
    [Fact]
    public void busy但WAL还小_不报警()
    {
        WriteBars(2000);
        var (conn, tx) = HoldRead(_dbPath);
        using (conn) using (tx)
        {
            Assert.Equal(1, _maint.CheckpointWal().Busy);      // 确实握住了
            Assert.Null(_maint.CheckpointWalAndDescribe());    // 但不值得报
        }
    }

    /// <summary>
    /// ⭐ **这一条才是那次事故的复现**：WAL 已经涨大、又回收不掉 —— 必须报。
    ///
    /// 2026-09-10 C 盘被 162GB 的 WAL 撑爆，而整个过程**一句日志都没有**。
    /// 事故的特征不是"某一轮 busy"，是"**一轮比一轮大、一直涨**"，所以判据盯的是大小。
    /// </summary>
    [Fact]
    public void WAL涨大又回收不掉_必须报警()
    {
        WriteBars(100);
        InflateWal(80);                                   // 越过 64MB 的报警线
        var (conn, tx) = HoldRead(_dbPath);
        using (conn) using (tx)
        {
            var note = _maint.CheckpointWalAndDescribe();

            Assert.NotNull(note);
            Assert.Contains("回收不掉", note);
            Assert.Contains("162GB", note);               // 把那次事故的量级带上，别只说"失败了"
            // 报警之前必须已经先排除过"握着库的是本进程自己的空闲连接池"——
            // 不然用户会被支去关一堆根本没开的程序。
            Assert.Contains("本进程的空闲连接已经清过一遍了", note);
        }
    }

    /// <summary>
    /// 本进程自己用完还给池子的**空闲**连接，不该被报成"有别的连接握着这个库"（2026-09-21）。
    ///
    /// 各 Repository 都是 <c>using var conn = Open()</c>，用完归还连接池时底层 sqlite 句柄并不关。
    /// 真机上【板块指数合成】改成增量、一轮只跑十几秒之后，每轮收尾都报一次那条告警，
    /// 而当时机器上根本没有第二个程序开着库——那条消息会把用户支去关一堆没开的程序。
    /// </summary>
    [Fact]
    public void 自己池子里的空闲连接_不该报成有人握着库()
    {
        WriteBars(2000);
        // 制造一批"用完归还池子"的连接：新开一个仓储对象反复读，连接会留在池里
        var reader = new SqliteBarRepository(_dbPath);
        for (int i = 0; i < 20; i++) reader.GetLatestBar("600000", Granularity.Day);

        var note = _maint.CheckpointWalAndDescribe();

        Assert.True(note is null || !note.Contains("有别的连接握着这个库"),
                    $"空闲连接池不该触发告警，实际：{note}");
        var wal = new FileInfo(_dbPath + "-wal");
        Assert.True(!wal.Exists || wal.Length == 0, $"WAL 还有 {(wal.Exists ? wal.Length : 0)} 字节");
    }

    /// <summary>WAL 本来就小、又全回收了，就别往日志里塞噪声。</summary>
    [Fact]
    public void 没什么可说的时候不报()
    {
        WriteBars(100);
        Assert.Null(_maint.CheckpointWalAndDescribe());
    }
}
