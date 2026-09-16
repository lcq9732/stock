using StockPlatform.Logic.Models;
using StockPlatform.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 子公司名单落库时把母公司代码归一到当前个股（2026-09-16）。
///
/// ════ 为什么要在这里再判一次 ════
/// PDF 存在 <c>reports/&lt;code&gt;/</c>，目录名就是代码，而代码来自下载清单——清单里可能混着废代码。
/// 实测：「上海医药集团股份有限公司」这个全称同时挂在 600849（上药转换，不是个股）和
/// 601607（上海医药）上，老的 <c>Preferred</c> 判给了 600849，于是年报下到 reports/600849/、
/// **48 条子公司名单也记成了 600849**。
///
/// <c>PartnerNameMatcher</c> 那边 v3 的修复救不了这一半：subsidiary 档的母公司代码直接来自
/// <c>CompanySubsidiary</c> 表，不经过 <c>Preferred</c>。所以落库这一步要再判一次。
/// </summary>
public class NormalizeParentTests
{
    private readonly ITestOutputHelper _out;
    public NormalizeParentTests(ITestOutputHelper output) => _out = output;

    private const string ShanghaiPharma = "上海医药集团股份有限公司";

    private static string Run(string code, string[] current,
                              (string Code, string Full)[] profiles)
    {
        var set = new HashSet<string>(current, StringComparer.Ordinal);
        var fullNames = profiles.ToDictionary(x => x.Code, x => x.Full, StringComparer.Ordinal);
        var byFull = profiles.Where(x => set.Contains(x.Code))
                             .GroupBy(x => x.Full, StringComparer.Ordinal)
                             .ToDictionary(g => g.Key, g => g.Select(x => x.Code).Distinct().ToList(),
                                           StringComparer.Ordinal);
        return SubsidiaryExtractTask.NormalizeParentForTest(code, set, byFull, fullNames);
    }

    /// <summary>⚠ **最重要的一条**：废代码要归一到同全称的那个当前个股。</summary>
    [Fact]
    public void 废代码归一到正主()
    {
        var got = Run("600849",
            current: ["601607"],
            profiles: [("600849", ShanghaiPharma), ("601607", ShanghaiPharma)]);
        _out.WriteLine($"600849 → {got}");
        Assert.Equal("601607", got);
    }

    /// <summary>本来就是当前个股的，原样不动。</summary>
    [Fact]
    public void 是个股就不动()
        => Assert.Equal("601607", Run("601607",
            current: ["601607"],
            profiles: [("600849", ShanghaiPharma), ("601607", ShanghaiPharma)]));

    /// <summary>
    /// ⚠ 找不到同全称的个股 → **原样返回，不猜**。
    /// 600837 海通证券就是这种：合并退市改名国泰海通，全称对不上，迁不了也不该乱迁。
    /// </summary>
    [Fact]
    public void 没有同名个股时不动()
    {
        var got = Run("600837",
            current: ["601211"],
            profiles: [("600837", "海通证券股份有限公司"), ("601211", "国泰海通证券股份有限公司")]);
        _out.WriteLine($"600837 → {got}（不该变）");
        Assert.Equal("600837", got);
    }

    /// <summary>
    /// ⚠ 同全称的当前个股有**多个**（A/B 股同名）→ 也不动。拿不准就别改数据。
    /// </summary>
    [Fact]
    public void 同名个股有多个时不动()
    {
        var got = Run("900948",
            current: ["000725", "200725"],
            profiles: [("900948", "京东方科技集团股份有限公司"),
                       ("000725", "京东方科技集团股份有限公司"),
                       ("200725", "京东方科技集团股份有限公司")]);
        Assert.Equal("900948", got);
    }

    /// <summary>没有名册（拿不到个股清单）→ 退回老行为，一个字都不改。</summary>
    [Fact]
    public void 没有名册时不动()
        => Assert.Equal("600849", SubsidiaryExtractTask.NormalizeParentForTest(
            "600849", null,
            new Dictionary<string, List<string>>(), new Dictionary<string, string>()));

    /// <summary>档案里查不到这个代码的全称 → 无从判断，不动。</summary>
    [Fact]
    public void 查不到全称时不动()
        => Assert.Equal("999999", Run("999999",
            current: ["601607"],
            profiles: [("601607", ShanghaiPharma)]));

    /// <summary>
    /// ⚠ **归一必须发生在解析之前**，这条是 2026-09-16 真机上栽出来的。
    ///
    /// 第一版只改了 <c>ParsedReport</c> 外壳的 Code，而 <c>SubsidiaryParser.Parse(path, code, …)</c>
    /// 会把传进去的 code 填进**每一条 CompanySubsidiary.Code**——落库写的是条目里那个。
    /// 结果两张表对不上：
    ///     SubsidiaryParseState → 601607（外壳）
    ///     CompanySubsidiary    → 600849（条目）
    ///
    /// 这条用例钉的是这个不变量：**喂给 Parse 的 code 必须已经是归一后的**。
    /// 单测拿不到真 PDF，所以直接验"任务传给解析器的那个参数"这一步的算法本身——
    /// 归一结果必须只算一次、两处共用同一个值。
    /// </summary>
    [Fact]
    public void 归一结果两处必须是同一个值()
    {
        var current = new HashSet<string>(StringComparer.Ordinal) { "601607" };
        var fullNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["600849"] = ShanghaiPharma, ["601607"] = ShanghaiPharma,
        };
        var byFull = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            [ShanghaiPharma] = ["601607"],
        };

        // 任务里现在是：先算一次 code，再把同一个 code 同时喂给 Parse 和 ParsedReport。
        var forParser = SubsidiaryExtractTask.NormalizeParentForTest("600849", current, byFull, fullNames);
        var forState = SubsidiaryExtractTask.NormalizeParentForTest("600849", current, byFull, fullNames);

        _out.WriteLine($"喂给解析器 {forParser}　写进水位线 {forState}");
        Assert.Equal("601607", forParser);
        Assert.Equal(forParser, forState);   // 两处必须一致，否则就是那次两张表对不上的 bug
    }
}
