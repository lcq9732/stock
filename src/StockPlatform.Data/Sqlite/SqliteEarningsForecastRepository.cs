using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 业绩预告 / 业绩快报的本地存取（2026-09-03）。
///
/// 跟板块那种"快照整体替换"不同，这两张表是**增量累积**的：预告一旦发布就是历史事实，
/// 后续修正是新增一行（主键带 notice_date），不是覆盖。所以只有 Upsert，没有 ReplaceAll。
/// </summary>
public class SqliteEarningsForecastRepository : IEarningsForecastRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteEarningsForecastRepository(string dbFilePath)
        => _connectionString = $"Data Source={dbFilePath}";

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

    public int UpsertForecasts(IEnumerable<EarningsForecast> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return 0;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO EarningsForecast
                (code, name, report_date, notice_date, predict_finance_code, predict_finance,
                 amount_lower, amount_upper, amplitude_lower, amplitude_upper,
                 predict_type, content, change_reason, preyear_same_period, is_latest, fetched_at)
            VALUES ($code, $name, $rd, $nd, $pfc, $pf, $al, $au, $ampl, $ampu,
                    $type, $content, $reason, $prey, $latest, $fetched);
            """;
        var p = AddParams(cmd, "$code", "$name", "$rd", "$nd", "$pfc", "$pf", "$al", "$au",
                          "$ampl", "$ampu", "$type", "$content", "$reason", "$prey",
                          "$latest", "$fetched");
        foreach (var f in list)
        {
            p["$code"].Value = f.Code;
            p["$name"].Value = f.Name;
            p["$rd"].Value = f.ReportDate.ToString(DateFormat, CultureInfo.InvariantCulture);
            p["$nd"].Value = f.NoticeDate.ToString(DateFormat, CultureInfo.InvariantCulture);
            p["$pfc"].Value = f.PredictFinanceCode;
            p["$pf"].Value = f.PredictFinance;
            p["$al"].Value = (object?)f.AmountLower ?? DBNull.Value;
            p["$au"].Value = (object?)f.AmountUpper ?? DBNull.Value;
            p["$ampl"].Value = (object?)f.AmplitudeLower ?? DBNull.Value;
            p["$ampu"].Value = (object?)f.AmplitudeUpper ?? DBNull.Value;
            p["$type"].Value = f.PredictType;
            p["$content"].Value = f.Content;
            p["$reason"].Value = f.ChangeReason;
            p["$prey"].Value = (object?)f.PreYearSamePeriod ?? DBNull.Value;
            p["$latest"].Value = f.IsLatest ? 1 : 0;
            p["$fetched"].Value = f.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }

    public int UpsertExpress(IEnumerable<EarningsExpress> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return 0;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO EarningsExpress
                (code, name, report_date, notice_date, update_date, eps, revenue, revenue_yoy,
                 np_parent, np_yoy, bvps, roe, revenue_qoq, np_qoq, fetched_at)
            VALUES ($code, $name, $rd, $nd, $ud, $eps, $rev, $revy, $np, $npy,
                    $bvps, $roe, $revq, $npq, $fetched);
            """;
        var p = AddParams(cmd, "$code", "$name", "$rd", "$nd", "$ud", "$eps", "$rev", "$revy",
                          "$np", "$npy", "$bvps", "$roe", "$revq", "$npq", "$fetched");
        foreach (var e in list)
        {
            p["$code"].Value = e.Code;
            p["$name"].Value = e.Name;
            p["$rd"].Value = e.ReportDate.ToString(DateFormat, CultureInfo.InvariantCulture);
            p["$nd"].Value = e.NoticeDate?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? (object)DBNull.Value;
            p["$ud"].Value = e.UpdateDate?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? (object)DBNull.Value;
            p["$eps"].Value = (object?)e.Eps ?? DBNull.Value;
            p["$rev"].Value = (object?)e.Revenue ?? DBNull.Value;
            p["$revy"].Value = (object?)e.RevenueYoy ?? DBNull.Value;
            p["$np"].Value = (object?)e.NetProfitParent ?? DBNull.Value;
            p["$npy"].Value = (object?)e.NetProfitYoy ?? DBNull.Value;
            p["$bvps"].Value = (object?)e.Bvps ?? DBNull.Value;
            p["$roe"].Value = (object?)e.Roe ?? DBNull.Value;
            p["$revq"].Value = (object?)e.RevenueQoq ?? DBNull.Value;
            p["$npq"].Value = (object?)e.NetProfitQoq ?? DBNull.Value;
            p["$fetched"].Value = e.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }

    /// <summary>
    /// 本地已有的最新公告日 —— 增量抓取的水位线。
    /// 刻意<b>不</b>从这一天的次日开始抓，而是从这一天本身重抓：同一天里公司陆续发预告，
    /// 上次抓的时候当天可能还没发完，从次日开始会漏掉当天后半段。主键 UPSERT 保证重抓不会重复。
    /// </summary>
    public DateTime? GetLatestForecastNoticeDate() => ScalarDate("SELECT MAX(notice_date) FROM EarningsForecast");

    public DateTime? GetLatestExpressUpdateDate() => ScalarDate("SELECT MAX(update_date) FROM EarningsExpress");

    public int CountForecasts() => ScalarInt("SELECT COUNT(*) FROM EarningsForecast");
    public int CountExpress() => ScalarInt("SELECT COUNT(*) FROM EarningsExpress");

    /// <summary>取某个报告期的全部最新预告（同一期同一股只取最后一次修正）。</summary>
    public List<EarningsForecast> QueryByReportDate(DateTime reportDate)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, name, report_date, notice_date, predict_finance_code, predict_finance,
                   amount_lower, amount_upper, amplitude_lower, amplitude_upper,
                   predict_type, content, change_reason, preyear_same_period, is_latest, fetched_at
            FROM EarningsForecast f
            WHERE report_date = $rd
              AND notice_date = (SELECT MAX(notice_date) FROM EarningsForecast x
                                  WHERE x.code = f.code AND x.report_date = f.report_date
                                    AND x.predict_finance_code = f.predict_finance_code)
            ORDER BY code;
            """;
        cmd.Parameters.AddWithValue("$rd", reportDate.ToString(DateFormat, CultureInfo.InvariantCulture));

        var list = new List<EarningsForecast>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new EarningsForecast
            {
                Code = r.GetString(0),
                Name = r.IsDBNull(1) ? "" : r.GetString(1),
                ReportDate = ParseDate(r.GetString(2)),
                NoticeDate = ParseDate(r.GetString(3)),
                PredictFinanceCode = r.IsDBNull(4) ? "" : r.GetString(4),
                PredictFinance = r.IsDBNull(5) ? "" : r.GetString(5),
                AmountLower = r.IsDBNull(6) ? null : r.GetDouble(6),
                AmountUpper = r.IsDBNull(7) ? null : r.GetDouble(7),
                AmplitudeLower = r.IsDBNull(8) ? null : r.GetDouble(8),
                AmplitudeUpper = r.IsDBNull(9) ? null : r.GetDouble(9),
                PredictType = r.IsDBNull(10) ? "" : r.GetString(10),
                Content = r.IsDBNull(11) ? "" : r.GetString(11),
                ChangeReason = r.IsDBNull(12) ? "" : r.GetString(12),
                PreYearSamePeriod = r.IsDBNull(13) ? null : r.GetDouble(13),
                IsLatest = !r.IsDBNull(14) && r.GetInt32(14) == 1,
                FetchedAt = r.IsDBNull(15) ? DateTime.MinValue
                    : DateTime.ParseExact(r.GetString(15), TimeFormat, CultureInfo.InvariantCulture),
            });
        }
        return list;
    }

    // ── 小工具 ─────────────────────────────────────────────────────
    private static Dictionary<string, SqliteParameter> AddParams(SqliteCommand cmd, params string[] names)
    {
        var map = new Dictionary<string, SqliteParameter>();
        foreach (var n in names)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = n;
            cmd.Parameters.Add(p);
            map[n] = p;
        }
        return map;
    }

    private DateTime? ScalarDate(string sql)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        if (v == null || v is DBNull) return null;
        var s = v.ToString();
        return string.IsNullOrEmpty(s) ? null : ParseDate(s);
    }

    private int ScalarInt(string sql)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static DateTime ParseDate(string s) =>
        DateTime.ParseExact(s.Substring(0, 10), DateFormat, CultureInfo.InvariantCulture);
}
