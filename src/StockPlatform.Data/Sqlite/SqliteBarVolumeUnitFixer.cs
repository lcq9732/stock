using Microsoft.Data.Sqlite;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 把 <c>Bar.volume</c> 里**按"股"存进去的行**改成"手"（÷100），2026-09-10。
///
/// 病根见 <see cref="Logic.Services.BarVolumeUnit"/>：腾讯对科创板给股、新浪对所有板块给股，
/// 而两个 fetcher 原来都是原样入库，于是这一列里并存两种单位。解析层已经归一化了，
/// 这个类负责**已经在库里的历史**。
///
/// ════ 三组口径，三套判据 ════
/// 决定判据的是"这个口径的 close 是不是真实成交价"：
///
/// <code>
///   day_raw            close 是真实价 → 自己判：amount/(volume×close) ≈1 就是股
///   day / hfq / adj    close 是复权价 → 跟 day_raw **同一天**比：复权不动量，本该逐行相等
///   week / month       本地聚合出来的 → 跟**同期 day_raw 之和**比
/// </code>
///
/// ⚠ 第一版把 day/week/month 也归进"自己判"，跑完漏了 20 多万行——688004 在 2020-06-15
/// 的 day.volume 还是 6,964,759 股，而 day_raw 已经是 69,647.59 手。因为 day 是前复权，
/// 那天的复权价 92.72、真实价 131.07，量额比被带偏、落不进区间。
/// 「只有 day_raw 的 close 是真实成交价」这句话在体检 V6 的注释里本来就写着。
///
/// ════ 判据不按代码前缀，因为要修的不止科创板 ════
/// 新浪回退（2026-09-10 已拆除）每触发一次，就往那只票的历史里掺一段股口径的行，
/// 哪只票哪一段全凭当时的网络抖动。实测 616 只受影响 = 科创板 613 + 新浪掺的 3 只。
///
/// ════ 幂等 ════
/// 三套判据改完之后都不再命中（比值从 ≈1 变 ≈100、从 100 倍差变 1 倍差），
/// 所以中断了直接重跑，不用记断点。
///
/// ════ 三类行碰都不碰 ════
///   · <b>amount 或 close 缺失/为 0</b>：判不出来，宁可漏也不能瞎改，留给体检 V6 报。
///   · <b>指数（带前缀的 8 位符号）和板块合成（BK 开头）</b>：它们的"成交量"是成分股汇总，
///     量额比没有物理意义（实测上证指数 7890 行落在两个区间之外）。
///   · <b>ETF</b>：单位是"份"，量额比也不落在这两个区间（实测沪深 ETF 各有 10%~13% 在区间外）。
///     口径没验过，先一起排除，宁可漏报不误改——跟体检 V6 的取舍一致。
/// </summary>
public sealed class SqliteBarVolumeUnitFixer(string dbPath)
{
    /// <summary>
    /// 唯一能用量额比自己判断的口径——**只有 day_raw 的 close 是真实成交价**。
    /// </summary>
    private static readonly string[] SelfJudging = ["day_raw"];

    /// <summary>
    /// close 是复权价、量额比失真，只能跟 day_raw **同一天**逐行比的口径。
    /// 依据是"复权只动价、不动量"，所以它们的 volume 本该逐行等于 day_raw。
    /// </summary>
    private static readonly string[] FollowRawSameDay = ["day", "day_hfq", "day_adj"];

    /// <summary>
    /// 周/月线是本地从日线**聚合**出来的（<c>BarAggregator</c>：volume 求和，
    /// period_start 取该期**第一个交易日**、不是周一/月初），所以比的是**同期 day_raw 之和**。
    /// </summary>
    private static readonly (string Gran, string Span)[] FollowRawAggregate =
        [("week", "+7 days"), ("month", "+1 month")];

    /// <summary>一手多少股。</summary>
    private const double SharesPerLot = 100;

