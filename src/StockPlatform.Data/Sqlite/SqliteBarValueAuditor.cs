using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// K线**值体检**的六条判据（2026-09-09，设计见 doc/bar-value-audit-design.md）。全部是纯查询、
/// 一个网络请求都不发。
///
/// ════ 为什么要有它 ════
/// 【全库数据体检】原有的判据全是"**行在不在**"，一条都不查"行里的值对不对"。于是两个真实事故
/// 都从它眼皮下溜过去了：
///   ① 东财终端日线导入让 day_raw ≤2015 的 702 万行 turnover 整列为 NULL，
///      【重算回测序列】对 2886 只票每只都抛异常，界面上"待重算 2886 只"永不下降、**潜伏三天**；
///   ② 任务落在交易时段跑，抓到的当日K线是半天快照（002650 在 2026-09-01 被记成"四价合一 6.04、
///      成交量 23 手"，真实收盘 6.01/20953 手），而水位线跨过午夜就再也不回头，错值被永久固化。
/// 两次的共同点：**数据是错的，不是缺的**，行一根不少，体检全绿。
///
/// ════ 判据能力边界（别误读体检全绿）════
/// <see cref="CrossGranularityMismatch"/> 只抓"口径之间对不上"。数据源**自己给错**它看不见——
/// 603999 在 2026-09-08 的 amount 记 6914 万而实际 2.13 亿，三个口径是同一批抓的，会一致地错。
/// 那种只有 <see cref="RowIssues"/> 里的 ratio 判据能兜一部分，或者靠跨源比对（要联网，不在体检里）。
/// </summary>
public sealed class SqliteBarValueAuditor
{
    private readonly string _dbPath;

    public SqliteBarValueAuditor(string dbFilePath) => _dbPath = dbFilePath;

    private const string DateFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>体检的四个日线口径。周/月是本地聚合出来的，量额是求和值，不适用这些判据。</summary>
    private static readonly string[] DailyGranularities =
        [Granularity.Day, Granularity.DayHfq, Granularity.DayRaw, Granularity.DayAdj];

    /// <summary>
    /// "收盘后确认"的判定时刻，跟 <c>IncrementalWindowCalculator.MarketCloseHour</c> 是同一个 16 点。
    /// 这里写成 SQL 里的 <c>'+16 hours'</c>，两处改了一处就会各说各话——所以谁改都要一起改。
    /// </summary>
    private const int MarketCloseHour = 16;

    /// <summary>
    /// 成交额的**量化刻度**（元）——两个口径差在这个数以内不算不一致（2026-09-12 用户定，
    /// doc/bar-value-audit-design.md §16 缺陷二方案 A）。
    ///
    /// ════ 为什么不是 0 ════
    /// 腾讯的成交额只有 **100 元刻度**（源给「万元」两位小数，入库 ×1e4；1995/2015/2026 各抽一整天
    /// 验过，万元从不出现第 3 位小数）。两个端点用的是同一个四舍五入规则，但在极少数行上
    /// <c>day_raw</c> 会偏高一格——2026-09-11 查到 31 只北交所票的 49 行，全部**正好差 100 元**。
    /// 拿**东财本地**那份元级精度的数据当第三方裁判，这些行的余数**恒为 49 &lt; 50**，四舍五入本该
    /// 得低值，也就是说两个渲染值都只是同一个真值的舍入结果。
    ///
    /// 所以这不是"数据不好看就放宽规则"：原来的 1e-6 要求的精度**超过了数据源能提供的分辨率**，
    /// 那样的段修不掉也报不完，<c>Tries</c> 只会一路爬。放过的宽度**正好是源的刻度**，
    /// 不是随手给的数——真错（量额差 100 倍）的相对误差是 99，照样必报。
    ///
    /// ⚠ 只放过**一格**。差 200 元就不是舍入了，仍然报。
    ///
    /// ⚠ 写成 <c>一格 + 浮点余量</c>（不是 <c>MAX(一格, 浮点余量)</c>）：入库的 amount 是
    /// 「万元 × 1e4」算出来的，本身就带末位噪声（920819 的 <c>day</c> 存的是
    /// <c>1010699.9999999999</c>），差值算出来是 <c>100.00000000011</c>——拿 <c>&gt; 100</c> 去卡，
    /// 正好差一格的行会**险些全部漏网**（2026-09-12 实测：49 行里 3 行因此仍被报）。
    /// </summary>
    private const int AmountQuantumYuan = 100;

