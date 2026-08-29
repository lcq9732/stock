using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// BankRegulatoryMetric / BankReportFetchState 两张表的存取（2026-08-29 新增）。
///
/// 这些指标来自财报 PDF 正文，跟三张报表是两条独立的抓取链路，所以状态表也独立
/// （见 SqliteSchema 里那两张表的注释）。
/// </summary>
public class SqliteBankRegulatoryRepository
{
    private readonly string _connectionString;

    public SqliteBankRegulatoryRepository(string dbFilePath)
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

    /// <summary>整批覆盖写入某一份财报解析出的全部指标。</summary>
    public void Upsert(IEnumerable<BankRegulatoryMetric> rows)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO BankRegulatoryMetric
                (code, report_date, metric_key, basis, value, standard_value, source_page, fetched_at)
            VALUES ($code, $date, $key, $basis, $val, $std, $page, $fetched);
            """;
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pDate = cmd.CreateParameter(); pDate.ParameterName = "$date"; cmd.Parameters.Add(pDate);
        var pKey = cmd.CreateParameter(); pKey.ParameterName = "$key"; cmd.Parameters.Add(pKey);
        var pBasis = cmd.CreateParameter(); pBasis.ParameterName = "$basis"; cmd.Parameters.Add(pBasis);
        var pVal = cmd.CreateParameter(); pVal.ParameterName = "$val"; cmd.Parameters.Add(pVal);
        var pStd = cmd.CreateParameter(); pStd.ParameterName = "$std"; cmd.Parameters.Add(pStd);
        var pPage = cmd.CreateParameter(); pPage.ParameterName = "$page"; cmd.Parameters.Add(pPage);
        var pFetched = cmd.CreateParameter(); pFetched.ParameterName = "$fetched"; cmd.Parameters.Add(pFetched);

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var r in rows)
        {
            pCode.Value = r.Code;
            pDate.Value = r.ReportDate.ToString("yyyy-MM-dd");
            pKey.Value = r.MetricKey;
            pBasis.Value = r.Basis ?? "";
            pVal.Value = r.Value;
            pStd.Value = (object?)r.StandardValue ?? DBNull.Value;
            pPage.Value = r.SourcePage;
            pFetched.Value = now;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>记录一份财报的解析状态。**失败也要记**——否则界面上分不清"没抓"和"抓失败"。</summary>
    public void UpsertState(BankReportFetchState st)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO BankReportFetchState
                (code, report_date, status, metric_count, message, pdf_url, pdf_path, fetched_at)
            VALUES ($code, $date, $status, $cnt, $msg, $url, $path, $fetched);
            """;
        cmd.Parameters.AddWithValue("$code", st.Code);
        cmd.Parameters.AddWithValue("$date", st.ReportDate.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$status", st.Status);
        cmd.Parameters.AddWithValue("$cnt", st.MetricCount);
        cmd.Parameters.AddWithValue("$msg", (object?)st.Message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$url", (object?)st.PdfUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$path", (object?)st.PdfPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fetched", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>已经成功解析过的 (code, report_date)——增量抓取据此跳过。</summary>
    public HashSet<(string Code, DateTime Date)> GetSucceeded()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, report_date FROM BankReportFetchState WHERE status = 'ok';";
        var set = new HashSet<(string, DateTime)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (DateTime.TryParse(r.GetString(1), out var d)) set.Add((r.GetString(0), d));
        return set;
    }

    /// <summary>某只银行的全部监管指标，按报告期降序。给体检表用。</summary>
    public List<BankRegulatoryMetric> GetByCode(string code)
    {
        using var conn = Open();
        SqliteSchema.EnsureSchema(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT report_date, metric_key, basis, value, standard_value, source_page
            FROM BankRegulatoryMetric WHERE code = $code ORDER BY report_date DESC;
            """;
        cmd.Parameters.AddWithValue("$code", code);
        var list = new List<BankRegulatoryMetric>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!DateTime.TryParse(r.GetString(0), out var d)) continue;
            list.Add(new BankRegulatoryMetric
            {
                Code = code,
                ReportDate = d,
                MetricKey = r.GetString(1),
                Basis = r.GetString(2),
                Value = r.IsDBNull(3) ? 0 : r.GetDouble(3),
                StandardValue = r.IsDBNull(4) ? null : r.GetString(4),
                SourcePage = r.IsDBNull(5) ? 0 : r.GetInt32(5),
            });
        }
        return list;
    }
}