    /// <summary>
    /// 只统计不改。
    ///
    /// ⚠ **别在 <see cref="FixAsync"/> 之前调它**：day_raw 那一趟是 2540 万行的全表扫
    /// （<c>granularity</c> 上没索引，那条要人点【优化数据库】才建），数一遍再改一遍等于翻倍。
    /// 要行数的话 Fix 自己就返回。
    /// </summary>
    public Dictionary<string, int> Preview()
    {
        using var conn = Open();
        var result = new Dictionary<string, int>();

        foreach (var gran in SelfJudging)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM Bar WHERE granularity = $g AND {SharesPredicate};";
            cmd.Parameters.AddWithValue("$g", gran);
            result[gran] = Convert.ToInt32(cmd.ExecuteScalar());
        }

        var affected = SelectAffectedCodes(conn, CancellationToken.None);
        foreach (var gran in FollowRawSameDay)
            result[gran] = PerCode(conn, affected, gran, SameDayPredicate, count: true, CancellationToken.None);
        foreach (var (gran, span) in FollowRawAggregate)
            result[gran] = PerCode(conn, affected, gran, AggregatePredicate(span), count: true, CancellationToken.None);

        return result;
    }

    /// <summary>
    /// 实际修正。按口径逐个来，每个口径一个事务——几百万行一个大事务会让 WAL 暴涨，
    /// 中途失败还全回滚白跑几分钟；分开之后停在哪都是"这个口径改完了"的干净状态。
    /// </summary>
    /// <param name="onProgress">每个口径改完报一次。</param>
    /// <returns>每个口径实际改了多少行。</returns>
    public Dictionary<string, int> FixAsync(Action<string>? onProgress = null, CancellationToken ct = default)
    {
        using var conn = Open();
        var result = new Dictionary<string, int>();

        // ⓪ 先把"哪些票要改"取出来。必须在改 day_raw **之前**——三条判据里的第①条
        //    （day_raw 自己的量额比）改完就失效了；另两条看的是口径之间一不一致，
        //    从任何中间状态重跑都成立。
        //    有了这份名单，后面几个口径只需在这几百只票的行里找，而不是对 1800 万行逐行
        //    做关联子查询（实测那样扫十分钟都出不来）。
        var affected = SelectAffectedCodes(conn, ct);
        onProgress?.Invoke($"　涉及 {affected.Count} 只标的" +
                           "（判据：day_raw 量额比 ≈1，或其它口径跟 day_raw 差 100 倍）");

        // ① day_raw：唯一能自己判的口径，也是后面所有口径的参照。必须第一个改。
        foreach (var gran in SelfJudging)
        {
            ct.ThrowIfCancellationRequested();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                UPDATE Bar SET volume = volume / {SharesPerLot}
                WHERE granularity = $g AND {SharesPredicate};
                """;
            cmd.Parameters.AddWithValue("$g", gran);
            int n = cmd.ExecuteNonQuery();
            tx.Commit();
            result[gran] = n;
            onProgress?.Invoke($"　{gran}：修正 {n} 行");
        }

        // ② 复权口径：跟 day_raw 同一天比。此刻 day_raw 已是手，还差 100 倍的就要跟上。
        foreach (var gran in FollowRawSameDay)
        {
            ct.ThrowIfCancellationRequested();
            int n = PerCode(conn, affected, gran, SameDayPredicate, count: false, ct);
            result[gran] = n;
            onProgress?.Invoke($"　{gran}：修正 {n} 行（比对 day_raw 同一天）");
        }

        // ③ 周/月线：跟同期 day_raw 之和比。
        foreach (var (gran, span) in FollowRawAggregate)
        {
            ct.ThrowIfCancellationRequested();
            int n = PerCode(conn, affected, gran, AggregatePredicate(span), count: false, ct);
            result[gran] = n;
            onProgress?.Invoke($"　{gran}：修正 {n} 行（比对同期 day_raw 之和）");
        }

        return result;
    }

    /// <summary>
    /// 按票逐个执行——数或改由 <paramref name="count"/> 决定，两条路共用同一段谓词，
    /// 免得 Preview 报的数跟 Fix 改的数对不上。
    ///
    /// 逐只票发语句而不是一条 <c>IN</c> 大列表：每条都走主键前缀
    /// (code, granularity, period_start)，几百只票各自秒回；<c>IN</c> 列表长到几百个之后
    /// SQLite 的查询计划会退化成全表扫。
    /// </summary>
    private static int PerCode(SqliteConnection conn, List<string> codes, string gran,
                               string predicate, bool count, CancellationToken ct)
    {
        using var tx = count ? null : conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = count
            ? $"SELECT COUNT(*) FROM Bar WHERE code = $c AND granularity = $g AND {predicate};"
            : $"UPDATE Bar SET volume = volume / {SharesPerLot} WHERE code = $c AND granularity = $g AND {predicate};";
        var pc = cmd.CreateParameter(); pc.ParameterName = "$c"; cmd.Parameters.Add(pc);
        cmd.Parameters.AddWithValue("$g", gran);

        int n = 0;
        foreach (var code in codes)
        {
            ct.ThrowIfCancellationRequested();
            pc.Value = code;
            n += count ? Convert.ToInt32(cmd.ExecuteScalar()) : cmd.ExecuteNonQuery();
        }
        tx?.Commit();
        return n;
    }

    /// <summary>
    /// 哪些标的还有按股存的行——**两个来源取并集**。
    ///
    /// ⚠ 2026-09-10 第二次修。原来只看一条："day_raw 的量额比 ≈1"。这在全新的库上没问题，
    /// 但**跑过一轮之后就失效了**：上一轮已经把 day_raw 全改成手，这条判据于是一条都找不到，
    /// 名单为空，后面几个口径全部空转——生产上真的这么跑了一遍，日志写着"涉及 0 只标的"、
    /// 六个口径各"修正 0 行"，而 day 那 199,860 行原封不动。
    ///
    /// 病根是拿"day_raw 还没修"当前提去找票，可那个前提正是上一轮亲手破坏的。
    /// 病根是拿"day_raw 还没修"当前提去找票，可那个前提正是上一轮亲手破坏的。
    /// 现在改成两条并集，任一命中都算：
    ///   ① day_raw 自己还是股（整只票都没修过）；
    ///   ② **day** 跟 day_raw 同一天还差 100 倍（day_raw 修了、它没跟上）。
    ///
    /// ② 只查 day 一个口径、不查 day_hfq/day_adj：三个复权口径是同一批漏改的，
    /// 查一个就够，而每多一个粒度就多扫 700 多万行（实测三个一起 10 分钟出不来）。
    ///
    /// ② 不依赖 day_raw 的状态、只看**两个口径一不一致**，所以从任何中间状态重跑都对。
    /// 它走主键前缀 (code, granularity, period_start) 的 join，几分钟能扫完。
    ///
    /// ════ 为什么不加"③ week/month 跟同期 day_raw 之和比" ════
    /// 试过，**太慢**：那要对 19 万行周/月线各做一次 SUM 子查询，10 分钟跑不完。
    /// 而收益近乎为零——周/月线是从日线聚合来的，同一只票的 week 有问题时 day 必然也有
    /// （实测上一轮漏改的 day 199,860 / week 41,110 / month 10,680 就是同一批 616 只票），
    /// ② 已经把它们全捞进来了。
    ///
    /// 残余风险：某只票的 day 对了、week 没对就会漏。那要求两个口径在不同轮次里被分别修过，
    /// 现实中没有这条路径。真出现了，逐行判据（<see cref="AggregatePredicate"/>）本身是准的，
    /// 把那只票的代码手工塞进名单就能修。
    private static List<string> SelectAffectedCodes(SqliteConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT code FROM (
                SELECT code FROM Bar
                WHERE granularity = 'day_raw' AND {SharesPredicate}

                UNION

                SELECT b.code FROM Bar b
                JOIN Bar r ON r.code = b.code AND r.granularity = 'day_raw'
                          AND r.period_start = b.period_start
                WHERE b.granularity = 'day'
                  AND b.volume > 0 AND r.volume > 0
                  AND b.code GLOB '[0-9][0-9][0-9][0-9][0-9][0-9]'
                  AND substr(b.code, 1, 2) NOT IN ('51', '15', '56', '58')
                  AND b.volume / r.volume > 50

            );
            """;
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            list.Add(r.GetString(0));
        }
        return list;
    }

    /// <summary>
    /// "这一行的 volume 是按股存的"——量额比落在 ≈1，且不是指数/板块/ETF。
    /// **只对 day_raw 成立**（别的口径 close 是复权价）。
    ///
    /// 代码过滤用 <c>GLOB</c> 而不是 LIKE：指数和板块指数带字母前缀（sh000001 / BK0475），
    /// 个股 ETF 全是 6 位纯数字，<c>GLOB '[0-9][0-9][0-9][0-9][0-9][0-9]'</c> 一刀切干净。
    /// ETF 再按 51/15/56/58 开头排除（沪深 ETF 的号段）。
    ///
    /// 区间取 0.8~1.25 而不是精确 1：成交额和收盘价都有舍入（腾讯的 amount 截断到百位）。
    /// 跟"手"的 80~125 之间留着很宽的空隙，判错的可能性极低。
    /// </summary>
    private const string SharesPredicate = """
        volume > 0 AND close > 0 AND amount > 0
        AND code GLOB '[0-9][0-9][0-9][0-9][0-9][0-9]'
        AND substr(code, 1, 2) NOT IN ('51', '15', '56', '58')
        AND amount / (volume * close) BETWEEN 0.8 AND 1.25
        """;

    /// <summary>
    /// day / day_hfq / day_adj 的判据：复权不动成交量，所以它**本该等于** day_raw 同一天的
    /// volume。day_raw 已经在前一步改成手了，此刻还差 100 倍的就是没跟上的那些。
    /// 用 &gt; 50 倍而不是 ==100 倍，是留出浮点与历史抓取时序造成的零星偏差。
    ///
    /// ⚠ 外层一律写全表名 <c>Bar.</c> 而不是别名——<c>UPDATE</c> 里给不了别名
    /// （SQLite 不支持 <c>UPDATE Bar b SET ...</c>），而这段谓词要被 UPDATE 和 SELECT 共用。
    /// </summary>
    private const string SameDayPredicate = """
        Bar.volume > 0
        AND Bar.code GLOB '[0-9][0-9][0-9][0-9][0-9][0-9]'
        AND substr(Bar.code, 1, 2) NOT IN ('51', '15', '56', '58')
        AND EXISTS (
            SELECT 1 FROM Bar r
            WHERE r.code = Bar.code AND r.granularity = 'day_raw' AND r.period_start = Bar.period_start
              AND r.volume > 0 AND Bar.volume / r.volume > 50
        )
        """;

    /// <summary>
    /// 周/月线的判据：它们的 volume 是同期日线求和，所以拿**同期 day_raw 之和**比。
    ///
    /// <paramref name="span"/> 是 SQLite 的日期偏移（<c>+7 days</c> / <c>+1 month</c>）。
    /// 起点用行自己的 period_start——聚合器取的是该期**第一个交易日**，往后推一周/一月
    /// 正好盖住这一期（下一期的第一个交易日一定在偏移之后）。
    ///
    /// day_raw 那一段完全缺失时 SUM 返回 NULL，比较不成立、不改——宁可漏也不瞎改。
    /// 缺几天不影响判断：差的是 100 倍，除非 day_raw 缺掉 98% 才可能误判。
    /// </summary>
    private static string AggregatePredicate(string span) => $"""
        Bar.volume > 0
        AND Bar.code GLOB '[0-9][0-9][0-9][0-9][0-9][0-9]'
        AND substr(Bar.code, 1, 2) NOT IN ('51', '15', '56', '58')
        AND Bar.volume / (
            SELECT SUM(r.volume) FROM Bar r
            WHERE r.code = Bar.code AND r.granularity = 'day_raw'
              AND r.period_start >= Bar.period_start
              AND r.period_start < date(Bar.period_start, '{span}')
        ) > 50
        """;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        return conn;
    }
}