    /// <summary>
    /// V6 的正常区间（2026-09-12 从 <c>[80,125]</c> 放宽到 <c>[50,250]</c>）。
    ///
    /// 原来的 ±25% 余量**太紧**：生产实测 592 行命中里 **318 行是误报**——比值落在 50~80 或
    /// 125~250，成因只是**当日均价偏离收盘** 20~37%。这在流动性差的标的上很正常（误报集中在
    /// 北交所 920xxx、以及 1990 年代成交稀疏的老股），一天之内价格走一段、成交集中在某一侧，
    /// 均价就会离收盘很远。
    ///
    /// 而**真错的特征是"整数倍偏离"**，一眼能跟噪声分开：实测的真问题全是
    /// ≈1（按股存）、≈2、≈5、≈10、≈500（1991-92 老数据成交量少记 5 倍）。
    /// 放宽到 [50,250] 之后：318 行误报归零，192 行真问题一条不漏
    /// （数量级错至少差 2 倍，离 50/250 这两个边界还很远）。
    ///
    /// ⚠ 代价说清楚：**恰好差 2 倍**的错（比如成交额翻倍）会被放过。实测一例都没有，
    /// 而且那种错另有 V3（跨口径）兜着——四个口径不会一起差 2 倍。
    /// 一条**六成是误报**的判据等于没有判据，这个取舍值得。
    /// </summary>
    private const int RatioLow = 50, RatioHigh = 250;

    /// <summary>
    /// 单行内能判出来的四类问题（V1/V2/V4/V6）。**一次扫描出四类**——分四遍扫 7000 万行是四倍的钱。
    /// </summary>
    /// <param name="Code">标的代码。</param>
    /// <param name="Granularity">口径。</param>
    /// <param name="Day">交易日。</param>
    /// <param name="Kind">见 <see cref="AuditFindingKind"/>。一行可能同时中几条，那就返回几条。</param>
    public sealed record RowIssue(string Code, string Granularity, DateTime Day, string Kind);

    /// <summary>
    /// 聚合成**段**的问题（一个 code × 一个口径 × 一类问题 = 一段）——体检用这个，不用行级。
    ///
    /// ════ 为什么不是行级 ════
    /// 待补名单的单位本来就是段，而行级可能是百万量级（前复权减法式的负价一度报出 5 万行）。
    /// 早先的做法是行级返回 + 每类 5 万条上限，那个上限有两个毛病：**把"截断"伪装成精确数字**
    /// （日志显示"50000 行"，看不出后面还有多少），而且真实规模从此不可知。
    /// 现在 SQL 一次扫描、C# **流式**边读边聚合：内存里只留段（几千个），行数只累加计数，
    /// 于是上限可以彻底去掉。
    /// </summary>
    /// <param name="Days">这一段里命中判据的**行数**（不是区间长度）。</param>
    public sealed record IssueSegment(
        string Code, string Granularity, string Kind, DateTime From, DateTime To, int Days);

