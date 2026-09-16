using Microsoft.Data.Sqlite;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 「删了不补」那两个 bug 的回归（2026-09-16）。
///
/// ════ 事情经过 ════
/// 实测 11 份 PDF 被删掉、状态写着"已删除待重下"，却一直没被补回来，最早的挂了半个月。
/// 两个 bug 叠在一起：
///   ① 重解析用 <c>LooksLikeReport</c> 判"不是报告正文"就删文件。拿全库 377 份金融报告扫一遍，
///      它判 false 的 13 份**全部**在库里有从它自己解析出来的指标——100% 假阳性。
///   ② 删完之后，下载循环那道"最新一期已有就整只票跳过"的优化把它们挡在门外，永远补不回来。
///
/// 这一组钉的就是这两条：**不再删**、**缺文件的票不许跳**。
/// </summary>
public class ReportRefillTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dbPath;
    private readonly string _dir;

    public ReportRefillTests(ITestOutputHelper output)
    {
        _out = output;
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"refill_{id}.sqlite");
        _dir = Path.Combine(Path.GetTempPath(), $"refill_{id}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* 临时目录 */ }
    }

    private SqliteBankRegulatoryRepository Repo()
    {
        var r = new SqliteBankRegulatoryRepository(_dbPath);
        r.EnsureSchema();
        return r;
    }

    /// <summary>
    /// ⚠ **最重要的一条**：状态非 ok 且本地文件不在 → 必须被 <c>GetUnsuccessful</c> 捞出来，
    /// 否则下载循环不知道该补谁。
    /// </summary>
    [Fact]
    public void 没成功的期次要能查出来()
    {
        var repo = Repo();
        repo.UpsertState(new BankReportFetchState
        {
            Code = "000166", ReportDate = new DateTime(2025, 12, 31),
            Status = "wrong_file", MetricCount = 0,
        });
        repo.UpsertState(new BankReportFetchState
        {
            Code = "000166", ReportDate = new DateTime(2026, 6, 30),
            Status = "ok", MetricCount = 4,
        });
        repo.UpsertState(new BankReportFetchState
        {
            Code = "601601", ReportDate = new DateTime(2024, 12, 31),
            Status = "no_pdf", MetricCount = 0,
        });

        var bad = repo.GetUnsuccessful();
        foreach (var x in bad) _out.WriteLine($"{x.Code} {x.ReportDate:yyyy-MM-dd}");

        Assert.Equal(2, bad.Count);                                   // ok 那条不算
        Assert.Contains(("000166", new DateTime(2025, 12, 31)), bad);
        Assert.Contains(("601601", new DateTime(2024, 12, 31)), bad);
        Assert.DoesNotContain(("000166", new DateTime(2026, 6, 30)), bad);
    }

    /// <summary>
    /// 「缺不缺文件」由调用方判——仓储不碰文件系统。这一条钉的是两边拼起来的口径：
    /// <b>状态非 ok 且本地没有文件</b> 才算要补。
    /// </summary>
    [Fact]
    public void 状态非ok但文件还在的不算要补()
    {
        var repo = Repo();
        var date = new DateTime(2025, 12, 31);
        repo.UpsertState(new BankReportFetchState
        {
            Code = "AAA", ReportDate = date, Status = "no_match", MetricCount = 0,
        });
        repo.UpsertState(new BankReportFetchState
        {
            Code = "BBB", ReportDate = date, Status = "wrong_file", MetricCount = 0,
        });

        // AAA 的文件还在（解析不出而已），BBB 的没了
        var p = SinaReportIndex.PathOf(_dir, "AAA", date);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[SinaReportIndex.MinPdfBytes + 1]);

        var needRefill = repo.GetUnsuccessful()
            .Where(x => !File.Exists(SinaReportIndex.PathOf(_dir, x.Code, x.ReportDate)))
            .Select(x => x.Code).ToHashSet(StringComparer.Ordinal);

        _out.WriteLine("要补的：" + string.Join(", ", needRefill));
        Assert.DoesNotContain("AAA", needRefill);   // 文件在，不用重下——重下也解析不出来
        Assert.Contains("BBB", needRefill);
    }

    /// <summary>
    /// ⚠ **重解析不许再删文件**（2026-09-16）。
    ///
    /// 喂一个根本不是 PDF 的文件进去：老实现会 <c>LooksLikeReport</c> 判 false 然后删掉，
    /// 现在必须留着、并记 <c>no_match</c> 让它进手工回填清单。
    /// 静默删除是这个 bug 的根源——看不见的东西没人会去补。
    /// </summary>
    [Fact]
    public void 重解析不删文件_解析不出记no_match()
    {
        var repo = Repo();
        var date = new DateTime(2025, 12, 31);
        var pdf = SinaReportIndex.PathOf(_dir, "601939", date);
        Directory.CreateDirectory(Path.GetDirectoryName(pdf)!);
        File.WriteAllText(pdf, "这根本不是 PDF");

        // 认成银行，否则会被 OwnsPdfOf 那道护栏跳过
        var latest = new Dictionary<string, FinancialSnapshot>
        {
            ["601939"] = new()
            {
                ReportDate = date,
                Values = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    [FinancialKeys.Revenue] = 100e8,
                    [FinancialKeys.InterestNet] = 62e8,
                },
            },
        };

        var result = new BankReportReparser(repo, _dir).Run(latest, s => _out.WriteLine(s));

        Assert.True(File.Exists(pdf), "文件被删了——这正是 2026-09-16 要根除的行为");
        _out.WriteLine($"重解析 {result.Reparsed} / 解析不出 {result.NoMatch}");
    }

    /// <summary>非金融股的目录一个字节都不许动——那道护栏合并目录后是唯一防线。</summary>
    [Fact]
    public void 非金融目录照旧不碰()
    {
        var repo = Repo();
        var pdf = SinaReportIndex.PathOf(_dir, "002594", new DateTime(2025, 12, 31));
        Directory.CreateDirectory(Path.GetDirectoryName(pdf)!);
        File.WriteAllText(pdf, "比亚迪年报（假的）");

        var latest = new Dictionary<string, FinancialSnapshot>
        {
            ["002594"] = new()
            {
                ReportDate = new DateTime(2025, 12, 31),
                Values = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    [FinancialKeys.Revenue] = 100e8,
                    [FinancialKeys.OperCost] = 75e8,     // 有营业成本 = 工商企业
                },
            },
        };

        var result = new BankReportReparser(repo, _dir).Run(latest, s => _out.WriteLine(s));

        Assert.True(File.Exists(pdf));
        Assert.Equal(1, result.SkippedNonFinancial);
    }
}
