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
        // ⚠ 不能用 INSERT OR REPLACE——那会把**人拍过板的数据**一起冲掉。
        // 解析规则每改进一次就会重跑一遍全部本地 PDF，而人工补的正是解析不出来的那些，
        // 冲掉就等于白填。这里用 ON CONFLICT DO UPDATE + WHERE，只覆盖机器自己产的行
        // （source 是 pdf 或 ocr），manual / ocr_confirmed 一律不动，见 MetricSources。
        // source 跟着每一行走（'pdf' 或 'ocr'）：OCR 出来的值要能被认出来，才好让人核对。
        cmd.CommandText = """
            INSERT INTO BankRegulatoryMetric
                (code, report_date, metric_key, basis, value, standard_value, source_page, fetched_at, source)
            VALUES ($code, $date, $key, $basis, $val, $std, $page, $fetched, $source)
            ON CONFLICT(code, report_date, metric_key, basis) DO UPDATE SET
                value = excluded.value,
                standard_value = excluded.standard_value,
                source_page = excluded.source_page,
                fetched_at = excluded.fetched_at,
                source = excluded.source
            WHERE IFNULL(BankRegulatoryMetric.source, 'pdf') NOT IN ('manual', 'ocr_confirmed');
            """;
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pDate = cmd.CreateParameter(); pDate.ParameterName = "$date"; cmd.Parameters.Add(pDate);
        var pKey = cmd.CreateParameter(); pKey.ParameterName = "$key"; cmd.Parameters.Add(pKey);
        var pBasis = cmd.CreateParameter(); pBasis.ParameterName = "$basis"; cmd.Parameters.Add(pBasis);
        var pVal = cmd.CreateParameter(); pVal.ParameterName = "$val"; cmd.Parameters.Add(pVal);
        var pStd = cmd.CreateParameter(); pStd.ParameterName = "$std"; cmd.Parameters.Add(pStd);
        var pPage = cmd.CreateParameter(); pPage.ParameterName = "$page"; cmd.Parameters.Add(pPage);
        var pFetched = cmd.CreateParameter(); pFetched.ParameterName = "$fetched"; cmd.Parameters.Add(pFetched);
        var pSource = cmd.CreateParameter(); pSource.ParameterName = "$source"; cmd.Parameters.Add(pSource);

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var r in rows)
        {
            pSource.Value = string.IsNullOrEmpty(r.Source) ? MetricSources.Pdf : r.Source;
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

    /// <summary>
    /// 写入**人工回填**的指标（来源标 'manual'）。跟 <see cref="Upsert"/> 的区别有两点：
    /// 它无条件覆盖（人工填的就是最新的判断），而且之后的自动重解析不会再动这些行。
    /// </summary>
    public void UpsertManual(IEnumerable<BankRegulatoryMetric> rows)
    {
        using var conn = Open();
        SqliteSchema.EnsureSchema(conn);
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO BankRegulatoryMetric
                (code, report_date, metric_key, basis, value, standard_value, source_page, fetched_at, source)
            VALUES ($code, $date, $key, $basis, $val, NULL, 0, $fetched, 'manual');
            """;
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var r in rows)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$code", r.Code);
            cmd.Parameters.AddWithValue("$date", r.ReportDate.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$key", r.MetricKey);
            cmd.Parameters.AddWithValue("$basis", r.Basis ?? "");
            cmd.Parameters.AddWithValue("$val", r.Value);
            cmd.Parameters.AddWithValue("$fetched", now);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// 把 OCR 出来的值标成**人已核对通过**（source: ocr → ocr_confirmed），2026-08-30 新增。
    ///
    /// 触发点是回填清单里"OCR 值那一列有数、但用户没在最后一列填东西"——按约定这就表示
    /// "我看过了，OCR 认得对"。标完之后这一项不再列进清单（不用每次都重看），重解析也不再
    /// 覆盖它（人的判断优先）。只动 source 还是 'ocr' 的行：已经是 manual 的不碰。
    /// </summary>
    public int MarkOcrConfirmed(IEnumerable<(string Code, DateTime ReportDate, string MetricKey)> rows)
    {
        using var conn = Open();
        SqliteSchema.EnsureSchema(conn);
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE BankRegulatoryMetric SET source = 'ocr_confirmed'
            WHERE code = $code AND report_date = $date AND metric_key = $key
              AND IFNULL(source, 'pdf') = 'ocr';
            """;
        int n = 0;
        foreach (var r in rows)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$code", r.Code);
            cmd.Parameters.AddWithValue("$date", r.ReportDate.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$key", r.MetricKey);
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
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
    /// <summary>
    /// 所有**没成功**的期次 (代码, 报告期)。调用方拿它去比对本地文件在不在——
    /// 仓储不碰文件系统，两件事分开。
    ///
    /// 用途：下载循环那道"最新一期已有就整只票跳过"的优化会把**缺文件的老期次**挡在门外。
    /// 实测 11 个期次就是这么永久缺失的（最早挂了半个月），见 BankRegulatoryTask 的跳过判据。
    /// </summary>
    public List<(string Code, DateTime ReportDate)> GetUnsuccessful()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, report_date FROM BankReportFetchState WHERE status <> 'ok';";
        var list = new List<(string, DateTime)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (DateTime.TryParse(r.GetString(1), out var d)) list.Add((r.GetString(0), d));
        return list;
    }

    public List<BankRegulatoryMetric> GetByCode(string code)
    {
        using var conn = Open();
        SqliteSchema.EnsureSchema(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT report_date, metric_key, basis, value, standard_value, source_page,
                   IFNULL(source, 'pdf')
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
                Source = r.IsDBNull(6) ? MetricSources.Pdf : r.GetString(6),
            });
        }
        return list;
    }
}
