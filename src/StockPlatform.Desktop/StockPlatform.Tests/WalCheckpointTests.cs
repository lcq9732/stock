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

    /// <summary>
    /// **这一条是那次事故的复现**：有别的连接握着库时，`TRUNCATE` 拿不到独占 ⇒ `busy=1`。
    /// 它必须被如实返回、而不是被当成"回收成功"——那正是 WAL 开始失控的信号。
    /// </summary>
    [Fact]
    public void 有别的连接握着时_busy要如实报出来()
    {
        WriteBars(2000);

        using var holder = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        holder.Open();
        using var tx = holder.BeginTransaction();          // 握一个读事务不放
        using var read = holder.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT COUNT(*) FROM Bar;";
        read.ExecuteScalar();

        var r = _maint.CheckpointWal();
        Assert.Equal(1, r.Busy);
        Assert.False(r.FullyReclaimed);

        var note = _maint.CheckpointWalAndDescribe();
        Assert.NotNull(note);
        Assert.Contains("有别的连接握着这个库", note);
        Assert.Contains("162GB", note);                     // 把那次事故的量级带上，别只说"失败了"
    }

    /// <summary>WAL 本来就小、又全回收了，就别往日志里塞噪声。</summary>
    [Fact]
    public void 没什么可说的时候不报()
    {
        WriteBars(100);
        Assert.Null(_maint.CheckpointWalAndDescribe());
    }
}
