using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Sqlite;

/// <summary>补算结果。</summary>
/// <param name="Days">扫过的交易日数。</param>
/// <param name="Rows">补上值的行数。</param>
/// <param name="NoClose">因为没有当日收盘价而补不上的行数（留 NULL）。</param>
public sealed record MarginShortBalanceFillResult(int Days, int Rows, int NoClose);

/// <summary>
/// 【融券余额补算】（2026-09-16）——把**沪市**行缺失的 <c>short_balance</c> 按交易所官方公式算出来。
/// **纯本地查库，一个请求都不发。**
///
/// ════ 修的是什么 ════
/// 上交所的两融明细接口 <c>rqylje</c>（融券余额）**恒为 null**（实测 2026-09-14 全量如此），
/// 而解析用的 <c>GetNum</c> 把 null 读成 0——于是 **1675 只沪市票的融券余额历史上从来没有过
/// 非 0 值**（3,139,554 行沪市数据，<c>short_balance &gt; 0</c> 的行数：0）。深市那张 xlsx
/// 第 6 列直接给了融券余额，所以只有沪市这一半瞎。
///
/// 后果是静默的：资金面诊断的「融券余额变化」对所有沪市票都算不出来，而界面上显示的是
/// "融券余额 0"，看起来像"这只票没人做空"。
///
/// ════ 凭什么能算出来 ════
/// 深交所报表页脚自己写着公式：**「本日融券余额(元) = 本日融券余量(股) × 本日收盘价」**。
/// 沪市不给余额，但给了余量（<c>rqyl</c>），收盘价本地有——所以能按同一个公式补出来，
/// 跟深市完全同口径。实测验证（2026-09-14）：
///   · 深市 1603 只（用它自己给的余额对照）：误差中位 0.0000%，99.7% 在 0.01% 以内
///   · 沪市 456 只（用东财公布的余额对照）：误差中位 0.0000%，最大 3.2%（停牌日收盘价不同步）
///
/// ════ 只动沪市，深市一行不碰 ════
/// 深市有 31% 的行 <c>short_balance = 0</c>，那些是**真的 0**（当天确实没有融券余量）。
/// 沪市的 0 全是假的（源头没给）。判据不能只看"是不是 0"，必须先分市场——
/// 而市场判断走 <see cref="MarketClassifier"/>，不自己写前缀规则（自写规则漏过 920 开头的
/// 北交所票，害得 342 只静默抓不到，见 feedback_market_prefix_via_classifier）。
///
/// ════ ETF 的代码形式不一样 ════
/// 两融标的里有 465 只 ETF，而 ETF 的K线在 <c>Bar</c> 里是**带市场前缀**存的（<c>sh510050</c>），
/// 两融表里却是裸码（<c>510050</c>）。只按裸码找收盘价会让 323 只本来有数据的 ETF 也算成"补不上"。
/// 所以查收盘价时三种形式都试（裸码 / sh+码 / sz+码）。
///
/// ════ 幂等、可中断 ════
/// **按交易日分批提交**，一天一个事务（沪市每天约 1700 行）。这样：
///   · WAL 不会涨——单事务写几百万行曾把 WAL 撑到 162GB（见 project_wal_blowup_and_stale_dotnet）
///   · 中断了直接重跑，已补的行不再满足"缺值"判据，不会重复算
/// 补不上的（没有当日收盘价，实测占 2.46%，基本是从没抓到过K线的 ETF 和退市股）**留 NULL**，
/// 不写 0——留 NULL 下次还有机会补上，写 0 就永久变成"确实没有融券"了。
/// </summary>
public class SqliteMarginShortBalanceFiller
{
    private readonly string _dbPath;

