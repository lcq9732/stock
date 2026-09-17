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
/// ════ 取价必须用**不复权**，不能用前复权 ════
/// 公式里的"当日收盘价"是当日**真实成交价**。而 <c>Bar</c> 的 <c>day</c> 是源给的**减法式
/// 前复权**（原价减去此后累计分红），高分红老股的早年价格会被减成**负数**——贵州茅台
/// 2012-05-02：<c>day</c> = **−127.19**，<c>day_raw</c> = 225.98（真实收盘约 232）；
/// 它 6005 根 <c>day</c> 里有 3529 根（59%）<c>close &lt;= 0</c>。全库 <c>day</c> 有
/// 745,340 行负价、涉及 739 只标的，而 <c>day_raw</c> 一行都没有。
///
/// 所以取价**逐标的优先 <c>day_raw</c>**（见 <see cref="HasRawBars"/>）：
///   · 用 <c>day</c> 的话，负价行被 <c>close &gt; 0</c> 挡掉——看着像"没有K线补不上"，
///     实际上 2026-09-16 实测那 2.36% 的"补不上"里，Top20 全是茅台/招行/中国平安这些
///     根本不缺K线的大票，缺失率还随年份单调下降（2010年 25.6% → 2026年 0.16%），
///     正是前复权"越往前偏得越多"的形状
///   · 更要紧的是**没被挡掉的那些也是错的**：2020-06-01 的茅台会按 1160.24 算而不是
///     1419.50，低 18%，越往前错得越多
/// 眼下 ETF 还没有 <c>day_raw</c>，它们回退 <c>day</c>；ETF 是纯分红的加法式失真
/// （510300 的 <c>raw − qfq</c> 恒 0.2110 元、价格 4 元上下 ⇒ 约 5% 偏低，不会为负），
/// 等 <c>doc/etf-backtest-granularity-design.md</c> 那套补上 ETF 的 <c>day_raw</c> 之后，
/// 重跑一次这一项就会自动变准。
///
/// ════ ETF 的代码形式不一样 ════
/// ETF 的K线在 <c>Bar</c> 里是**带市场前缀**存的（<c>sh510050</c>），两融表里却是裸码
/// （<c>510050</c>）。只按裸码找收盘价会让本来有数据的 ETF 也算成"补不上"。
/// 所以查收盘价时两种形式都试（裸码 / sh+码）。
///
/// ════ 幂等、可中断 ════
/// **按交易日分批提交**，一天一个事务（沪市每天约 1700 行）。这样：
///   · WAL 不会涨——单事务写几百万行曾把 WAL 撑到 162GB（见 project_wal_blowup_and_stale_dotnet）
///   · 中断了直接重跑，已补的行不再满足"缺值"判据，不会重复算
/// 补不上的（没有当日收盘价）**留 NULL**，不写 0——留 NULL 下次还有机会补上，
/// 写 0 就永久变成"确实没有融券"了。剩下补不上的是**停牌日**（有融券余量但当天不交易，
/// 没有收盘价），加上 <c>Bar</c> 里一根都没有的 15 只标的——689009 九号公司（名单源
/// <c>hs_a</c> 不含科创板 CDR，1425 行）、12 只退市/换代码股、2 只已清盘 ETF，
/// 详见 <c>doc/missing-instruments-design.md</c>。
/// </summary>
public class SqliteMarginShortBalanceFiller
{
    /// <summary>取价首选：不复权。交易所公式里的收盘价是当日**真实成交价**。</summary>
    private const string GranRaw = "day_raw";

    /// <summary>回退：源给的前复权。只有没有 <see cref="GranRaw"/> 的标的（眼下是 ETF）才用，
    /// 算出来的余额偏低——见类注释。</summary>
    private const string GranQfq = "day";

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
    ///
    /// <paramref name="recompute"/>＝**连已经有值的行也重算一遍**。平时不要开——
    /// 只在**取价口径变了**的时候用一次。
    ///
    /// 为什么需要它：常规判据是"<c>short_balance</c> 缺值（NULL 或 0）才补"，所以口径改了之后
    /// 直接重跑是**没用的**——已经填过的行不再满足判据，错值会一直留着。2026-09-16 就真出了
    /// 这事：第一版用前复权 <c>day</c> 取价跑完了全历史（274 万行），当天的对账全过
    /// （前复权的最新点＝真实价，误差 0.0000%），而历史行系统性偏低（2018 年那批低 33%~83%）。
    ///
    /// 重算是安全的：旧口径填得上的行，新口径一定也填得上（有 day_raw 用它、没有就还用 day），
    /// 所以不会留下"清成 NULL 又补不回来"的行。深市依旧一行不碰。
    /// </summary>
    public MarginShortBalanceFillResult Fill(
        Action<string>? onProgress = null, DateTime? sinceDate = null, CancellationToken ct = default,
        bool recompute = false)
    {
        using var conn = Open();
        if (recompute)
            onProgress?.Invoke("　⚠ 重算模式：连已经有值的沪市行也会按当前口径重新算一遍（深市仍不碰）");

        var (shCodes, withRaw) = PrepareShanghaiCodeTable(conn, ct);
        onProgress?.Invoke($"　沪市两融标的 {shCodes} 只（按 MarketClassifier 判，含 ETF）");
        onProgress?.Invoke($"　取价口径：{withRaw} 只走不复权 day_raw；"
                           + $"{shCodes - withRaw} 只没有 day_raw、回退前复权 day（基本是 ETF，算出的余额偏低）");

        var days = SelectPendingDays(conn, sinceDate, recompute, ct);
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
            rows += FillOneDay(conn, day, recompute);
            done++;
            // 进度别报太密：全历史有 3900 多天，一天一条日志会把日志刷爆
            if (done % 100 == 0 || done == days.Count)
                onProgress?.Invoke($"　{done}/{days.Count} 天，已补 {rows:N0} 行（当前 {day}）");
        }

