using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>龙虎榜的本地存取（Lhb 表）——按交易日累积保留历史（INSERT OR IGNORE），仿
/// <see cref="SqliteNetInflowRepository"/>。</summary>
public class SqliteLhbRepository : ILhbRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteLhbRepository(string dbFilePath)
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

    public void InsertOrIgnore(IEnumerable<LhbRow> rows)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO Lhb (trade_date, stock_code, stock_name, close_price, deviation, volume, amount, reason, fetched_at)
            VALUES ($date, $code, $name, $close, $dev, $vol, $amt, $reason, $at);
            """;
        var pDate = cmd.CreateParameter(); pDate.ParameterName = "$date"; cmd.Parameters.Add(pDate);
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
        var pClose = cmd.CreateParameter(); pClose.ParameterName = "$close"; cmd.Parameters.Add(pClose);
        var pDev = cmd.CreateParameter(); pDev.ParameterName = "$dev"; cmd.Parameters.Add(pDev);
        var pVol = cmd.CreateParameter(); pVol.ParameterName = "$vol"; cmd.Parameters.Add(pVol);
        var pAmt = cmd.CreateParameter(); pAmt.ParameterName = "$amt"; cmd.Parameters.Add(pAmt);
        var pReason = cmd.CreateParameter(); pReason.ParameterName = "$reason"; cmd.Parameters.Add(pReason);
        var pAt = cmd.CreateParameter(); pAt.ParameterName = "$at"; cmd.Parameters.Add(pAt);

        foreach (var r in rows)
        {
            pDate.Value = r.TradeDate.ToString(DateFormat, CultureInfo.InvariantCulture);
            pCode.Value = r.StockCode;
            pName.Value = (object?)r.StockName ?? DBNull.Value;
            pClose.Value = r.ClosePrice;
            pDev.Value = r.Deviation;
            pVol.Value = r.Volume;
            pAmt.Value = r.Amount;
            pReason.Value = r.Reason;
            pAt.Value = r.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public DateTime? GetLatestTradeDate()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(trade_date) FROM Lhb;";
        var result = cmd.ExecuteScalar();
        if (result == null || result is DBNull) return null;
        return DateTime.ParseExact((string)result, DateFormat, CultureInfo.InvariantCulture);
    }

    public HashSet<DateOnly> GetTradeDates()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT trade_date FROM Lhb;";
        var set = new HashSet<DateOnly>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (DateOnly.TryParseExact(reader.GetString(0), DateFormat, out var d)) set.Add(d);
        return set;
    }
}
