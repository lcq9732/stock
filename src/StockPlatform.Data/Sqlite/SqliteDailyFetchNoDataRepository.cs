using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Sqlite;

/// <summary>「确认这天就是没有」名单的本地存取（DailyFetchNoData 表）。判据见接口和建表注释。</summary>
public class SqliteDailyFetchNoDataRepository : IDailyFetchNoDataRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteDailyFetchNoDataRepository(string dbFilePath)
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

    public HashSet<DateOnly> GetConfirmed(string dataset)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT day FROM DailyFetchNoData WHERE dataset = $ds;";
        cmd.Parameters.AddWithValue("$ds", dataset);
        var set = new HashSet<DateOnly>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (DateOnly.TryParseExact(reader.GetString(0), DateFormat, out var d)) set.Add(d);
        return set;
    }

    public void Confirm(string dataset, DateOnly day)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DailyFetchNoData (dataset, day, confirmed_at) VALUES ($ds, $day, $at)
            ON CONFLICT(dataset, day) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$ds", dataset);
        cmd.Parameters.AddWithValue("$day", day.ToString(DateFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public void Remove(string dataset, DateOnly day)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM DailyFetchNoData WHERE dataset = $ds AND day = $day;";
        cmd.Parameters.AddWithValue("$ds", dataset);
        cmd.Parameters.AddWithValue("$day", day.ToString(DateFormat, CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public int Clear(string dataset)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM DailyFetchNoData WHERE dataset = $ds;";
        cmd.Parameters.AddWithValue("$ds", dataset);
        return cmd.ExecuteNonQuery();
    }

    public int Count(string dataset)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM DailyFetchNoData WHERE dataset = $ds;";
        cmd.Parameters.AddWithValue("$ds", dataset);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }
}
