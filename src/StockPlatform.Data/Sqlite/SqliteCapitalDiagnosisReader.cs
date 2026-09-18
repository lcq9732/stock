using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 把资金面诊断要的几张表一次读齐（见 <see cref="CapitalDiagnosisInput"/>）。
///
/// 跟 <see cref="SqliteStockDossierReader"/> 同样按表直读、只读不写（Mode=ReadOnly），理由也一样：
/// 各 I*Repository 是给全市场扫描用的，这里要的是"某一只票、跨五张表、取数值序列"，
/// 硬加到那些接口上会污染语义。区别在于档案读的是**格式化字符串**（给表格看），
/// 这里读的是**数值**（给算法算）。
///
/// ⚠ 调用顺序是有讲究的：<see cref="Read"/> 内部先读本股K线 → 调
/// <see cref="CapitalDiagnosisAnalyzer.ResolveWindows"/> 定出区间 → 再按区间去查指数和同业。
/// 反过来不行：区间由本股的阶段高点决定，不先算出来就不知道该查哪一段。
///
/// 性能：MarginDetail / BlockTrade / Lhb 的主键最左列都不是股票代码（分别是 trade_date /
/// trade_date / trade_date），按代码查是全表扫，在几 GB 的库上可能要一两秒——调用方必须
/// 放到后台线程（现有"其他数据"按钮就是这么做的）。同业中位数那一段更重：要扫同行业几百只票
/// 在三个区间的首尾K线，所以用一条 SQL 在库里算完，不把K线搬进内存。
/// </summary>
public class SqliteCapitalDiagnosisReader
{
    private readonly string _connectionString;

