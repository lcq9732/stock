using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>停复牌记录的本地存取（Suspension / SuspensionFetchMonth 表）。表结构见 <see cref="SqliteSchema"/>。</summary>
public class SqliteSuspensionRepository : ISuspensionRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private readonly string _connectionString;

    public SqliteSuspensionRepository(string dbFilePath)
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

    public void Upsert(IReadOnlyList<SuspensionRow> rows)
    {
        if (rows.Count == 0) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Suspension (source, market, code, event_key, name, start_day, start_time,
                                    end_day, end_time, kind, stop_time, reason,
                                    end_reason, control_type, end_kind, start_type, end_type,
                                    date_source, full_name, fetched_at)
            VALUES ($source, $market, $code, $key, $name, $sd, $st, $ed, $et, $kind, $stop, $reason,
                    $endReason, $control, $endKind, $startType, $endType, $dateSource, $fullName, $at)
            ON CONFLICT(source, market, code, event_key) DO UPDATE SET
                name = excluded.name, end_day = excluded.end_day, end_time = excluded.end_time,
                reason = excluded.reason, end_reason = excluded.end_reason,
                control_type = excluded.control_type, end_kind = excluded.end_kind,
                start_type = excluded.start_type, end_type = excluded.end_type,
                date_source = excluded.date_source, full_name = excluded.full_name,
                fetched_at = excluded.fetched_at;
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var n in new[] { "$source", "$market", "$code", "$key", "$name", "$sd", "$st",
                                  "$ed", "$et", "$kind", "$stop", "$reason", "$endReason", "$control",
                                  "$endKind", "$startType", "$endType", "$dateSource", "$fullName" })
            p[n] = cmd.Parameters.Add(n, SqliteType.Text);
        cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

        foreach (var r in rows)
        {
            p["$source"].Value = r.Source;
            p["$market"].Value = r.Market;
            p["$code"].Value = r.Code;
            p["$key"].Value = r.Key;
            p["$name"].Value = r.Name;
            p["$sd"].Value = (object?)r.StartDay?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? DBNull.Value;
            p["$st"].Value = r.StartTime;
            p["$ed"].Value = (object?)r.EndDay?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? DBNull.Value;
            p["$et"].Value = r.EndTime;
            p["$kind"].Value = r.Kind;
            p["$stop"].Value = r.StopTime;
            p["$reason"].Value = r.Reason;
            p["$endReason"].Value = r.EndReason;
            p["$control"].Value = r.ControlType;
            p["$endKind"].Value = r.EndKind;
            p["$startType"].Value = r.StartType;
            p["$endType"].Value = r.EndType;
            p["$dateSource"].Value = r.DateSource;
            p["$fullName"].Value = r.FullName;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public HashSet<DateOnly> GetFetchedMonths(string source)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT month FROM SuspensionFetchMonth WHERE source = $source;";
        cmd.Parameters.AddWithValue("$source", source);
        var set = new HashSet<DateOnly>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (DateOnly.TryParseExact(r.GetString(0), DateFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d))
                set.Add(d);
        return set;
    }

    public void MarkMonthFetched(string source, DateOnly month, int rows)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SuspensionFetchMonth (source, month, rows, fetched_at) VALUES ($source, $month, $rows, $at)
            ON CONFLICT(source, month) DO UPDATE SET rows = excluded.rows, fetched_at = excluded.fetched_at;
            """;
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$month", new DateOnly(month.Year, month.Month, 1)
            .ToString(DateFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$rows", rows);
        cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, List<SuspensionRow>> GetByBarCodes(IReadOnlyCollection<string> barCodes)
    {
        var result = new Dictionary<string, List<SuspensionRow>>(StringComparer.Ordinal);
        if (barCodes.Count == 0) return result;

        // Bar 代码 → 6 位裸码；按裸码批量查，回来再按 BarCodeMatches 分回去（ETF 要连市场一起对）
        var byBare = barCodes.Distinct(StringComparer.Ordinal)
            .GroupBy(c => c.Length == 8 ? c[2..] : c, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        using var conn = Open();
        foreach (var chunk in byBare.Keys.Chunk(500))
        {
            using var cmd = conn.CreateCommand();
            var names = chunk.Select((_, i) => $"$c{i}").ToList();
            cmd.CommandText = "SELECT source, market, code, name, start_day, start_time, end_day, end_time, "
                            + "kind, stop_time, reason, end_reason, control_type, end_kind, start_type, end_type, "
                            + "date_source, full_name FROM Suspension "
                            + $"WHERE code IN ({string.Join(",", names)});";
            for (int i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue(names[i], chunk[i]);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = new SuspensionRow(
                    r.GetString(0), r.GetString(1), r.GetString(2), Text(r, 3),
                    Day(r, 4), Text(r, 5), Day(r, 6), Text(r, 7),
                    Text(r, 8), Text(r, 9), Text(r, 10), Text(r, 11), Text(r, 12),
                    Text(r, 13), Text(r, 14), Text(r, 15), Text(r, 16), Text(r, 17));
                foreach (var bc in byBare[row.Code])
                {
                    if (!row.BarCodeMatches(bc)) continue;
                    if (!result.TryGetValue(bc, out var list)) result[bc] = list = [];
                    list.Add(row);
                }
            }
        }
        return result;
    }

    private static string Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);

    private static DateOnly? Day(SqliteDataReader r, int i) =>
        !r.IsDBNull(i) && DateOnly.TryParseExact(r.GetString(i), DateFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;
}