    public SqliteMarginShortBalanceFiller(string dbFilePath) => _dbPath = dbFilePath;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    /// <summary>
    /// 补算。<paramref name="sinceDate"/> 给了就只处理这天及以后（日更用，传上一个交易日即可），
    /// 不给就全历史回填。
    /// </summary>
    public MarginShortBalanceFillResult Fill(
        Action<string>? onProgress = null, DateTime? sinceDate = null, CancellationToken ct = default)
    {
        using var conn = Open();

        int shCodes = PrepareShanghaiCodeTable(conn, ct);
        onProgress?.Invoke($"　沪市两融标的 {shCodes} 只（按 MarketClassifier 判，含 ETF）");

        var days = SelectPendingDays(conn, sinceDate, ct);
        if (days.Count == 0)
        {
            onProgress?.Invoke("　没有待补算的交易日——这一项是幂等的，跑过就会是这个结果");
            return new MarginShortBalanceFillResult(0, 0, 0);
        }
        onProgress?.Invoke($"　待补算 {days.Count} 个交易日（{days[0]} ~ {days[^1]}）");

        int rows = 0, done = 0;
        foreach (var day in days)
        {
            ct.ThrowIfCancellationRequested();
            rows += FillOneDay(conn, day);
            done++;
            // 进度别报太密：全历史有 3900 多天，一天一条日志会把日志刷爆
            if (done % 100 == 0 || done == days.Count)
                onProgress?.Invoke($"　{done}/{days.Count} 天，已补 {rows:N0} 行（当前 {day}）");
        }

        int noClose = CountStillMissing(conn, sinceDate);
        onProgress?.Invoke($"　完成：补上 {rows:N0} 行；仍缺 {noClose:N0} 行"
                           + "（这些标的没有当日收盘价——多是从未抓到过K线的 ETF 和退市股，留 NULL）");
        return new MarginShortBalanceFillResult(days.Count, rows, noClose);
    }

