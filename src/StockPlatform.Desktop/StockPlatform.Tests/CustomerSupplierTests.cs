using System.Text.Json;
using Microsoft.Data.Sqlite;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 前五大客户/供应商的解析与落库（2026-09-07）。JSON 全部从线上原样抄，不联网。
///
/// 最要紧的一条：<b>上游下游不能搞反</b>。TYPE_CODE 1=客户(下游)、2=供应商(上游)，
/// 反了的话整份产业链数据的方向就是错的，而且错得毫无征兆——两边都是合法公司名、
/// 合法金额，任何"数据完整性"检查都发现不了。
/// </summary>
public class CustomerSupplierTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteCustomerSupplierRepository _repo;

    public CustomerSupplierTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"custsupp_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteCustomerSupplierRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
    private static readonly DateTime Now = new(2026, 9, 7, 12, 0, 0);

    // ── 线上原样抄来的三行 ──

    /// <summary>宁德时代 2025 年报第一大客户——大公司**匿名披露**的典型。</summary>
    private const string AnonCustomer = """
        {"AMOUNT":58159202000,"ITEM_NAME":"第一名","ORG_CODE":"10554205","RANK":1,
         "REPORT_DATE":"2025-12-31 00:00:00","REPORT_NAME":"2025年报","REPORT_YEAR":"2025",
         "SECUCODE":"300750.SZ","SECURITY_CODE":"300750","SECURITY_NAME_ABBR":"宁德时代",
         "SUM_AMOUNT":423669232546.201,"TOI_RATIO":13.73,"TYPE":"客户","TYPE_CODE":"1"}
        """;

    /// <summary>「其余供应商」——rank 6，是校验和那一行。</summary>
    private const string RestSupplier = """
        {"AMOUNT":517501132260.116,"ITEM_NAME":"其余供应商","ORG_CODE":"10554205","RANK":6,
         "REPORT_DATE":"2025-12-31 00:00:00","REPORT_NAME":"2025年报","REPORT_YEAR":"2025",
         "SECUCODE":"300750.SZ","SECURITY_CODE":"300750","SECURITY_NAME_ABBR":"宁德时代",
         "SUM_AMOUNT":577439335260.116,"TOI_RATIO":89.62,"TYPE":"供应商","TYPE_CODE":"2"}
        """;

    /// <summary>带真实公司名的一条——第二期连供应链网络靠的就是这种。</summary>
    private const string RealNamed = """
        {"AMOUNT":4445707.98,"ITEM_NAME":"福建时代星云科技有限公司","ORG_CODE":"10004088","RANK":1,
         "REPORT_DATE":"2025-12-31 00:00:00","REPORT_NAME":"2025年报","REPORT_YEAR":"2025",
         "SECUCODE":"000004.SZ","SECURITY_CODE":"000004","SECURITY_NAME_ABBR":"国华退",
         "SUM_AMOUNT":33697136.885657,"TOI_RATIO":13.19,"TYPE":"供应商","TYPE_CODE":"2"}
        """;

    // ════════ 解析 ════════

    [Fact]
    public void 客户是下游_TYPE_CODE1对应IsSupplier为假()
    {
        // ★ 搞反了整份产业链数据方向就是错的，而且没有任何征兆
        var x = EastMoneyCustomerSupplierProvider.Parse(J(AnonCustomer), Now);

        Assert.NotNull(x);
        Assert.False(x!.IsSupplier);          // 客户＝下游
        Assert.Equal("300750", x.Code);       // 取 SECURITY_CODE，不是带后缀的 SECUCODE
        Assert.Equal(new DateTime(2025, 12, 31), x.ReportDate);
        Assert.Equal(1, x.Rank);
        Assert.Equal("第一名", x.PartnerName); // 匿名披露照存，不当成缺失
        Assert.Equal(13.73, x.Pct);
        Assert.Equal("2025年报", x.ReportName);
    }

    [Fact]
    public void 供应商是上游_TYPE_CODE2对应IsSupplier为真()
    {
        var x = EastMoneyCustomerSupplierProvider.Parse(J(RestSupplier), Now);

        Assert.NotNull(x);
        Assert.True(x!.IsSupplier);           // 供应商＝上游
        Assert.Equal(6, x.Rank);              // 「其余」那一行
    }

    [Fact]
    public void 占比是占该类合计而不是占营收()
    {
        // ⚠ 东财那列叫 TOI_RATIO（Total Operating Income），名字骗人：
        //   供应商组的分母是**采购总额** 5774 亿，比宁德时代的营收还大。
        //   这条测试钉住的是"我们知道它是 amount/total_amount"这件事。
        var x = EastMoneyCustomerSupplierProvider.Parse(J(RestSupplier), Now)!;

        Assert.Equal(89.62, x.Pct);
        Assert.Equal(89.62, Math.Round(100 * x.Amount / x.TotalAmount, 2));
        // 供应商合计 > 客户合计，正是因为两者口径不同源
        var cust = EastMoneyCustomerSupplierProvider.Parse(J(AnonCustomer), Now)!;
        Assert.True(x.TotalAmount > cust.TotalAmount);
    }

    [Fact]
    public void 真实公司名原样保留()
    {
        var x = EastMoneyCustomerSupplierProvider.Parse(J(RealNamed), Now);
        Assert.Equal("福建时代星云科技有限公司", x!.PartnerName);
    }

    [Theory]
    // TYPE_CODE 不是 1/2 一律丢弃——宁可少几行，也不能把上游下游搞反
    [InlineData("""{"SECURITY_CODE":"300750","REPORT_DATE":"2025-12-31 00:00:00","RANK":1,"TYPE_CODE":"3"}""")]
    [InlineData("""{"SECURITY_CODE":"300750","REPORT_DATE":"2025-12-31 00:00:00","RANK":1}""")]
    // 关键字段缺失
    [InlineData("""{"SECURITY_CODE":"300750","RANK":1,"TYPE_CODE":"1"}""")]
    [InlineData("""{"REPORT_DATE":"2025-12-31 00:00:00","RANK":1,"TYPE_CODE":"1"}""")]
    [InlineData("""{"SECURITY_CODE":"300750","REPORT_DATE":"2025-12-31 00:00:00","TYPE_CODE":"1"}""")]
    // 代码不是 6 位（港股等）
    [InlineData("""{"SECURITY_CODE":"00700","REPORT_DATE":"2025-12-31 00:00:00","RANK":1,"TYPE_CODE":"1"}""")]
    public void 关键字段不全或类型不认的行一律丢弃(string json)
        => Assert.Null(EastMoneyCustomerSupplierProvider.Parse(J(json), Now));

    [Fact]
    public void 金额缺失不丢行只当零()
    {
        // 金额是可以为 0 的（披露了对手但没给金额），不该因此丢掉整条关系——
        // 关系本身（谁是谁的供应商）才是这份数据最有价值的部分。
        var x = EastMoneyCustomerSupplierProvider.Parse(
            J("""{"SECURITY_CODE":"300750","REPORT_DATE":"2025-12-31 00:00:00","RANK":2,"TYPE_CODE":"1","ITEM_NAME":"某某公司"}"""),
            Now);

        Assert.NotNull(x);
        Assert.Equal("某某公司", x!.PartnerName);
        Assert.Equal(0, x.Amount);
    }

    [Theory]
    // ★ 2026-09-09 补的一条，代价是 118,155 行脏数据（占 16.4%）。
    //   原来只判 code.Length != 6，于是东财那边形如 A21653 / A04018 的 6 位代码
    //   一路混进库里——1946 只，**一条都不在 StockMeta 里**。它们是非 A 股主体
    //   （新三板/改制前之类），作为"上市公司之间的交易关系"一端根本不在股票池里。
    //
    //   同一批写的 EastMoneyCompanyProfileProvider.Parse 一直有这个检查，这边漏了。
    //   两个 Provider 的代码校验必须一致，所以下面那条测试把两边一起钉住。
    [InlineData("A21653")]
    [InlineData("A04018")]
    [InlineData("12345A")]
    [InlineData("ABCDEF")]
    public void 六位但含字母的代码要丢掉(string code)
    {
        var json = $$"""
            {"SECURITY_CODE":"{{code}}","REPORT_DATE":"2025-12-31 00:00:00","RANK":1,
             "TYPE_CODE":"1","ITEM_NAME":"某公司"}
            """;
        Assert.Null(EastMoneyCustomerSupplierProvider.Parse(J(json), Now));
    }

    [Theory]
    [InlineData("A21653")]
    [InlineData("12345A")]
    public void 两个Provider的代码校验必须一致(string code)
    {
        // 不一致过一次就够了。这条测试的作用是：以后谁改松了任何一边，另一边立刻暴露。
        var custSupp = $$"""
            {"SECURITY_CODE":"{{code}}","REPORT_DATE":"2025-12-31 00:00:00","RANK":1,"TYPE_CODE":"1"}
            """;
        var profile = $$"""
            {"SECUCODE":"{{code}}.SZ","SECURITY_CODE":"{{code}}","ORG_NAME":"某公司股份有限公司"}
            """;

        Assert.Null(EastMoneyCustomerSupplierProvider.Parse(J(custSupp), Now));
        Assert.Null(EastMoneyCompanyProfileProvider.Parse(J(profile), Now));
    }

    [Fact]
    public void 年份过滤器用东财自带的年份列()
        => Assert.Equal("""(REPORT_YEAR="2025")""", EastMoneyCustomerSupplierProvider.YearFilter(2025));

    // ════════ 抓哪些年 ════════
    // ★ 这几条钉的是一类**无声**的错：数据永久残缺，而界面上一切正常。

    /// <summary>造一个"收全了"的年份状态：接口 1000 行，落库 1000、丢弃 0。</summary>
    private static Dictionary<int, (int, int, int)> Full(params int[] years)
        => years.ToDictionary(y => y, _ => (1000, 1000, 0));

    /// <summary>空的年份状态。</summary>
    private static Dictionary<int, (int, int, int)> NoState() => new();

    [Fact]
    public void 首轮空库_从今年一路抓到2002()
    {
        var years = EastMoneyCustomerSupplierProvider.PlanYearsToFetch(2026, NoState());

        Assert.Equal(2026, years[0]);            // 新的在前：先拿到最有用的
        Assert.Equal(2002, years[^1]);           // 实测数据最早到 2002 年报
        Assert.Equal(2026 - 2002 + 1, years.Count);
    }

    [Fact]
    public void 今年和去年每轮都重抓_哪怕已经抓齐了()
    {
        // ★★ 年报是**分批披露**的：3 月抓到的 2025 年报只有一部分，4、5 月还在陆续出。
        //    这两年不能靠完成度判断——那一刻的 reported 本来就还没长全。
        var states = Full(Enumerable.Range(2002, 25).ToArray());

        var years = EastMoneyCustomerSupplierProvider.PlanYearsToFetch(2026, states);

        Assert.Equal([2026, 2025], years);       // 今年 + 去年，一个不少
    }

    [Fact]
    public void 抓齐的老年份不再抓()
    {
        // 那些数据不会再变，重抓纯属浪费请求
        var states = Full(2026, 2025, 2024, 2023);
        var years = EastMoneyCustomerSupplierProvider.PlanYearsToFetch(2026, states);

        Assert.Equal(2026, years[0]);
        Assert.Equal(2025, years[1]);
        Assert.DoesNotContain(2024, years);      // 抓齐了，跳过
        Assert.DoesNotContain(2023, years);
        Assert.Contains(2022, years);            // 没记录，要补
    }

    [Fact]
    public void 被截断的年份下轮会重抓()
    {
        // ★★★ 本文件最要紧的一条，也是"按新架构重做"时才暴露出来的。
        //
        // 骨架会在 Deadline / MaxItems 到点时**从批中间收尾**，而且那算**正常完成**：
        // 走 OnCompletedAsync、返回 Completed、界面上打勾。所以"这一年有没有数据"根本
        // 不能当判据——2019 年抓了 6000/66000 行也"有数据"，下轮一跳过，剩下 6 万行永远不来。
        // 今年去年靠"每轮都重抓"能自愈，2002-2024 不能。
        var states = Full(2026, 2025, 2024, 2023, 2022);
        states[2019] = (66000, 6000, 0);         // 被截断在这儿：接口 66000，只落了 6000、没丢弃

        var years = EastMoneyCustomerSupplierProvider.PlanYearsToFetch(2026, states);

        Assert.Contains(2019, years);            // 没抓齐 → 必须重抓
        Assert.DoesNotContain(2024, years);      // 抓齐的不动
    }

    [Fact]
    public void 主动过滤掉的行不算没抓齐()
    {
        // ★★★ 2026-09-09 真的误判了：判据当时是 saved < reported，而
        //   reported 是**接口自报**的、含非 A 股主体（形如 A21653，约 16.4%），
        //   落库的只有 A 股。于是：
        //       2023 年 51,677/62,791、2025 年 52,214/62,774
        //   看着像"没抓齐"，差额比例 17.7%/16.8% 正好等于非 A 股占比。
        //   那几年会每轮重抓，而且**永远抓不齐**——因为差额永远消不掉。
        //
        //   正确判据是 saved + skipped >= reported。
        var states = Full(2026, 2025, 2024, 2022);
        states[2023] = (62791, 51677, 11114);     // 收全了：51677 落库 + 11114 主动丢弃 = 62791

        var years = EastMoneyCustomerSupplierProvider.PlanYearsToFetch(2026, states);

        Assert.DoesNotContain(2023, years);       // 不该重抓
    }

    [Fact]
    public void 真的少收了才重抓()
    {
        // 跟上一条对照：同样有丢弃，但三个数加起来对不上 → 确实少收了
        var states = Full(2026, 2025, 2024, 2022);
        states[2023] = (62791, 40000, 11114);     // 40000 + 11114 = 51114 < 62791

        var years = EastMoneyCustomerSupplierProvider.PlanYearsToFetch(2026, states);

        Assert.Contains(2023, years);
    }

    [Fact]
    public void 中间断档的年份会被补上()
    {
        // 某一年当时抓失败了，后面几年却抓成功了——不能因为"新的都有了"就把它漏下
        var states = Full(2026, 2025, 2024, 2022);   // 2023 没记录
        var years = EastMoneyCustomerSupplierProvider.PlanYearsToFetch(2026, states);

        Assert.Contains(2023, years);
        Assert.DoesNotContain(2024, years);
    }

    [Fact]
    public void 首次整段回补无视完成度_所有年份重来()
    {
        var states = Full(Enumerable.Range(2002, 25).ToArray());

        var years = EastMoneyCustomerSupplierProvider.PlanYearsToFetch(2026, states, rebuild: true);

        Assert.Equal(2026 - 2002 + 1, years.Count);
    }

    // ════════ 落库 ════════

    private static CustomerSupplier Row(string code, int year, bool supplier, int rank,
                                        string name = "对手", double amt = 100, double pct = 10) => new()
    {
        Code = code,
        ReportDate = new DateTime(year, 12, 31),
        IsSupplier = supplier,
        Rank = rank,
        PartnerName = name,
        Amount = amt,
        Pct = pct,
        TotalAmount = 1000,
        ReportName = $"{year}年报",
        FetchedAt = Now,
    };

    [Fact]
    public void 空批次是空操作()
    {
        _repo.Upsert([Row("300750", 2025, false, 1)]);
        Assert.Equal(0, _repo.Upsert([]));
        Assert.Equal(1, _repo.GetStats().Rows);
    }

    [Fact]
    public void 客户和供应商同名次是两行不是一行()
    {
        // 主键含 is_supplier —— 漏了它的话，供应商第一名会把客户第一名覆盖掉，
        // 一半数据凭空消失，而且看不出来（行数少了一半没人会注意）。
        _repo.Upsert([
            Row("300750", 2025, false, 1, "客户甲"),
            Row("300750", 2025, true, 1, "供应商乙")]);

        Assert.Equal(2, _repo.GetStats().Rows);
    }

    [Fact]
    public void 重抓同一期是覆盖不是重复()
    {
        _repo.Upsert([Row("300750", 2025, false, 1, "旧名", amt: 100)]);
        _repo.Upsert([Row("300750", 2025, false, 1, "新名", amt: 200)]);

        Assert.Equal(1, _repo.GetStats().Rows);
    }

    [Fact]
    public void 累积语义_新报告期不会删掉旧报告期()
    {
        // 这份数据按报告期一期一期出，老报告期的行**永远有效**，
        // 跟板块那种"这轮没出现＝已下架"的快照表正好相反。
        _repo.Upsert([Row("300750", 2023, false, 1)]);
        _repo.Upsert([Row("300750", 2024, false, 1)]);
        _repo.Upsert([Row("300750", 2025, false, 1)]);

        var (rows, stocks, first, last) = _repo.GetStats();
        Assert.Equal(3, rows);
        Assert.Equal(1, stocks);
        Assert.Equal(new DateTime(2023, 12, 31), first);
        Assert.Equal(new DateTime(2025, 12, 31), last);
    }

    [Fact]
    public void 年度完成度是抓取的断点()
    {
        _repo.Upsert([
            Row("300750", 2024, false, 1), Row("300750", 2024, false, 2),
            Row("000001", 2025, true, 1)]);

        Assert.Equal(2, _repo.CountByYear(2024));
        Assert.Equal(1, _repo.CountByYear(2025));
        Assert.Equal(0, _repo.CountByYear(2023));

        // 完成度单独记：光看"有多少行"不知道该有多少行，也不知道主动丢了多少
        _repo.SaveYearState(2024, reported: 12, saved: 2, skipped: 10);   // 收全了：2 + 10 = 12
        _repo.SaveYearState(2025, reported: 500, saved: 1, skipped: 0);   // 被截断了

        var states = _repo.GetYearStates();
        Assert.Equal((12, 2, 10), states[2024]);
        Assert.Equal((500, 1, 0), states[2025]);
        Assert.False(states.ContainsKey(2023));   // 没抓过的不在里面
    }

    [Fact]
    public void 前五加其余等于百分之百_可以当校验和用()
    {
        // rank=6 那行不是冗余：前五 + 其余 = 100%，落库后一眼能看出有没有漏行。
        _repo.Upsert([
            Row("300750", 2025, false, 1, pct: 13.73), Row("300750", 2025, false, 2, pct: 11.12),
            Row("300750", 2025, false, 3, pct: 7.13),  Row("300750", 2025, false, 4, pct: 3.64),
            Row("300750", 2025, false, 5, pct: 3.34),  Row("300750", 2025, false, 6, pct: 61.04)]);

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SUM(pct) FROM StockCustomerSupplier WHERE code='300750' AND is_supplier=0;";
        Assert.Equal(100.0, Math.Round(Convert.ToDouble(cmd.ExecuteScalar()), 2));
    }
}
