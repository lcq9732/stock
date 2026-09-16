using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 把一只标的在本地库里**除K线之外**的所有数据一次读齐（见 <see cref="StockDossier"/>）——供"行情
/// 详情"窗口的"其他数据"按钮展示。
///
/// 故意不走各 I*Repository：那些接口是给全市场扫描用的（批量快照、只暴露分析真正要的那一两个
/// 字段），这里要的是"某一只票、每张表、每个字段全都摊开看"，硬加到十几个接口上会污染它们的语义，
/// 而且 TopShareholder / Lhb / IndexCons / OrderWinAnnouncement 这几张表在 Analyzer 里本来就没有
/// 对应的仓储被注入。所以这里按表直接读，只读不写（连接串带 Mode=ReadOnly，这个窗口不可能写库）。
///
/// 时间序列的节除了格式化好的表格行，还产出一份**数值正序**的 <see cref="DossierChart"/> 给趋势图
/// 用（表格倒序、图正序，见 <see cref="DossierSection.Chart"/>）。图上不放颜色/线型，那是 Analyzer
/// 那边 DossierChartBuilder 的事——本层不引用 OxyPlot。
///
/// 日期字段的存储格式两种混用（跟各仓储保持一致，不做统一）：Bar.period_start 和
/// OrderWinAnnouncement.publish_date 是 "yyyy-MM-dd HH:mm:ss"，其余都是 "yyyy-MM-dd"——所以前两者
/// 取 substr(...,1,10) 再显示/比较。ISO 日期串的字典序等于时间序，"最近一条不晚于某日"这种查找
/// 直接用字符串比较，不必先 parse 成 DateTime。
///
/// 每节独立 try/catch：库文件可能是旧版本（缺表/缺列），单节读失败只把原因写进那一节的
/// <see cref="DossierSection.Error"/>，其它节照常显示。
/// </summary>
public class SqliteStockDossierReader
{
    private readonly string _connectionString;