    /// <summary>
    /// 把沪市两融标的写进临时表，并连带算好"在 Bar 里该用哪个代码"的三种候选形式。
    ///
    /// 为什么要过一遍临时表、不直接在 SQL 里用 <c>substr(code,1,1) IN ('6','9')</c>：
    /// 市场判断必须走 <see cref="MarketClassifier"/>（SQL 里调不到），而逐票执行 UPDATE
    /// 又太慢。临时表是两者的折中——判断在 C# 做、批量在 SQL 做。
    /// </summary>
    private static int PrepareShanghaiCodeTable(SqliteConnection conn, CancellationToken ct)
    {
        var all = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT code FROM MarginDetail;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) { ct.ThrowIfCancellationRequested(); all.Add(r.GetString(0)); }
        }

        var codes = new List<string>();
        foreach (var code in all)
        {
            ct.ThrowIfCancellationRequested();
            var board = MarketClassifier.Classify(code);
            // 只要沪市——深市的 short_balance 是源头给的真值，一行都不碰
            if (board is MarketBoard.ShanghaiMain or MarketBoard.ShanghaiStar or MarketBoard.ShanghaiB)
                codes.Add(code);                     // 股票：MarketClassifier 说了算
            else if (board == MarketBoard.Unknown && IsShanghaiByStoredPrefix(conn, code))
                codes.Add(code);                     // ETF：库里存的前缀说了算，见那个方法的注释
            // 其余（深市股票、北交所、判不出来的）一律不碰
        }

        using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS _sh_margin(
                    code    TEXT PRIMARY KEY,
                    bar_raw TEXT,   -- 裸码（个股在 Bar 里是这个形式）
                    bar_pre TEXT    -- sh+码（ETF 在 Bar 里是这个形式）
                );
                DELETE FROM _sh_margin;
                """;
            create.ExecuteNonQuery();
        }
        using (var tx = conn.BeginTransaction())
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO _sh_margin(code, bar_raw, bar_pre) VALUES($c, $c, 'sh'||$c);";
            var p = ins.Parameters.Add("$c", SqliteType.Text);
            foreach (var c in codes) { p.Value = c; ins.ExecuteNonQuery(); }
            tx.Commit();
        }
        return codes.Count;
    }

    /// <summary>
    /// 判断一个 <see cref="MarketBoard.Unknown"/> 的代码（实际上就是 ETF）是不是上交所的——
    /// **看库里实际存成什么前缀，不猜代码规则**。
    ///
    /// 为什么不扩展 <see cref="MarketClassifier"/>：那个类的职责是"从 6 位**股票**代码判板块"，
    /// 它认 6/9/688/300/92 这些，ETF 的 5xxxx / 15xxxx 不在它范围里（510050 返回 Unknown）。
    /// 而 ETF 的市场前缀在这个项目里**从来不是算出来的**——是新浪的 ETF 列表接口直接给的
    /// 8 位符号（sh510300 / sz159915，见 SinaEtfListProvider），我们原样存进 Bar 和 StockMeta。
    /// 所以这里用同一个事实来源，不另造一套"5 开头算上交所"的规则（自写前缀规则的代价见
    /// feedback_market_prefix_via_classifier：漏掉 920 开头的北交所票，342 只静默抓不到）。
    ///
    /// 先查 StockMeta（6000 行，快），没有再按主键探一下 Bar。ETF 只有几百个，不慢。
    /// </summary>
    private static bool IsShanghaiByStoredPrefix(SqliteConnection conn, string code)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM StockMeta WHERE code = 'sh' || $c "
            + "UNION ALL "
            + "SELECT 1 FROM Bar WHERE code = 'sh' || $c AND granularity = 'day' LIMIT 1;";
        cmd.Parameters.AddWithValue("$c", code);
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>
    /// 哪些交易日还有待补的行。**判据是"沪市 + 有余量 + 余额缺失"**，
    /// 其中"缺失"包括 NULL 和 0——沪市的 0 全是源头 null 被读成 0 留下的
    /// （已验证：3,139,554 行沪市数据里 short_balance &gt; 0 的有 0 行）。
    /// </summary>
    private static List<string> SelectPendingDays(
        SqliteConnection conn, DateTime? since, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT m.trade_date
            FROM MarginDetail m JOIN _sh_margin s ON s.code = m.code
            WHERE m.short_volume > 0
              AND (m.short_balance IS NULL OR m.short_balance = 0)
              AND ($since IS NULL OR m.trade_date >= $since)
            ORDER BY m.trade_date;
            """;
        cmd.Parameters.AddWithValue("$since",
            since.HasValue ? since.Value.ToString("yyyy-MM-dd") : DBNull.Value);
        var days = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) { ct.ThrowIfCancellationRequested(); days.Add(r.GetString(0)); }
        return days;
    }

    /// <summary>
    /// 补一天。一天一个事务——沪市每天约 1700 行，事务小、WAL 不涨，中断也只丢当天。
    ///
    /// 收盘价按三种代码形式找：裸码（个股）、sh+码（ETF）。找不到就 <c>close IS NULL</c>，
    /// 此时 <c>short_volume * NULL = NULL</c>，SQLite 会写回 NULL——正是想要的（留 NULL 待下次）。
    /// 所以 WHERE 里要挡掉"算出来还是 NULL"的行，免得白写一遍。
    /// </summary>
    private static int FillOneDay(SqliteConnection conn, string day)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE MarginDetail
            SET short_balance = short_volume * (
                    SELECT b.close FROM Bar b
                    JOIN _sh_margin s ON s.code = MarginDetail.code
                    WHERE b.granularity = 'day'
                      AND substr(b.period_start, 1, 10) = MarginDetail.trade_date
                      AND b.code IN (s.bar_raw, s.bar_pre)
                      AND b.close > 0
                    LIMIT 1)
            WHERE trade_date = $d
              AND short_volume > 0
              AND (short_balance IS NULL OR short_balance = 0)
              AND code IN (SELECT code FROM _sh_margin)
              AND EXISTS (
                    SELECT 1 FROM Bar b
                    JOIN _sh_margin s ON s.code = MarginDetail.code
                    WHERE b.granularity = 'day'
                      AND substr(b.period_start, 1, 10) = MarginDetail.trade_date
                      AND b.code IN (s.bar_raw, s.bar_pre)
                      AND b.close > 0);
            """;
        cmd.Parameters.AddWithValue("$d", day);
        int n = cmd.ExecuteNonQuery();
        tx.Commit();
        return n;
    }

    /// <summary>补完之后还缺的行数——即"有融券余量但没有当日收盘价"的那些。</summary>
    private static int CountStillMissing(SqliteConnection conn, DateTime? since)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM MarginDetail m JOIN _sh_margin s ON s.code = m.code
            WHERE m.short_volume > 0
              AND (m.short_balance IS NULL OR m.short_balance = 0)
              AND ($since IS NULL OR m.trade_date >= $since);
            """;
        cmd.Parameters.AddWithValue("$since",
            since.HasValue ? since.Value.ToString("yyyy-MM-dd") : DBNull.Value);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }
}
