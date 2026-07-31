using Microsoft.Data.Sqlite;

namespace StockPlatform.FactorLab.Core;

/// <summary>一只股票一个报告期的关键财务科目（单位：元；利润表/现金流为**年内累计**口径，TTM/同比由因子换算）。
/// 缺失科目为 NaN（如银行没有营业成本）。</summary>
public readonly record struct FinQuarter(
    int Year, int Month, int AvailIdx,
    double Revenue, double OperCost, double NetProfit, double NpParent,
    double Assets, double Liab, double EquityParent, double Ocf);

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
    public required bool[] IsSt { get; init; }                   // 按当前名称判断ST/退市标记（局限：无历史更名）
    public required int[] FirstBarIdx { get; init; }             // 每只股票首个有日线的日下标（-1=无数据）
    public required int[] LastBarIdx { get; init; }              // 每只股票最后一根日线的日下标（-1=无数据）
    /// <summary>是否在 DelistedStock 名单中（已终止上市）。退市股不做按名剔除（终止时名称几乎都带
    /// 退/ST，按名剔会把整段历史删掉、幸存者偏差白修了），改为剔除临近退市的最后一段，见 Evaluator。</summary>
    public required bool[] IsDelisted { get; init; }
    public required double[][] Open { get; init; }
    public required double[][] Close { get; init; }
    public required double[][] High { get; init; }
    public required double[][] Low { get; init; }
    public required double[][] Amount { get; init; }
    public required double[][] Turnover { get; init; }
    public required double[][] MarginBalance { get; init; }      // 融资余额（非标的日为 NaN）
    /// <summary>每股票按可用日下标升序的（可用日, 户数环比变化取负）序列。可用日=报告基准日后第一个交易日+披露滞后。</summary>
    public required (int AvailIdx, double Value)[][] HolderChg { get; init; }
    /// <summary>市值折算系数 = 最新流通市值 / 最新收盘价（与价格序列同口径）。乘以某日收盘即得
    /// "按当前股本折算的历史市值"——用后复权序列时这个折算是正确的（比值即真实涨跌），但**忽略了
    /// 期间的股本变动**（增发/送转），窗口越长偏差越大，见因子手册的局限说明。无数据为 NaN。</summary>
    public required double[] FloatShares { get; init; }
    /// <summary>行业编号（Board board_type=1，多归属取第一个）。-1=无行业数据（覆盖率约46%，中性化时单独一组）。</summary>
    public required int[] Industry { get; init; }
    /// <summary>上证指数收盘价（按交易日历对齐），用于 MA 择时。</summary>
    public required double[] IndexClose { get; init; }
    /// <summary>真实价格（前复权日线的收盘，最新日=实际成交价），只用于名单展示，不参与任何计算。</summary>
    public required double[][] DisplayClose { get; init; }
    /// <summary>本次用的是不是后复权数据。false 表示库里还没有 day_hfq、降级用了前复权，
    /// 长周期收益率不可信，报告里必须显著标注。</summary>
    public required bool UsingHfq { get; init; }
    /// <summary>每股票按报告期升序的财务季度记录（基本面因子用）。库里没抓过财报时全为空数组。
    /// AvailIdx=按法定披露截止日（一季报4-30/半年报8-31/三季报10-31/年报次年4-30）折算的可用交易日下标
    /// ——数据源没有公告日，用法定截止日是保守估计（宁可信号晚到、不可前视）。</summary>
    public required FinQuarter[][] Financials { get; init; }
    /// <summary>被物理上限规则剔除的脏K线数量（见 Config.MaxDailyReturn）。</summary>
    public required int DirtyBarsDropped { get; init; }
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
        var amount = Alloc(); var turnover = Alloc(); var displayClose = Alloc();

        // 3) 全表扫日线。**回测用后复权**（Granularity.DayHfq）：数据源的前复权是减法式，十年前的
        //    高分红股复权价接近零甚至为负，收益率完全失真（见 Granularity.DayHfq 注释）。库里没有
        //    后复权时降级用前复权，但会在报告里显著标注、不静默。
        bool usingHfq;
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM Bar WHERE granularity='day_hfq' LIMIT 1";
            usingHfq = Convert.ToInt64(probe.ExecuteScalar() ?? 0L) > 0;
        }
        string priceGran = usingHfq ? "day_hfq" : "day";
        long bars = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT code, substr(period_start,1,10), open, close, high, low, amount, turnover FROM Bar WHERE granularity=$g AND length(code)=6";
            cmd.Parameters.AddWithValue("$g", priceGran);
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
        log(usingHfq ? $"后复权日线 {bars:N0} 条（回测用）" : $"⚠ 库里没有后复权日线，降级用前复权 {bars:N0} 条——长周期收益率不可信，请在 Fetcher 跑一次\"拉取区间数据\"");

        // 展示价（前复权，最新日=真实成交价）：只给"最新名单"显示用，不参与任何计算
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT code, substr(period_start,1,10), close FROM Bar WHERE granularity='day' AND length(code)=6";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!codeIndex.TryGetValue(r.GetString(0), out int s)) continue;
                if (!DateOnly.TryParse(r.GetString(1), out var d) || !dateIndex.TryGetValue(d, out int t)) continue;
                displayClose[s][t] = r.IsDBNull(2) ? double.NaN : r.GetDouble(2);
            }
        }

        // 脏数据防线：相邻交易日涨跌超过物理上限的，整根剔除（见 Config.MaxDailyReturn）
        int dirty = 0;
        for (int s = 0; s < nS; s++)
        {
            double prev = double.NaN;
            for (int t = 0; t < nD; t++)
            {
                double cNow = close[s][t];
                if (double.IsNaN(cNow)) continue;
                if (cNow <= 0)
                {
                    open[s][t] = close[s][t] = high[s][t] = low[s][t] = double.NaN;
                    dirty++;
                    continue;
                }
                if (!double.IsNaN(prev) && prev > 0 && Math.Abs(cNow / prev - 1) > Config.MaxDailyReturn)
                {
                    open[s][t] = close[s][t] = high[s][t] = low[s][t] = double.NaN;
                    dirty++;
                    continue; // prev 保持不变：后面若能回到正常水平就继续，避免一根脏数据带崩整段
                }
                prev = cNow;
            }
        }
        if (dirty > 0) log($"脏数据剔除 {dirty:N0} 根（价格≤0 或单日涨跌超 ±{Config.MaxDailyReturn:P0}）");

        var firstBar = new int[nS];
        var lastBar = new int[nS];
        for (int s = 0; s < nS; s++)
        {
            firstBar[s] = -1;
            lastBar[s] = -1;
            for (int t = 0; t < nD; t++)
                if (!double.IsNaN(close[s][t])) { firstBar[s] = t; break; }
            for (int t = nD - 1; t >= 0; t--)
                if (!double.IsNaN(close[s][t])) { lastBar[s] = t; break; }
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

        // 9) 退市名单（DelistedStock 表，老库可能还没有这张表）
        var isDelisted = new bool[nS];
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='DelistedStock'";
            if (check.ExecuteScalar() != null)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT code FROM DelistedStock";
                using var r = cmd.ExecuteReader();
                int n = 0;
                while (r.Read())
                    if (codeIndex.TryGetValue(r.GetString(0), out int s)) { isDelisted[s] = true; n++; }
                log($"退市股 {n} 只（不做按名剔除，只剔近退市{Config.DelistExcludeDays}日）");
            }
        }

        // 10) 财务报表（FinancialReport 表，基本面因子用；老库/没抓过时为空）
        var financials = new FinQuarter[nS][];
        for (int s = 0; s < nS; s++) financials[s] = [];
        bool finTableExists;
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='FinancialReport'";
            finTableExists = check.ExecuteScalar() != null;
        }
        if (!finTableExists)
        {
            log("⚠ 财务报表数据未抓取——基本面因子(ROE/成长/估值等)无值，请在 Fetcher 跑一次\"拉取财务报表\"");
        }
        else
        {
            {
                // 先按 (股票, 报告期) 聚合科目，再折算可用日、排序成数组
                var acc = new Dictionary<(int S, DateOnly D), double[]>(); // 值槽位见 keySlot
                int KeySlot(string k) => k switch
                {
                    "revenue" => 0, "oper_cost" => 1, "net_profit" => 2, "np_parent" => 3,
                    "assets" => 4, "liab" => 5, "equity_parent" => 6, "ocf" => 7, _ => -1,
                };
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT code, report_date, metric_key, value FROM FinancialReport WHERE value IS NOT NULL";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        if (!codeIndex.TryGetValue(r.GetString(0), out int s)) continue;
                        if (!DateOnly.TryParse(r.GetString(1), out var d)) continue;
                        int slot = KeySlot(r.GetString(2));
                        if (slot < 0) continue;
                        if (!acc.TryGetValue((s, d), out var vals))
                        {
                            vals = new double[8];
                            Array.Fill(vals, double.NaN);
                            acc[(s, d)] = vals;
                        }
                        vals[slot] = r.GetDouble(3);
                    }
                }

                // 法定披露截止日：一季报4-30 / 半年报8-31 / 三季报10-31 / 年报次年4-30
                static DateOnly Deadline(DateOnly report) => report.Month switch
                {
                    3 => new DateOnly(report.Year, 4, 30),
                    6 => new DateOnly(report.Year, 8, 31),
                    9 => new DateOnly(report.Year, 10, 31),
                    _ => new DateOnly(report.Year + 1, 4, 30),
                };

                var lastDate = dates[^1];
                var byStock = acc.GroupBy(kv => kv.Key.S);
                int finStocks = 0;
                foreach (var g in byStock)
                {
                    var list = new List<FinQuarter>();
                    foreach (var kv in g.OrderBy(kv => kv.Key.D))
                    {
                        var d = kv.Key.D;
                        var deadline = Deadline(d);
                        if (deadline > lastDate) continue; // 窗口内还不可用的报告期
                        int avail = deadline < dates[0] ? 0 : LowerBound(dates, deadline);
                        var v = kv.Value;
                        list.Add(new FinQuarter(d.Year, d.Month, avail, v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7]));
                    }
                    if (list.Count > 0) { financials[g.Key] = list.ToArray(); finStocks++; }
                }
                log(finStocks > 0
                    ? $"财务报表覆盖 {finStocks} 只（{acc.Count:N0} 个报告期记录）"
                    : "⚠ 财务报表数据为空——基本面因子无值，请在 Fetcher 跑一次\"拉取财务报表\"");
            }
        }

        // ST/退市标记按"当前名称"判断（无历史更名数据）；"退"覆盖退市整理期的 XX退/退市XX 命名
        var isSt = names.Select(n => n.Contains("ST", StringComparison.OrdinalIgnoreCase) || n.Contains('退')).ToArray();

        return new MarketData
        {
            Dates = dates, DateIndex = dateIndex, Codes = codes, Names = names.ToArray(),
            IsSt = isSt, FirstBarIdx = firstBar, LastBarIdx = lastBar, IsDelisted = isDelisted,
            Open = open, Close = close, High = high, Low = low, Amount = amount, Turnover = turnover,
            MarginBalance = margin, HolderChg = holderChg, FloatShares = floatShares,
            Industry = industry, LhbFlag = lhb, IndexClose = indexClose,
            DisplayClose = displayClose, UsingHfq = usingHfq, DirtyBarsDropped = dirty,
            Financials = financials,
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
