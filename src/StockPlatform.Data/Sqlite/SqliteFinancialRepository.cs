using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>FinancialReport 表（财务报表关键科目）的存取。每次抓取返回该股全部历史，整体覆盖
/// （DELETE+INSERT，与股东数据的 ReplaceByCode 同一套语义）。</summary>
public class SqliteFinancialRepository : IFinancialRepository
{
    private readonly string _connectionString;

    public SqliteFinancialRepository(string dbFilePath)
    {
        _connectionString = $"Data Source={dbFilePath}";
    }

    public void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);
    }

    /// <param name="target">
    /// 这一轮**冲着哪个报告期**来抓的（见 <see cref="FinancialFetchState.TargetDate"/>）。
    /// 传 null 表示调用方没有目标概念（补抓金融股科目那条路），这一列保持原值不动。
    /// </param>
    public void ReplaceByCode(string code, IEnumerable<FinancialValue> rows, DateTime? target = null)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        // ⚠ 内容没变就别重写（2026-09-19）：整只票是 DELETE + 逐行 INSERT，一只票 2000+ 行。
        //   抓回来的最新期没有前进、科目集版本也没变 ⇒ 跟库里那份是同一份数据，重写一遍只是在
        //   25GB 的库上白白制造写放大（实测 13 只票每 20 分钟重写 29263 行，连着四天）。
        //   状态列仍然要更新——**"什么时候问的、冲着哪期问的"正是下一轮判断要不要再问的依据**。
        var incoming = rows as IReadOnlyCollection<FinancialValue> ?? rows.ToList();
        DateTime? incomingMax = null;
        foreach (var r in incoming)
            if (incomingMax == null || r.ReportDate > incomingMax.Value) incomingMax = r.ReportDate;

        if (incomingMax != null && IsSameAsStored(conn, code, incomingMax.Value))
        {
            TouchFetchState(conn, code, incomingMax.Value, target);
            return;
        }

        rows = incoming;
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM FinancialReport WHERE code = $code;";
            del.Parameters.AddWithValue("$code", code);
            del.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO FinancialReport (code, report_date, metric_key, value, fetched_at)
            VALUES ($code, $date, $key, $value, $fetched);
            """;
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pDate = cmd.CreateParameter(); pDate.ParameterName = "$date"; cmd.Parameters.Add(pDate);
        var pKey = cmd.CreateParameter(); pKey.ParameterName = "$key"; cmd.Parameters.Add(pKey);
        var pValue = cmd.CreateParameter(); pValue.ParameterName = "$value"; cmd.Parameters.Add(pValue);
        var pFetched = cmd.CreateParameter(); pFetched.ParameterName = "$fetched"; cmd.Parameters.Add(pFetched);

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        // 顺便记下最新报告期给 FinancialFetchState 用。**不能事后 rows.Max()**——rows 是
        // IEnumerable，二次遍历对延迟求值的序列可能重算甚至取不到值。
        DateTime? maxDate = null;
        foreach (var r in rows)
        {
            if (maxDate == null || r.ReportDate > maxDate.Value) maxDate = r.ReportDate;
            pCode.Value = code;
            pDate.Value = r.ReportDate.ToString("yyyy-MM-dd");
            pKey.Value = r.Key;
            pValue.Value = r.Value;
            pFetched.Value = now;
            cmd.ExecuteNonQuery();
        }

        // 抓取状态跟数据在**同一个事务**里写——否则中途崩了会出现"数据是新版、状态还是旧版"
        // （下次白重抓一遍，不致命）或者更糟的"状态是新版、数据没写进去"（新科目永远补不上）。
        using (var st = conn.CreateCommand())
        {
            st.Transaction = tx;
            st.CommandText = """
                INSERT OR REPLACE INTO FinancialFetchState (code, keys_version, report_date, target_date, fetched_at)
                VALUES ($code, $ver, $date, $target, $fetched);
                """;
            st.Parameters.AddWithValue("$code", code);
            st.Parameters.AddWithValue("$ver", FinancialKeys.Version);
            st.Parameters.AddWithValue("$date",
                maxDate.HasValue ? maxDate.Value.ToString("yyyy-MM-dd") : (object)DBNull.Value);
            st.Parameters.AddWithValue("$target",
                target.HasValue ? target.Value.ToString("yyyy-MM-dd") : (object)DBNull.Value);
            st.Parameters.AddWithValue("$fetched", now);
            st.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// 问过了、但数据源一行都没给——只记"什么时候问的、冲着哪期问的"，不碰数据。
    ///
    /// 没有这一笔的话，"接口对这只票返回空"会绕开 <see cref="ReplaceByCode"/> 的整条写入路径，
    /// 状态永远停在旧值、下一轮照样把它算成待抓，又是一个空转循环。
    ///
    /// ⚠ 只更新已有行（<c>UPDATE ... WHERE code</c>）：一次都没抓成功过的票没有 report_date，
    ///   写进去也会被 <see cref="GetFetchStateByCode"/> 过滤掉。那种票目前仍会每轮重试一次
    ///   （一只票 3 个请求），量级可以忽略，真变多了再单开"从没抓到过"的水位线。
    /// </summary>
    public void MarkAsked(string code, DateTime? target)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE FinancialFetchState SET target_date = $target, fetched_at = $fetched WHERE code = $code;";
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$target",
            target.HasValue ? target.Value.ToString("yyyy-MM-dd") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$fetched", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>库里这只票已经是同一份数据了吗——最新报告期没前进、科目集版本也是当前版。</summary>
    private static bool IsSameAsStored(SqliteConnection conn, string code, DateTime incomingMax)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT keys_version, report_date FROM FinancialFetchState WHERE code = $code;";
        cmd.Parameters.AddWithValue("$code", code);
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(1)) return false;
        if (r.IsDBNull(0) || r.GetInt32(0) != FinancialKeys.Version) return false;
        return DateTime.TryParseExact(r.GetString(1), "yyyy-MM-dd",
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var stored)
               && incomingMax <= stored;
    }

    /// <summary>数据没动，只把"什么时候问的、冲着哪期问的"记下来。</summary>
    private static void TouchFetchState(SqliteConnection conn, string code, DateTime reportDate, DateTime? target)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO FinancialFetchState (code, keys_version, report_date, target_date, fetched_at)
            VALUES ($code, $ver, $date, $target, $fetched);
            """;
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$ver", FinancialKeys.Version);
        cmd.Parameters.AddWithValue("$date", reportDate.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$target",
            target.HasValue ? target.Value.ToString("yyyy-MM-dd") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$fetched", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    public List<FinancialSnapshot> GetAllByCode(string code)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT report_date, metric_key, value FROM FinancialReport
            WHERE code = $code AND value IS NOT NULL
            ORDER BY report_date DESC;
            """;
        cmd.Parameters.AddWithValue("$code", code);

        var byDate = new Dictionary<DateTime, FinancialSnapshot>();
        var order = new List<DateTime>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!DateTime.TryParseExact(reader.GetString(0), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            if (!byDate.TryGetValue(d, out var snap))
            {
                snap = new FinancialSnapshot { ReportDate = d };
                byDate[d] = snap;
                order.Add(d);
            }
            snap.Values[reader.GetString(1)] = reader.GetDouble(2);
        }
        return order.Select(d => byDate[d]).ToList();
    }

    public Dictionary<string, FinancialFetchState> GetFetchStateByCode()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, keys_version, report_date, target_date, fetched_at
            FROM FinancialFetchState WHERE report_date IS NOT NULL;
            """;
        var result = new Dictionary<string, FinancialFetchState>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(2)) continue;
            if (!DateTime.TryParseExact(reader.GetString(2), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            result[reader.GetString(0)] = new FinancialFetchState(
                d,
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                ParseDay(reader, 3),
                ParseTime(reader, 4));
        }
        return result;
    }

    private static DateTime? ParseDay(SqliteDataReader r, int i)
        => !r.IsDBNull(i) && DateTime.TryParseExact(r.GetString(i), "yyyy-MM-dd",
               CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>fetched_at 存的是 "yyyy-MM-dd HH:mm:ss"，但老行里可能只有日期——两种都收。</summary>
    private static DateTime? ParseTime(SqliteDataReader r, int i)
        => !r.IsDBNull(i) && DateTime.TryParse(r.GetString(i),
               CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public Dictionary<string, FinancialSnapshot> GetLatestSnapshotByCode()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        // 每只股票只取它自己最新那一期的全部科目——相关子查询在 (code, report_date) 主键上走索引，
        // 比把 268 万行全拉回内存再筛快得多。
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT f.code, f.report_date, f.metric_key, f.value
            FROM FinancialReport f
            WHERE f.report_date = (SELECT MAX(report_date) FROM FinancialReport WHERE code = f.code);
            """;
        var result = new Dictionary<string, FinancialSnapshot>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var code = reader.GetString(0);
            if (!result.TryGetValue(code, out var snap))
            {
                snap = new FinancialSnapshot { ReportDate = DateTime.Parse(reader.GetString(1)) };
                result[code] = snap;
            }
            if (!reader.IsDBNull(3)) snap.Values[reader.GetString(2)] = reader.GetDouble(3);
        }
        return result;
    }

    public Dictionary<string, List<(int Year, double NetProfitParent)>> GetRecentAnnualNetProfitByCode(int count)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        // 只取 12-31 的年报口径（A股财报是年内累计，只有12-31那期等于全年）。一次全捞回来再在
        // 内存里按 code 截取最近 N 年——比每只股票发一次查询快得多（5000+ 只 × 一次往返的差别）。
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT code, report_date, value FROM FinancialReport
            WHERE metric_key = '{FinancialKeys.NetProfitParent}' AND report_date LIKE '%-12-31'
            ORDER BY code, report_date;
            """;
        var result = new Dictionary<string, List<(int, double)>>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(2)) continue;
            var code = reader.GetString(0);
            var year = int.Parse(reader.GetString(1)[..4]);
            if (!result.TryGetValue(code, out var list)) result[code] = list = new List<(int, double)>();
            list.Add((year, reader.GetDouble(2)));
        }
        // ORDER BY 保证了年份升序，这里只留最近 count 个
        foreach (var (code, list) in result)
            if (list.Count > count) result[code] = list.GetRange(list.Count - count, count);
        return result;
    }

    /// <summary>每个代码本地最新的报告期——给"按报告期跳过"的增量逻辑用（财报一季度才变一次，
    /// 已经有最新一期的股票不用再发请求）。</summary>
    public Dictionary<string, DateTime> GetLatestReportDateByCode()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, MAX(report_date) FROM FinancialReport GROUP BY code;";
        var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1)) continue;
            result[reader.GetString(0)] = DateTime.Parse(reader.GetString(1));
        }
        return result;
    }
}