    public SqliteCapitalDiagnosisReader(string dbFilePath)
    {
        _connectionString = $"Data Source={dbFilePath};Mode=ReadOnly";
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public (CapitalDiagnosisInput Input, DiagnosisWindows Windows) Read(string code, string? name = null)
    {
        using var conn = Open();

        var bars = ReadBars(conn, code);
        var windows = CapitalDiagnosisAnalyzer.ResolveWindows(bars);
        if (bars.Count == 0)
            return (new CapitalDiagnosisInput { Code = code, Name = name }, windows);

        var (indexCode, indexName) = PickIndex(code);
        var (peerScope, peerCodes, downgraded) = ReadPeers(conn, code);

        var input = new CapitalDiagnosisInput
        {
            Code = code,
            Name = name,
            Bars = bars,
            IndexName = indexName,
            IndexReturns = ReadIndexReturns(conn, indexCode, windows),
            PeerScopeName = peerScope,
            PeerTotal = peerCodes.Count + 1,
            PeerMedianReturns = ReadPeerMedians(conn, peerCodes, windows, out int valid),
            PeerValid = valid,
            PeerDowngraded = downgraded,
            Flows = ReadFlows(conn, code),
            FlowStart = ScalarDate(conn, "SELECT MIN(trade_date) FROM NetInflowDetail WHERE code=$c", code),
            FlowMarketStart = ReadFlowMarketStart(conn),
            Margins = ReadMargins(conn, code),
            IsMarginTarget = Scalar(conn, "SELECT COUNT(*) FROM MarginDetail WHERE code=$c LIMIT 1", code) > 0,
            MarginLatest = ScalarDate(conn, "SELECT MAX(trade_date) FROM MarginDetail", null),
            MarginNormalLagDays = 1,   // 两所 T+1 披露；实测每个交易日都在次日入库
            BlockTrades = ReadBlockTrades(conn, code),
        };

        var main = windows.Ranges[0];
        input.MarginMissingDays.AddRange(ReadMarginMissingDays(conn, bars, main.Start));
        input.LhbDates.AddRange(ReadLhbDates(conn, code, main.Start));
        input.LhbLastEver = ScalarDate(conn, "SELECT MAX(trade_date) FROM Lhb WHERE stock_code=$c", code);
        return (input, windows);
    }

    // ── K线 ───────────────────────────────────────────────────────────────

    private static List<Bar> ReadBars(SqliteConnection conn, string code)
    {
        using var cmd = conn.CreateCommand();
        // 不复权：诊断里的价格是"当时实际成交的价"，跟成交额/换手率同一口径。
        cmd.CommandText = @"SELECT substr(period_start,1,10), close, amount, turnover
                            FROM Bar WHERE code=$c AND granularity='day' ORDER BY period_start;";
        cmd.Parameters.AddWithValue("$c", code);
        var list = new List<Bar>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!DateTime.TryParse(r.GetString(0), out var d)) continue;
            list.Add(new Bar
            {
                Code = code,
                Granularity = Granularity.Day,
                PeriodStart = d,
                Close = r.IsDBNull(1) ? 0 : r.GetDouble(1),
                Amount = r.IsDBNull(2) ? 0 : r.GetDouble(2),
                Turnover = r.IsDBNull(3) ? 0 : r.GetDouble(3),
            });
        }
        return list;
    }

    /// <summary>
    /// 按板块选对照指数。走 <see cref="MarketClassifier"/> 而不是自己写前缀规则——
    /// 自写规则漏掉 920 开头的北交所票，害得 342 只票静默抓不到（feedback_market_prefix_via_classifier）。
    /// </summary>
    private static (string Code, string Name) PickIndex(string code) =>
        MarketClassifier.Classify(code) switch
        {
            MarketBoard.ShenzhenChiNext => ("sz399006", "创业板指"),
            MarketBoard.ShanghaiStar => ("sh000688", "科创50"),
            MarketBoard.Beijing => ("bj899050", "北证50"),
            MarketBoard.ShenzhenMain or MarketBoard.ShenzhenB => ("sz399001", "深证成指"),
            _ => ("sh000001", "上证指数"),
        };

    private static Dictionary<string, double> ReadIndexReturns(
        SqliteConnection conn, string indexCode, DiagnosisWindows w)
    {
        var map = new Dictionary<string, double>();
        foreach (var r in w.Ranges)
        {
            var v = RangeReturn(conn, indexCode, r.Start, r.End);
            if (v is { } x) map[r.Label] = x;
        }
        return map;
    }

    /// <summary>某只标的在 [start,end] 的涨跌幅（%）；首尾任一没有K线就返回 null。</summary>
    private static double? RangeReturn(SqliteConnection conn, string code, DateTime start, DateTime end)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT (SELECT close FROM Bar WHERE code=$c AND granularity='day'
                     AND substr(period_start,1,10)>=$s ORDER BY period_start LIMIT 1),
                   (SELECT close FROM Bar WHERE code=$c AND granularity='day'
                     AND substr(period_start,1,10)<=$e ORDER BY period_start DESC LIMIT 1);";
        cmd.Parameters.AddWithValue("$c", code);
        cmd.Parameters.AddWithValue("$s", start.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$e", end.ToString("yyyy-MM-dd"));
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(0) || r.IsDBNull(1)) return null;
        double a = r.GetDouble(0), b = r.GetDouble(1);
        return a > 0 ? (b / a - 1) * 100 : null;
    }

    // ── 同业 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 同业名单。先用 major_name（85 个大类，中位 27 只/类），样本不足 10 只退回 class_name
    /// （20 个门类）——跟 PE 行业分位一样的降级规则（project_industry_pe_percentile）。
    /// 两级都不够就返回空名单，界面上同业那几列显示"—"。
    /// </summary>
    private static (string Scope, List<string> Codes, bool Downgraded) ReadPeers(
        SqliteConnection conn, string code)
    {
        var (major, klass) = ReadIndustry(conn, code);
        if (major.Length > 0)
        {
            var codes = ReadIndustryPeers(conn, "major_name", major, code);
            if (codes.Count + 1 >= CapitalDiagnosisAnalyzer.MinPeerSample) return (major, codes, false);
        }
        if (klass.Length > 0)
        {
            var codes = ReadIndustryPeers(conn, "class_name", klass, code);
            if (codes.Count + 1 >= CapitalDiagnosisAnalyzer.MinPeerSample) return (klass, codes, true);
        }
        return ("", new List<string>(), false);
    }

    private static (string Major, string Class) ReadIndustry(SqliteConnection conn, string code)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT major_name, class_name FROM StockIndustry WHERE code=$c LIMIT 1;";
        cmd.Parameters.AddWithValue("$c", code);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return ("", "");
        return (r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1));
    }

    private static List<string> ReadIndustryPeers(SqliteConnection conn, string column, string value, string self)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT code FROM StockIndustry WHERE {column}=$v AND code<>$c;";
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$c", self);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>
    /// 同业中位数收益。几百只票 × 三个区间，**在库里算完首尾价再回内存取中位数**——
    /// 把几百只票的完整日K搬进内存只为算三个中位数不划算。
    /// </summary>
    private static Dictionary<string, double> ReadPeerMedians(
        SqliteConnection conn, List<string> peers, DiagnosisWindows w, out int valid)
    {
        valid = 0;
        var map = new Dictionary<string, double>();
        if (peers.Count == 0) return map;

        // 临时表比拼几百个参数的 IN 列表快得多，也绕开 SQLite 的参数个数上限
        using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TEMP TABLE IF NOT EXISTS _peer(code TEXT PRIMARY KEY); DELETE FROM _peer;";
            create.ExecuteNonQuery();
        }
        using (var tx = conn.BeginTransaction())
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO _peer(code) VALUES($c);";
            var p = ins.Parameters.Add("$c", SqliteType.Text);
            foreach (var c in peers) { p.Value = c; ins.ExecuteNonQuery(); }
            tx.Commit();
        }

        foreach (var r in w.Ranges)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT (SELECT close FROM Bar WHERE code=p.code AND granularity='day'
                         AND substr(period_start,1,10)>=$s ORDER BY period_start LIMIT 1) AS a,
                       (SELECT close FROM Bar WHERE code=p.code AND granularity='day'
                         AND substr(period_start,1,10)<=$e ORDER BY period_start DESC LIMIT 1) AS b
                FROM _peer p;";
            cmd.Parameters.AddWithValue("$s", r.Start.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$e", r.End.ToString("yyyy-MM-dd"));
            var rets = new List<double>();
            using (var rd = cmd.ExecuteReader())
                while (rd.Read())
                {
                    if (rd.IsDBNull(0) || rd.IsDBNull(1)) continue;
                    double a = rd.GetDouble(0), b = rd.GetDouble(1);
                    if (a > 0) rets.Add((b / a - 1) * 100);
                }
            if (rets.Count == 0) continue;
            valid = Math.Max(valid, rets.Count);
            rets.Sort();
            map[r.Label] = rets.Count % 2 == 1
                ? rets[rets.Count / 2]
                : (rets[rets.Count / 2 - 1] + rets[rets.Count / 2]) / 2;
        }
        return map;
    }

    // ── 资金流 / 两融 / 大宗 / 龙虎榜 ────────────────────────────────────

    private static List<NetInflowDetail> ReadFlows(SqliteConnection conn, string code)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT trade_date, main_net, super_net, big_net, mid_net, small_net
                            FROM NetInflowDetail WHERE code=$c ORDER BY trade_date;";
        cmd.Parameters.AddWithValue("$c", code);
        var list = new List<NetInflowDetail>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!DateTime.TryParse(r.GetString(0), out var d)) continue;
            list.Add(new NetInflowDetail
            {
                Code = code,
                TradeDate = d,
                MainNet = r.IsDBNull(1) ? null : r.GetDouble(1),
                SuperNet = r.IsDBNull(2) ? null : r.GetDouble(2),
                BigNet = r.IsDBNull(3) ? null : r.GetDouble(3),
                MidNet = r.IsDBNull(4) ? null : r.GetDouble(4),
                SmallNet = r.IsDBNull(5) ? null : r.GetDouble(5),
            });
        }
        return list;
    }

    /// <summary>
    /// 全市场分档资金流的**可用**起点：覆盖只数达到峰值 80% 的第一天。
    ///
    /// ⚠ 不能用 <c>MIN(trade_date)</c>：实测全表 MIN 是 2025-11-14，但那天库里只有 1 只票，
    /// 2026-03-13 才 1,526 只、03-16 才 5,464 只。用 MIN 会虚报 4 个月的可用历史。
    /// </summary>
    private static DateTime? ReadFlowMarketStart(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH c AS (SELECT trade_date d, COUNT(*) n FROM NetInflowDetail GROUP BY trade_date)
            SELECT MIN(d) FROM c WHERE n >= (SELECT MAX(n) * 0.8 FROM c);";
        var v = cmd.ExecuteScalar();
        return v is string s && DateTime.TryParse(s, out var dt) ? dt : null;
    }

    private static List<MarginDetailRow> ReadMargins(SqliteConnection conn, string code)
    {
        using var cmd = conn.CreateCommand();
        // margin_repay 只有沪市有（深交所那张表没这一列）——拿它算官方口径的净买入；
        // 深市为 NULL，算净买入时退回余额差分（见 CapitalDiagnosisAnalyzer.NetBuySeries）。
        cmd.CommandText = @"SELECT trade_date, margin_balance, margin_buy, short_balance, margin_repay,
                                   short_volume
                            FROM MarginDetail WHERE code=$c ORDER BY trade_date;";
        cmd.Parameters.AddWithValue("$c", code);
        var list = new List<MarginDetailRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!DateTime.TryParse(r.GetString(0), out var d)) continue;
            list.Add(new MarginDetailRow
            {
                Code = code,
                TradeDate = d,
                MarginBalance = r.IsDBNull(1) ? 0 : r.GetDouble(1),
                MarginBuy = r.IsDBNull(2) ? 0 : r.GetDouble(2),
                // ⚠ 融券余额的 NULL 必须原样传下去，**不能变 0**：沪市源头不给这一列，
                // 补算过才有值。读成 0 的话界面会显示"融券余额为零"，那正是修掉的那个 bug。
                ShortBalance = r.IsDBNull(3) ? null : r.GetDouble(3),
                MarginRepay = r.IsDBNull(4) ? null : r.GetDouble(4),
                // 融券**余量**（股）是两所都给的源头字段，既不需要补算也不含价格因素——
                // 维度4 判"做空力量"就靠它，余额只当规模参考（2026-09-18）。
                ShortVolume = r.IsDBNull(5) ? 0 : r.GetDouble(5),
            });
        }
        return list;
    }

    /// <summary>
    /// 区间内**整天没有两融数据**的交易日。不是本票的问题——实测 2026-08-21 / 2026-09-02
    /// 深市整个缺失（当天只有沪市约 2,000 只，正常 4,100 只）。这种残缺日必须显式告警：
    /// 它既不会被当天的抓取发现，也会被后续的整段回补当成"已有"跳过。
    /// </summary>
    private static List<DateTime> ReadMarginMissingDays(
        SqliteConnection conn, List<Bar> bars, DateTime start)
    {
        var have = new HashSet<DateTime>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT trade_date FROM MarginDetail WHERE trade_date>=$s;";
            cmd.Parameters.AddWithValue("$s", start.ToString("yyyy-MM-dd"));
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (DateTime.TryParse(r.GetString(0), out var d)) have.Add(d.Date);
        }
        var latest = have.Count > 0 ? have.Max() : DateTime.MinValue;
        // 只报"中间的洞"，不报末尾那几天——末尾是披露滞后，另有一条告警说它
        return bars.Where(b => b.PeriodStart.Date >= start.Date && b.PeriodStart.Date < latest)
                   .Select(b => b.PeriodStart.Date)
                   .Where(d => !have.Contains(d))
                   .ToList();
    }

    private static List<BlockTrade> ReadBlockTrades(SqliteConnection conn, string code)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT trade_date, deal_amount, premium_ratio, buyer_name, seller_name
                            FROM BlockTrade WHERE code=$c ORDER BY trade_date;";
        cmd.Parameters.AddWithValue("$c", code);
        var list = new List<BlockTrade>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!DateTime.TryParse(r.GetString(0), out var d)) continue;
            list.Add(new BlockTrade
            {
                Code = code,
                TradeDate = d,
                DealAmount = r.IsDBNull(1) ? null : r.GetDouble(1),
                PremiumRatio = r.IsDBNull(2) ? null : r.GetDouble(2),
                BuyerName = r.IsDBNull(3) ? "" : r.GetString(3),
                SellerName = r.IsDBNull(4) ? "" : r.GetString(4),
            });
        }
        return list;
    }

    /// <summary>⚠ Lhb 的股票代码列叫 <c>stock_code</c>，不是 <c>code</c>。</summary>
    private static List<DateTime> ReadLhbDates(SqliteConnection conn, string code, DateTime start)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT trade_date FROM Lhb WHERE stock_code=$c AND trade_date>=$s;";
        cmd.Parameters.AddWithValue("$c", code);
        cmd.Parameters.AddWithValue("$s", start.ToString("yyyy-MM-dd"));
        var list = new List<DateTime>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (DateTime.TryParse(r.GetString(0), out var d)) list.Add(d);
        return list;
    }

    // ── 小工具 ───────────────────────────────────────────────────────────

    private static long Scalar(SqliteConnection conn, string sql, string? code)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (code != null) cmd.Parameters.AddWithValue("$c", code);
        var v = cmd.ExecuteScalar();
        return v is long l ? l : 0;
    }

    private static DateTime? ScalarDate(SqliteConnection conn, string sql, string? code)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (code != null) cmd.Parameters.AddWithValue("$c", code);
        var v = cmd.ExecuteScalar();
        return v is string s && DateTime.TryParse(s, out var d) ? d : null;
    }
}
