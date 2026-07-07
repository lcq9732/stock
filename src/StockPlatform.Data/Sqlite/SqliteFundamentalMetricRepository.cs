using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

public class SqliteFundamentalMetricRepository : IFundamentalMetricRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private readonly string _connectionString;

    public SqliteFundamentalMetricRepository(string dbFilePath)
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

    public void InsertOrIgnore(IEnumerable<FundamentalMetric> metrics)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO FundamentalMetric (code, metric_key, as_of_date, value, source, fetched_at)
            VALUES ($code, $metric_key, $as_of_date, $value, $source, $fetched_at);
            """;
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pKey = cmd.CreateParameter(); pKey.ParameterName = "$metric_key"; cmd.Parameters.Add(pKey);
        var pDate = cmd.CreateParameter(); pDate.ParameterName = "$as_of_date"; cmd.Parameters.Add(pDate);
        var pValue = cmd.CreateParameter(); pValue.ParameterName = "$value"; cmd.Parameters.Add(pValue);
        var pSource = cmd.CreateParameter(); pSource.ParameterName = "$source"; cmd.Parameters.Add(pSource);
        var pFetchedAt = cmd.CreateParameter(); pFetchedAt.ParameterName = "$fetched_at"; cmd.Parameters.Add(pFetchedAt);

        foreach (var m in metrics)
        {
            pCode.Value = m.Code;
            pKey.Value = m.MetricKey;
            pDate.Value = m.AsOfDate.ToString(DateFormat, CultureInfo.InvariantCulture);
            pValue.Value = m.Value;
            pSource.Value = m.Source;
            pFetchedAt.Value = m.FetchedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<FundamentalMetric> Query(string code, string metricKey, DateTime? start = null, DateTime? end = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, metric_key, as_of_date, value, source, fetched_at
            FROM FundamentalMetric
            WHERE code = $code AND metric_key = $metric_key
              AND ($start IS NULL OR as_of_date >= $start)
              AND ($end IS NULL OR as_of_date <= $end)
            ORDER BY as_of_date;
            """;
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$metric_key", metricKey);
        cmd.Parameters.AddWithValue("$start", (object?)start?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$end", (object?)end?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? DBNull.Value);

        var result = new List<FundamentalMetric>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new FundamentalMetric
            {
                Code = reader.GetString(0),
                MetricKey = reader.GetString(1),
                AsOfDate = DateTime.ParseExact(reader.GetString(2), DateFormat, CultureInfo.InvariantCulture),
                Value = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                Source = reader.IsDBNull(4) ? "" : reader.GetString(4),
                FetchedAt = reader.IsDBNull(5) ? default : DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            });
        }
        return result;
    }
}