    /// <summary>
    /// 扫出所有单行问题（V1 盘中固化 / V2 关键列 NULL / V4 OHLC 不自洽 / V6 量额比率异常）。
    ///
    /// <para><b>V1 盘中固化</b>：<c>fetched_at</c> 早于该交易日 16:00 ⇒ OHLC 是当时的瞬时价、
    /// 量额换手是半天累计值。</para>
    ///
    /// <para><b>V2 关键列 NULL</b>：写入路径向来写 0 而不是 NULL，所以这一条实际抓的是
    /// **批量导入绕过写入路径**那类。</para>
    ///
    /// <para><b>V4 OHLC 不自洽</b>：<c>high</c> 装不下 open/close，或 <c>low</c> 比它们还高。
    /// 抓的是脏数据、解析错位、字段串位。留 1e-9 容差，免得被浮点末位噪声刷屏。
    ///
    /// ⚠ <b>"价格 ≤ 0" 这一条只对 <see cref="Granularity.DayRaw"/> 用</b>（2026-09-09 生产实测修）：
    /// 前复权是**减法式**的（原价 − 累计分红），高分红股票往前推十几年会被减到零以下——万科
    /// 1997 年的前复权价是 −8.17。那是已知的口径失真、不是脏数据，拿它报警的话一轮体检报出
    /// 5 万行（244 只票，判据上限都被打满），把真问题全淹了。只有原始成交价必须恒 &gt; 0。</para>
    ///
    /// <para><b>V6 量额比率</b>：<c>amount / (volume × close)</c> 必须 ≈100——volume 的单位是
    /// **手**，全库唯一口径。落在外面的就是量或额本身不对：603999 在 2026-09-08 的 amount
    /// 少了约 2/3，ratio≈33。
    ///
    /// ⚠ <b>2026-09-10 收紧：≈1 不再放过。</b> 原来这条判据把 ≈1 也当正常，因为当时科创板
    /// 688/689 的 volume 确实是按**股**存的（腾讯给股，而 fetcher 原样入库）。那是个真 bug，
    /// 不是该被容忍的第二种口径——它让含科创板的板块指数成交量常年虚高 100 倍。
    /// 现在解析层统一归一化成手（<c>Logic/Services/BarVolumeUnit.cs</c>），历史由任务
    /// 【统一成交量单位】<c>StepFixVolumeUnit</c> 修正，所以这里必须收紧：不收的话，
    /// 以后哪个数据源再掺进股口径，照样没人发现。
    /// <b>体检报出一批 ratio≈1 的行时，先跑【统一成交量单位】</b>（纯本地、幂等，几分钟），
    /// 而不是去查数据源。
    ///
    /// ⚠ <b>只对「个股 × 不复权」用</b>（2026-09-09 生产实测两次收窄）：
    /// · <b>只对个股</b>——指数和板块指数的 <c>close</c> 是**点位**、不是价格，跟成交额压根没有
    ///   这个倍数关系（上证指数首日 1990-12-19 就被报出来了）。判据是"代码 6 位纯数字"：
    ///   指数/ETF/板块指数在本库里都带前缀（sh000001 / sh510300 / BK0594），个股不带。
    ///   ETF 虽有真实价格，但量额口径没验过，先一起排除，宁可漏报不误报。
    /// · <b>只对不复权</b>——这个比值里只有 <c>close</c> 随复权变，而 <c>amount</c> 永远是**真实
    ///   成交额**。拿前复权的复权价去除真实成交额，比值必然对不上：第一次收窄之后仍报出
    ///   2621 万行，最多的 600601 一只票 8420 行（全部历史），全是这么来的。
    ///   只有 day_raw 的 close 是真实成交价，这条判据才成立。</para>
    /// </summary>
    /// <param name="cutoff">这一天之后的不查（数据源可能还没更新完）。</param>
    /// <param name="since">只查这一天起的（null＝全历史）。值判据是全表扫描，限个下界能省很多时间。</param>
    /// <param name="limitPerKind">每一类最多返回多少条，防止一次拉回上百万行把内存打爆。</param>
    /// <param name="codes">
    /// 只查这些代码。**复查时必须传**：这几条判据是全表扫描，拿全库扫描去复查一批 500 段
    /// 就是把体检重跑一遍（见 doc/bar-value-audit-design.md §5）。体检那边不用这个方法——
    /// 它走 <see cref="RowIssueSegments"/>，流式聚合成段、不把行拉进内存。
    /// </param>
    public List<RowIssue> RowIssues(DateTime cutoff, DateTime? since = null,
                                    IReadOnlyCollection<string>? codes = null)
        => ReadRowIssues(cutoff, since, codes).ToList();

    /// <summary>
    /// 单行判据的**流式**读取（V1/V2/V4/V6，一次扫描出四类）。调用方要么聚合成段
    /// （<see cref="RowIssueSegments"/>），要么已经用 <paramref name="codes"/> 把范围限小了
    /// （复查）——**别无条件 ToList 全库**，那是百万量级。
    /// </summary>
    private IEnumerable<RowIssue> ReadRowIssues(
        DateTime cutoff, DateTime? since, IReadOnlyCollection<string>? codes)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();

