using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>融资融券明细本地存取（MarginDetail 表）——按交易日累积(INSERT OR IGNORE)，仿
/// <see cref="SqliteLhbRepository"/>。</summary>
public class SqliteMarginRepository : IMarginRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteMarginRepository(string dbFilePath)
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

    public void InsertOrIgnore(IEnumerable<MarginDetailRow> rows)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO MarginDetail (trade_date, code, name, margin_balance, margin_buy,
                                                short_balance, short_volume,
                                                margin_repay, short_sell_volume, short_repay_volume, fetched_at)
            VALUES ($d, $c, $n, $mb, $bu, $sb, $sv, $rp, $ssv, $srv, $at);
            """;
        var pd = cmd.CreateParameter(); pd.ParameterName = "$d"; cmd.Parameters.Add(pd);
        var pc = cmd.CreateParameter(); pc.ParameterName = "$c"; cmd.Parameters.Add(pc);
        var pn = cmd.CreateParameter(); pn.ParameterName = "$n"; cmd.Parameters.Add(pn);
        var pmb = cmd.CreateParameter(); pmb.ParameterName = "$mb"; cmd.Parameters.Add(pmb);
        var pbu = cmd.CreateParameter(); pbu.ParameterName = "$bu"; cmd.Parameters.Add(pbu);
        var psb = cmd.CreateParameter(); psb.ParameterName = "$sb"; cmd.Parameters.Add(psb);
        var psv = cmd.CreateParameter(); psv.ParameterName = "$sv"; cmd.Parameters.Add(psv);
        var prp = cmd.CreateParameter(); prp.ParameterName = "$rp"; cmd.Parameters.Add(prp);
        var pssv = cmd.CreateParameter(); pssv.ParameterName = "$ssv"; cmd.Parameters.Add(pssv);
        var psrv = cmd.CreateParameter(); psrv.ParameterName = "$srv"; cmd.Parameters.Add(psrv);
        var pat = cmd.CreateParameter(); pat.ParameterName = "$at"; cmd.Parameters.Add(pat);

        foreach (var r in rows)
        {
            pd.Value = r.TradeDate.ToString(DateFormat, CultureInfo.InvariantCulture);
            pc.Value = r.Code;
            pn.Value = (object?)r.Name ?? DBNull.Value;
            pmb.Value = r.MarginBalance;
            pbu.Value = r.MarginBuy;
            // ⚠ 可空的四项必须写 DBNull 而不是 0：源头不提供（沪市没有融券余额、
            // 深市没有偿还额）跟"当天确实是 0"是两回事，混在一起就是之前那个
            // "1675 只沪市票融券余额恒 0"的 bug。
            psb.Value = (object?)r.ShortBalance ?? DBNull.Value;
            psv.Value = r.ShortVolume;
            prp.Value = (object?)r.MarginRepay ?? DBNull.Value;
            pssv.Value = (object?)r.ShortSellVolume ?? DBNull.Value;
            psrv.Value = (object?)r.ShortRepayVolume ?? DBNull.Value;
            pat.Value = r.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public DateTime? GetLatestTradeDate()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(trade_date) FROM MarginDetail;";
        var result = cmd.ExecuteScalar();
        if (result == null || result is DBNull) return null;
        return DateTime.ParseExact((string)result, DateFormat, CultureInfo.InvariantCulture);
    }

    public HashSet<DateOnly> GetTradeDates()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT trade_date FROM MarginDetail;";
        var set = new HashSet<DateOnly>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (DateOnly.TryParseExact(reader.GetString(0), DateFormat, out var d)) set.Add(d);
        return set;
    }

    public List<(DateTime TradeDate, double MarginBalance)> GetBalanceSeries(string code)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT trade_date, margin_balance FROM MarginDetail WHERE code = $c ORDER BY trade_date;";
        cmd.Parameters.AddWithValue("$c", code);
        var result = new List<(DateTime, double)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((
                DateTime.ParseExact(reader.GetString(0), DateFormat, CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? 0 : reader.GetDouble(1)));
        return result;
    }
}
