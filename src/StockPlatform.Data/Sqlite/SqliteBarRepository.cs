using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Sqlite;

public class SqliteBarRepository : IBarRepository
{
    private const string DateFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteBarRepository(string dbFilePath)
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

    /// <summary>
    /// 写入K线：库里没有的插入；**已经"收盘后确认"过的行永不覆盖**（历史行的OHLC是当年抓取时的
    /// 复权基准，重抓同一天可能因其间除权而整体平移，覆盖会造成同一序列里新旧基准混杂）。
    ///
    /// 唯一的例外是**盘中抓的行**（<c>fetched_at</c> 早于它自己那天 16:00，见
    /// <c>FetchOrchestrator.IsConfirmedFinal</c>）：那种行的 OHLC 是当时的瞬时价、量额换手是半天
    /// 累计值，本来就不是最终数据，后来抓到的更晚数据一律覆盖它。
    ///
    /// ⚠ 2026-09-09 加这个例外之前的后果：2026-09-01 早上 09:25~10:10 跑【不复权首次整段回补】，
    /// 4020 只票的当天K线被写成"开盘半小时"的快照（002650 四价合一 6.04、成交量 23 手），
    /// day_adj 原样继承 1555 行；而水位线只在"最新那根就是今天"时才判确认，跨过午夜就再也不回头，
    /// 于是那批错值永久固化。同一个坑在 2026-07-16 11:25 也吃过一次（1330 个指数/ETF，含上证指数）。
    /// </summary>
    public void InsertOrRefreshUnconfirmed(IEnumerable<Bar> bars)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Bar
                (code, granularity, period_start, open, close, high, low, volume, amount, turnover, fetched_at)
            VALUES
                ($code, $granularity, $period_start, $open, $close, $high, $low, $volume, $amount, $turnover, $fetched_at)
            ON CONFLICT(code, granularity, period_start) DO UPDATE SET
                open = excluded.open, close = excluded.close, high = excluded.high, low = excluded.low,
                volume = excluded.volume, amount = excluded.amount, turnover = excluded.turnover,
                fetched_at = excluded.fetched_at
            WHERE Bar.fetched_at IS NOT NULL
              AND Bar.fetched_at < datetime(Bar.period_start, '+16 hours')
              AND excluded.fetched_at > Bar.fetched_at;
            """;
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pGran = cmd.CreateParameter(); pGran.ParameterName = "$granularity"; cmd.Parameters.Add(pGran);
        var pStart = cmd.CreateParameter(); pStart.ParameterName = "$period_start"; cmd.Parameters.Add(pStart);
        var pOpen = cmd.CreateParameter(); pOpen.ParameterName = "$open"; cmd.Parameters.Add(pOpen);
        var pClose = cmd.CreateParameter(); pClose.ParameterName = "$close"; cmd.Parameters.Add(pClose);
        var pHigh = cmd.CreateParameter(); pHigh.ParameterName = "$high"; cmd.Parameters.Add(pHigh);
        var pLow = cmd.CreateParameter(); pLow.ParameterName = "$low"; cmd.Parameters.Add(pLow);
        var pVolume = cmd.CreateParameter(); pVolume.ParameterName = "$volume"; cmd.Parameters.Add(pVolume);
        var pAmount = cmd.CreateParameter(); pAmount.ParameterName = "$amount"; cmd.Parameters.Add(pAmount);
        var pTurnover = cmd.CreateParameter(); pTurnover.ParameterName = "$turnover"; cmd.Parameters.Add(pTurnover);
        var pFetchedAt = cmd.CreateParameter(); pFetchedAt.ParameterName = "$fetched_at"; cmd.Parameters.Add(pFetchedAt);

        foreach (var bar in bars)
        {
            pCode.Value = bar.Code;
            pGran.Value = bar.Granularity;
            pStart.Value = bar.PeriodStart.ToString(DateFormat, CultureInfo.InvariantCulture);
            pOpen.Value = bar.Open;
            pClose.Value = bar.Close;
            pHigh.Value = bar.High;
            pLow.Value = bar.Low;
            pVolume.Value = bar.Volume;
            pAmount.Value = bar.Amount;
            pTurnover.Value = bar.Turnover;
            pFetchedAt.Value = bar.FetchedAt.ToString(DateFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>删掉某个 code 在某粒度下的全部bar——本地合成板块指数(BoardIndexSynthesizer)重算前
    /// 先清旧值再写新值用（成分股/数据会变，不能用 INSERT OR IGNORE 累积）。个股/指数抓取不用它
    /// （那些走水位线增量）。</summary>
    public int DeleteByCode(string code, string granularity)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Bar WHERE code = $code AND granularity = $granularity;";
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$granularity", granularity);
        return cmd.ExecuteNonQuery();
    }

    public DateTime? GetLatestPeriodStart(string code, string granularity)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(period_start) FROM Bar WHERE code = $code AND granularity = $granularity;";
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$granularity", granularity);
        var result = cmd.ExecuteScalar();
        if (result == null || result is DBNull) return null;
        return DateTime.ParseExact((string)result, DateFormat, CultureInfo.InvariantCulture);
    }

    public DateTime? GetOverallLatestPeriodStart(string granularity)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(period_start) FROM Bar WHERE granularity = $granularity;";
        cmd.Parameters.AddWithValue("$granularity", granularity);
        var result = cmd.ExecuteScalar();
        if (result == null || result is DBNull) return null;
        return DateTime.ParseExact((string)result, DateFormat, CultureInfo.InvariantCulture);
    }

    public DateTime? GetOverallLatestPeriodStartOnOrBefore(string granularity, DateTime cutoff)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(period_start) FROM Bar WHERE granularity = $granularity AND period_start <= $cutoff;";
        cmd.Parameters.AddWithValue("$granularity", granularity);
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString(DateFormat, CultureInfo.InvariantCulture));
        var result = cmd.ExecuteScalar();
        if (result == null || result is DBNull) return null;
        return DateTime.ParseExact((string)result, DateFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>一次查询同时拿到全库最早+最晚的 period_start（2026-08-04新增）——给 Fetcher 的
    /// "本地数据覆盖范围：X 至 Y"用。原来是分别调 <see cref="GetOverallEarliestPeriodStart"/> 和
    /// <see cref="GetOverallLatestPeriodStart"/>，那是**两次**全索引扫描：Bar 的主键是
    /// (code, granularity, period_start)，前导列不是 granularity，MIN/MAX 都用不上有序性，只能把整个
    /// 主键覆盖索引扫完。库 7GB 时实测分两次约 5.2 秒、合成一次约 3.1 秒（省 41%），一次扫描就够。</summary>
    public (DateTime? Earliest, DateTime? Latest) GetOverallPeriodStartRange(string granularity)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(period_start), MAX(period_start) FROM Bar WHERE granularity = $granularity;";
        cmd.Parameters.AddWithValue("$granularity", granularity);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (null, null);
        DateTime? Parse(int i) => reader.IsDBNull(i)
            ? null
            : DateTime.ParseExact(reader.GetString(i), DateFormat, CultureInfo.InvariantCulture);
        return (Parse(0), Parse(1));
    }

    public DateTime? GetOverallEarliestPeriodStart(string granularity)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(period_start) FROM Bar WHERE granularity = $granularity;";
        cmd.Parameters.AddWithValue("$granularity", granularity);
        var result = cmd.ExecuteScalar();
        if (result == null || result is DBNull) return null;
        return DateTime.ParseExact((string)result, DateFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// **单个**代码本地最早的 period_start（2026-09-10 新增）。
    ///
    /// 跟下面那个"一次返回全部代码"的批量版分工明确：批量版是 GROUP BY 全表，2540 万行的库上
    /// 要几十秒，只在"拉取区间数据"那种要遍历全市场的场合值得；这里查的是几条指数，走主键前缀
    /// (code, granularity, period_start) 是毫秒级，拿批量版来查九条纯属浪费。
    ///
    /// 用途：【指数日K·首次整段回补】跑完之后报告"每条指数补到哪年了"。
    /// 同样只加在具体实现上、不进 <see cref="Logic.Abstractions.IBarRepository"/> 接口。
    /// </summary>
    public DateTime? GetEarliestPeriodStart(string code, string granularity)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(period_start) FROM Bar WHERE code = $code AND granularity = $granularity;";
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$granularity", granularity);
        var result = cmd.ExecuteScalar();
        if (result == null || result is DBNull) return null;
        return DateTime.ParseExact((string)result, DateFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>每个代码本地最早的 period_start（一次查询返回全部代码，2026-07-29新增）——给"拉取指定
    /// 年份"（【拉取区间数据】的整段回补）决定每只标的在那一年里要
    /// 补哪一段：最早日已经在目标年之前=那年本地已有（增量抓取保证历史是连续的），直接跳过不发请求；
    /// 最早日落在目标年内=只补"年初→最早日前一天"这段缺口；最早日在目标年之后=整年都缺、抓一整年。
    /// 故意不做成逐个代码查（全市场5000+只，逐个查会有5000+次往返），而是一次 GROUP BY 全拿回来。
    /// 只加在具体实现上、没进 <see cref="Logic.Abstractions.IBarRepository"/> 接口——这是抓取端专用的
    /// 批量查询，Analyzer 侧的 CutoffBarRepository 等实现不需要跟着实现它。</summary>
    /// <summary>
    /// 某一段窗口内、每只标的有多少根K线（2026-09-11 加，闭区间）。
    ///
    /// 用途：分档资金流的排队判据拿它当**期望行数**——有K线的那天就该有资金流。
    /// 固定门槛（"不足 100 行就算没补齐"）对上市不足 100 个交易日的票永远不可达，
    /// 那些票会每轮重抓、待办数永不归零；而真正缺了一整天的老票行数远超门槛，反倒没人管。
    /// 见 <see cref="Orchestration.MoneyFlowBackfillPlan"/>。
    ///
    /// 一次 GROUP BY 走 (code, granularity, period_start) 主键的范围扫描，
    /// 本机 7.5GB 库上实测 0.3 秒。
    /// </summary>
    public Dictionary<string, int> CountByCodeBetween(string granularity, DateTime from, DateTime to)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT code, COUNT(*) FROM Bar WHERE granularity = $granularity "
          + "AND period_start >= $from AND period_start <= $to GROUP BY code;";
        cmd.Parameters.AddWithValue("$granularity", granularity);
        cmd.Parameters.AddWithValue("$from", from.ToString(DateFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$to", to.ToString(DateFormat, CultureInfo.InvariantCulture));
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    public Dictionary<string, DateTime> GetEarliestPeriodStartByCode(string granularity)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, MIN(period_start) FROM Bar WHERE granularity = $granularity GROUP BY code;";
        cmd.Parameters.AddWithValue("$granularity", granularity);
        var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1)) continue;
            result[reader.GetString(0)] = DateTime.ParseExact(reader.GetString(1), DateFormat, CultureInfo.InvariantCulture);
        }
        return result;
    }

    /// <summary>一次取回所有代码的**最后**一根K线日期（对应 <see cref="GetEarliestPeriodStartByCode"/>）——
    /// 逐只查 5000+ 次往返太慢时用。目前给"拉取全部"的退市股收尾用：判断某只退市股本地是否还缺最后几天。</summary>
    public Dictionary<string, DateTime> GetLatestPeriodStartByCode(string granularity)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, MAX(period_start) FROM Bar WHERE granularity = $granularity GROUP BY code;";
        cmd.Parameters.AddWithValue("$granularity", granularity);
        var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1)) continue;
            result[reader.GetString(0)] = DateTime.ParseExact(reader.GetString(1), DateFormat, CultureInfo.InvariantCulture);
        }
        return result;
    }

    /// <summary>一次取回某个粒度下**全市场**出现过的所有K线日期（去重、升序）——给交易日历用。
    /// 为什么不用单只指数的序列当日历：那只票自己缺哪一段，日历就瞎哪一段。2026-09-06 踩过——
    /// 拿上证指数的 day 序列当日历，而它自己只有 2016-01-04 起，结果【拉取区间数据 1990~2016】
    /// 把 2,360 只最该补历史的老股判成"缺口里没有交易日"全部静默跳过
    /// （见 <see cref="Logic.Services.TradingCalendar"/>）。全市场并集就不会有这个盲区。
    /// 走 ix_bar_gran_date(granularity, period_start) 索引扫描。</summary>
    public List<DateTime> GetDistinctPeriodStarts(string granularity)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT period_start FROM Bar WHERE granularity = $granularity ORDER BY period_start;";
        cmd.Parameters.AddWithValue("$granularity", granularity);
        var result = new List<DateTime>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            result.Add(DateTime.ParseExact(reader.GetString(0), DateFormat, CultureInfo.InvariantCulture));
        }
        return result;
    }

    /// <summary>
    /// 取K线。<b>周线/月线不落库了（2026-09-10），这里从日线现场聚合。</b>
    ///
    /// ════ 为什么不存 ════
    /// 它们 100% 是本地从日线算出来的（<see cref="BarAggregator"/>），一个字节都不是抓来的，
    /// 却占了 Bar 表 20%（week 414 万行 + month 100 万行）。代价不只是空间：
    ///   · 同一个数据错误要在**六个口径**上分别修——2026-09-10 的成交量单位事故就是这么放大的；
    ///   · 日线更新到周月线重算之间永远有个不一致窗口；
    ///   · 每只票抓完日线都要"读全历史 + 聚合 + 两次 upsert"，全市场 5500 只是笔不小的开销。
    /// 而现算的成本几乎为零：一次遍历 8000 行日线，纯 CPU、零额外 IO。
    ///
    /// ⚠ <b>先聚合、再按 start/end 过滤，顺序不能反</b>：先截断日线再聚合的话，
    /// 区间边界那一周/月只会用到落在区间内的那几天，算出来的开盘价、最高最低、成交量全是错的
    /// ——而且错得很像真的，图上看不出来。
    /// </summary>
    public List<Bar> Query(string code, string granularity, DateTime? start = null, DateTime? end = null)
    {
        if (granularity is Granularity.Week or Granularity.Month)
        {
            var days = QueryStored(code, Granularity.Day, null, null);
            if (days.Count == 0) return [];
            var aggregated = granularity == Granularity.Week
                ? BarAggregator.ToWeekly(days)
                : BarAggregator.ToMonthly(days);
            if (start == null && end == null) return aggregated;
            return aggregated
                .Where(b => (start == null || b.PeriodStart >= start) && (end == null || b.PeriodStart <= end))
                .ToList();
        }
        return QueryStored(code, granularity, start, end);
    }

    /// <summary>
    /// <see cref="ReadBar"/> 依赖的列序。读 Bar 的 SELECT 都拼这个常量，别各写各的：
    /// 列序是靠位置下标读的，某处手写的顺序跟这里差一列，读出来的就是张冠李戴的数字，
    /// 而且看上去完全正常。
    /// </summary>
    private const string BarColumns =
        "code, granularity, period_start, open, close, high, low, volume, amount, turnover, fetched_at";

    /// <summary>真正读表的那一半（<see cref="Query"/> 对周/月线会绕开它）。</summary>
    /// <inheritdoc cref="IBarRepository.QueryForAppend"/>
    public List<Bar> QueryForAppend(string code, string granularity, DateTime from)
    {
        if (granularity is Granularity.Week or Granularity.Month)
            throw new ArgumentException("周/月线不落库、由日线现算，这个方法只服务落库的粒度", nameof(granularity));

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 两段并起来：① from 之前的**最后一根**（算第一天涨幅的基准，可能因停牌远在几百天前）
        //             ② from 及以后的全部
        // 走的都是主键 (code, granularity, period_start) 的有序前缀，是索引定位不是扫表。
        //
        // ⚠ 第一段必须**套在子查询里**：SQLite 不允许 UNION ALL 的分支自带 ORDER BY/LIMIT
        //   （"ORDER BY clause should come after UNION ALL not before"）。末尾那个 ORDER BY
        //   是作用在整个复合结果上的，合法，两段合起来天然有序。
        cmd.CommandText = $"""
            SELECT * FROM (
                SELECT {BarColumns} FROM Bar
                WHERE code = $code AND granularity = $granularity AND period_start < $from
                ORDER BY period_start DESC LIMIT 1
            )
            UNION ALL
            SELECT {BarColumns} FROM Bar
            WHERE code = $code AND granularity = $granularity AND period_start >= $from
            ORDER BY period_start;
            """;
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$granularity", granularity);
        cmd.Parameters.AddWithValue("$from", from.ToString(DateFormat, CultureInfo.InvariantCulture));

        var result = new List<Bar>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(ReadBar(reader));
        return result;
    }

    private List<Bar> QueryStored(string code, string granularity, DateTime? start, DateTime? end)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {BarColumns}
            FROM Bar
            WHERE code = $code AND granularity = $granularity
              AND ($start IS NULL OR period_start >= $start)
              AND ($end IS NULL OR period_start <= $end)
            ORDER BY period_start;
            """;
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$granularity", granularity);
        cmd.Parameters.AddWithValue("$start", (object?)start?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$end", (object?)end?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? DBNull.Value);

        var result = new List<Bar>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(ReadBar(reader));
        return result;
    }

