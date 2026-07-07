using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// Upserts (INSERT OR REPLACE) bars — unlike raw day bars (immutable facts, always INSERT OR
/// IGNORE via <see cref="SqliteBarRepository"/>), derived week/month bars need to be
/// recomputed/overwritten as new days arrive within the still-open period.
/// </summary>
public static class SqliteBarUpsert
{
    private const string DateFormat = "yyyy-MM-dd HH:mm:ss";

    public static void Upsert(string dbFilePath, IEnumerable<Bar> bars)
    {
        using var conn = new SqliteConnection($"Data Source={dbFilePath}");
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO Bar
                (code, granularity, period_start, open, close, high, low, volume, amount, pct_chg, turnover)
            VALUES
                ($code, $granularity, $period_start, $open, $close, $high, $low, $volume, $amount, $pct_chg, $turnover);
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
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