    public SqliteStockDossierReader(string dbFilePath)
    {
        _connectionString = $"Data Source={dbFilePath};Mode=ReadOnly";
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public StockDossier Read(string code, string? name = null)
    {
        var dossier = new StockDossier { Code = code, Name = name };
        using var conn = Open();

        Add(dossier, "基本信息 / 数据覆盖", "StockMeta + DelistedStock + EtfIndexMap，以及本地各粒度K线的起止（K线本身在图上，这里只报覆盖范围）。", () => ReadBasics(conn, code));
        Add(dossier, "融资融券", "MarginDetail：交易所官方（上交所+深交所）。**只覆盖两融标的**，非标的天生没有数据（空表≠余额为0）；北交所两个源都不提供。环比=与上一交易日比。", () => ReadMargin(conn, code));
        Add(dossier, "股东户数", "ShareholderCount。环比=与表里上一行比；每日晨检那列用的是\"两期间隔<55天算同一期的补充披露\"口径，会与这里不同。", () => ReadShareholderCount(conn, code));
        Add(dossier, "十大股东", "TopShareholder(kind=total)。每次抓取整体覆盖该股全部历史。", () => ReadTopShareholder(conn, code, "total"));
        Add(dossier, "十大流通股东", "TopShareholder(kind=float)。", () => ReadTopShareholder(conn, code, "float"));
        Add(dossier, "分红送配", "Dividend：新浪分红派息页。表里金额是**每10股**口径，\"每股股息\"列已除10。进度为\"预案/董事会通过\"的方案可能变更。", () => ReadDividend(conn, code));
        Add(dossier, "财务报表", "FinancialReport：单位亿元，**年内累计**口径（不是单季）。报告期是季度末，不是公告日。比率列为本表现算。", () => ReadFinancial(conn, code));
        Add(dossier, "被谁列为客户/供应商", "StockCustomerSupplier 的**反向查询**：别家年报里把这只票列进前五大的记录。\n对龙头股这一节往往比下面那节有用得多——大公司自己披露时基本匿名（「第一名」「客户1」），\n而点它名的中小票通常写实名，所以只有站在这一边才看得到这条边。「占对方」是这笔生意占**对方**\n该类合计的比例，不是占这只票的。", () => ReadInboundPartners(conn, code));
        Add(dossier, "前五大客户与供应商", "StockCustomerSupplier：东财，来自年报「主要客户及供应商」。名次6=「其余」，前五+其余=100%（校验和）。\n⚠ 占比的分母两组不同：客户组≈营收，供应商组=采购总额，**两组的金额和占比都不能互相比**。\n对手方约半数是匿名披露（公司自己决定写不写实名），匿名行连不出边，是正常的。", () => ReadCustomerSupplier(conn, code));
        Add(dossier, "主力资金净流入", "NetInflow：日频，正=净流入。", () => ReadNetInflow(conn, code));
        Add(dossier, "龙虎榜", "Lhb：新浪龙虎榜。同一天可因多个上榜指标出现多行；\"对应值\"随指标而定（涨跌幅/偏离值）。", () => ReadLhb(conn, code));
        Add(dossier, "通用基本面指标", "FundamentalMetric：键值表，目前实际写入的是流通市值（元，抓取日快照，不是每个交易日都有）。", () => ReadFundamental(conn, code));
        Add(dossier, "所属板块", "BoardMember + Board：东财概念/行业板块的官方成分名单（2026-09-03 起，此前是新浪）。**当下快照**，每次拉板块整体覆盖，没有历史，所以没有趋势图。", () => ReadBoards(conn, code));
        Add(dossier, "所属指数", "IndexCons + IndexWeight。成分名单只留最新一版；权重仅中证系指数有（来自中证官网 closeweight），按调样基准日版本化。", () => ReadIndexes(conn, code));
        Add(dossier, "中标/订单公告", "OrderWinAnnouncement：巨潮全文检索，按界面上填的关键词抓。金额是从正文里解析出来的，可能为空或不准，所以不画趋势图。", () => ReadAnnouncements(conn, code));

        return dossier;
    }

    /// <summary>跑一节的读取，异常不外抛——只记在该节的 Error 上（见类注释）。标题/说明在这里统一
    /// 赋上，各读取方法只管产数据。</summary>
    private static void Add(StockDossier dossier, string title, string note, Func<DossierSection> read)
    {
        DossierSection section;
        try
        {
            section = read();
        }
        catch (Exception ex)
        {
            section = new DossierSection { Error = ex.Message };
        }
        section.Title = title;
        section.Note = note;
        dossier.Sections.Add(section);
    }

    // ── 各节 ─────────────────────────────────────────────────────────────────────

    private static DossierSection ReadBasics(SqliteConnection conn, string code)
    {
        var columns = new List<DossierColumn> { new("项目", 150), new("值") };
        var rows = new List<string[]>();
        void Row(string k, string v) => rows.Add(new[] { k, v });

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name, exchange, list_date, last_updated FROM StockMeta WHERE code = $c;";
            cmd.Parameters.AddWithValue("$c", code);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                Row("名称", Str(r, 0));
                Row("交易所", Str(r, 1));
                Row("上市日期", Str(r, 2));
                Row("列表更新时间", Str(r, 3));
            }
            else Row("StockMeta", "（这只标的不在本地标的清单里）");
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name, exchange, list_date, delist_date FROM DelistedStock WHERE code = $c;";
            cmd.Parameters.AddWithValue("$c", code);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                Row("⚠ 已退市", $"{Str(r, 0)}（{Str(r, 1)}）");
                Row("终止上市日", Str(r, 3, "—（上交所转板/合并的行缺这个字段）"));
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT index_code, match_type FROM EtfIndexMap WHERE etf_code = $c;";
            cmd.Parameters.AddWithValue("$c", code);
            using var r = cmd.ExecuteReader();
            if (r.Read())
                Row("ETF跟踪指数", $"{Str(r, 0, "未匹配到")}（匹配方式 {Str(r, 1)}）");
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT granularity, COUNT(*), MIN(substr(period_start,1,10)), MAX(substr(period_start,1,10))
                FROM Bar WHERE code = $c GROUP BY granularity ORDER BY granularity;
                """;
            cmd.Parameters.AddWithValue("$c", code);
            using var r = cmd.ExecuteReader();
            bool any = false;
            while (r.Read())
            {
                any = true;
                Row($"K线覆盖（{GranularityLabel(r.GetString(0))}）", $"{r.GetInt32(1)} 根，{Str(r, 2)} ~ {Str(r, 3)}");
            }
            if (!any) Row("K线覆盖", "（没有任何K线）");
        }

        return new DossierSection { Columns = columns, Rows = rows };
    }

    private static string GranularityLabel(string g) => g switch
    {
        Granularity.Day => "日线-前复权",
        Granularity.DayHfq => "日线-后复权",
        Granularity.Week => "周线",
        Granularity.Month => "月线",
        _ => g,
    };

    /// <summary>
    /// 融资融券 + 三个占比。分母不在这张表里，要另外取：
    /// - 占流通市值：FundamentalMetric 的 circulating_market_cap 只在抓取日写快照、不是每个交易日
    ///   都有，所以取"不晚于该交易日的最近一条"；一条都没有就留空（不拿今天的市值去除历史余额）。
    /// - 融资买入占成交额：Bar 的日线成交额，按同一天精确匹配，没有当天K线就留空。
    /// 图：融资/融券余额（亿元，左轴）+ 融资余额占流通市值（%，右轴）。融券余额往往贴着0，跟融资
    /// 同轴会被压平——但"融券几乎没有"本身就是要看的信息，所以不单独拆一张图。
    /// </summary>
    private static DossierSection ReadMargin(SqliteConnection conn, string code)
    {
        var columns = new List<DossierColumn>
        {
            new("交易日", 90),
            new("融资余额", 100, true),
            new("融资余额环比", 100, true),
            new("融资余额/流通市值", 120, true),
            new("融资买入额", 100, true),
            new("融资买入/成交额", 120, true),
            new("融券余额", 100, true),
            new("融券余额环比", 100, true),
            new("融券余额/流通市值", 120, true),
            new("融券余量(股)", 0, true),
        };

        // 流通市值快照（升序）——按交易日二分找"不晚于它的最近一条"。
        var capDates = new List<string>();
        var capValues = new List<double>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT as_of_date, value FROM FundamentalMetric
                WHERE code = $c AND metric_key = $k AND value > 0 ORDER BY as_of_date;
                """;
            cmd.Parameters.AddWithValue("$c", code);
            cmd.Parameters.AddWithValue("$k", MetricKeys.CirculatingMarketCap);
            using var r = cmd.ExecuteReader();
            while (r.Read()) { capDates.Add(r.GetString(0)); capValues.Add(r.GetDouble(1)); }
        }