        int noClose = CountStillMissing(conn, sinceDate);
        onProgress?.Invoke($"　完成：补上 {rows:N0} 行；仍缺 {noClose:N0} 行"
                           + "（这些标的没有当日收盘价——停牌日，以及 689009 和几只退市/换码股，留 NULL）");
        return new MarginShortBalanceFillResult(days.Count, rows, noClose);
    }

    /// <summary>
    /// 把沪市两融标的写进临时表，并连带算好"在 Bar 里该用哪个代码"的三种候选形式。
    ///
    /// 为什么要过一遍临时表、不直接在 SQL 里用 <c>substr(code,1,1) IN ('6','9')</c>：
    /// 市场判断必须走 <see cref="MarketClassifier"/>（SQL 里调不到），而逐票执行 UPDATE
    /// 又太慢。临时表是两者的折中——判断在 C# 做、批量在 SQL 做。
    /// </summary>
    private static (int Total, int WithRaw) PrepareShanghaiCodeTable(SqliteConnection conn, CancellationToken ct)
    {
        var all = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT code FROM MarginDetail;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) { ct.ThrowIfCancellationRequested(); all.Add(r.GetString(0)); }
        }

        var codes = new List<(string Code, string Gran)>();
        foreach (var code in all)
        {
            ct.ThrowIfCancellationRequested();
            var board = MarketClassifier.Classify(code);
            // 只要沪市——深市的 short_balance 是源头给的真值，一行都不碰
            bool isSh = board is MarketBoard.ShanghaiMain or MarketBoard.ShanghaiStar or MarketBoard.ShanghaiB
                                                     // 股票：MarketClassifier 说了算
                        || (board == MarketBoard.Unknown && IsShanghaiByStoredPrefix(conn, code));
                                                     // ETF：库里存的前缀说了算，见那个方法的注释
            // 其余（深市股票、北交所、判不出来的）一律不碰
            if (isSh) codes.Add((code, HasRawBars(conn, code) ? GranRaw : GranQfq));
        }

        using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS _sh_margin(
                    code    TEXT PRIMARY KEY,
                    bar_raw TEXT,   -- 裸码（个股在 Bar 里是这个形式）
                    bar_pre TEXT,   -- sh+码（ETF 在 Bar 里是这个形式）
                    gran    TEXT    -- 这只标的取价用哪个粒度：day_raw 优先，没有才回退 day
                );
                DELETE FROM _sh_margin;
                """;
            create.ExecuteNonQuery();
        }
        using (var tx = conn.BeginTransaction())
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText =
                "INSERT OR IGNORE INTO _sh_margin(code, bar_raw, bar_pre, gran) VALUES($c, $c, 'sh'||$c, $g);";
            var p = ins.Parameters.Add("$c", SqliteType.Text);
            var g = ins.Parameters.Add("$g", SqliteType.Text);
            foreach (var (code, gran) in codes) { p.Value = code; g.Value = gran; ins.ExecuteNonQuery(); }
            tx.Commit();
        }
        return (codes.Count, codes.Count(x => x.Gran == GranRaw));
    }

    /// <summary>
    /// 这只标的在 <c>Bar</c> 里有没有不复权序列。**有就用 day_raw 取价，没有才回退 day**——
    /// 理由见类注释「取价必须用不复权」那一段。
    ///
    /// 逐标的判、不按表一刀切：个股基本都有 day_raw，ETF 目前一只都没有
    /// （<c>doc/etf-backtest-granularity-design.md</c> 那套方案还没实施）。一刀切的话，
    /// 要么个股陪着 ETF 一起用错口径，要么 ETF 的融券余额整块补不出来。
    /// </summary>
    private static bool HasRawBars(SqliteConnection conn, string code)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM Bar WHERE code IN ($c, 'sh' || $c) AND granularity = 'day_raw' LIMIT 1;";
        cmd.Parameters.AddWithValue("$c", code);
        return cmd.ExecuteScalar() != null;
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
        SqliteConnection conn, DateTime? since, bool recompute, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT m.trade_date
            FROM MarginDetail m JOIN _sh_margin s ON s.code = m.code
            WHERE m.short_volume > 0
              {(recompute ? "" : "AND (m.short_balance IS NULL OR m.short_balance = 0)")}
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
    private static int FillOneDay(SqliteConnection conn, string day, bool recompute)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            UPDATE MarginDetail
            SET short_balance = short_volume * (
                    SELECT b.close FROM Bar b
                    JOIN _sh_margin s ON s.code = MarginDetail.code
                    WHERE b.granularity = s.gran
                      AND substr(b.period_start, 1, 10) = MarginDetail.trade_date
                      AND b.code IN (s.bar_raw, s.bar_pre)
                      AND b.close > 0
                    LIMIT 1)
            WHERE trade_date = $d
              AND short_volume > 0
              {(recompute ? "" : "AND (short_balance IS NULL OR short_balance = 0)")}
              AND code IN (SELECT code FROM _sh_margin)
              AND EXISTS (
                    SELECT 1 FROM Bar b
                    JOIN _sh_margin s ON s.code = MarginDetail.code
                    WHERE b.granularity = s.gran
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