        var grans = string.Join(",", DailyGranularities.Select((_, i) => $"$g{i}"));
        var codeFilter = CodeFilter(codes, "code");
        cmd.CommandText = $"""
            SELECT code, granularity, period_start,
                   CASE WHEN fetched_at IS NOT NULL
                             AND fetched_at < datetime(period_start, '+{MarketCloseHour} hours')
                        THEN 1 ELSE 0 END AS intraday,
                   CASE WHEN open IS NULL OR close IS NULL OR high IS NULL OR low IS NULL
                             OR volume IS NULL OR amount IS NULL OR turnover IS NULL
                        THEN 1 ELSE 0 END AS nullv,
                   CASE WHEN open IS NOT NULL AND close IS NOT NULL
                             AND high IS NOT NULL AND low IS NOT NULL
                             AND (high < MAX(open, close) - 1e-9
                                  OR low > MIN(open, close) + 1e-9
                                  OR high < low - 1e-9
                                  OR (granularity = $rawGran AND low <= 0))
                        THEN 1 ELSE 0 END AS ohlc,
                   CASE WHEN granularity = $rawGran
                             AND code GLOB '[0-9][0-9][0-9][0-9][0-9][0-9]'
                             AND volume IS NOT NULL AND volume > 0
                             AND close IS NOT NULL AND close > 0
                             AND amount IS NOT NULL AND amount > 0
                             AND NOT (amount / (volume * close) BETWEEN {RatioLow} AND {RatioHigh})
                        THEN 1 ELSE 0 END AS ratio
            FROM Bar
            WHERE granularity IN ({grans})
              AND period_start <= $cutoff
              AND ($since IS NULL OR period_start >= $since)
              {codeFilter}
              AND (
                    (fetched_at IS NOT NULL AND fetched_at < datetime(period_start, '+{MarketCloseHour} hours'))
                 OR open IS NULL OR close IS NULL OR high IS NULL OR low IS NULL
                 OR volume IS NULL OR amount IS NULL OR turnover IS NULL
                 OR (open IS NOT NULL AND close IS NOT NULL AND high IS NOT NULL AND low IS NOT NULL
                     AND (high < MAX(open, close) - 1e-9 OR low > MIN(open, close) + 1e-9
                          OR high < low - 1e-9 OR (granularity = $rawGran AND low <= 0)))
                 OR (granularity = $rawGran
                     AND code GLOB '[0-9][0-9][0-9][0-9][0-9][0-9]'
                     AND volume IS NOT NULL AND volume > 0 AND close IS NOT NULL AND close > 0
                     AND amount IS NOT NULL AND amount > 0
                     AND NOT (amount / (volume * close) BETWEEN {RatioLow} AND {RatioHigh}))
              );
            """;
        for (int i = 0; i < DailyGranularities.Length; i++)
            cmd.Parameters.AddWithValue($"$g{i}", DailyGranularities[i]);
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString(DateFormat));
        cmd.Parameters.AddWithValue("$since", (object?)since?.ToString(DateFormat) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rawGran", Granularity.DayRaw);
        BindCodes(cmd, codes);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var code = r.GetString(0);
            var gran = r.GetString(1);
            var day = DateTime.Parse(r.GetString(2));
            // 一行可能同时中几条判据，那就产出几条
            if (r.GetInt64(3) != 0) yield return new RowIssue(code, gran, day, AuditFindingKind.Intraday);
            if (r.GetInt64(4) != 0) yield return new RowIssue(code, gran, day, AuditFindingKind.NullValue);
            if (r.GetInt64(5) != 0) yield return new RowIssue(code, gran, day, AuditFindingKind.Ohlc);
            if (r.GetInt64(6) != 0) yield return new RowIssue(code, gran, day, AuditFindingKind.Ratio);
        }
    }

    /// <summary>
    /// V3：同一只票同一天，几个口径的 volume/amount/turnover 对不上（<see cref="AuditFindingKind.Inconsistent"/>）。
    ///
    /// 依据是实测：同源、盘后抓的必然逐值相同——50 只票 × 2016 年后 112,473 天，day vs day_hfq
    /// **0 差异**；day vs day_raw 的 25 处差异全部是 2026-09-01 那批盘中行。所以对不上就是异常。
    ///
    /// 以 <see cref="Granularity.Day"/> 为基准逐个比（它覆盖最全：个股+ETF+指数+板块指数）。
    /// 用**相对差 1e-6** 而不是相等：这三列会经过"万元 ×1e4"之类的换算，末位浮点噪声不算问题。
    /// </summary>
    /// <summary>把 V3 的命中流式聚合成段（体检用，无条数上限，理由见 <see cref="IssueSegment"/>）。</summary>
    public List<IssueSegment> CrossGranularitySegments(DateTime cutoff, DateTime? since = null)
    {
        var acc = new Dictionary<(string, string), (DateTime Min, DateTime Max, int N)>();
        foreach (var i in ReadCrossGranularity(cutoff, since, null))
        {
            var key = (i.Code, i.Granularity);
            if (acc.TryGetValue(key, out var cur))
                acc[key] = (i.Day < cur.Min ? i.Day : cur.Min, i.Day > cur.Max ? i.Day : cur.Max, cur.N + 1);
            else
                acc[key] = (i.Day, i.Day, 1);
        }
        return acc
            .Select(kv => new IssueSegment(kv.Key.Item1, kv.Key.Item2, AuditFindingKind.Inconsistent,
                                           kv.Value.Min, kv.Value.Max, kv.Value.N))
            .OrderBy(x => x.Code, StringComparer.Ordinal)
            .ThenBy(x => x.Granularity, StringComparer.Ordinal)
            .ToList();
    }

    /// <param name="codes">只查这些代码。理由同 <see cref="RowIssues"/>（复查用）。</param>
    public List<RowIssue> CrossGranularityMismatch(DateTime cutoff, DateTime? since = null,
                                                   IReadOnlyCollection<string>? codes = null)
        => ReadCrossGranularity(cutoff, since, codes).ToList();

    private IEnumerable<RowIssue> ReadCrossGranularity(
        DateTime cutoff, DateTime? since, IReadOnlyCollection<string>? codes)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        var codeFilter = CodeFilter(codes, "a.code");
        cmd.CommandText = $"""
            SELECT b.code, b.granularity, b.period_start
            FROM Bar a
            JOIN Bar b ON b.code = a.code AND b.period_start = a.period_start
            WHERE a.granularity = $base
              AND b.granularity IN ($g1, $g2, $g3)
              AND a.period_start <= $cutoff
              AND ($since IS NULL OR a.period_start >= $since)
              {codeFilter}
              AND (
                   -- volume 是**硬事实**：同源抓回来必须逐值相同，1e-6 只留浮点噪声的余量
                   ABS(COALESCE(a.volume, 0) - COALESCE(b.volume, 0)) > 1e-6 * MAX(ABS(COALESCE(a.volume, 0)), 1)
                   -- amount 放过**一个量化刻度**（见 AmountQuantumYuan）：源只有百元精度，
                   -- 两个端点各自四舍五入，偶尔会落在相邻的两格上
                OR ABS(COALESCE(a.amount, 0) - COALESCE(b.amount, 0))
                     > {AmountQuantumYuan} + 1e-6 * MAX(ABS(COALESCE(a.amount, 0)), 1)
                   -- turnover 是数据源**算出来的派生值**（成交量 ÷ 流通股本），而各口径是不同时刻
                   -- 抓的：期间股本一变（解禁/增发），同一天的换手率就被重算成另一个数。
                   -- 2026-09-09 实测 000153 的 08-27：volume/amount 完全一致，turnover 7.39 vs 7.36
                   -- （相隔 5 天抓的），拿 1e-6 去判会报出一堆修不掉的段。放宽到 2% 或绝对 0.05。
                OR ABS(COALESCE(a.turnover, 0) - COALESCE(b.turnover, 0))
                     > MAX(0.05, 0.02 * ABS(COALESCE(a.turnover, 0)))
              );
            """;
        cmd.Parameters.AddWithValue("$base", Granularity.Day);
        cmd.Parameters.AddWithValue("$g1", Granularity.DayHfq);
        cmd.Parameters.AddWithValue("$g2", Granularity.DayRaw);
        cmd.Parameters.AddWithValue("$g3", Granularity.DayAdj);
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString(DateFormat));
        cmd.Parameters.AddWithValue("$since", (object?)since?.ToString(DateFormat) ?? DBNull.Value);
        BindCodes(cmd, codes);

        using var r = cmd.ExecuteReader();
        while (r.Read())
            yield return new RowIssue(r.GetString(0), r.GetString(1),
                DateTime.Parse(r.GetString(2)), AuditFindingKind.Inconsistent);
    }

    /// <summary>
    /// 体检用：把单行判据（V1/V2/V4/V6）的命中**流式聚合成段**，没有条数上限（理由见
    /// <see cref="IssueSegment"/>）。一次全表扫描出四类——分四遍扫是四倍的钱。
    /// </summary>
    public List<IssueSegment> RowIssueSegments(DateTime cutoff, DateTime? since = null)
    {
        var acc = new Dictionary<(string Code, string Gran, string Kind), (DateTime Min, DateTime Max, int N)>();
        foreach (var i in ReadRowIssues(cutoff, since, null))
        {
            var key = (i.Code, i.Granularity, i.Kind);
            if (acc.TryGetValue(key, out var cur))
                acc[key] = (i.Day < cur.Min ? i.Day : cur.Min, i.Day > cur.Max ? i.Day : cur.Max, cur.N + 1);
            else
                acc[key] = (i.Day, i.Day, 1);
        }
        return acc
            .Select(kv => new IssueSegment(kv.Key.Code, kv.Key.Gran, kv.Key.Kind,
                                           kv.Value.Min, kv.Value.Max, kv.Value.N))
            .OrderBy(x => x.Code, StringComparer.Ordinal)
            .ThenBy(x => x.Granularity, StringComparer.Ordinal)
            .ThenBy(x => x.Kind, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>拼 <c>AND code IN ($c0,$c1,…)</c>。空/null 就不加这一条（＝全库）。
    /// 一批最多 500 段（AuditBatchSize），加上其它参数远低于 SQLite 的变量上限。</summary>
    private static string CodeFilter(IReadOnlyCollection<string>? codes, string column) =>
        codes is not { Count: > 0 }
            ? ""
            : $"AND {column} IN (" + string.Join(",", Enumerable.Range(0, codes.Count).Select(i => $"$c{i}")) + ")";

    private static void BindCodes(SqliteCommand cmd, IReadOnlyCollection<string>? codes)
    {
        if (codes is not { Count: > 0 }) return;
        int i = 0;
        foreach (var c in codes) cmd.Parameters.AddWithValue($"$c{i++}", c);
    }

    /// <summary>V5 的统计结果：回测序列跟它的输入（不复权）脱节到什么程度。</summary>
    /// <param name="RawOnlyRows">不复权有、day_adj 没有的行数（＝回测序列还没重算到那里）。</param>
    /// <param name="AdjOnlyRows">day_adj 有、不复权没有的行数（＝不复权那边被删过或换过口径，反常）。</param>
    /// <param name="ValueMismatchRows">两边都有、但量额换手对不上的行数。</param>
    public sealed record AdjSeriesDrift(long RawOnlyRows, long AdjOnlyRows, long ValueMismatchRows);

    /// <summary>
    /// V5：<c>day_adj</c> 与 <c>day_raw</c> 的行集/量额是否脱节。
    ///
    /// day_adj 是本地从 day_raw 算出来的，量额换手是原样搬过去的，所以**正常情况下两边逐行对齐**。
    /// 脱节说明重算落后了（或者不复权那边动过）——这一条只报数，处置是去跑【重算回测序列】，
    /// 不进待补名单（本地算的，抓不来）。
    /// </summary>
    public AdjSeriesDrift AdjVsRawDrift(DateTime cutoff, DateTime? since = null)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH raw AS (
                SELECT code, period_start, volume, amount, turnover FROM Bar
                WHERE granularity = $raw AND period_start <= $cutoff
                  AND ($since IS NULL OR period_start >= $since)
            ), adj AS (
                SELECT code, period_start, volume, amount, turnover FROM Bar
                WHERE granularity = $adj AND period_start <= $cutoff
                  AND ($since IS NULL OR period_start >= $since)
            )
            SELECT
              (SELECT COUNT(*) FROM raw LEFT JOIN adj USING (code, period_start) WHERE adj.code IS NULL),
              (SELECT COUNT(*) FROM adj LEFT JOIN raw USING (code, period_start) WHERE raw.code IS NULL),
              (SELECT COUNT(*) FROM raw JOIN adj USING (code, period_start)
                WHERE ABS(COALESCE(raw.volume, 0)   - COALESCE(adj.volume, 0))   > 1e-6 * MAX(ABS(COALESCE(raw.volume, 0)), 1)
                   OR ABS(COALESCE(raw.amount, 0)   - COALESCE(adj.amount, 0))   > 1e-6 * MAX(ABS(COALESCE(raw.amount, 0)), 1)
                   OR ABS(COALESCE(raw.turnover, 0) - COALESCE(adj.turnover, 0)) > 1e-6 * MAX(ABS(COALESCE(raw.turnover, 0)), 1));
            """;
        cmd.Parameters.AddWithValue("$raw", Granularity.DayRaw);
        cmd.Parameters.AddWithValue("$adj", Granularity.DayAdj);
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString(DateFormat));
        cmd.Parameters.AddWithValue("$since", (object?)since?.ToString(DateFormat) ?? DBNull.Value);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new AdjSeriesDrift(0, 0, 0);
        return new AdjSeriesDrift(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
    }
}
