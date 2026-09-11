using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 年报子公司名单的落库（2026-09-11）。
///
/// 盯三件事，每一件都是"错了不会报错、只会悄悄变成坏数据"的那种：
/// 1. <b>名单和水位线必须同一个事务</b>——只写名单不写水位线会重解析；
///    只写水位线不写名单，那份 PDF 的结果就永远丢了而且没人知道。
/// 2. <b>空名单也要写水位线</b>。found_count = 0 说明版式认不出来，是要记下来的**事实**，
///    不是"没处理过"。不记的话每轮都会重试这份认不出来的 PDF。
/// 3. <b>重解析要先删旧行</b>。规则改了之后旧规则捞出来的错名字必须消失——
///    只做 upsert 的话它们会一直留在库里继续连出错边。
/// </summary>
public class CompanySubsidiaryRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteCompanySubsidiaryRepository _repo;
    private static readonly DateTime P2025 = new(2025, 12, 31);
    private static readonly DateTime P2024 = new(2024, 12, 31);

    public CompanySubsidiaryRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"csub_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteCompanySubsidiaryRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    private static CompanySubsidiary S(string code, DateTime d, string name,
                                       double? pct = null, int page = 1)
        => new() { Code = code, ReportDate = d, Name = name, HoldPct = pct, SourcePage = page };

    [Fact]
    public void 写名单同时写水位线()
    {
        _repo.Save("601668", P2025, 1,
        [
            S("601668", P2025, "中国建筑第六工程局有限公司", 100, 210),
            S("601668", P2025, "中国建筑第八工程局有限公司", null, 210),
        ]);

        var st = _repo.GetStats();
        Assert.Equal(2, st.Rows);
        Assert.Equal(1, st.Parents);
        Assert.Equal(1, st.Parsed);
        Assert.Equal(0, st.Empty);
        Assert.Contains(("601668", P2025), _repo.GetParsed(1));
    }

    [Fact]
    public void 空名单也要留下水位线()
    {
        _repo.Save("300432", P2025, 1, []);

        var st = _repo.GetStats();
        Assert.Equal(0, st.Rows);
        Assert.Equal(1, st.Parsed);     // 处理过了
        Assert.Equal(1, st.Empty);      // 而且是认不出来那种
        // 下轮不该再试这份
        Assert.Contains(("300432", P2025), _repo.GetParsed(1));
    }

    [Fact]
    public void 规则版本升了_旧记录不算已解析()
    {
        _repo.Save("601668", P2025, 1, [S("601668", P2025, "中国建筑第六工程局有限公司")]);

        Assert.Contains(("601668", P2025), _repo.GetParsed(1));
        Assert.DoesNotContain(("601668", P2025), _repo.GetParsed(2));   // v2 要重跑
    }

    [Fact]
    public void 重解析先删旧行_旧规则的错名字必须消失()
    {
        _repo.Save("601668", P2025, 1,
        [
            S("601668", P2025, "中国建筑第六工程局有限公司"),
            S("601668", P2025, "程局有限公司"),          // 旧规则捞出来的断片
        ]);
        Assert.Equal(2, _repo.GetStats().Rows);

        // 规则修好了，重解析同一份 PDF
        _repo.Save("601668", P2025, 2, [S("601668", P2025, "中国建筑第六工程局有限公司")]);

        var st = _repo.GetStats();
        Assert.Equal(1, st.Rows);                                  // 断片没了
        Assert.DoesNotContain("程局有限公司", _repo.GetNameToParent().Keys);
    }

    [Fact]
    public void 重解析只动自己那一期_别的报告期不受影响()
    {
        _repo.Save("601668", P2024, 1, [S("601668", P2024, "中国建筑第四工程局有限公司")]);
        _repo.Save("601668", P2025, 1, [S("601668", P2025, "中国建筑第六工程局有限公司")]);
        Assert.Equal(2, _repo.GetStats().Rows);

        _repo.Save("601668", P2025, 2, [S("601668", P2025, "中国建筑第八工程局有限公司")]);

        var names = _repo.GetNameToParent().Keys;
        Assert.Contains("中国建筑第四工程局有限公司", names);   // 2024 那期原封不动
        Assert.Contains("中国建筑第八工程局有限公司", names);
        Assert.DoesNotContain("中国建筑第六工程局有限公司", names);
    }

    [Fact]
    public void 一个名字落到两个母公司_索引里整个丢掉()
    {
        _repo.Save("601668", P2025, 1, [S("601668", P2025, "某某科技有限公司")]);
        _repo.Save("002594", P2025, 1, [S("002594", P2025, "某某科技有限公司")]);

        // 两家都把它写进了自己的名单，无从判断算谁的——宁可不连，也不能连错
        Assert.DoesNotContain("某某科技有限公司", _repo.GetNameToParent().Keys);
        Assert.Equal(2, _repo.GetStats().Rows);    // 但原始行都留着，是事实
    }

    [Fact]
    public void 同一子公司出现在多期_索引里只算一次()
    {
        _repo.Save("601668", P2024, 1, [S("601668", P2024, "中国建筑第六工程局有限公司")]);
        _repo.Save("601668", P2025, 1, [S("601668", P2025, "中国建筑第六工程局有限公司")]);

        var map = _repo.GetNameToParent();
        Assert.Equal("601668", map["中国建筑第六工程局有限公司"]);   // 同一个母公司，不算歧义
        Assert.Equal(2, _repo.GetStats().Rows);
    }

    [Fact]
    public void 持股比例可以为空()
    {
        _repo.Save("600426", P2025, 1,
        [
            S("600426", P2025, "华鲁恒升（荆州）有限公司", 70.0, 160),
            S("600426", P2025, "某全资子公司有限公司", null, 161),
        ]);
        Assert.Equal(2, _repo.GetStats().Rows);
    }
}
