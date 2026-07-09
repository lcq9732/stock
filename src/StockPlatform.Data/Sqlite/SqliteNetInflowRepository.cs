using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

public class SqliteNetInflowRepository : INetInflowRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss"; // fetched_at需要精确到时分秒，跟period_start(交易日)不是同一种格式
    private readonly string _connectionString;

    public SqliteNetInflowRepository(string dbFilePath)
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

    public void InsertOrIgnore(IEnumerable<NetInflow> rows)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO NetInflow (code, period_start, main_net_inflow, fetched_at)
            VALUES ($code, $period_start, $main_net_inflow, $fetched_at);
            """;
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pStart = cmd.CreateParameter(); pStart.ParameterName = "$period_start"; cmd.Parameters.Add(pStart);
        var pValue = cmd.CreateParameter(); pValue.ParameterName = "$main_net_inflow"; cmd.Parameters.Add(pValue);
        var pFetchedAt = cmd.CreateParameter(); pFetchedAt.ParameterName = "$fetched_at"; cmd.Parameters.Add(pFetchedAt);

        foreach (var row in rows)
        {
            pCode.Value = row.Code;
            pStart.Value = row.PeriodStart.ToString(DateFormat, CultureInfo.InvariantCulture);
            pValue.Value = row.MainNetInflow;
            pFetchedAt.Value = row.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>覆盖写入单独一行（用于"今天"这一天可能是盘中抓的、还需要收盘后再覆盖一次"的场景，
    /// 跟Bar表的SqliteBarUpsert同样的思路）——InsertOrIgnore拿来的历史交易日永远是"新事实"，不会
    /// 冲突，只有"今天"这一天才可能被同一批请求触发第二次写入。</summary>
    public void Upsert(IEnumerable<NetInflow> rows)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO NetInflow (code, period_start, main_net_inflow, fetched_at)
            VALUES ($code, $period_start, $main_net_inflow, $fetched_at);
            """;
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pStart = cmd.CreateParameter(); pStart.ParameterName = "$period_start"; cmd.Parameters.Add(pStart);
        var pValue = cmd.CreateParameter(); pValue.ParameterName = "$main_net_inflow"; cmd.Parameters.Add(pValue);
        var pFetchedAt = cmd.CreateParameter(); pFetchedAt.ParameterName = "$fetched_at"; cmd.Parameters.Add(pFetchedAt);

        foreach (var row in rows)
        {
            pCode.Value = row.Code;
            pStart.Value = row.PeriodStart.ToString(DateFormat, CultureInfo.InvariantCulture);
            pValue.Value = row.MainNetInflow;
            pFetchedAt.Value = row.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public DateTime? GetLatestPeriodStart(string code)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(period_start) FROM NetInflow WHERE code = $code;";
        cmd.Parameters.AddWithValue("$code", code);
        var result = cmd.ExecuteScalar();
        if (result == null || result is DBNull) return null;
        return DateTime.ParseExact((string)result, DateFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>最新一行的日期+实际抓取时间——跟SqliteBarRepository.GetLatestBarInfo同样的用途，
    /// FetchOrchestrator用它判断"今天"这一天是不是已经收盘后确认过了，不用再发请求。</summary>
    public (DateTime PeriodStart, DateTime FetchedAt)? GetLatestRowInfo(string code)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT period_start, fetched_at FROM NetInflow WHERE code = $code ORDER BY period_start DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$code", code);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var periodStart = DateTime.ParseExact(reader.GetString(0), DateFormat, CultureInfo.InvariantCulture);
        var fetchedAt = reader.IsDBNull(1) ? DateTime.MinValue : DateTime.ParseExact(reader.GetString(1), TimeFormat, CultureInfo.InvariantCulture);
        return (periodStart, fetchedAt);
    }

    public List<NetInflow> Query(string code, DateTime? start = null, DateTime? end = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, period_start, main_net_inflow, fetched_at
            FROM NetInflow
            WHERE code = $code
              AND ($start IS NULL OR period_start >= $start)
              AND ($end IS NULL OR period_start <= $end)
            ORDER BY period_start;
            """;
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$start", (object?)start?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$end", (object?)end?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? DBNull.Value);

        var result = new List<NetInflow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new NetInflow
            {
                Code = reader.GetString(0),
                PeriodStart = DateTime.ParseExact(reader.GetString(1), DateFormat, CultureInfo.InvariantCulture),
                MainNetInflow = reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                FetchedAt = reader.IsDBNull(3) ? DateTime.MinValue : DateTime.ParseExact(reader.GetString(3), TimeFormat, CultureInfo.InvariantCulture),
            });
        }
        return result;
    }
}
