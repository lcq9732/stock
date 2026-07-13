using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

public class SqliteBarRepository : IBarRepository
{
    private const string DateFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteBarRepository(string dbFilePath)
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

    public void InsertOrIgnore(IEnumerable<Bar> bars)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO Bar
                (code, granularity, period_start, open, close, high, low, volume, amount, pct_chg, turnover, fetched_at)
            VALUES
                ($code, $granularity, $period_start, $open, $close, $high, $low, $volume, $amount, $pct_chg, $turnover, $fetched_at);
            """;
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pGran = cmd.CreateParameter(); pGran.ParameterName = "$granularity"; cmd.Parameters.Add(pGran);
        var pStart = cmd.CreateParameter(); pStart.ParameterName = "$period_start"; cmd.Parameters.Add(pStart);
        var pOpen = cmd.CreateParameter(); pOpen.ParameterName = "$open"; cmd.Parameters.Add(pOpen);
        var pClose = cmd.CreateParameter(); pClose.ParameterName = "$close"; cmd.Parameters.Add(pClose);
        var pHigh = cmd.CreateParameter(); pHigh.ParameterName = "$high"; cmd.Parameters.Add(pHigh);
        var pLow = cmd.CreateParameter(); pLow.ParameterName = "$low"; cmd.Parameters.Add(pLow);
        var pVolume = cmd.CreateParameter(); pVolume.ParameterName = "$volume"; cmd.Parameters.Add(pVolume);
        var pAmount = cmd.CreateParameter(); pAmount.ParameterName = "$amount"; cmd.Parameters.Add(pAmount);
        var pPct = cmd.CreateParameter(); pPct.ParameterName = "$pct_chg"; cmd.Parameters.Add(pPct);
        var pTurnover = cmd.CreateParameter(); pTurnover.ParameterName = "$turnover"; cmd.Parameters.Add(pTurnover);
        var pFetchedAt = cmd.CreateParameter(); pFetchedAt.ParameterName = "$fetched_at"; cmd.Parameters.Add(pFetchedAt);

        foreach (var bar in bars)
        {
            pCode.Value = bar.Code;
            pGran.Value = bar.Granularity;
            pStart.Value = bar.PeriodStart.ToString(DateFormat, CultureInfo.InvariantCulture);
            pOpen.Value = bar.Open;
            pClose.Value = bar.Close;
            pHigh.Value = bar.High;
            pLow.Value = bar.Low;
            pVolume.Value = bar.Volume;
            pAmount.Value = bar.Amount;
            pPct.Value = bar.PctChange;
            pTurnover.Value = bar.Turnover;
            pFetchedAt.Value = bar.FetchedAt.ToString(DateFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public DateTime? GetLatestPeriodStart(string code, string granularity)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(period_start) FROM Bar WHERE code = $code AND granularity = $granularity;";
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$granularity", granularity);
        var result = cmd.ExecuteScalar();
        if (result == null || result is DBNull) return null;
        return DateTime.ParseExact((string)result, DateFormat, CultureInfo.InvariantCulture);
    }

    public DateTime? GetOverallLatestPeriodStart(string granularity)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(period_start) FROM Bar WHERE granularity = $granularity;";
        cmd.Parameters.AddWithValue("$granularity", granularity);
        var result = cmd.ExecuteScalar();
        if (result == null || result is DBNull) return null;
        return DateTime.ParseExact((string)result, DateFormat, CultureInfo.InvariantCulture);
    }

    public DateTime? GetOverallLatestPeriodStartOnOrBefore(string granularity, DateTime cutoff)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(period_start) FROM Bar WHERE granularity = $granularity AND period_start <= $cutoff;";
        cmd.Parameters.AddWithValue("$granularity", granularity);
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString(DateFormat, CultureInfo.InvariantCulture));
        var result = cmd.ExecuteScalar();
        if (result == null || result is DBNull) return null;
        return DateTime.ParseExact((string)result, DateFormat, CultureInfo.InvariantCulture);
    }

    public DateTime? GetOverallEarliestPeriodStart(string granularity)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(period_start) FROM Bar WHERE granularity = $granularity;";
        cmd.Parameters.AddWithValue("$granularity", granularity);
        var result = cmd.ExecuteScalar();
        if (result == null || result is DBNull) return null;
        return DateTime.ParseExact((string)result, DateFormat, CultureInfo.InvariantCulture);
    }

    public List<Bar> Query(string code, string granularity, DateTime? start = null, DateTime? end = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, granularity, period_start, open, close, high, low, volume, amount, pct_chg, turnover, fetched_at
            FROM Bar
            WHERE code = $code AND granularity = $granularity
              AND ($start IS NULL OR period_start >= $start)
              AND ($end IS NULL OR period_start <= $end)
            ORDER BY period_start;
            """;
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$granularity", granularity);
        cmd.Parameters.AddWithValue("$start", (object?)start?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$end", (object?)end?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? DBNull.Value);

        var result = new List<Bar>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new Bar
            {
                Code = reader.GetString(0),
                Granularity = reader.GetString(1),
                PeriodStart = DateTime.ParseExact(reader.GetString(2), DateFormat, CultureInfo.InvariantCulture),
                Open = reader.GetDouble(3),
                Close = reader.GetDouble(4),
                High = reader.GetDouble(5),
                Low = reader.GetDouble(6),
                Volume = reader.GetDouble(7),
                Amount = reader.GetDouble(8),
                PctChange = reader.GetDouble(9),
                Turnover = reader.GetDouble(10),
                // 老数据（这个字段2026-07-09之前没有）读出来是DBNull——用MinValue兜底，永远判定为
                // "未确认最终"，直到这一天被重新抓到一次为止（只影响"今天"这一天的判断，更早的
                // 历史天数不会因为FetchedAt是MinValue而被误判成需要重新抓——见FetchOrchestrator
                // 的水位线逻辑，只有period_start等于当前日期时才会去看FetchedAt）。
                FetchedAt = reader.IsDBNull(11) ? DateTime.MinValue : DateTime.ParseExact(reader.GetString(11), DateFormat, CultureInfo.InvariantCulture),
            });
        }
        return result;
    }

    /// <summary>最新一行的日期+实际抓取时间，一次查询同时拿到两者（比先GetLatestPeriodStart再
    /// 单独查一次fetched_at少一次往返）——FetchOrchestrator用它判断"今天"这一天是不是已经收盘后
    /// 确认过了，不用再发请求。</summary>
    public (DateTime PeriodStart, DateTime FetchedAt)? GetLatestBarInfo(string code, string granularity)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT period_start, fetched_at FROM Bar
            WHERE code = $code AND granularity = $granularity
            ORDER BY period_start DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$granularity", granularity);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var periodStart = DateTime.ParseExact(reader.GetString(0), DateFormat, CultureInfo.InvariantCulture);
        var fetchedAt = reader.IsDBNull(1) ? DateTime.MinValue : DateTime.ParseExact(reader.GetString(1), DateFormat, CultureInfo.InvariantCulture);
        return (periodStart, fetchedAt);
    }

    public List<string> GetAllCodes()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT code FROM Bar ORDER BY code;";
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }
}
