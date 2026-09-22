using System.Globalization;
using Microsoft.Data.Sqlite;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// <c>BarProbeFloor</c> 表的存取——"数据源在这一天之前没有这只标的的K线"这个已探明的结论。
///
/// ⚠ **2026-09-22 起只有读、没有写**：唯一的写入方是老【拉取区间数据】的 RecordProbeFloors，
/// 那一项改成分派器时整段删掉了。已有的 17541 行永远成立（水位是数据源的属性、不是我们库的状态），
/// 所以读它安全；但这张表**不再长大**——新上市的标的、以后新探明的空区间都不会被记下来。
/// 要补写入的话，判据现成（<see cref="StockPlatform.Logic.Services.ProbeFloorPlanner"/>），
/// 落点在各K线任务的抓取路径上（BarFetchTaskBase.FetchOneAsync 那里知道"成功且返回 0 行"）。
///
/// 读它的是各K线任务的整段回补（BarFetchTaskBase.PlanGaps →
/// <see cref="StockPlatform.Logic.Services.YearGapCalculator"/>）：往前补历史时先把缺口起点抬到
/// 这个水位，抬过缺口就整只跳过、连请求都不发。没有它的话，每次重跑都要把"那些年还没上市"的票
/// 重新试一遍（2026-09-07 实测一万六千个请求、四个半小时零写入）。
///
/// 写入方**只信"请求成功但返回 0 行"**这一种情形，且只在往前补缺口时记（终点早于本地最早一根）。
/// 请求失败/被限流走的是另一条路（记进失败名单重试），绝不能记成"数据源没有"。
/// 判定逻辑本身在 <see cref="StockPlatform.Logic.Services.ProbeFloorPlanner"/>（纯计算、可单测）。
/// </summary>
public class SqliteBarProbeFloorRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private readonly string _connectionString;

    public SqliteBarProbeFloorRepository(string dbFilePath)
    {
        _connectionString = $"Data Source={dbFilePath}";
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void EnsureSchema()
    {
        using var conn = Open();
        SqliteSchema.EnsureSchema(conn);
    }

    /// <summary>某个粒度下全部已探明的水位（code → 这天之前没数据）。表不存在就返回空字典。</summary>
    public Dictionary<string, DateTime> GetAll(string granularity)
    {
        var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT code, no_data_before FROM BarProbeFloor WHERE granularity = $g;";
            cmd.Parameters.AddWithValue("$g", granularity);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (DateTime.TryParseExact(r.GetString(1), DateFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var d))
                    result[r.GetString(0)] = d;
            }
        }
        catch (SqliteException) { /* 老库还没有这张表 → 当作一条水位都没有，行为回到改造前 */ }
        return result;
    }

    /// <summary>
    /// 记下水位。**只抬高、不下调**（ON CONFLICT 里用 MAX）——同一只票在更宽的区间上又探到一次空，
    /// 水位应该往后走；而某次探测窗口较窄（比如只补了 2016 那一年）不该把已有的更高水位改矮。
    /// </summary>
    public int Record(IEnumerable<(string Code, DateTime NoDataBefore)> entries, string granularity)
    {
        var list = entries.ToList();
        if (list.Count == 0) return 0;

        using var conn = Open();
        SqliteSchema.EnsureSchema(conn);
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO BarProbeFloor (code, granularity, no_data_before, probed_at)
            VALUES ($code, $g, $d, $at)
            ON CONFLICT(code, granularity) DO UPDATE SET
                no_data_before = MAX(no_data_before, excluded.no_data_before),
                probed_at = excluded.probed_at;
            """;
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pDay = cmd.CreateParameter(); pDay.ParameterName = "$d"; cmd.Parameters.Add(pDay);
        cmd.Parameters.AddWithValue("$g", granularity);
        cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

        foreach (var (code, day) in list)
        {
            pCode.Value = code;
            pDay.Value = day.ToString(DateFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }

    /// <summary>表里有多少条（报个数，让人知道攒了多少已探明的水位）。</summary>
    public int Count()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM BarProbeFloor;";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        catch { return 0; }
    }

    /// <summary>清空——【全库数据体检】勾「彻底体检」时用，把探明的结论全部作废、下一轮重新探。</summary>
    public void Clear()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM BarProbeFloor;";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException) { /* 表还不存在 → 本来就是空的 */ }
    }
}
