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

    public void InsertOrIgnore(IEnumerable<Bar> bars)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO Bar
                (code, granularity, period_start, open, close, high, low, volume, amount, turnover, fetched_at)
            VALUES
                ($code, $granularity, $period_start, $open, $close, $high, $low, $volume, $amount, $turnover, $fetched_at);
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
                Volume = reader.GetDouble(7),
                Amount = reader.GetDouble(8),
                Turnover = reader.GetDouble(9),
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