    /// <summary>
    /// 把一行读成 <see cref="Bar"/>。列序固定为 <see cref="BarColumns"/>，
    /// <see cref="QueryStored"/> 和 <see cref="GetLatestBar"/> 共用——两处各写一遍的话，
    /// 加一列时漏改一处就是静默的错值。
    /// </summary>
    private static Bar ReadBar(Microsoft.Data.Sqlite.SqliteDataReader reader)
        => new()
            {
                Code = reader.GetString(0),
                Granularity = reader.GetString(1),
                PeriodStart = DateTime.ParseExact(reader.GetString(2), DateFormat, CultureInfo.InvariantCulture),
                Open = reader.GetDouble(3),
                Close = reader.GetDouble(4),
                High = reader.GetDouble(5),
                Low = reader.GetDouble(6),
                // 量/额/换手三列都做 NULL 兜底（2026-09-09 加）：写入路径向来写 0 而不是 NULL，
                // 但**批量导入**能绕过它们——东财终端日线不含换手率，那次导入让 day_raw 2015 年
                // 及以前 702 万行的 turnover 整列为 NULL，这里原来是 GetDouble(9) 硬读，于是
                // 【重算回测序列】对 2886 只票每只都抛 "data is NULL at ordinal 9"，界面上
                // "待重算 2886 只"永不下降、潜伏了三天没人发现。数据已就地修好，兜底留着防下一次。
                Volume = reader.IsDBNull(7) ? 0 : reader.GetDouble(7),
                Amount = reader.IsDBNull(8) ? 0 : reader.GetDouble(8),
                Turnover = reader.IsDBNull(9) ? 0 : reader.GetDouble(9),
                // 老数据（这个字段2026-07-09之前没有）读出来是DBNull——用MinValue兜底，永远判定为
                // "未确认最终"，直到这一天被重新抓到一次为止（只影响"今天"这一天的判断，更早的
                // 历史天数不会因为FetchedAt是MinValue而被误判成需要重新抓——见FetchOrchestrator
                // 的水位线逻辑，只有period_start等于当前日期时才会去看FetchedAt）。
                FetchedAt = reader.IsDBNull(10) ? DateTime.MinValue : DateTime.ParseExact(reader.GetString(10), DateFormat, CultureInfo.InvariantCulture),
            };