        var amountByDay = new Dictionary<string, double>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT substr(period_start,1,10), amount FROM Bar WHERE code = $c AND granularity = $g;";
            cmd.Parameters.AddWithValue("$c", code);
            cmd.Parameters.AddWithValue("$g", Granularity.Day);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(1)) amountByDay[r.GetString(0)] = r.GetDouble(1);
        }

        // 升序读出来算环比，表格倒序显示、图正序用。
        var raw = new List<(string Date, double Mb, double Buy, double Sb, double Sv)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT trade_date, margin_balance, margin_buy, short_balance, short_volume
                FROM MarginDetail WHERE code = $c ORDER BY trade_date;
                """;
            cmd.Parameters.AddWithValue("$c", code);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                raw.Add((r.GetString(0), Dbl(r, 1), Dbl(r, 2), Dbl(r, 3), Dbl(r, 4)));
        }

        var mbYi = new double[raw.Count];
        var sbYi = new double[raw.Count];
        var mbShare = new double[raw.Count];
        var rows = new List<string[]>();

        for (int i = 0; i < raw.Count; i++)
        {
            double? cap = CapAsOf(capDates, capValues, raw[i].Date);
            mbYi[i] = raw[i].Mb / 1e8;
            sbYi[i] = raw[i].Sb / 1e8;
            mbShare[i] = cap is > 0 ? raw[i].Mb / cap.Value * 100 : double.NaN;
        }
        for (int i = raw.Count - 1; i >= 0; i--)
        {
            var cur = raw[i];
            double? mbChg = i > 0 && raw[i - 1].Mb > 0 ? (cur.Mb - raw[i - 1].Mb) / raw[i - 1].Mb * 100 : null;
            double? sbChg = i > 0 && raw[i - 1].Sb > 0 ? (cur.Sb - raw[i - 1].Sb) / raw[i - 1].Sb * 100 : null;
            double? cap = CapAsOf(capDates, capValues, cur.Date);
            double? amount = amountByDay.TryGetValue(cur.Date, out var a) && a > 0 ? a : null;

            rows.Add(new[]
            {
                cur.Date,
                Money(cur.Mb),
                Signed(mbChg),
                Share(cur.Mb, cap),
                Money(cur.Buy),
                Share(cur.Buy, amount),
                Money(cur.Sb),
                Signed(sbChg),
                Share(cur.Sb, cap),
                Num(cur.Sv, 0),
            });
        }

        var chart = raw.Count < 2 ? null : new DossierChart
        {
            XLabels = raw.Select(x => x.Date).ToList(),
            LeftAxisTitle = "亿元",
            RightAxisTitle = "%",
            Series = new List<DossierSeries>
            {
                new("融资余额（亿元）", mbYi),
                new("融券余额（亿元）", sbYi),
                new("融资余额/流通市值（%，右轴）", mbShare, OnRightAxis: true),
            },
            Note = "融券余额通常贴近0，与融资余额同轴会被压平——这本身也是信息。占流通市值只在有市值快照的日子有值。",
        };

        return new DossierSection { Columns = columns, Rows = rows, Chart = chart };
    }

    /// <summary>不晚于 <paramref name="date"/> 的最近一条流通市值（都晚于它就返回 null）。</summary>
    private static double? CapAsOf(List<string> dates, List<double> values, string date)
    {
        int lo = 0, hi = dates.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (string.CompareOrdinal(dates[mid], date) <= 0) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found < 0 ? null : values[found];
    }

    private static DossierSection ReadShareholderCount(SqliteConnection conn, string code)
    {
        var columns = new List<DossierColumn>
        {
            new("报告期", 100), new("股东户数", 110, true), new("户数环比", 100, true), new("户均持股数", 0, true),
        };

        var raw = new List<(string Date, double Num, double Avg)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT report_date, holder_num, avg_shares FROM ShareholderCount WHERE code = $c ORDER BY report_date;";
        cmd.Parameters.AddWithValue("$c", code);
        using (var r = cmd.ExecuteReader())
            while (r.Read()) raw.Add((r.GetString(0), Dbl(r, 1), Dbl(r, 2)));

        var counts = new double[raw.Count];
        var chgs = new double[raw.Count];
        for (int i = 0; i < raw.Count; i++)
        {
            counts[i] = raw[i].Num / 1e4;
            chgs[i] = i > 0 && raw[i - 1].Num > 0 ? (raw[i].Num - raw[i - 1].Num) / raw[i - 1].Num * 100 : double.NaN;
        }

        var rows = new List<string[]>();
        for (int i = raw.Count - 1; i >= 0; i--)
        {
            double? chg = double.IsNaN(chgs[i]) ? null : chgs[i];
            rows.Add(new[] { raw[i].Date, Num(raw[i].Num, 0), Signed(chg), Num(raw[i].Avg, 0) });
        }

        var chart = raw.Count < 2 ? null : new DossierChart
        {
            XLabels = raw.Select(x => x.Date).ToList(),
            LeftAxisTitle = "万户",
            RightAxisTitle = "%",
            Series = new List<DossierSeries>
            {
                new("股东户数（万户）", counts),
                new("户数环比（%，右轴）", chgs, OnRightAxis: true, AsBars: true),
            },
            Note = "户数上升=筹码分散（散户进场），下降=筹码集中。晨检的筹码警示阈值是环比 >+20%。",
        };

        return new DossierSection { Columns = columns, Rows = rows, Chart = chart };
    }

    /// <summary>十大（流通）股东。图画的是**每期前十合计占比**——单个股东逐期连线在股东名次进出
    /// 变化时会连出假趋势，合计占比才是"筹码集中度"这个真正想看的东西。</summary>
    private static DossierSection ReadTopShareholder(SqliteConnection conn, string code, string kind)
    {
        var columns = new List<DossierColumn>
        {
            new("报告期", 100), new("名次", 55, true), new("股东名称", 0), new("持股数量", 130, true), new("占比", 80, true), new("股本性质", 120),
        };
        var rows = new List<string[]>();
        var sumByPeriod = new Dictionary<string, double>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT report_date, rank, holder_name, shares, ratio, share_type
            FROM TopShareholder WHERE code = $c AND kind = $k ORDER BY report_date DESC, rank;
            """;
        cmd.Parameters.AddWithValue("$c", code);
        cmd.Parameters.AddWithValue("$k", kind);
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var period = r.GetString(0);
                var ratio = Dbl(r, 4);
                sumByPeriod[period] = sumByPeriod.GetValueOrDefault(period) + ratio;
                rows.Add(new[] { period, Num(Dbl(r, 1), 0), Str(r, 2), Num(Dbl(r, 3), 0), Pct(ratio), Str(r, 5) });
            }

        var periods = sumByPeriod.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var chart = periods.Count < 2 ? null : new DossierChart
        {
            XLabels = periods,
            LeftAxisTitle = "%",
            Series = new List<DossierSeries> { new("前十合计占比（%）", periods.Select(p => sumByPeriod[p]).ToArray()) },
            Note = "画合计而不是逐个股东连线——名次进出会让单个股东连出假趋势。合计上升=筹码向大股东集中。",
        };

        return new DossierSection { Columns = columns, Rows = rows, Chart = chart };
    }

    private static DossierSection ReadDividend(SqliteConnection conn, string code)
    {
        var columns = new List<DossierColumn>
        {
            new("公告日期", 100), new("每10股送股", 95, true), new("每10股转增", 95, true), new("每10股派息(元)", 110, true),
            new("每股股息(元)", 100, true), new("进度", 110), new("股权登记日", 100), new("除权除息日", 0),
        };

        var raw = new List<(string Announce, double Bonus, double Transfer, double Cash, string Progress, string Record, string Ex)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT announce_date, bonus_shares, transfer_shares, dividend_yuan, progress, record_date, ex_date
            FROM Dividend WHERE code = $c ORDER BY announce_date DESC;
            """;
        cmd.Parameters.AddWithValue("$c", code);
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                raw.Add((r.GetString(0), Dbl(r, 1), Dbl(r, 2), Dbl(r, 3), Str(r, 4), Str(r, 5), Str(r, 6)));

        var rows = raw.Select(x => new[]
        {
            x.Announce, Num(x.Bonus, 2), Num(x.Transfer, 2), Num(x.Cash, 3),
            Num(x.Cash / 10, 4), x.Progress, x.Record, x.Ex,
        }).ToList();

        // 只画**已实施**的现金分红：预案会变、不分配的行派息是0，混进来看不出真实分红节奏。
        // x 轴用除权除息日（真正到账的时点），缺失就退回公告日。
        var implemented = raw
            .Where(x => x.Progress.Contains("实施") && x.Cash > 0)
            .Select(x => (Date: x.Ex != "—" ? x.Ex : x.Announce, PerShare: x.Cash / 10))
            .OrderBy(x => x.Date, StringComparer.Ordinal)
            .ToList();

        var chart = implemented.Count < 2 ? null : new DossierChart
        {
            XLabels = implemented.Select(x => x.Date).ToList(),
            LeftAxisTitle = "元/股",
            Series = new List<DossierSeries> { new("每股股息（元，税前）", implemented.Select(x => x.PerShare).ToArray(), AsBars: true) },
            Note = "只画进度=实施且派息>0 的方案（预案会变、不分配的是0）；x 轴是除权除息日，缺失时退回公告日。同一年可能有中期+年度两次。",
        };

        return new DossierSection { Columns = columns, Rows = rows, Chart = chart };
    }

    /// <summary>
    /// 财报按报告期横排（一期一行、科目做列）——键值表原样列出来一期八行，看不出趋势。比率列在这里
    /// 现算，不入库。
    ///
    /// 图画的是**单季**（相邻累计相减），不是表格里的累计：累计口径每年Q1重新起算，直接连线会画出
    /// 一条每年初暴跌的锯齿，看着像业绩崩了，实际只是口径。只有"上一期正好是同年的上一个季度"时才
    /// 算得出单季，否则留空（缺季度的年份不硬凑）；Q1 的单季就等于它的累计。资产负债率是时点值，
    /// 没有这个问题，原样画。
    /// </summary>
    private static DossierSection ReadFinancial(SqliteConnection conn, string code)
    {
        var keys = new (string Key, string Header)[]
        {
            (FinancialKeys.Revenue, "营业收入"),
            (FinancialKeys.OperCost, "营业成本"),
            (FinancialKeys.NetProfit, "净利润"),
            (FinancialKeys.NetProfitParent, "归母净利润"),
            (FinancialKeys.Ocf, "经营现金流净额"),
            (FinancialKeys.TotalAssets, "总资产"),
            (FinancialKeys.TotalLiabilities, "总负债"),
            (FinancialKeys.EquityParent, "归母权益"),
        };

        var columns = new List<DossierColumn> { new("报告期", 90) };
        foreach (var (_, header) in keys) columns.Add(new DossierColumn(header, 105, true));
        columns.Add(new DossierColumn("毛利率", 80, true));
        columns.Add(new DossierColumn("净利率", 80, true));
        columns.Add(new DossierColumn("资产负债率", 0, true));

        var byPeriod = new Dictionary<string, Dictionary<string, double>>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT report_date, metric_key, value FROM FinancialReport WHERE code = $c;";
        cmd.Parameters.AddWithValue("$c", code);
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                if (r.IsDBNull(2)) continue;
                if (!byPeriod.TryGetValue(r.GetString(0), out var map))
                    byPeriod[r.GetString(0)] = map = new Dictionary<string, double>();
                map[r.GetString(1)] = r.GetDouble(2);
            }

        var ascending = byPeriod.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var rows = new List<string[]>();
        foreach (var period in Enumerable.Reverse(ascending))
        {
            var map = byPeriod[period];
            var cells = new List<string> { period };
            foreach (var (key, _) in keys)
                cells.Add(map.TryGetValue(key, out var v) ? Yi(v) : "—");

            cells.Add(Ratio(map, FinancialKeys.Revenue, FinancialKeys.OperCost, gross: true));
            cells.Add(Ratio(map, FinancialKeys.Revenue, FinancialKeys.NetProfit, gross: false));
            cells.Add(Ratio(map, FinancialKeys.TotalAssets, FinancialKeys.TotalLiabilities, gross: false));
            rows.Add(cells.ToArray());
        }

        var chart = ascending.Count < 2 ? null : new DossierChart
        {
            XLabels = ascending,
            LeftAxisTitle = "亿元",
            RightAxisTitle = "%",
            Series = new List<DossierSeries>
            {
                new("单季营业收入（亿元）", QuarterSeries(ascending, byPeriod, FinancialKeys.Revenue)),
                new("单季净利润（亿元）", QuarterSeries(ascending, byPeriod, FinancialKeys.NetProfit)),
                new("单季经营现金流净额（亿元）", QuarterSeries(ascending, byPeriod, FinancialKeys.Ocf)),
                new("资产负债率（%，右轴）", ascending.Select(p => PointRatio(byPeriod[p], FinancialKeys.TotalAssets, FinancialKeys.TotalLiabilities)).ToArray(), OnRightAxis: true),
            },
            Note = "收入/净利润/现金流画的是**单季**（相邻累计相减），表格里是年内累计——直接画累计会出现每年Q1归零的锯齿。缺前一季度的年份留空不连线。资产负债率是时点值。",
        };

        return new DossierSection { Columns = columns, Rows = rows, Chart = chart };
    }

    /// <summary>累计转单季：上一期是同年上一个季度才相减，Q1 直接取累计，其余留 NaN。</summary>
    private static double[] QuarterSeries(List<string> ascending, Dictionary<string, Dictionary<string, double>> byPeriod, string key)
    {
        var result = new double[ascending.Count];
        for (int i = 0; i < ascending.Count; i++)
        {
            result[i] = double.NaN;
            if (!byPeriod[ascending[i]].TryGetValue(key, out var cumulative)) continue;

            var quarter = QuarterOf(ascending[i]);
            if (quarter == 1) { result[i] = cumulative / 1e8; continue; }
            if (i == 0) continue;

            var prev = ascending[i - 1];
            if (prev[..4] != ascending[i][..4] || QuarterOf(prev) != quarter - 1) continue;
            if (!byPeriod[prev].TryGetValue(key, out var prevCumulative)) continue;
            result[i] = (cumulative - prevCumulative) / 1e8;
        }
        return result;
    }

    /// <summary>报告期字符串（yyyy-MM-dd，月份是 03/06/09/12）→ 季度序号；认不出返回 0。</summary>
    private static int QuarterOf(string reportDate) =>
        reportDate.Length >= 7 && int.TryParse(reportDate.AsSpan(5, 2), out var month) ? (month + 2) / 3 : 0;

    private static string Ratio(Dictionary<string, double> map, string denominatorKey, string numeratorKey, bool gross)
    {
        if (!map.TryGetValue(denominatorKey, out var den) || den == 0) return "—";
        if (!map.TryGetValue(numeratorKey, out var num)) return "—";
        return Pct(gross ? (den - num) / den * 100 : num / den * 100);
    }

    private static double PointRatio(Dictionary<string, double> map, string denominatorKey, string numeratorKey)
    {
        if (!map.TryGetValue(denominatorKey, out var den) || den == 0) return double.NaN;
        if (!map.TryGetValue(numeratorKey, out var num)) return double.NaN;
        return num / den * 100;
    }

    /// <summary>主力净流入。图上除了单日柱还加一条**累计**净流入曲线——单日在正负之间跳，几千根柱
    /// 子看不出方向；累计曲线的斜率才是"这段时间到底在净流入还是净流出"。</summary>
    private static DossierSection ReadNetInflow(SqliteConnection conn, string code)
    {
        var columns = new List<DossierColumn> { new("日期", 110), new("主力净流入", 0, true) };

        var raw = new List<(string Date, double Value)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT period_start, main_net_inflow FROM NetInflow WHERE code = $c ORDER BY period_start;";
        cmd.Parameters.AddWithValue("$c", code);
        using (var r = cmd.ExecuteReader())
            while (r.Read()) raw.Add((r.GetString(0), Dbl(r, 1)));

        var rows = Enumerable.Reverse(raw).Select(x => new[] { x.Date, Money(x.Value) }).ToList();

        var daily = new double[raw.Count];
        var cumulative = new double[raw.Count];
        double running = 0;
        for (int i = 0; i < raw.Count; i++)
        {
            daily[i] = raw[i].Value / 1e8;
            running += daily[i];
            cumulative[i] = running;
        }

        var chart = raw.Count < 2 ? null : new DossierChart
        {
            XLabels = raw.Select(x => x.Date).ToList(),
            LeftAxisTitle = "亿元",
            RightAxisTitle = "亿元(累计)",
            Series = new List<DossierSeries>
            {
                new("单日净流入（亿元）", daily, AsBars: true),
                new("累计净流入（亿元，右轴）", cumulative, OnRightAxis: true),
            },
            Note = "累计曲线的斜率才看得出方向（单日在正负之间跳）。累计从本地最早一条起算，不是上市以来。",
        };

        return new DossierSection { Columns = columns, Rows = rows, Chart = chart };
    }

    /// <summary>龙虎榜。不画趋势图——它是离散事件（哪天上榜、因为什么指标），把"对应值"连成线没有
    /// 意义（不同指标的对应值不是同一个量）。</summary>
    private static DossierSection ReadLhb(SqliteConnection conn, string code)
    {
        var columns = new List<DossierColumn>
        {
            new("交易日", 95), new("收盘价", 80, true), new("对应值", 85, true),
            new("成交量", 110, true), new("成交额", 110, true), new("上榜指标", 0),
        };
        var rows = new List<string[]>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT trade_date, close_price, deviation, volume, amount, reason
            FROM Lhb WHERE stock_code = $c ORDER BY trade_date DESC, reason;
            """;
        cmd.Parameters.AddWithValue("$c", code);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new[] { r.GetString(0), Num(Dbl(r, 1), 2), Num(Dbl(r, 2), 2), Num(Dbl(r, 3), 0), Money(Dbl(r, 4)), Str(r, 5) });
        return new DossierSection { Columns = columns, Rows = rows };
    }

    /// <summary>键值表。图只画流通市值那一个键——其它键（pe/pb/roe 之类）目前没有任何抓取程序写入，
    /// 真有了再按同样方式加序列即可。</summary>
    private static DossierSection ReadFundamental(SqliteConnection conn, string code)
    {
        var columns = new List<DossierColumn> { new("指标", 200), new("基准日", 100), new("值", 140, true), new("来源", 0) };
        var rows = new List<string[]>();
        var caps = new List<(string Date, double Yi)>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT metric_key, as_of_date, value, source FROM FundamentalMetric WHERE code = $c ORDER BY as_of_date DESC, metric_key;";
        cmd.Parameters.AddWithValue("$c", code);
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var key = r.GetString(0);
                var raw = Dbl(r, 2);
                // 流通市值存的是"元"，按亿显示才看得懂；其它键原样给数字。
                var isCap = key == MetricKeys.CirculatingMarketCap;
                if (isCap) caps.Add((r.GetString(1), raw / 1e8));
                rows.Add(new[] { MetricLabel(key), r.GetString(1), isCap ? Yi(raw) : Num(raw, 4), Str(r, 3) });
            }

        caps.Reverse();  // 表格是倒序读出来的，图要正序
        var chart = caps.Count < 2 ? null : new DossierChart
        {
            XLabels = caps.Select(x => x.Date).ToList(),
            LeftAxisTitle = "亿元",
            Series = new List<DossierSeries> { new("流通市值（亿元）", caps.Select(x => x.Yi).ToArray()) },
            Note = "只在抓取日有快照，不是每个交易日都有——相邻两点之间可能隔好几天。",
        };

        return new DossierSection { Columns = columns, Rows = rows, Chart = chart };
    }

    private static string MetricLabel(string key) => key switch
    {
        MetricKeys.CirculatingMarketCap => "流通市值（亿元）",
        MetricKeys.Revenue => "营业收入",
        MetricKeys.NetProfit => "净利润",
        MetricKeys.Roe => "ROE",
        MetricKeys.Eps => "每股收益",
        MetricKeys.Bvps => "每股净资产",
        MetricKeys.Pe => "市盈率",
        MetricKeys.Pb => "市净率",
        _ => key,
    };

    /// <summary>所属板块。不画图——Board 是当下快照，每次拉板块整体覆盖，本地压根没有历史。</summary>
    private static DossierSection ReadBoards(SqliteConnection conn, string code)
    {
        var columns = new List<DossierColumn>
        {
            new("类型", 70), new("板块名", 160), new("板块代码", 110), new("成分股数", 80, true),
            new("板块涨跌幅", 90, true), new("板块成交额", 110, true), new("领涨股", 110), new("快照时刻", 0),
        };
        var rows = new List<string[]>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT b.board_type, b.name, b.board_code, b.member_count, b.change_pct, b.amount, b.leader_name, b.as_of
            FROM BoardMember m JOIN Board b ON b.board_code = m.board_code
            WHERE m.stock_code = $c ORDER BY b.board_type, b.change_pct DESC;
            """;
        cmd.Parameters.AddWithValue("$c", code);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new[]
            {
                r.IsDBNull(0) ? "—" : (r.GetInt32(0) == 1 ? "行业" : "概念"),
                Str(r, 1), Str(r, 2), Num(Dbl(r, 3), 0), Pct(Dbl(r, 4)), Money(Dbl(r, 5)), Str(r, 6), Str(r, 7),
            });
        return new DossierSection { Columns = columns, Rows = rows };
    }

    /// <summary>所属指数。不画图——成分名单只留最新一版，权重虽然按基准日版本化，但中证只给最新一期、
    /// 本地是从接入那天起才开始累积，多数股票只有一两个基准日，连不成趋势。</summary>
    private static DossierSection ReadIndexes(SqliteConnection conn, string code)
    {
        var columns = new List<DossierColumn>
        {
            new("指数代码", 90), new("指数名称", 220), new("纳入日期", 100), new("最新权重", 90, true), new("权重基准日", 0),
        };
        var names = IndexCatalog.All.ToDictionary(i => i.Code, i => i.Name);
        var rows = new List<string[]>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.index_code, c.in_date,
                   (SELECT w.weight FROM IndexWeight w
                     WHERE w.index_code = c.index_code AND w.stock_code = c.stock_code
                     ORDER BY w.as_of_date DESC LIMIT 1),
                   (SELECT w.as_of_date FROM IndexWeight w
                     WHERE w.index_code = c.index_code AND w.stock_code = c.stock_code
                     ORDER BY w.as_of_date DESC LIMIT 1)
            FROM IndexCons c WHERE c.stock_code = $c ORDER BY c.index_code;
            """;
        cmd.Parameters.AddWithValue("$c", code);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var indexCode = r.GetString(0);
            rows.Add(new[]
            {
                indexCode,
                names.TryGetValue(indexCode, out var n) ? n : "—",
                Str(r, 1),
                r.IsDBNull(2) ? "—" : Pct(r.GetDouble(2)),
                Str(r, 3),
            });
        }
        return new DossierSection { Columns = columns, Rows = rows };
    }

    private static DossierSection ReadAnnouncements(SqliteConnection conn, string code)
    {
        var columns = new List<DossierColumn>
        {
            new("公告日期", 100), new("标题", 420), new("解析金额", 110, true), new("命中关键词", 100), new("来源", 80), new("PDF", 0),
        };
        var rows = new List<string[]>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT substr(publish_date,1,10), title, total_amount_yuan, keyword, source, pdf_url
            FROM OrderWinAnnouncement WHERE code = $c ORDER BY publish_date DESC;
            """;
        cmd.Parameters.AddWithValue("$c", code);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new[]
            {
                r.GetString(0), Str(r, 1), r.IsDBNull(2) ? "—" : Money(r.GetDouble(2)), Str(r, 3), Str(r, 4), Str(r, 5),
            });
        return new DossierSection { Columns = columns, Rows = rows };
    }

    // ── 取值 / 格式化 ────────────────────────────────────────────────────────────

    private static double Dbl(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetDouble(i);

    private static string Str(SqliteDataReader r, int i, string ifNull = "—")
    {
        if (r.IsDBNull(i)) return ifNull;
        var s = r.GetString(i);
        return string.IsNullOrWhiteSpace(s) || s == "--" ? ifNull : s;
    }

    /// <summary>金额按亿/万自适应——余额、成交额、净流入这些跨好几个数量级的列用它。</summary>
    private static string Money(double v)
    {
        var abs = Math.Abs(v);
        return abs switch
        {
            0 => "0",
            >= 1e8 => $"{v / 1e8:F2}亿",
            >= 1e4 => $"{v / 1e4:F2}万",
            _ => v.ToString("F2", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>固定按亿元——财报科目跨期比较时量纲统一才看得出趋势，不能这期"万"下期"亿"。</summary>
    private static string Yi(double v) => $"{v / 1e8:F2}";

    private static string Num(double v, int decimals) => v.ToString("N" + decimals, CultureInfo.InvariantCulture);

    private static string Pct(double v) => $"{v.ToString("F2", CultureInfo.InvariantCulture)}%";

    /// <summary>带正负号的百分比（环比这类"方向本身是信息"的列）。</summary>
    private static string Signed(double? v) =>
        v == null ? "—" : $"{(v.Value >= 0 ? "+" : "")}{v.Value.ToString("F2", CultureInfo.InvariantCulture)}%";

    /// <summary>占比——分母缺失（没有对应日的流通市值/成交额）时明确留空，不退回用别的日期的分母。</summary>
    private static string Share(double numerator, double? denominator) =>
        denominator == null || denominator.Value <= 0 ? "—" : Pct(numerator / denominator.Value * 100);
}
