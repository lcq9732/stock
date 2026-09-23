using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>一只 ETF 的一根日K里跟换手率校正有关的那几列。</summary>
/// <param name="Granularity">day / day_raw / day_adj。</param>
/// <param name="PeriodStart">Bar.period_start 原样（写回时按它定位，不重新格式化）。</param>
/// <param name="Day">交易日。</param>
public sealed record EtfTurnoverBarRow(
    string Granularity, string PeriodStart, DateOnly Day, double? Volume, double? Turnover);

/// <summary>
/// 【ETF换手率校正】的 Bar 读写（2026-09-23）。只读写，判据在
/// <see cref="StockPlatform.Logic.Services.EtfTurnoverRule"/>，编排在 EtfTurnoverRecalcTask。
///
/// ════ 按只读、按只写 ════
/// Bar 的主键是 (code, granularity, period_start)，按 code 取走的是主键前缀、很快；
/// 按日期取要扫全表（granularity/period_start 都没有单独的索引）。所以这里一只 ETF 一次，
/// 写回也一只一个事务——一只 ETF 最多三四千天 × 3 个口径，事务小、WAL 不涨，中断只丢当前这只
/// （单事务写几百万行曾把 WAL 撑到 162GB，见 project_wal_blowup_and_stale_dotnet）。
///
/// ⚠ **只写 turnover 一列**，OHLC、volume、amount、fetched_at 一概不碰。
/// </summary>
public class SqliteEtfTurnoverStore
{
    /// <summary>
    /// 要校正的三个口径。三套的 volume 逐值相同，换手率也应该相同。
    /// ETF 没有后复权（day_hfq），所以不在里面。
    /// </summary>
    public static readonly IReadOnlyList<string> Granularities =
        [Granularity.Day, Granularity.DayRaw, Granularity.DayAdj];

    private readonly string _connectionString;

    public SqliteEtfTurnoverStore(string dbFilePath)
    {
        _connectionString = $"Data Source={dbFilePath}";
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// 本地名册里某个市场的 ETF（StockMeta.type='etf' 且存成该市场前缀的，如 sh510150）。
    ///
    /// 只用来**报数**：交易所份额表里没有的 ETF（沪市的货币 ETF 就不在上交所那个接口里）这一项校正不了，
    /// 不报出来的话它们会安安静静地停在 0。前缀是新浪 ETF 列表原样给的，不是按代码规则猜的。
    /// </summary>
    public List<string> ListEtfCodes(string market)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code FROM StockMeta WHERE type = 'etf' AND substr(code, 1, 2) = $market ORDER BY code;";
        cmd.Parameters.AddWithValue("$market", market);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>一只 ETF 在三个口径下的全部日K（只取校正要用的列）。</summary>
    /// <param name="barCode">Bar 里的代码，带前缀（sh510150）。</param>
    public List<EtfTurnoverBarRow> ReadBars(string barCode)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT granularity, period_start, volume, turnover
            FROM Bar
            WHERE code = $code AND granularity IN ($g1, $g2, $g3);
            """;
        cmd.Parameters.AddWithValue("$code", barCode);
        cmd.Parameters.AddWithValue("$g1", Granularities[0]);
        cmd.Parameters.AddWithValue("$g2", Granularities[1]);
        cmd.Parameters.AddWithValue("$g3", Granularities[2]);

        var rows = new List<EtfTurnoverBarRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var ps = r.GetString(1);
            if (ps.Length < 10 || !DateOnly.TryParseExact(ps[..10], "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                continue;
            rows.Add(new EtfTurnoverBarRow(
                r.GetString(0), ps, day,
                r.IsDBNull(2) ? null : r.GetDouble(2),
                r.IsDBNull(3) ? null : r.GetDouble(3)));
        }
        return rows;
    }

    /// <summary>
    /// 把一只 ETF 的若干行换手率写回。一个事务。返回实际改到的行数。
    /// </summary>
    public int UpdateTurnover(string barCode,
        IReadOnlyList<(string Granularity, string PeriodStart, double Turnover)> updates)
    {
        if (updates.Count == 0) return 0;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE Bar SET turnover = $t
            WHERE code = $code AND granularity = $g AND period_start = $ps;
            """;
        cmd.Parameters.AddWithValue("$code", barCode);
        var pG = cmd.Parameters.Add("$g", SqliteType.Text);
        var pPs = cmd.Parameters.Add("$ps", SqliteType.Text);
        var pT = cmd.Parameters.Add("$t", SqliteType.Real);

        int n = 0;
        foreach (var (g, ps, t) in updates)
        {
            pG.Value = g;
            pPs.Value = ps;
            pT.Value = t;
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }
}
