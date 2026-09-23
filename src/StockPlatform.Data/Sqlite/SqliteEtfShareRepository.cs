using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>沪深交易所 ETF 份额的本地存取（EtfShare 表）。表结构见 <see cref="SqliteSchema"/>。</summary>
public class SqliteEtfShareRepository : IEtfShareRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private readonly string _connectionString;

    public SqliteEtfShareRepository(string dbFilePath)
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

    public void Upsert(IReadOnlyList<EtfShareRow> rows)
    {
        if (rows.Count == 0) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO EtfShare (market, code, trade_date, shares_wan, fetched_at)
            VALUES ($market, $code, $day, $wan, $at)
            ON CONFLICT(code, trade_date) DO UPDATE SET
                market = excluded.market, shares_wan = excluded.shares_wan, fetched_at = excluded.fetched_at;
            """;
        var pMarket = cmd.Parameters.Add("$market", SqliteType.Text);
        var pCode = cmd.Parameters.Add("$code", SqliteType.Text);
        var pDay = cmd.Parameters.Add("$day", SqliteType.Text);
        var pWan = cmd.Parameters.Add("$wan", SqliteType.Real);
        cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

        foreach (var r in rows)
        {
            pMarket.Value = r.Market;
            pCode.Value = r.Code;
            pDay.Value = r.TradeDate.ToString(DateFormat, CultureInfo.InvariantCulture);
            pWan.Value = r.SharesWan;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public HashSet<DateOnly> GetDays(string market)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT trade_date FROM EtfShare WHERE market = $market;";
        cmd.Parameters.AddWithValue("$market", market);
        var set = new HashSet<DateOnly>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (DateOnly.TryParseExact(r.GetString(0), DateFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d))
                set.Add(d);
        return set;
    }

    public List<(string Market, string Code)> GetCodes()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT market, code FROM EtfShare ORDER BY market, code;";
        var list = new List<(string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    public Dictionary<DateOnly, double> GetByCode(string market, string code)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT trade_date, shares_wan FROM EtfShare WHERE market = $market AND code = $code;";
        cmd.Parameters.AddWithValue("$market", market);
        cmd.Parameters.AddWithValue("$code", code);
        var map = new Dictionary<DateOnly, double>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (DateOnly.TryParseExact(r.GetString(0), DateFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d))
                map[d] = r.GetDouble(1);
        return map;
    }
}
