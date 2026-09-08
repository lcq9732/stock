using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Sqlite;

/// <summary>交易日历的本地存取（TradingDay 表）。表结构和两个来源的分工见 <see cref="SqliteSchema"/>。</summary>
public class SqliteTradingDayRepository : ITradingDayRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private readonly string _connectionString;

    public SqliteTradingDayRepository(string dbFilePath)
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

    public void Upsert(IEnumerable<(DateOnly Day, string Source)> days)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // 官方值要能纠正归纳值，所以是 UPSERT 不是 INSERT OR IGNORE
        cmd.CommandText = """
            INSERT INTO TradingDay (day, source) VALUES ($day, $src)
            ON CONFLICT(day) DO UPDATE SET source = excluded.source;
            """;
        var pDay = cmd.CreateParameter(); pDay.ParameterName = "$day"; cmd.Parameters.Add(pDay);
        var pSrc = cmd.CreateParameter(); pSrc.ParameterName = "$src"; cmd.Parameters.Add(pSrc);

        foreach (var (day, source) in days)
        {
            pDay.Value = day.ToString(DateFormat, CultureInfo.InvariantCulture);
            pSrc.Value = source;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<DateTime> GetAll()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT day FROM TradingDay ORDER BY day;";
        var list = new List<DateTime>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (DateTime.TryParseExact(reader.GetString(0), DateFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                list.Add(d);
        return list;
    }

    public (DateTime? Min, DateTime? Max) GetRange()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(day), MAX(day) FROM TradingDay;";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0)) return (null, null);
        return (Parse(reader.GetString(0)), Parse(reader.GetString(1)));

        static DateTime? Parse(string s) => DateTime.TryParseExact(
            s, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    public int Count(string? source = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = source == null
            ? "SELECT COUNT(*) FROM TradingDay;"
            : "SELECT COUNT(*) FROM TradingDay WHERE source = $src;";
        if (source != null) cmd.Parameters.AddWithValue("$src", source);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public HashSet<DateOnly> GetBetween(DateOnly from, DateOnly to)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT day FROM TradingDay WHERE day >= $from AND day <= $to;";
        cmd.Parameters.AddWithValue("$from", from.ToString(DateFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$to", to.ToString(DateFormat, CultureInfo.InvariantCulture));
        var set = new HashSet<DateOnly>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (DateOnly.TryParseExact(reader.GetString(0), DateFormat, out var d)) set.Add(d);
        return set;
    }
}
