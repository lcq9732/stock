using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 「其他数据」窗口里新加的两节：**被谁列为客户/供应商**（反向边）和**前五大客户与供应商**
/// （2026-09-16）。
///
/// ════ 为什么用临时库而不是 Mock ════
/// 这两节的全部逻辑都在 SQL 里——反向查 partner_code、跨表取对方名称、只收年报算集中度。
/// 把 SQL 抽到接口后面再 Mock，测的就是 Mock 不是 SQL 了，而真正会错的恰恰是 SQL。
/// 所以建一个只有两张表的临时库，跑**真的** <see cref="SqliteStockDossierReader"/>。
/// 其余十几节会因为表不存在各自记 Error——那是这个 reader 设计好的行为（单节失败不影响别节），
/// 这里只断言自己这两节。
///
/// ════ 造的这组数据在讲什么 ════
/// 照着真实发现的那个规律编：**大公司匿名、小公司实名**。
///   · 300750（大）自己披露前五大客户是「第一名」「第二名」——一条边都连不出来
///   · 300432（小）实名点了 300750，占它自己营收 68%
/// 于是站在 300750 这一边，只有反向那一节看得见这条边。
/// </summary>
public class DossierCustomerSupplierTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _db;

    private const string Big = "300750";
    private const string Small = "300432";
    private const string Parent = "601857";   // 中石油：演 parent_group 那一档

    public DossierCustomerSupplierTests(ITestOutputHelper output)
    {
        _out = output;
        _db = Path.Combine(Path.GetTempPath(), $"dossier-cs-{Guid.NewGuid():N}.sqlite");
        Seed();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();   // 不清池子文件删不掉
        try { File.Delete(_db); } catch { /* 临时文件，删不掉就算了 */ }
    }

    private void Seed()
    {
        using var conn = new SqliteConnection($"Data Source={_db}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE StockMeta (code TEXT PRIMARY KEY, name TEXT, exchange TEXT,
                                    list_date TEXT, last_updated TEXT, type TEXT);
            CREATE TABLE StockCustomerSupplier (
                code TEXT, report_date TEXT, is_supplier INTEGER, rank INTEGER,
                partner_name TEXT, amount REAL, pct REAL, total_amount REAL,
                report_name TEXT, fetched_at TEXT, partner_code TEXT, match_type TEXT,
                PRIMARY KEY (code, report_date, is_supplier, rank));

            INSERT INTO StockMeta (code,name,type) VALUES
                ('300750','宁德时代','stock'),
                ('300432','富临精工','stock'),
                ('601857','中国石油','stock');

            -- 大公司自己的披露：全匿名，partner_code 一律 NULL
            INSERT INTO StockCustomerSupplier
                (code,report_date,is_supplier,rank,partner_name,amount,pct,partner_code,match_type) VALUES
                ('300750','2025-12-31',0,1,'第一名',   58159000000, 13.73, NULL, NULL),
                ('300750','2025-12-31',0,2,'第二名',   47128000000, 11.12, NULL, NULL),
                ('300750','2025-12-31',0,6,'其余客户',258608000000, 75.15, NULL, NULL),
                ('300750','2025-12-31',1,1,'第一名',   23318000000,  4.04, NULL, NULL),
                ('300750','2025-12-31',1,6,'其余供应商',552682000000,95.96,NULL, NULL),
                ('300750','2024-12-31',0,1,'第一名',   40000000000, 20.00, NULL, NULL),
                ('300750','2024-12-31',0,6,'其余客户',160000000000, 80.00, NULL, NULL),
                ('300750','2024-12-31',1,1,'第一名',   10000000000,  8.00, NULL, NULL),
                ('300750','2024-12-31',1,6,'其余供应商',115000000000,92.00,NULL,NULL);

            -- 小公司实名点大公司：这条边只有反向查才看得到
            INSERT INTO StockCustomerSupplier
                (code,report_date,is_supplier,rank,partner_name,amount,pct,partner_code,match_type) VALUES
                ('300432','2025-12-31',0,1,'宁德时代新能源科技股份有限公司',9173000000,68.03,'300750','exact'),
                ('300432','2025-12-31',0,6,'其余客户',4310000000,31.97,NULL,NULL),
                ('300432','2024-12-31',0,1,'宁德时代新能源科技股份有限公司',6000000000,55.00,'300750','exact'),
                ('300432','2024-12-31',0,6,'其余客户',4909000000,45.00,NULL,NULL),
                -- 中报：表里要有，但**不能**进集中度趋势图
                ('300432','2026-06-30',0,1,'宁德时代',5000000000,50.00,'300750','short');

            -- parent_group：对手其实是非上市的中石油集团，代码却指向 601857。
            -- ⚠ **只给 2025 这一期**：供应商那条线在 2024 要断开，这正是下面那条用例要钉的。
            INSERT INTO StockCustomerSupplier
                (code,report_date,is_supplier,rank,partner_name,amount,pct,partner_code,match_type) VALUES
                ('300432','2025-12-31',1,1,'中国石油天然气集团有限公司',2000000000,74.00,'601857','parent_group');
            """;
        cmd.ExecuteNonQuery();
    }

    private DossierSection Section(string code, string title)
    {
        var dossier = new SqliteStockDossierReader(_db).Read(code);
        var s = dossier.Sections.Single(x => x.Title == title);
        Assert.Null(s.Error);
        return s;
    }

    private void Dump(DossierSection s)
    {
        _out.WriteLine($"【{s.Title}】{s.Rows.Count} 行");
        _out.WriteLine("  " + string.Join(" | ", s.Columns.Select(c => c.Header)));
        foreach (var row in s.Rows) _out.WriteLine("  " + string.Join(" | ", row));
    }

    // ── 反向边 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// ⚠ **这一节存在的全部理由**：大公司自己一条实名边都没有，反向查却看得见。
    /// </summary>
    [Fact]
    public void 大公司自己全匿名但反向查得到实名边()
    {
        var own = Section(Big, "前五大客户与供应商");
        var inbound = Section(Big, "被谁列为客户/供应商");
        Dump(own);
        Dump(inbound);

        // 自己披露的行里，「对应个股」列（下标 6）全是空——匿名连不出边
        Assert.All(own.Rows, r => Assert.Equal("", r[6]));

        // 反向查到了富临精工那条（钉住报告期——它在 2024/2025/2026中报 都点过名）
        var hit = Assert.Single(inbound.Rows, r => r[1] == Small && r[0] == "2025-12-31");
        Assert.Equal("富临精工", hit[2]);
        Assert.Equal("客户", hit[3]);        // 对方把它记在客户栏 = 对方卖给它
        Assert.Equal("68.03%", hit[6]);
        Assert.Equal("全称精确", hit[7]);
    }

    /// <summary>
    /// 反向边按**占对方的比例**倒序，不按名次——同样是"第1名"，占 68% 和占 4% 完全是两回事。
    /// </summary>
    [Fact]
    public void 反向边按占对方比例倒序()
    {
        var rows = Section(Big, "被谁列为客户/供应商").Rows;
        var pct = rows.Where(r => r[0] == "2025-12-31")
                      .Select(r => double.Parse(r[6].TrimEnd('%'))).ToList();
        _out.WriteLine("2025 年报这一期的占比：" + string.Join(", ", pct));
        Assert.Equal(pct.OrderByDescending(x => x).ToList(), pct);
    }

    /// <summary>没人点过名的票 → 空节，不是报错。界面会显示"本地库里这只标的没有这类数据"。</summary>
    [Fact]
    public void 没人点名就是空节()
        => Assert.Empty(Section(Small, "被谁列为客户/供应商").Rows);

    // ── 自己披露的那节 ───────────────────────────────────────────────────────

    /// <summary>名次 6 是「其余」那行校验和，**必须标成"其余"**，不能显示成 6——
    /// 否则会被当成"第 6 大客户"。</summary>
    [Fact]
    public void 名次六显示成其余()
    {
        var rows = Section(Big, "前五大客户与供应商").Rows;
        var rest = rows.Where(r => r[3].StartsWith("其余", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(rest);
        Assert.All(rest, r => Assert.Equal("其余", r[2]));
    }

    /// <summary>
    /// ⚠ 集中度趋势**只画年报**。中报混进来会在年报线上造出假拐点，而那几个点解释不了任何东西
    /// （全库 53 万行里非年报只有 569 行）。
    /// </summary>
    [Fact]
    public void 集中度趋势只收年报()
    {
        var s = Section(Small, "前五大客户与供应商");
        Dump(s);

        // 表里有中报
        Assert.Contains(s.Rows, r => r[0] == "2026-06-30");
        // 图里没有
        Assert.NotNull(s.Chart);
        Assert.DoesNotContain("2026-06-30", s.Chart!.XLabels);
        _out.WriteLine("图上的报告期：" + string.Join(", ", s.Chart.XLabels));
    }

    /// <summary>集中度 = 前五名 pct 之和，且**不依赖实名披露**——匿名的「第一名」照样有占比。</summary>
    [Fact]
    public void 集中度算的是前五合计且不依赖实名()
    {
        var chart = Section(Big, "前五大客户与供应商").Chart;
        Assert.NotNull(chart);
        Assert.Equal(["2024-12-31", "2025-12-31"], chart!.XLabels);

        var cust = chart.Series.Single(s => s.Label.Contains("客户", StringComparison.Ordinal));
        var supp = chart.Series.Single(s => s.Label.Contains("供应商", StringComparison.Ordinal));
        _out.WriteLine($"客户   {string.Join(" → ", cust.Values)}");
        _out.WriteLine($"供应商 {string.Join(" → ", supp.Values)}");

        Assert.Equal(20.00, cust.Values[0], 2);              // 2024：只有第1名 20%
        Assert.Equal(13.73 + 11.12, cust.Values[1], 2);      // 2025：第1+第2
        Assert.Equal(8.00, supp.Values[0], 2);
        Assert.Equal(4.04, supp.Values[1], 2);
    }

    /// <summary>
    /// ⚠ x 轴取两边报告期的**并集**，缺的那边留 NaN 断开。
    ///
    /// 要是改成各画各的下标，300432 只有 2025 有供应商披露、客户有 2024+2025，两条线就会
    /// **错位一格**——2025 的供应商占比会画在 2024 的位置上，而图上完全看不出来。
    /// </summary>
    [Fact]
    public void 只披露一边时另一条线断开()
    {
        var chart = Section(Small, "前五大客户与供应商").Chart;
        Assert.NotNull(chart);
        Assert.Equal(["2024-12-31", "2025-12-31"], chart!.XLabels);

        var cust = chart.Series.Single(s => s.Label.Contains("客户", StringComparison.Ordinal));
        var supp = chart.Series.Single(s => s.Label.Contains("供应商", StringComparison.Ordinal));
        _out.WriteLine($"客户   {string.Join(" → ", cust.Values)}");
        _out.WriteLine($"供应商 {string.Join(" → ", supp.Values)}");

        Assert.Equal(chart.XLabels.Count, supp.Values.Length);
        Assert.Equal(chart.XLabels.Count, cust.Values.Length);

        Assert.Equal(55.00, cust.Values[0], 2);
        Assert.True(double.IsNaN(supp.Values[0]), "2024 没有供应商披露，该断开而不是补 0");
        Assert.Equal(74.00, supp.Values[1], 2);   // 没有滑到 [0] 去
    }

    // ── 匹配档 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// ⚠ **最要紧的一条**：parent_group 必须在界面上当场说清"不是这家上市公司本身"。
    ///
    /// 这一档的 partner_code 指向上市平台，对手方却是它的非上市母集团。
    /// 「富临精工 74% 采购来自中石油集团」是真的，「它是 601857 的供应商」是假的。
    /// 不标出来，这条边就会被当成普通边用——而那正是错的用法。
    /// </summary>
    [Fact]
    public void 母集团档必须带警告字样()
    {
        var row = Assert.Single(Section(Small, "前五大客户与供应商").Rows,
                                r => r[6].StartsWith(Parent, StringComparison.Ordinal));
        _out.WriteLine(string.Join(" | ", row));
        Assert.Contains("母集团", row[7], StringComparison.Ordinal);
        Assert.Contains("⚠", row[7], StringComparison.Ordinal);
    }

    /// <summary>六档都要有中文标签，不能把 'qualified' 这种原样漏到界面上。</summary>
    [Theory]
    [InlineData("exact", "全称精确")]
    [InlineData("short", "简称精确")]
    [InlineData("qualified", "全称+限定词")]
    [InlineData("normalized", "归一化后相等")]
    [InlineData("subsidiary", "子公司归并（弱）")]
    public void 每一档都有中文标签(string matchType, string expected)
    {
        using (var conn = new SqliteConnection($"Data Source={_db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE StockCustomerSupplier SET match_type = $m " +
                              "WHERE code = $c AND report_date = '2025-12-31' AND is_supplier = 0 AND rank = 1;";
            cmd.Parameters.AddWithValue("$m", matchType);
            cmd.Parameters.AddWithValue("$c", Small);
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var row = Section(Small, "前五大客户与供应商").Rows
                  .Single(r => r[0] == "2025-12-31" && r[1] == "客户" && r[2] == "1");
        Assert.Equal(expected, row[7]);
    }

    /// <summary>没匹配上（match_type 为 NULL）→ 空白，**不显示"未匹配"之类的字**。
    /// 匿名披露占了约一半，每行都挂个标签只会把表填满噪音。</summary>
    [Fact]
    public void 没匹配上就留空()
    {
        var row = Section(Big, "前五大客户与供应商").Rows.First();
        Assert.Equal("", row[7]);
    }
}
