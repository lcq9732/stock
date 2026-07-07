using System.Linq;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// Merges a daily delta file into a target database (master or a consumer's local database),
/// per doc/data-platform-design.md section 6.4. Idempotent and transactional.
/// </summary>
public static class SqliteMerger
{
    /// <summary>
    /// Granularities the fetcher recomputes/overwrites every run (see <see cref="SqliteBarUpsert"/>)
    /// because their current period is still open. A delta file's row for these can be *newer* than
    /// what's already in the target for the same primary key, so the merge must overwrite rather than
    /// ignore, or the target's current-week/current-month bar would stay frozen at its first-ever
    /// value forever. Raw fact granularities (day, and any future intraday ones) never change once
    /// written, so they keep INSERT OR IGNORE.
    /// </summary>
    private static readonly string[] DerivedGranularities = { Granularity.Week, Granularity.Month };

    public static void MergeInto(string targetDbFilePath, string deltaDbFilePath)
    {
        using var conn = new SqliteConnection($"Data Source={targetDbFilePath}");
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var attach = conn.CreateCommand();
        attach.CommandText = "ATTACH DATABASE $path AS delta;";
        attach.Parameters.AddWithValue("$path", deltaDbFilePath);
        attach.ExecuteNonQuery();

        using var tx = conn.BeginTransaction();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                var derivedList = string.Join(",", DerivedGranularities.Select(g => $"'{g}'"));
                cmd.CommandText = $"""
                    INSERT OR IGNORE INTO main.Bar SELECT * FROM delta.Bar WHERE granularity NOT IN ({derivedList});
                    INSERT OR REPLACE INTO main.Bar SELECT * FROM delta.Bar WHERE granularity IN ({derivedList});
                    INSERT OR IGNORE INTO main.FundamentalMetric SELECT * FROM delta.FundamentalMetric;
                    INSERT OR IGNORE INTO main.StockMeta SELECT * FROM delta.StockMeta;
                    """;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        finally
        {
            using var detach = conn.CreateCommand();
            detach.CommandText = "DETACH DATABASE delta;";
            detach.ExecuteNonQuery();
        }
    }
}
