using StockPlatform.Logic.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 同一个全称对应多个代码时选谁（2026-09-16）。
///
/// ════ 为什么会有多个 ════
/// <c>CompanyProfile</c> 里有 479 行不是当前个股——退市股、B 股、转换证券、换过代码的老主体——
/// 而它们的 <c>full_name</c> 恰恰就是真实上市主体的全称。实测 100 组同全称冲突里，
/// **24 组的赢家不是当前个股**，波及 465 行 / 110 个对手名：
///   · 「上海医药集团股份有限公司」→ 600849（上药转换），一家占 104 行
///   · 「招商局港口集团股份有限公司」→ 000022（深赤湾A，已退市）而不是 001872
///   · 「中航成飞股份有限公司」→ 300114 而不是换代码后的 302132
///
/// 光靠"B 股让位给 A 股"拦不住：000022 和 001872 **都不是 B 股**，那条规则失效之后
/// 落到"按代码序取小"的兜底，正好取错。
/// </summary>
public class PreferCurrentStockTests
{
    private readonly ITestOutputHelper _out;
    public PreferCurrentStockTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// ⚠ **最重要的一条**：换过代码的公司，全称要判给**现在那个**代码。
    /// 老代码还躺在 CompanyProfile 里，而且字典序更小——不看名册就一定取错。
    /// </summary>
    [Theory]
    [InlineData("000022", "001872")]   // 招商局港口：深赤湾A 已退市 → 001872
    [InlineData("000043", "001914")]   // 招商积余
    [InlineData("000765", "001267")]   // 汇绿生态
    [InlineData("300114", "302132")]   // 中航成飞
    public void 换过代码的判给现在那个(string oldCode, string newCode)
    {
        var current = new HashSet<string>(StringComparer.Ordinal) { newCode };

        // 两个方向都要对——结果不能跟输入顺序有关
        Assert.Equal(newCode, PartnerNameMatcher.Preferred(oldCode, newCode, current));
        Assert.Equal(newCode, PartnerNameMatcher.Preferred(newCode, oldCode, current));

        // 不给名册就会取错（这正是 2026-09-16 之前的行为）
        _out.WriteLine($"不给名册时取 {PartnerNameMatcher.Preferred(oldCode, newCode)}，"
                     + $"给了名册取 {PartnerNameMatcher.Preferred(oldCode, newCode, current)}");
    }

    /// <summary>上药转换那一条：600849 不是个股，601607 才是。</summary>
    [Fact]
    public void 转换证券让位给正主()
    {
        var current = new HashSet<string>(StringComparer.Ordinal) { "601607" };
        Assert.Equal("601607", PartnerNameMatcher.Preferred("600849", "601607", current));
    }

    /// <summary>
    /// A/B 股那条老规则不能被破坏——两个都是当前个股时，仍然 A 股优先。
    /// 实测「京东方科技集团股份有限公司」同时对应 000725(A) 和 200725(B)。
    /// </summary>
    [Fact]
    public void 都是个股时仍然A股优先()
    {
        var current = new HashSet<string>(StringComparer.Ordinal) { "000725", "200725" };
        Assert.Equal("000725", PartnerNameMatcher.Preferred("000725", "200725", current));
        Assert.Equal("000725", PartnerNameMatcher.Preferred("200725", "000725", current));
    }

    /// <summary>
    /// 两个都不是当前个股（公司整个退市了，只剩 A/B 两个退市代码）→ 退回老规则：A 让 B 让位。
    /// 这种情况指到退市代码不算错，那家公司确实只有退市代码。
    /// </summary>
    [Fact]
    public void 都不是个股时退回老规则()
    {
        var current = new HashSet<string>(StringComparer.Ordinal) { "601607" };   // 都不在名册里
        Assert.Equal("000003", PartnerNameMatcher.Preferred("000003", "200003", current));
    }

    /// <summary>
    /// ⚠ **不给名册就必须是老行为**。拿不到个股名册时不该乱猜，
    /// 跟"空集合是空操作"同一条铁律——否则某天名册查询挂了会静默改变全库匹配结果。
    /// </summary>
    [Fact]
    public void 没有名册时行为不变()
    {
        Assert.Equal("000725", PartnerNameMatcher.Preferred("000725", "200725"));
        Assert.Equal("000022", PartnerNameMatcher.Preferred("000022", "001872"));   // 老行为：取小
        Assert.Equal("000022", PartnerNameMatcher.Preferred("000022", "001872", null));
    }

    /// <summary>
    /// 建索引时整条链要通：同全称的两个代码喂进去，索引里留下的必须是当前个股那个。
    /// 只测 <c>Preferred</c> 证明不了 <c>BuildIndex</c> 真的把名册传下去了。
    /// </summary>
    [Fact]
    public void 建索引时名册真的生效()
    {
        const string full = "招商局港口集团股份有限公司";
        var companies = new[] { ("000022", full), ("001872", full) };
        var current = new HashSet<string>(StringComparer.Ordinal) { "001872" };

        var withRoster = PartnerNameMatcher.BuildIndex(companies, current);
        var without = PartnerNameMatcher.BuildIndex(companies);

        var (code, type) = PartnerNameMatcher.Match(full, withRoster);
        _out.WriteLine($"有名册 → {code} [{type}]");
        Assert.Equal("001872", code);
        Assert.Equal(PartnerNameMatcher.Exact, type);

        var (bad, _) = PartnerNameMatcher.Match(full, without);
        _out.WriteLine($"无名册 → {bad}");
        Assert.Equal("000022", bad);     // 老行为，留作对照
    }

    /// <summary>改判据就要改版本号，否则历史数据不会重算——465 行错配修不掉。</summary>
    [Fact]
    public void 版本号必须到v3()
        => Assert.True(PartnerNameMatcher.MatcherVersion >= 3,
                       "同全称判据改了就要 +1，任务靠它决定要不要全量重匹");
}