    /// <summary>
    /// 这只票某个粒度下**最后一根**K线，没有就返回 null。
    ///
    /// 为什么要它而不是 <c>Query(code, gran)[^1]</c>（2026-09-17）：只想知道"最新收盘价是多少、
    /// 哪一天"的调用方（分析详情窗口的估值、行情面板的现价）本来要把全历史读出来才能取到末行，
    /// 老股 5000+ 行、每行还要 ParseExact 两次日期，全花在马上就丢掉的对象上。
    /// 这里走 <c>ORDER BY period_start DESC LIMIT 1</c>，命中主键 (code, granularity, period_start)
    /// 的索引尾端，只读一行。
    ///
    /// ⚠ 只对**存着的**粒度成立。周/月线现在是从日线现算的（见 <see cref="Query"/>），
    /// 传 week/month 进来会查到空表——所以这里挡住，调用方要末根周线就自己 Query 后取末尾。
    /// </summary>
    public Bar? GetLatestBar(string code, string granularity)
    {
        if (granularity is Granularity.Week or Granularity.Month)
            throw new ArgumentException(
                "周/月线不落库、由日线现算，取末根请走 Query()", nameof(granularity));

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {BarColumns}
            FROM Bar
            WHERE code = $code AND granularity = $granularity
            ORDER BY period_start DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$granularity", granularity);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadBar(reader) : null;
    }

