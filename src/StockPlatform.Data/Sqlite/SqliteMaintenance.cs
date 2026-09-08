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
