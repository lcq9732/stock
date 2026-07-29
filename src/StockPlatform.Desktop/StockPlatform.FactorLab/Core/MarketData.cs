using Microsoft.Data.Sqlite;

namespace StockPlatform.FactorLab.Core;

/// <summary>
/// 把评估需要的全部数据一次性读进内存并按 [股票][交易日] 对齐成矩阵（缺数据为 NaN）。
/// 只读打开数据库，绝不写入。日线是腾讯 qfq 前复权口径（收益率用价格比值，除权日附近正确）。
/// </summary>
public sealed class MarketData
{
    public required List<DateOnly> Dates { get; init; }          // 交易日历（上证指数日线，升序）
    public required Dictionary<DateOnly, int> DateIndex { get; init; }
    public required List<string> Codes { get; init; }            // 6位A股代码（00/30/60/68）
    public required string[] Names { get; init; }
    public required bool[] IsSt { get; init; }                   // 按当前名称判断（局限：无历史更名）
    public required int[] FirstBarIdx { get; init; }             // 每只股票首个有日线的日下标（-1=无数据）
    public required double[][] Open { get; init; }
    public required double[][] Close { get; init; }
    public required double[][] High { get; init; }
    public required double[][] Low { get; init; }
    public required double[][] Amount { get; init; }
    public required double[][] Turnover { get; init; }
    public required double[][] MarginBalance { get; init; }      // 融资余额（非标的日为 NaN）
    /// <summary>每股票按可用日下标升序的（可用日, 户数环比变化取负）序列。可用日=报告基准日后第一个交易日+披露滞后。</summary>
    public required (int AvailIdx, double Value)[][] HolderChg { get; init; }
    /// <summary>近似最新流通股本 = 最新流通市值 / 最新收盘价（qfq末日=真实价）。无数据为 NaN。</summary>
    public required double[] FloatShares { get; init; }
    /// <summary>行业编号（Board board_type=1，多归属取第一个）。-1=无行业数据（覆盖率约46%，中性化时单独一组）。</summary>
    public required int[] Industry { get; init; }
    /// <summary>上证指数收盘价（按交易日历对齐），用于 MA 择时。</summary>
    public required double[] IndexClose { get; init; }
    /// <summary>当日是否上龙虎榜（同日多条上榜原因只记一次）。</summary>
    public required bool[][] LhbFlag { get; init; }

    public int NDays => Dates.Count;
    public int NStocks => Codes.Count;

    static readonly string[] IncludedPrefixes = ["00", "30", "60", "68"]; // 默认不含北交所920

    public static MarketData Load(string dbPath, Action<string> log)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();

