using Microsoft.Data.Sqlite;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【金融监管指标】的开工检查（2026-10-21，随这一项挪进日更）。打在真 SQLite 上。
///
/// 这道门防的是"每个工作日白跑一刻钟"：原来不管有没有新东西，开工就要读全市场财务快照、
/// 把全部 PDF 重解析一遍。门判错的两个方向后果不对等——多放行只是多花一刻钟，
/// **漏放行是静默的**（新年报永远不解析），所以每条"该干活"的情形都要有正向用例。
/// </summary>
public class BankRegulatoryPlannerTests : IDisposable
{
    private readonly string _dir;
    private readonly FetchPaths _paths;
    private readonly SqliteBankRegulatoryRepository _bank;
    private static readonly DateTime H1 = new(2026, 6, 30);
    private static readonly DateTime Q3 = new(2026, 9, 30);
    private static readonly DateTime FY = new(2025, 12, 31);
    private const string Bank = "601939";

    public BankRegulatoryPlannerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"bankPlan_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        _bank = new SqliteBankRegulatoryRepository(_paths.CurrentDb);
        _bank.EnsureSchema();
        Industry(Bank, "J");
        BankFinancials(Bank);
    }

    public void Dispose()
    {
        // 不调 SqliteConnection.ClearAllPools()——那是进程级的，会崩掉别的测试类
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录 */ }
    }

    // ── 造数据 ────────────────────────────────────────────────────

    private void Industry(string code, string cls)
        => new SqliteIndustryRepository(_paths.CurrentDb).Upsert(
            [new StockIndustry { Code = code, ClassCode = cls, ClassName = cls, MajorName = cls }]);

    /// <summary>一份认得出是银行的财务：利息净收入占营收六成、没有营业成本。</summary>
    private void BankFinancials(string code)
        => new SqliteFinancialRepository(_paths.CurrentDb).ReplaceByCode(code,
        [
            new FinancialValue { Code = code, ReportDate = H1, Key = FinancialKeys.Revenue, Value = 100e8 },
            new FinancialValue { Code = code, ReportDate = H1, Key = FinancialKeys.InterestNet, Value = 60e8 },
        ]);

    private void Disclosed(string code, DateTime period, DateTime actual)
    {
        var repo = new SqliteEarningsScheduleRepository(_paths.CurrentDb);
        repo.EnsureSchema();
        repo.Upsert([new EarningsScheduleRow(code, period, actual, null, null, null, actual)]);
    }

    /// <summary>记一笔解析状态，最后一次尝试在 <paramref name="daysAgo"/> 天前。</summary>
    private void State(string code, DateTime period, string status, int daysAgo)
    {
        _bank.UpsertState(new BankReportFetchState { Code = code, ReportDate = period, Status = status });
        using var conn = new SqliteConnection($"Data Source={_paths.CurrentDb}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE BankReportFetchState SET fetched_at = $t WHERE code = $c AND report_date = $d;";
        cmd.Parameters.AddWithValue("$t", DateTime.Now.AddDays(-daysAgo).ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$c", code);
        cmd.Parameters.AddWithValue("$d", period.ToString("yyyy-MM-dd"));
        cmd.ExecuteNonQuery();
    }

    private BankRegulatoryPlan Plan() => new BankRegulatoryPlanner(_paths).Plan(DateTime.Today);

    // ── 没活 ──────────────────────────────────────────────────────

    [Fact]
    public void 最新年报中报都解析过了_没活()
    {
        Disclosed(Bank, H1, DateTime.Today.AddDays(-60));
        State(Bank, H1, "ok", 50);

        var plan = Plan();
        Assert.True(plan.NothingToDo);
        Assert.Equal(1, plan.InstitutionCount);
    }

    [Fact]
    public void 三季报出了也不算_三季报本来就不解析()
    {
        // 原来的跳过判据取"最新已披露的任意一期"，三季报一出就成了 09-30——永远不在已解析里，
        // 于是每家都被拉去查公告列表。挪进日更后那就是天天近百个白请求。
        Disclosed(Bank, H1, DateTime.Today.AddDays(-60));
        Disclosed(Bank, Q3, DateTime.Today.AddDays(-1));
        State(Bank, H1, "ok", 50);

        Assert.True(Plan().NothingToDo);
    }

    [Fact]
    public void 解析不出指标的不自动重试()
    {
        Disclosed(Bank, H1, DateTime.Today.AddDays(-60));
        State(Bank, H1, "no_match", 30);

        Assert.True(Plan().NothingToDo);
    }

    // ── 有活 ──────────────────────────────────────────────────────

    [Fact]
    public void 新披露的中报还没解析_有活()
    {
        Disclosed(Bank, H1, DateTime.Today);

        var plan = Plan();
        Assert.False(plan.NothingToDo);
        Assert.Equal([H1], plan.Work[Bank]);
        Assert.Equal(1, plan.Fresh);
    }

    [Fact]
    public void 上次报错_今天试过就不再试_明天再试()
    {
        Disclosed(Bank, H1, DateTime.Today.AddDays(-2));
        State(Bank, H1, "error", 0);
        Assert.True(Plan().NothingToDo);

        State(Bank, H1, "error", 1);
        Assert.Equal(1, Plan().Retry);
    }

    [Fact]
    public void 披露超过一周还没成_改成每周一试()
    {
        Disclosed(Bank, H1, DateTime.Today.AddDays(-20));
        State(Bank, H1, "no_pdf", 3);
        Assert.True(Plan().NothingToDo);

        State(Bank, H1, "no_pdf", 7);
        Assert.False(Plan().NothingToDo);
    }

    [Fact]
    public void 老期次文件丢了_每周补一次_找不到就不再试()
    {
        Disclosed(Bank, H1, DateTime.Today.AddDays(-60));
        State(Bank, H1, "ok", 50);
        State(Bank, FY, "wrong_file", 8);           // 文件不在 reports 目录里

        var plan = Plan();
        Assert.Equal([FY], plan.Work[Bank]);
        Assert.Contains((Bank, FY), plan.RefillKeys);

        State(Bank, FY, "wrong_file", 2);
        Assert.True(Plan().NothingToDo);

        State(Bank, FY, BankRegulatoryPlanner.NotListed, 30);
        Assert.True(Plan().NothingToDo);
    }

    [Fact]
    public void 老期次文件还在_不算丢()
    {
        Disclosed(Bank, H1, DateTime.Today.AddDays(-60));
        State(Bank, H1, "ok", 50);
        State(Bank, FY, "error", 30);
        var file = Path.Combine(_paths.ReportsDir, Bank, $"{FY:yyyy-MM-dd}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "pdf");

        Assert.True(Plan().NothingToDo);
    }

    // ── 名单 ──────────────────────────────────────────────────────

    [Fact]
    public void 非金融股混在状态表里_不当机构()
    {
        // 002594 比亚迪在生产的 BankReportFetchState 里有一行 wrong_file（早年的误删留下的），
        // 拿那张表当名单的话，它每周都会把整套重解析拖出来跑一遍。
        Industry("002594", "C");
        new SqliteFinancialRepository(_paths.CurrentDb).ReplaceByCode("002594",
            [new FinancialValue { Code = "002594", ReportDate = H1, Key = FinancialKeys.OperCost, Value = 1e9 }]);
        State("002594", FY, "wrong_file", 30);
        Disclosed(Bank, H1, DateTime.Today.AddDays(-60));
        State(Bank, H1, "ok", 50);

        Assert.True(Plan().NothingToDo);
    }

    [Fact]
    public void 新上市的银行还没有行业归属_照样认得出()
    {
        const string newBank = "601665";
        BankFinancials(newBank);                      // 财务已被【拉取财务报表】抓到，行业还没跟上
        Disclosed(Bank, H1, DateTime.Today.AddDays(-60));
        State(Bank, H1, "ok", 50);
        Disclosed(newBank, H1, DateTime.Today.AddDays(-1));

        var plan = Plan();
        Assert.Equal(2, plan.InstitutionCount);
        Assert.True(plan.Work.ContainsKey(newBank));
    }

    [Fact]
    public void 全新库没有行业数据_退回全量()
    {
        var dir = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(Path.Combine(dir, "local"));
        var plan = new BankRegulatoryPlanner(new FetchPaths(dir)).Plan(DateTime.Today);
        Assert.True(plan.FullRun);
        Assert.False(plan.NothingToDo);
    }

    [Theory]
    [InlineData("2026-10-21", "2026-06-30")]
    [InlineData("2026-08-30", "2025-12-31")]
    [InlineData("2026-04-30", "2025-12-31")]
    [InlineData("2026-04-29", "2025-06-30")]
    public void 法定截止日兜底的期次(string today, string expected)
        => Assert.Equal(DateTime.Parse(expected), BankRegulatoryPlanner.LatestExpectedHalfYearPeriod(DateTime.Parse(today)));
}
