using System.Diagnostics;
using Microsoft.Data.Sqlite;
using StockPlatform.Data.Orchestration;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 库维护操作（2026-08-29 新增）——目前只有"建大表二级索引"，由 Fetcher 的【优化数据库】按钮触发。
///
/// 独立于各 Repository：它不属于任何一张表的读写职责，而且要跨表操作、要报进度、耗时以分钟计。
/// 直接拿 db 文件路径开自己的连接（跟各 Repository 同样的做法），不进 <c>IBarRepository</c> 之类
/// 的接口，免得为一个一次性维护动作污染所有实现。
///
/// ⚠ 跑的时候别同时抓数据：WAL 下读不受影响（Analyzer 可以开着，只是会变慢），但 CREATE INDEX
/// 会阻塞写入。
/// </summary>
public class SqliteMaintenance
{
    private readonly string _connectionString;

    public SqliteMaintenance(string dbFilePath)
    {
        _connectionString = $"Data Source={dbFilePath}";
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>一次 WAL 回收的结果。字段就是 <c>PRAGMA wal_checkpoint</c> 返回的那三列。</summary>
    /// <param name="Busy">1 = 没拿到独占、没能完整回收（**有连接握着这个库**）。</param>
    /// <param name="LogPages">WAL 里现有的页数。</param>
    /// <param name="CheckpointedPages">这次搬回主库的页数。<c>== LogPages</c> 说明数据已全进主库。</param>
    public readonly record struct WalCheckpointResult(int Busy, int LogPages, int CheckpointedPages)
    {
        /// <summary>一页 4KB（SQLite 默认），换算成 MB 好读。</summary>
        public double LogMegabytes => LogPages * 4096.0 / 1024 / 1024;
        public bool FullyReclaimed => Busy == 0 && LogPages == CheckpointedPages;
    }

    /// <summary>
    /// 主动回收 WAL（2026-09-12 新增）。**批量写完之后叫一次**。
    ///
    /// ════ 为什么必须主动做 ════
    /// SQLite 默认的 autocheckpoint 是**被动**的：commit 时顺带试一下，而**只要有任何读连接
    /// 活着就跳过**。2026-09-10 那次 C 盘被 162GB 的 WAL 撑爆（931G 用到 0 可用）就是这么来的——
    /// 上一个会话在 scratchpad 里 <c>dotnet run</c> 起的验证程序跑完没退出，握着这个库两个多小时，
    /// 其间【板块指数合成】写了 468 万行，每一页都只能堆在 WAL 里。
    ///
    /// ⚠ **当时的诊断"单事务写 468 万行"是错的**（2026-09-12 查证）：那个循环从
    /// <c>1a69255</c> 起就是**一个板块一个事务**（<see cref="SqliteBarRepository.InsertOrRefreshUnconfirmed"/>
    /// 自己开连接 + <c>BeginTransaction</c>），950 个板块 = 950 个事务，每个约 4900 行。
    /// 拆事务修不了这个问题，因为本来就是拆的。
    ///
    /// ════ 为什么用 TRUNCATE ════
    /// <c>PASSIVE</c> 只搬数据、不缩文件；<c>TRUNCATE</c> 搬完还把 WAL 文件截成 0。
    /// 拿不到独占时它**不会阻塞**，直接返回 <c>busy=1</c>——所以调用方拿到 busy 要**报出来**，
    /// 那正是"有人握着库、WAL 正在失控增长"的唯一早期信号。
    ///
    /// ⚠ 别给这个连接设长 timeout：busy 时会空等满整个 timeout 才返回，看着像死锁其实是忙等。
    /// </summary>
    public WalCheckpointResult CheckpointWal()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.CommandTimeout = 15;      // busy 就赶紧回来，别忙等
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new WalCheckpointResult(0, 0, 0);
        return new WalCheckpointResult(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
    }

    /// <summary>
    /// 回收 WAL 并把结果说成人话，给日志用。没什么可说的（WAL 本来就小、又全回收了）就返回 null。
    /// </summary>
    public string? CheckpointWalAndDescribe()
    {
        WalCheckpointResult r;
        try { r = CheckpointWal(); }
        catch (Exception ex) { return $"⚠ 回收 WAL 失败：{ex.Message}"; }

        if (r.Busy != 0)
            return $"⚠ **WAL 没能回收**（还有 {r.LogPages} 页 / {r.LogMegabytes:F0} MB，"
                 + $"这次只搬回 {r.CheckpointedPages} 页）——**有别的连接握着这个库**。"
                 + "不处理的话它会一直涨：2026-09-10 那次涨到 162GB、C 盘可用归零。"
                 + "先看还有谁开着（Analyzer、或者调试起的程序），关掉再跑一次。";
        return r.LogMegabytes >= 64 ? $"已回收 WAL（{r.LogMegabytes:F0} MB）" : null;
    }

    /// <summary>还没建的索引名；空 = 已经都建好了。</summary>
    public List<string> GetMissingIndexes()
    {
        using var conn = Open();
        return SqliteSchema.GetMissingIndexes(conn);
    }

    /// <summary>
    /// 建齐 <see cref="SqliteSchema.BigTableIndexes"/> 里缺的索引，然后跑一次 ANALYZE。
    ///
    /// 逐条建、每条报耗时：Bar 那条在 7.5GB 库上要几分钟，没有中间反馈用户会以为卡死。
    /// <paramref name="ct"/> 只在**两条之间**检查——单条 CREATE INDEX 一旦下发就无法中断，
    /// 这是 SQLite 的限制，不是这里偷懒。中断后已建好的索引会保留（每条都是独立事务），
    /// 下次再点会跳过已有的继续建，所以中断是安全的。
    /// </summary>
    /// <param name="liveness">
    /// 「我还活着」的旁路通道（2026-09-08）：单条 CREATE INDEX 下发之后到返回之间没有任何
    /// 可上报的东西，库现在 23GB，一条跑十几分钟很正常。这里每 30 秒播一句"仍在建 xxx"，
    /// 免得看着像死了。⚠ 它走的是 <see cref="FetchOrchestrator.Liveness"/>，不是 progress——
    /// 那句话证明不了这条 SQL 在前进，不能拿去骗看门狗。
    /// </param>
    public void BuildIndexes(Action<string>? log, CancellationToken ct = default, Action<string>? liveness = null)
    {
        using var conn = Open();

        var missing = SqliteSchema.GetMissingIndexes(conn);
        if (missing.Count == 0)
        {
            log?.Invoke("索引已经是最新的，无需重建。");
            return;
        }
        log?.Invoke($"待建索引 {missing.Count} 条：{string.Join("、", missing)}");
        log?.Invoke("提示：期间数据库写入会被阻塞，请勿同时抓取数据。");

        var total = Stopwatch.StartNew();
        foreach (var (name, table, sql) in SqliteSchema.BigTableIndexes)
        {
            ct.ThrowIfCancellationRequested();
            if (!missing.Contains(name)) continue;

            log?.Invoke($"正在建 {name}（{table}）...");
            var sw = Stopwatch.StartNew();
            using (var beat = Heartbeat.Start(liveness, $"建索引 {name}"))
            using (var cmd = conn.CreateCommand())
            {
                // 大表建索引可能跑很久，默认 30 秒的命令超时不够。
                cmd.CommandTimeout = 0;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            log?.Invoke($"  {name} 完成，耗时 {sw.Elapsed.TotalSeconds:F1} 秒");
        }

        // 没有统计信息时查询计划器可能不选新索引——建完必须跑一次。
        log?.Invoke("正在更新统计信息（ANALYZE）...");
        var asw = Stopwatch.StartNew();
        using (var beat = Heartbeat.Start(liveness, "更新统计信息（ANALYZE）"))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandTimeout = 0;
            cmd.CommandText = "ANALYZE;";
            cmd.ExecuteNonQuery();
        }
        log?.Invoke($"  ANALYZE 完成，耗时 {asw.Elapsed.TotalSeconds:F1} 秒");
        log?.Invoke($"优化完成，总耗时 {total.Elapsed.TotalMinutes:F1} 分钟。");
    }
}