        // 1) 交易日历 + 指数收盘：上证指数日线
        var dates = new List<DateOnly>();
        var indexCloseList = new List<double>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT substr(period_start,1,10), close FROM Bar WHERE code='sh000001' AND granularity='day' ORDER BY 1";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                dates.Add(DateOnly.Parse(r.GetString(0)));
                indexCloseList.Add(r.IsDBNull(1) ? double.NaN : r.GetDouble(1));
            }
        }
        var indexClose = indexCloseList.ToArray();
        var dateIndex = new Dictionary<DateOnly, int>(dates.Count);
        for (int i = 0; i < dates.Count; i++) dateIndex[dates[i]] = i;
        log($"交易日历 {dates.Count} 天：{dates[0]} ~ {dates[^1]}");

        // 2) 股票清单
        var codes = new List<string>();
        var names = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT code, COALESCE(name,'') FROM StockMeta WHERE length(code)=6 ORDER BY code";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var code = r.GetString(0);
                if (!IncludedPrefixes.Any(code.StartsWith)) continue;
                codes.Add(code);
                names.Add(r.GetString(1));
            }
        }
        var codeIndex = new Dictionary<string, int>(codes.Count);
        for (int i = 0; i < codes.Count; i++) codeIndex[codes[i]] = i;
        int nS = codes.Count, nD = dates.Count;
        log($"股票池 {nS} 只（00/30/60/68，不含北交所）");

        double[][] Alloc()
        {
            var m = new double[nS][];
            for (int i = 0; i < nS; i++) { m[i] = new double[nD]; Array.Fill(m[i], double.NaN); }
            return m;
        }
        var open = Alloc(); var close = Alloc(); var high = Alloc(); var low = Alloc();
        var amount = Alloc(); var turnover = Alloc();

        // 3) 全表扫日线
        long bars = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT code, substr(period_start,1,10), open, close, high, low, amount, turnover FROM Bar WHERE granularity='day' AND length(code)=6";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!codeIndex.TryGetValue(r.GetString(0), out int s)) continue;
                if (!DateOnly.TryParse(r.GetString(1), out var d) || !dateIndex.TryGetValue(d, out int t)) continue;
                open[s][t] = r.IsDBNull(2) ? double.NaN : r.GetDouble(2);
                close[s][t] = r.IsDBNull(3) ? double.NaN : r.GetDouble(3);
                high[s][t] = r.IsDBNull(4) ? double.NaN : r.GetDouble(4);
                low[s][t] = r.IsDBNull(5) ? double.NaN : r.GetDouble(5);
                amount[s][t] = r.IsDBNull(6) ? double.NaN : r.GetDouble(6);
                turnover[s][t] = r.IsDBNull(7) ? double.NaN : r.GetDouble(7);
                bars++;
            }
        }
        log($"日线 {bars:N0} 条");

        var firstBar = new int[nS];
        for (int s = 0; s < nS; s++)
        {
            firstBar[s] = -1;
            for (int t = 0; t < nD; t++)
                if (!double.IsNaN(close[s][t])) { firstBar[s] = t; break; }
        }

        // 4) 融资余额
        var margin = Alloc();
        long marginRows = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT code, substr(trade_date,1,10), margin_balance FROM MarginDetail WHERE margin_balance IS NOT NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!codeIndex.TryGetValue(r.GetString(0), out int s)) continue;
                if (!DateOnly.TryParse(r.GetString(1), out var d) || !dateIndex.TryGetValue(d, out int t)) continue;
                margin[s][t] = r.GetDouble(2);
                marginRows++;
            }
        }
        log($"融资余额 {marginRows:N0} 条");

        // 5) 股东户数 → 每股票的（可用日, 环比变化取负）序列。报告基准日无公告日，按滞后估计。
        var holderRaw = new Dictionary<int, List<(DateOnly Date, double Num)>>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT code, report_date, holder_num FROM ShareholderCount WHERE holder_num IS NOT NULL AND holder_num > 0 AND report_date >= '2022-01-01' ORDER BY code, report_date";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!codeIndex.TryGetValue(r.GetString(0), out int s)) continue;
                if (!DateOnly.TryParse(r.GetString(1)[..10], out var d)) continue;
                if (!holderRaw.TryGetValue(s, out var list)) holderRaw[s] = list = [];
                list.Add((d, r.GetInt64(2)));
            }
        }
        var holderChg = new (int, double)[nS][];
        for (int s = 0; s < nS; s++)
        {
            if (!holderRaw.TryGetValue(s, out var list) || list.Count < 2) { holderChg[s] = []; continue; }
            var seq = new List<(int, double)>();
            for (int i = 1; i < list.Count; i++)
            {
                int raw = LowerBound(dates, list[i].Date);
                int avail = list[i].Date < dates[0] ? 0 : Math.Min(raw + Config.HolderLagDays, nD - 1);
                double chg = -(list[i].Num / list[i - 1].Num - 1); // 户数下降(筹码集中)→值为正→预期越大越好
                seq.Add((avail, chg));
            }
            holderChg[s] = seq.OrderBy(x => x.Item1).ToArray();
        }
        log($"股东户数覆盖 {holderRaw.Count} 只");

        // 6) 最新流通市值 → 近似流通股本
        var floatShares = new double[nS];
        Array.Fill(floatShares, double.NaN);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT code, value FROM FundamentalMetric WHERE metric_key='circulating_market_cap' AND value IS NOT NULL AND value > 0 ORDER BY as_of_date";
            using var r = cmd.ExecuteReader();
            while (r.Read()) // 按 as_of_date 升序，同代码后读的覆盖先读的 → 留下最新一期
            {
                if (!codeIndex.TryGetValue(r.GetString(0), out int s)) continue;
                double lastClose = double.NaN;
                for (int t = nD - 1; t >= 0; t--)
                    if (!double.IsNaN(close[s][t])) { lastClose = close[s][t]; break; }
                if (!double.IsNaN(lastClose) && lastClose > 0)
                    floatShares[s] = r.GetDouble(1) / lastClose;
            }
        }
        log($"流通股本近似值覆盖 {floatShares.Count(x => !double.IsNaN(x))} 只");

        // 7) 行业归属（board_type=1；覆盖率约46%，无归属为-1）
        var industry = new int[nS];
        Array.Fill(industry, -1);
        int industryCount = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT bm.stock_code, MIN(bm.board_code) FROM BoardMember bm
                JOIN Board b ON bm.board_code = b.board_code
                WHERE b.board_type = 1 GROUP BY bm.stock_code
                """;
            var boardIds = new Dictionary<string, int>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!codeIndex.TryGetValue(r.GetString(0), out int s)) continue;
                var board = r.GetString(1);
                if (!boardIds.TryGetValue(board, out int id)) boardIds[board] = id = boardIds.Count;
                industry[s] = id;
                industryCount++;
            }
        }
        log($"行业归属覆盖 {industryCount} 只");

        // 8) 龙虎榜上榜标记
        var lhb = new bool[nS][];
        for (int s = 0; s < nS; s++) lhb[s] = new bool[nD];
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT stock_code, substr(trade_date,1,10) FROM Lhb";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!codeIndex.TryGetValue(r.GetString(0), out int s)) continue;
                if (!DateOnly.TryParse(r.GetString(1), out var d) || !dateIndex.TryGetValue(d, out int t)) continue;
                lhb[s][t] = true;
            }
        }

        var isSt = names.Select(n => n.Contains("ST", StringComparison.OrdinalIgnoreCase)).ToArray();

        return new MarketData
        {
            Dates = dates, DateIndex = dateIndex, Codes = codes, Names = names.ToArray(),
            IsSt = isSt, FirstBarIdx = firstBar,
            Open = open, Close = close, High = high, Low = low, Amount = amount, Turnover = turnover,
            MarginBalance = margin, HolderChg = holderChg, FloatShares = floatShares,
            Industry = industry, LhbFlag = lhb, IndexClose = indexClose,
        };
    }

    /// <summary>首个 ≥ target 的日历下标（target 超出末日则返回最后一天）。</summary>
    static int LowerBound(List<DateOnly> dates, DateOnly target)
    {
        int lo = 0, hi = dates.Count - 1;
        if (target > dates[hi]) return hi;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (dates[mid] < target) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
