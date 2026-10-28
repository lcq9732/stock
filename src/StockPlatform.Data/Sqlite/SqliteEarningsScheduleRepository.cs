using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 定期报告预约披露日的本地存取（EarningsSchedule 表，2026-09-01 新增）。
/// 按 (code, report_period) 整条 upsert——预约日会变更，每次抓到的都是最新状态，直接覆盖。
/// </summary>
public class SqliteEarningsScheduleRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteEarningsScheduleRepository(string dbFilePath)
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

    private static string? Fmt(DateTime? d) => d?.ToString(DateFormat);
    private static DateTime? Parse(object? v)
        => v is string s && DateTime.TryParse(s, out var d) ? d : null;

    public int Upsert(IReadOnlyList<EarningsScheduleRow> rows)
    {
        if (rows.Count == 0) return 0;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO EarningsSchedule
                (code, report_period, appoint_date, change1, change2, change3, actual_date, fetched_at)
            VALUES ($c, $p, $a, $c1, $c2, $c3, $ac, $f)
            ON CONFLICT(code, report_period) DO UPDATE SET
                appoint_date = excluded.appoint_date,
                change1      = excluded.change1,
                change2      = excluded.change2,
                change3      = excluded.change3,
                actual_date  = excluded.actual_date,
                fetched_at   = excluded.fetched_at;
            """;
        var pc = cmd.Parameters.Add("$c", SqliteType.Text);
        var pp = cmd.Parameters.Add("$p", SqliteType.Text);
        var pa = cmd.Parameters.Add("$a", SqliteType.Text);
        var p1 = cmd.Parameters.Add("$c1", SqliteType.Text);
        var p2 = cmd.Parameters.Add("$c2", SqliteType.Text);
        var p3 = cmd.Parameters.Add("$c3", SqliteType.Text);
        var pac = cmd.Parameters.Add("$ac", SqliteType.Text);
        var pf = cmd.Parameters.Add("$f", SqliteType.Text);
        string now = DateTime.Now.ToString(TimeFormat);

        int n = 0;
        foreach (var r in rows)
        {
            pc.Value = r.Code;
            pp.Value = r.ReportPeriod.ToString(DateFormat);
            pa.Value = (object?)Fmt(r.AppointDate) ?? DBNull.Value;
            p1.Value = (object?)Fmt(r.Change1) ?? DBNull.Value;
            p2.Value = (object?)Fmt(r.Change2) ?? DBNull.Value;
            p3.Value = (object?)Fmt(r.Change3) ?? DBNull.Value;
            pac.Value = (object?)Fmt(r.ActualDate) ?? DBNull.Value;
            pf.Value = now;
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    /// <summary>
    /// 东财版预约日入库（2026-10-27，见 doc/financial-fetch-disclosure-race-design.md）。
    /// 跟巨潮那张一样按 (code, report_period) 整条覆盖——每次抓到的都是最新状态。
    /// 两家各存各的表，**合并只发生在读取端**（见下面几个查询）。
    /// </summary>
    public int UpsertEm(IReadOnlyList<EarningsScheduleEmRow> rows)
    {
        if (rows.Count == 0) return 0;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO EarningsScheduleEm
                (code, report_period, appoint_date, first_appoint_date, change1, change2, change3,
                 actual_date, is_publish, report_type_name, quarter, trade_market, security_type,
                 board_name, new_board_code, residual_days, appoint_change, appoint_change_days,
                 info_code, eitime, fetched_at)
            VALUES ($c, $p, $a, $fa, $c1, $c2, $c3, $ac, $ip, $rt, $q, $tm, $st,
                    $bn, $bc, $rd, $apc, $apd, $ic, $ei, $f)
            ON CONFLICT(code, report_period) DO UPDATE SET
                appoint_date        = excluded.appoint_date,
                first_appoint_date  = excluded.first_appoint_date,
                change1             = excluded.change1,
                change2             = excluded.change2,
                change3             = excluded.change3,
                actual_date         = excluded.actual_date,
                is_publish          = excluded.is_publish,
                report_type_name    = excluded.report_type_name,
                quarter             = excluded.quarter,
                trade_market        = excluded.trade_market,
                security_type       = excluded.security_type,
                board_name          = excluded.board_name,
                new_board_code      = excluded.new_board_code,
                residual_days       = excluded.residual_days,
                appoint_change      = excluded.appoint_change,
                appoint_change_days = excluded.appoint_change_days,
                info_code           = excluded.info_code,
                eitime              = excluded.eitime,
                fetched_at          = excluded.fetched_at;
            """;
        var pc = cmd.Parameters.Add("$c", SqliteType.Text);
        var pp = cmd.Parameters.Add("$p", SqliteType.Text);
        var pa = cmd.Parameters.Add("$a", SqliteType.Text);
        var pfa = cmd.Parameters.Add("$fa", SqliteType.Text);
        var p1 = cmd.Parameters.Add("$c1", SqliteType.Text);
        var p2 = cmd.Parameters.Add("$c2", SqliteType.Text);
        var p3 = cmd.Parameters.Add("$c3", SqliteType.Text);
        var pac = cmd.Parameters.Add("$ac", SqliteType.Text);
        var pip = cmd.Parameters.Add("$ip", SqliteType.Integer);
        var prt = cmd.Parameters.Add("$rt", SqliteType.Text);
        var pq = cmd.Parameters.Add("$q", SqliteType.Text);
        var ptm = cmd.Parameters.Add("$tm", SqliteType.Text);
        var pst = cmd.Parameters.Add("$st", SqliteType.Text);
        var pbn = cmd.Parameters.Add("$bn", SqliteType.Text);
        var pbc = cmd.Parameters.Add("$bc", SqliteType.Text);
        var prd = cmd.Parameters.Add("$rd", SqliteType.Integer);
        var papc = cmd.Parameters.Add("$apc", SqliteType.Text);
        var papd = cmd.Parameters.Add("$apd", SqliteType.Integer);
        var pic = cmd.Parameters.Add("$ic", SqliteType.Text);
        var pei = cmd.Parameters.Add("$ei", SqliteType.Text);
        var pf = cmd.Parameters.Add("$f", SqliteType.Text);
        string now = DateTime.Now.ToString(TimeFormat);

        static object Val(string? s) => (object?)s ?? DBNull.Value;
        static object ValN(int? i) => (object?)i ?? DBNull.Value;

        int n = 0;
        foreach (var r in rows)
        {
            pc.Value = r.Code;
            pp.Value = r.ReportPeriod.ToString(DateFormat);
            pa.Value = Val(Fmt(r.AppointDate));
            pfa.Value = Val(Fmt(r.FirstAppointDate));
            p1.Value = Val(Fmt(r.Change1));
            p2.Value = Val(Fmt(r.Change2));
            p3.Value = Val(Fmt(r.Change3));
            pac.Value = Val(Fmt(r.ActualDate));
            pip.Value = ValN(r.IsPublish);
            prt.Value = Val(r.ReportTypeName);
            pq.Value = Val(r.Quarter);
            ptm.Value = Val(r.TradeMarket);
            pst.Value = Val(r.SecurityType);
            pbn.Value = Val(r.BoardName);
            pbc.Value = Val(r.NewBoardCode);
            prd.Value = ValN(r.ResidualDays);
            papc.Value = Val(r.AppointChange);
            papd.Value = ValN(r.AppointChangeDays);
            pic.Value = Val(r.InfoCode);
            pei.Value = Val(r.Eitime);
            pf.Value = now;
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    /// <summary>东财版某一期存了多少条（任务判断"这一期抓过没有"用）。</summary>
    public Dictionary<string, int> CountByPeriodEm()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT report_period, COUNT(*) FROM EarningsScheduleEm GROUP BY report_period;";
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        while (r.Read()) map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }

    /// <summary>
    /// 两家在某一期上的对账（2026-10-27）——这一项挪成双源后，每轮收工报一句，
    /// 用来回答方案里悬着的那个问题：**东财的"已披露"标记到底比巨潮快多少**。
    ///
    /// 返回：各自的总条数、各自已标已披露的条数、以及**只有一家标了**的条数。
    /// 最后那个数就是双源的净收益——它要是长期为 0，说明两家同步，这一路可以撤掉。
    /// </summary>
    public (int CninfoTotal, int EmTotal, int CninfoDisclosed, int EmDisclosed,
            int OnlyCninfo, int OnlyEm) CompareSources(DateTime period)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("$p", period.ToString(DateFormat));
        cmd.CommandText = """
            WITH c AS (SELECT code, actual_date FROM EarningsSchedule     WHERE report_period = $p),
                 e AS (SELECT code, actual_date FROM EarningsScheduleEm   WHERE report_period = $p),
                 cd AS (SELECT code FROM c WHERE actual_date IS NOT NULL AND actual_date <> ''),
                 ed AS (SELECT code FROM e WHERE actual_date IS NOT NULL AND actual_date <> '')
            SELECT (SELECT COUNT(*) FROM c),
                   (SELECT COUNT(*) FROM e),
                   (SELECT COUNT(*) FROM cd),
                   (SELECT COUNT(*) FROM ed),
                   (SELECT COUNT(*) FROM cd WHERE code NOT IN (SELECT code FROM ed)),
                   (SELECT COUNT(*) FROM ed WHERE code NOT IN (SELECT code FROM cd));
            """;
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (0, 0, 0, 0, 0, 0);
        return (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5));
    }

    private static EarningsScheduleRow Read(SqliteDataReader r) => new(
        r.GetString(0),
        DateTime.TryParse(r.GetString(1), out var p) ? p : default,
        Parse(r.IsDBNull(2) ? null : r.GetString(2)),
        Parse(r.IsDBNull(3) ? null : r.GetString(3)),
        Parse(r.IsDBNull(4) ? null : r.GetString(4)),
        Parse(r.IsDBNull(5) ? null : r.GetString(5)),
        Parse(r.IsDBNull(6) ? null : r.GetString(6)));

    private const string Cols =
        "code, report_period, appoint_date, change1, change2, change3, actual_date";

    /// <summary>
    /// 消费端读的那张表（2026-10-27）：巨潮和东财**合并之后**的结果，字段跟 EarningsSchedule 一模一样。
    ///
    /// 合并在抓取时做完（<c>EarningsScheduleMerge</c>），这里只读——所以下面的查询都是最简单的单表 SQL，
    /// 谁都不需要知道底下有两个数据源，将来再加第三家也不用改这些查询。
    /// 上面那两张（EarningsSchedule / EarningsScheduleEm）是各家的原始落库，只给对账和重算用。
    /// </summary>
    private const string MergedTable = "EarningsScheduleMerged";

    /// <summary>
    /// 把合并结果整条覆盖进合并表。调用方是【拉取财报预约日】——它抓完两家后算一次，
    /// 见 <c>EarningsScheduleMerge.Merge</c>。
    /// </summary>
    public int UpsertMerged(IReadOnlyList<EarningsScheduleRow> rows)
    {
        if (rows.Count == 0) return 0;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO {MergedTable}
                (code, report_period, appoint_date, change1, change2, change3, actual_date, fetched_at)
            VALUES ($c, $p, $a, $c1, $c2, $c3, $ac, $f)
            ON CONFLICT(code, report_period) DO UPDATE SET
                appoint_date = excluded.appoint_date,
                change1      = excluded.change1,
                change2      = excluded.change2,
                change3      = excluded.change3,
                actual_date  = excluded.actual_date,
                fetched_at   = excluded.fetched_at;
            """;
        var pc = cmd.Parameters.Add("$c", SqliteType.Text);
        var pp = cmd.Parameters.Add("$p", SqliteType.Text);
        var pa = cmd.Parameters.Add("$a", SqliteType.Text);
        var p1 = cmd.Parameters.Add("$c1", SqliteType.Text);
        var p2 = cmd.Parameters.Add("$c2", SqliteType.Text);
        var p3 = cmd.Parameters.Add("$c3", SqliteType.Text);
        var pac = cmd.Parameters.Add("$ac", SqliteType.Text);
        var pf = cmd.Parameters.Add("$f", SqliteType.Text);
        string now = DateTime.Now.ToString(TimeFormat);

        int n = 0;
        foreach (var r in rows)
        {
            pc.Value = r.Code;
            pp.Value = r.ReportPeriod.ToString(DateFormat);
            pa.Value = (object?)Fmt(r.AppointDate) ?? DBNull.Value;
            p1.Value = (object?)Fmt(r.Change1) ?? DBNull.Value;
            p2.Value = (object?)Fmt(r.Change2) ?? DBNull.Value;
            p3.Value = (object?)Fmt(r.Change3) ?? DBNull.Value;
            pac.Value = (object?)Fmt(r.ActualDate) ?? DBNull.Value;
            pf.Value = now;
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    /// <summary>某只股票的全部报告期记录，按报告期倒序。</summary>
    public List<EarningsScheduleRow> GetByCode(string code)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM {MergedTable} WHERE code=$c ORDER BY report_period DESC;";
        cmd.Parameters.AddWithValue("$c", code);
        using var r = cmd.ExecuteReader();
        var list = new List<EarningsScheduleRow>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    /// <summary>
    /// 每只股票"下一次财报日"——取**还没实际披露**的报告期里最早的那个。
    /// 界面上的财报日列直接用它。已经全部披露完的股票不在返回里（那就是没有下一次可报）。
    /// </summary>
    public Dictionary<string, EarningsScheduleRow> GetUpcomingByCode()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM {MergedTable} WHERE actual_date IS NULL ORDER BY report_period;";
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, EarningsScheduleRow>(StringComparer.Ordinal);
        while (r.Read())
        {
            var row = Read(r);
            if (row.EffectiveDate is null) continue;
            // ORDER BY 保证先来的报告期更早，已有就不覆盖
            if (!map.ContainsKey(row.Code)) map[row.Code] = row;
        }
        return map;
    }

    /// <summary>
    /// 每只股票在界面上该显示的那一条：**优先"还没披露的最早一期"**（那才是"下次财报"），
    /// 没有就退回**"最近一期已披露"**。
    ///
    /// ⚠ 为什么要有这个退路（2026-09-02 用户反馈"库里明明有，列却是空的"）：
    /// 接口只给最近两期，而这两期一旦都披露完，就进入一段**空窗**——下一期的预约表还没发布。
    /// 光取"未披露"的话，这段空窗里全市场 5000 多只票只剩 3 只有值，整列几乎全空，
    /// 看着像功能坏了。空窗期显示"最近一次财报是哪天出的"同样有用，只是要在界面上
    /// 用颜色和提示区分开：**未披露的是预告，已披露的是回顾**（见 EarningsScheduleRow.Pending）。
    /// </summary>
    public Dictionary<string, EarningsScheduleRow> GetLatestByCode()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 排序即优先级：未披露的排前面（取最早那期），已披露的排后面（取最晚那期）
        cmd.CommandText = $"""
            SELECT {Cols} FROM {MergedTable}
            ORDER BY code,
                     CASE WHEN actual_date IS NULL THEN 0 ELSE 1 END,
                     CASE WHEN actual_date IS NULL THEN report_period END ASC,
                     report_period DESC;
            """;
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, EarningsScheduleRow>(StringComparer.Ordinal);
        while (r.Read())
        {
            var row = Read(r);
            if (row.EffectiveDate is null) continue;
            if (!map.ContainsKey(row.Code)) map[row.Code] = row;   // 每只票只留排序最靠前的那条
        }
        return map;
    }

    /// <summary>某一个报告期的全部记录（复查时用来挑出还没披露的那些）。</summary>
    public List<EarningsScheduleRow> GetByPeriod(DateTime period)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM {MergedTable} WHERE report_period=$p ORDER BY code;";
        cmd.Parameters.AddWithValue("$p", period.ToString(DateFormat));
        using var rd = cmd.ExecuteReader();
        var list = new List<EarningsScheduleRow>();
        while (rd.Read()) list.Add(Read(rd));
        return list;
    }

    /// <summary>本地已经存了哪些报告期（用来判断某一期的预约表是不是已经抓过）。</summary>
    public Dictionary<string, int> CountByPeriod()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT report_period, COUNT(*) FROM EarningsSchedule GROUP BY report_period;";
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        while (r.Read()) map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }

    /// <summary>还没实际披露的记录数——就是"还要盯着看会不会改期"的那批。</summary>
    /// <summary>
    /// 每只票**已经实际披露**的最新报告期（截至 <paramref name="asOf"/>）。
    ///
    /// 给财务报表和金融监管指标判断"这只票现在到底有没有新数据可取"用的。原来两边都拿
    /// <c>LatestExpectedReportPeriod</c>——**法定披露截止日**——一刀切，跟每家公司实际
    /// 什么时候披露没关系：2026 年半年报法定截止 8/31，而 66% 的公司挤在 8/25–8/29 那五天
    /// 披露，程序却要等 8/31 过完才认这一期，于是 5478 只同时涌进待补队列，按每轮 300 只
    /// 要补三四天，数据到 9 月上旬才可用（2026-09-03 用户提出用本地的预约日表来判断）。
    ///
    /// 用的是 <c>actual_date</c>（实际披露日，巨潮官方数据）而不是预约日——预约日会改，
    /// 实测有 12% 改过；实际披露日是既成事实，不会变。
    ///
    /// ⚠ 查不到记录的票（新股、B股、老退市股）**不在返回值里**，调用方必须退回原来的
    ///   法定截止日判据。少取比多取危险得多。
    /// </summary>
    public Dictionary<string, DateTime> GetLatestDisclosedPeriodByCode(DateTime asOf)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 读的是**合并表**（2026-10-27）：两家的并集在抓取时就算好了，见 EarningsScheduleMerge。
        // 这里不做任何合并——判据归 Logic，仓储只读写。
        cmd.CommandText = $"""
            SELECT code, MAX(report_period) FROM {MergedTable}
            WHERE actual_date IS NOT NULL AND actual_date <> '' AND actual_date <= $asOf
            GROUP BY code;
            """;
        cmd.Parameters.AddWithValue("$asOf", asOf.ToString(DateFormat, CultureInfo.InvariantCulture));
        var map = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(1)) continue;
            if (DateTime.TryParse(r.GetString(1), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var period))
                map[r.GetString(0)] = period.Date;
        }
        return map;
    }

    /// <summary>
    /// 跟 <see cref="GetLatestDisclosedPeriodByCode"/> 同一个判据，但连**实际披露日**一起给（2026-10-21）。
    ///
    /// 【拉取财务报表】挪进日更后要分辨两种"数据源没有这一期"：刚披露（东财还没更新，明天再取）
    /// 和拖了好几天（报错让人知道）——靠的就是这一天。
    /// </summary>
    public Dictionary<string, (DateTime Period, DateTime ActualDate)> GetLatestDisclosureByCode(DateTime asOf)
        => QueryLatestDisclosure(asOf, halfYearOnly: false);

    /// <summary>
    /// 每只票**已实际披露的最新一期年报或中报**（只认 06-30 / 12-31），连披露日（2026-10-21）。
    ///
    /// 给【金融监管指标】判"有没有新东西要下"：监管指标只在年报/中报的 PDF 里有。
    /// 拿 <see cref="GetLatestDisclosedPeriodByCode"/> 的话，三季报一出最新一期就成了 09-30——
    /// 那一期永远不会被解析，于是每一家都被判成"有活"、挨家去查公告列表，查完才发现没东西可下。
    /// </summary>
    public Dictionary<string, (DateTime Period, DateTime ActualDate)> GetLatestHalfYearDisclosureByCode(DateTime asOf)
        => QueryLatestDisclosure(asOf, halfYearOnly: true);

    private Dictionary<string, (DateTime Period, DateTime ActualDate)> QueryLatestDisclosure(DateTime asOf, bool halfYearOnly)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 同一只票取 report_period 最大的那一行；SQLite 的 bare column + MAX() 保证 actual_date 来自同一行。
        // 读的是合并表（2026-10-27）——"两家都标了取较晚的日期"那条规则在抓取时就算完了，
        // 见 EarningsScheduleMerge，这里不重复。
        cmd.CommandText = $"""
            SELECT code, MAX(report_period), actual_date FROM {MergedTable}
            WHERE actual_date IS NOT NULL AND actual_date <> '' AND actual_date <= $asOf
            {(halfYearOnly ? "AND (substr(report_period, 6, 5) = '06-30' OR substr(report_period, 6, 5) = '12-31')" : "")}
            GROUP BY code;
            """;
        cmd.Parameters.AddWithValue("$asOf", asOf.ToString(DateFormat, CultureInfo.InvariantCulture));
        var map = new Dictionary<string, (DateTime, DateTime)>(StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(1) || r.IsDBNull(2)) continue;
            if (DateTime.TryParse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.None, out var period)
                && DateTime.TryParse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.None, out var actual))
                map[r.GetString(0)] = (period.Date, actual.Date);
        }
        return map;
    }

    public int PendingCount()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {MergedTable} WHERE actual_date IS NULL;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }
}