    /// <summary>最新一行的日期+实际抓取时间，一次查询同时拿到两者（比先GetLatestPeriodStart再
    /// 单独查一次fetched_at少一次往返）——FetchOrchestrator用它判断"今天"这一天是不是已经收盘后
    /// 确认过了，不用再发请求。</summary>
    public (DateTime PeriodStart, DateTime FetchedAt)? GetLatestBarInfo(string code, string granularity)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT period_start, fetched_at FROM Bar
            WHERE code = $code AND granularity = $granularity
            ORDER BY period_start DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$granularity", granularity);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var periodStart = DateTime.ParseExact(reader.GetString(0), DateFormat, CultureInfo.InvariantCulture);
        var fetchedAt = reader.IsDBNull(1) ? DateTime.MinValue : DateTime.ParseExact(reader.GetString(1), DateFormat, CultureInfo.InvariantCulture);
        return (periodStart, fetchedAt);
    }

    public List<string> GetAllCodes()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 只返回6位纯数字的个股代码——大盘指数用带前缀的8位符号存（"sh000001"，见
        // MarketIndexCatalog），必须挡在这里：这个方法是Analyzer各选股Tab的扫描全集，
        // 指数混进去会被当成个股跑筛选规则、出现在选股结果里。
        cmd.CommandText = "SELECT DISTINCT code FROM Bar WHERE code GLOB '[0-9][0-9][0-9][0-9][0-9][0-9]' ORDER BY code;";
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>
    /// "上一个交易日有这一根、最新交易日却没有"的个股代码（2026-08-21新增，给
    /// FetchOrchestrator.CheckLatestDayCoverage 做一轮抓完之后的体检用）。
    ///
    /// 为什么要用"上一个交易日有"来过滤，而不是直接拿全部代码去减：长期停牌股、早已退市的票，
    /// 本地最后一根可能停在几个月前，它们缺最新交易日是**正常的**，混进名单只会让重试永远做无用功。
    /// 只有"昨天还在交易、今天却没有"的才是真正值得重试的漏抓。
    ///
    /// 挑哪些标的看 <paramref name="type"/>（<c>StockMeta.type</c>）。
    ///
    /// ⚠ **2026-09-16 从"六位纯数字代码"换成按类型过滤**，这是个真漏洞：ETF 和指数在本地是
    /// **带前缀存**的（<c>sh510300</c>、<c>sh000001</c>），`GLOB '[0-9]{6}'` 那条把它们全挡在外面，
    /// 于是 1,665 只 ETF 和 9 条指数**当天整批没抓到也不会有任何告警**，体检照样打印
    /// "当天的个股日线是齐的"。教训跟"市场前缀走 MarketClassifier"是同一条：
    /// **别靠代码字面猜标的类型**。
    ///
    /// 顺带收紧的一处：退市股（<c>type='delisted'</c>）原来也是六位数字、混在个股名单里，
    /// 现在按类型天然分开了——它们的尾巴归【退市股收尾】，重试名单里出现只会做无用功。
    /// </summary>
    /// <param name="type"><c>StockMeta.type</c>：stock / etf / index。老行没有这一列，按 stock 算。</param>
    public List<string> GetCodesMissingDay(string granularity, DateTime latest, DateTime previous,
                                           string type = SqliteStockMetaUpsert.TypeStock)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT b.code FROM Bar b
            JOIN StockMeta m ON m.code = b.code
            WHERE b.granularity = $g AND b.period_start = $prev
              AND COALESCE(m.type, 'stock') = $type
              AND NOT EXISTS (
                    SELECT 1 FROM Bar x
                    WHERE x.code = b.code AND x.granularity = $g AND x.period_start = $latest)
            ORDER BY b.code;
            """;
        cmd.Parameters.AddWithValue("$g", granularity);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$latest", latest.ToString(DateFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$prev", previous.ToString(DateFormat, CultureInfo.InvariantCulture));

        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>日线里还有成交额缺失（amount=0）的代码及其缺失区间——用它决定每个代码要重抓哪段日期。
    /// ⚠ **眼下没有调用方**：原来的"回填成交额/换手率"（FetchOrchestrator.RunBackfillAmountTurnoverAsync）
    /// 是 2026-07-13 的一次性修复，跑完就没人调了，方法本体删于 2026-09-23。这个判据留着是因为
    /// 它比方法值钱——真要再补一次，照它写一个新式任务即可。
    /// 判定只看 amount：turnover 跟着同一次UPDATE顺带补，某些标的（如B股）接口天生不给换手率，
    /// 如果把 turnover=0 也算"缺失"，这些行会永远补不满、每次回填都白白重抓一遍。2026-07-10
    /// 之前入库的历史行两个字段都是0（老 fqkline 接口不带这两个字段），是回填的主要目标。</summary>
    public List<(string Code, DateTime Min, DateTime Max, int Count)> GetDayCodesWithMissingAmount()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, MIN(period_start), MAX(period_start), COUNT(*)
            FROM Bar WHERE granularity = 'day' AND amount = 0
            GROUP BY code ORDER BY code;
            """;
        var result = new List<(string, DateTime, DateTime, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((
                reader.GetString(0),
                DateTime.ParseExact(reader.GetString(1), DateFormat, CultureInfo.InvariantCulture),
                DateTime.ParseExact(reader.GetString(2), DateFormat, CultureInfo.InvariantCulture),
                reader.GetInt32(3)));
        }
        return result;
    }

    /// <summary>只回填日线行的成交额/换手率两列，其余列一律不动——历史行的OHLC是当年抓取时的
    /// 前复权基准，现在重抓同一天的前复权价可能因为其间的分红除权而整体平移过，覆盖会造成同一只
    /// 股票序列里新旧复权基准混杂；而成交额/换手率是不受复权影响的原始事实，单独更新是安全的。
    /// 只更新 amount=0 的行（回填语义——已经有值的行不碰），抓回来仍是0的行直接跳过不发UPDATE。
    /// 返回实际更新的行数。</summary>
    public int UpdateDayAmountTurnover(IEnumerable<Bar> bars) =>
        UpdateVolumeAmountTurnover(bars, Granularity.Day, onlyWhenAmountZero: true);

    /// <summary>
    /// 只更新 <c>volume</c>/<c>amount</c>/<c>turnover</c> 三列，**其余列一律不动**——这是
    /// <see cref="UpdateDayAmountTurnover"/> 的通用版（2026-09-09 扩：任意口径 + 可选择是否只填空）。
    ///
    /// 为什么单独更新这三列是安全的、而价格不行：这三列**不受复权影响**（实测腾讯 qfq 与不复权
    /// 640 天 0 差异），任何时候重抓都是同一个值；而历史行的 OHLC 是当年抓取时的复权基准，
    /// 重抓可能因其间除权而整体平移，覆盖会造成同一序列里新旧基准混杂。
    ///
    /// 两个用法：
    /// · <paramref name="onlyWhenAmountZero"/>=true —— 回填老行（2026-07-10 换接口前入库的日线
    ///   amount/turnover 全是 0）。已经有值的行不碰；抓回来仍是 0 的（新浪不给成交额）直接跳过。
    /// · false —— 修【全库数据体检】V3 报出的"多口径量额对不上"：以数据源当前值为准覆盖，
    ///   连 volume 一起（2026-09-01 那批盘中行差的就是 volume，23 手 vs 20953 手）。
    /// </summary>
    /// <returns>实际更新的行数。</returns>
    public int UpdateVolumeAmountTurnover(
        IEnumerable<Bar> bars, string granularity, bool onlyWhenAmountZero = false)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            UPDATE Bar SET volume = $volume, amount = $amount, turnover = $turnover
            WHERE code = $code AND granularity = $granularity AND period_start = $period_start
            {(onlyWhenAmountZero ? "AND amount = 0" : "")};
            """;
        var pVolume = cmd.CreateParameter(); pVolume.ParameterName = "$volume"; cmd.Parameters.Add(pVolume);
        var pAmount = cmd.CreateParameter(); pAmount.ParameterName = "$amount"; cmd.Parameters.Add(pAmount);
        var pTurnover = cmd.CreateParameter(); pTurnover.ParameterName = "$turnover"; cmd.Parameters.Add(pTurnover);
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pGran = cmd.CreateParameter(); pGran.ParameterName = "$granularity"; cmd.Parameters.Add(pGran);
        var pStart = cmd.CreateParameter(); pStart.ParameterName = "$period_start"; cmd.Parameters.Add(pStart);
        pGran.Value = granularity;

        int updated = 0;
        foreach (var bar in bars)
        {
            if (onlyWhenAmountZero && bar.Amount == 0) continue;
            pVolume.Value = bar.Volume;
            pAmount.Value = bar.Amount;
            pTurnover.Value = bar.Turnover;
            pCode.Value = bar.Code;
            pStart.Value = bar.PeriodStart.ToString(DateFormat, CultureInfo.InvariantCulture);
            updated += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return updated;
    }
}
