using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

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

    /// <summary>每个代码本地最早的 period_start（一次查询返回全部代码，2026-07-29新增）——给"拉取指定
    /// 年份"（<see cref="Orchestration.FetchOrchestrator.RunFetchYearAsync"/>）决定每只标的在那一年里要
    /// 补哪一段：最早日已经在目标年之前=那年本地已有（增量抓取保证历史是连续的），直接跳过不发请求；
    /// 最早日落在目标年内=只补"年初→最早日前一天"这段缺口；最早日在目标年之后=整年都缺、抓一整年。
    /// 故意不做成逐个代码查（全市场5000+只，逐个查会有5000+次往返），而是一次 GROUP BY 全拿回来。
    /// 只加在具体实现上、没进 <see cref="Logic.Abstractions.IBarRepository"/> 接口——这是抓取端专用的
    /// 批量查询，Analyzer 侧的 CutoffBarRepository 等实现不需要跟着实现它。</summary>
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

    public List<Bar> Query(string code, string granularity, DateTime? start = null, DateTime? end = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, granularity, period_start, open, close, high, low, volume, amount, turnover, fetched_at
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
        while (reader.Read())
        {
            result.Add(new Bar
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
            });
        }
        return result;
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
    /// 只认6位纯数字代码（跟 <see cref="GetAllCodes"/> 同一条线）——指数/ETF/板块合成各有自己的
    /// 抓取路径和覆盖情况，不该混在个股这份名单里。
    /// </summary>
    public List<string> GetCodesMissingDay(string granularity, DateTime latest, DateTime previous)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT b.code FROM Bar b
            WHERE b.granularity = $g AND b.period_start = $prev
              AND b.code GLOB '[0-9][0-9][0-9][0-9][0-9][0-9]'
              AND NOT EXISTS (
                    SELECT 1 FROM Bar x
                    WHERE x.code = b.code AND x.granularity = $g AND x.period_start = $latest)
            ORDER BY b.code;
            """;
        cmd.Parameters.AddWithValue("$g", granularity);
        cmd.Parameters.AddWithValue("$latest", latest.ToString(DateFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$prev", previous.ToString(DateFormat, CultureInfo.InvariantCulture));

        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>日线里还有成交额缺失（amount=0）的代码及其缺失区间——"回填成交额/换手率"
    /// （见 FetchOrchestrator.RunBackfillAmountTurnoverAsync）用它决定每个代码要重抓哪段日期。
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
    public int UpdateDayAmountTurnover(IEnumerable<Bar> bars)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE Bar SET amount = $amount, turnover = $turnover
            WHERE code = $code AND granularity = 'day' AND period_start = $period_start AND amount = 0;
            """;
        var pAmount = cmd.CreateParameter(); pAmount.ParameterName = "$amount"; cmd.Parameters.Add(pAmount);
        var pTurnover = cmd.CreateParameter(); pTurnover.ParameterName = "$turnover"; cmd.Parameters.Add(pTurnover);
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pStart = cmd.CreateParameter(); pStart.ParameterName = "$period_start"; cmd.Parameters.Add(pStart);

        int updated = 0;
        foreach (var bar in bars)
        {
            if (bar.Amount == 0) continue; // 数据源没给成交额（比如新浪），写0没意义，留给下次回填
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
