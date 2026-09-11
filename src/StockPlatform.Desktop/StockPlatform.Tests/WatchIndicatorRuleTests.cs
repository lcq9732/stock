using System.IO;
using StockPlatform.Data.Orchestration;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 规则配置文件（<c>data/watch-indicator-rules.json</c>）的契约。
///
/// **默认必须启用锂电池那两条**：全部注释掉等于这一项上线后零效果，而人不知道要来改这个
/// 文件，就成了静默失效——正是这套设计反复要躲的那类问题。
/// 分界线是**有没有实证支撑**，不是"是不是判断"：东财自带映射只给上游资源股挂原材料价，
/// 锂电池板块 33 只里只有 1 只有映射、宁德一个都没有，补它是修已证明的缺陷。
/// </summary>
public class WatchIndicatorRuleStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"watchrules-{Guid.NewGuid():N}.json");

    public void Dispose() { try { File.Delete(_path); } catch { } }

    [Fact]
    public void 模板默认启用锂电池两条规则()
    {
        WatchIndicatorRuleStore.EnsureTemplate(_path);
        var (rules, warnings) = WatchIndicatorRuleStore.Read(_path);

        Assert.Equal(2, rules.Count);
        Assert.Equal("BK1303", rules[0].BoardCode);      // 三级，更具体的排前面
        Assert.Equal("BK1033", rules[1].BoardCode);
        Assert.All(rules, r => Assert.Contains("EMI00662659", r.IndicatorIds));
        Assert.Empty(warnings);
    }

    /// <summary>整车销量那条是反例，必须**保持注释**——它给不出份额信号。</summary>
    [Fact]
    public void 模板里的反例规则保持注释()
    {
        WatchIndicatorRuleStore.EnsureTemplate(_path);
        var (rules, _) = WatchIndicatorRuleStore.Read(_path);

        Assert.DoesNotContain(rules, r => r.BoardCode == "BK1015");
        Assert.Contains("BK1015", File.ReadAllText(_path));   // 但要留在文件里当说明
    }

    /// <summary>已存在的文件一个字都不许动——程序改写会丢掉用户写的注释。</summary>
    [Fact]
    public void 文件已存在_不覆盖()
    {
        File.WriteAllText(_path, "{ \"BoardIndicatorRules\": [] }");
        WatchIndicatorRuleStore.EnsureTemplate(_path);

        Assert.Equal("{ \"BoardIndicatorRules\": [] }", File.ReadAllText(_path));
    }

    [Fact]
    public void 坏文件_返回空加告警_不抛()
    {
        File.WriteAllText(_path, "{ this is not json ");
        var (rules, warnings) = WatchIndicatorRuleStore.Read(_path);

        Assert.Empty(rules);          // 空规则 → ReplaceRuleLinks 是空操作 → 保留上一轮映射
        Assert.NotEmpty(warnings);    // 但必须告警，否则就是静默失效
    }
}

/// <summary>
/// L1 派生引擎的判据（见 doc/watch-item-design.md §6.4）。
///
/// 这里测的重点**不是"能不能铺出行"**——那是显然的；是那几条"错了也不报错"的行为：
/// 配置指向不存在的指标会不会告警、一票多规则谁赢、板块空了会不会悄悄产出 0 行。
/// 这些每一条静默失效的后果都是"某只票恰好没有指标"，界面上看不出来。
/// </summary>
public class WatchIndicatorRuleTests
{
    private static readonly HashSet<string> KnownIndicators =
        new(StringComparer.OrdinalIgnoreCase) { "EMI00662659", "EMI00100216" };

    /// <summary>宁德在 BK1303(锂电池)/BK1033(电池)/BK1200(电力设备) 三级里各一条，跟真实数据一致。</summary>
    private static List<(string, string)> CatlBoards() =>
    [
        ("300750", "BK1200"),
        ("300750", "BK1033"),
        ("300750", "BK1303"),
    ];

    [Fact]
    public void 规则挂在三级板块上_能铺到个股()
    {
        var rules = new List<BoardIndicatorRule>
        {
            new("BK1303", "锂电池", ["EMI00662659"], "碳酸锂是主要原材料成本"),
        };

        var r = WatchIndicatorRuleEngine.Build(CatlBoards(), rules, KnownIndicators);

        var link = Assert.Single(r.Links);
        Assert.Equal("300750", link.Code);
        Assert.Equal("EMI00662659", link.IndicatorId);
        Assert.Equal(1, link.Weight);
        Assert.Equal(WatchIndicatorOrigin.Rule, link.Origin);
        Assert.Contains("锂电池", link.Reason);
    }

    /// <summary>
    /// 规则挂在**二级**(BK1033 电池)上也要能匹配 —— 这条是 GetAllIndustryLinks 存在的理由：
    /// 只取最细一级的话，挂在二级上的规则对谁都匹配不上，而且是静默匹配不上。
    /// </summary>
    [Fact]
    public void 规则挂在二级板块上_同样能铺到个股()
    {
        var rules = new List<BoardIndicatorRule>
        {
            new("BK1033", "电池", ["EMI00662659"], "碳酸锂是主要原材料成本"),
        };

        var r = WatchIndicatorRuleEngine.Build(CatlBoards(), rules, KnownIndicators);

        Assert.Single(r.Links);
        Assert.Equal("300750", r.Links[0].Code);
    }

    /// <summary>一票被多条规则命中时，**配置顺序即优先级**，先命中的赢（越靠前越具体）。</summary>
    [Fact]
    public void 一票多规则命中_保留先命中的那条()
    {
        var rules = new List<BoardIndicatorRule>
        {
            new("BK1303", "锂电池", ["EMI00662659"], "更具体"),
            new("BK1033", "电池", ["EMI00662659"], "更粗的兜底"),
        };

        var r = WatchIndicatorRuleEngine.Build(CatlBoards(), rules, KnownIndicators);

        var link = Assert.Single(r.Links);
        Assert.Contains("锂电池", link.Reason);
        Assert.DoesNotContain("更粗的兜底", link.Reason);
    }

    /// <summary>
    /// **这条是整套校验存在的理由**：指标码写错一个字母，产出 0 行，而"0 行"跟
    /// "这个板块本来就没成分股"在库里长得一模一样——不告警就没人会发现配置坏了。
    /// </summary>
    [Fact]
    public void 指标码不存在_跳过并告警()
    {
        var rules = new List<BoardIndicatorRule>
        {
            new("BK1303", "锂电池", ["EMI00662658"], "写错了一位"),
        };

        var r = WatchIndicatorRuleEngine.Build(CatlBoards(), rules, KnownIndicators);

        Assert.Empty(r.Links);
        Assert.Contains(r.Warnings, w => w.Contains("EMI00662658") && w.Contains("不存在"));
    }

    /// <summary>坏指标要报，哪怕这个板块根本没成分股——否则"板块空"会把"指标写错"盖住。</summary>
    [Fact]
    public void 板块没成分股且指标也写错_两条都要告警()
    {
        var rules = new List<BoardIndicatorRule>
        {
            new("BK9999", "不存在的板块", ["EMI00000000"], "两处都错"),
        };

        var r = WatchIndicatorRuleEngine.Build(CatlBoards(), rules, KnownIndicators);

        Assert.Empty(r.Links);
        Assert.Contains(r.Warnings, w => w.Contains("EMI00000000"));
        Assert.Contains(r.Warnings, w => w.Contains("没有成分股"));
    }

    /// <summary>一条规则坏掉不该连累其余——这是"逐条跳过"而不是"整项放弃"的区别。</summary>
    [Fact]
    public void 一条规则坏掉_其余照常产出()
    {
        var rules = new List<BoardIndicatorRule>
        {
            new("BK1303", "锂电池", ["EMI00000000"], "坏的"),
            new("BK1200", "电力设备", ["EMI00100216"], "好的"),
        };

        var r = WatchIndicatorRuleEngine.Build(CatlBoards(), rules, KnownIndicators);

        var link = Assert.Single(r.Links);
        Assert.Equal("EMI00100216", link.IndicatorId);
        Assert.Contains(r.Warnings, w => w.Contains("EMI00000000"));
    }

    /// <summary>多个指标按配置顺序定 weight，合并显示时才排得对。</summary>
    [Fact]
    public void 多个指标_按配置顺序定weight()
    {
        var rules = new List<BoardIndicatorRule>
        {
            new("BK1303", "锂电池", ["EMI00662659", "EMI00100216"], "两个"),
        };

        var r = WatchIndicatorRuleEngine.Build(CatlBoards(), rules, KnownIndicators);

        Assert.Equal(2, r.Links.Count);
        Assert.Equal(1, r.Links.Single(l => l.IndicatorId == "EMI00662659").Weight);
        Assert.Equal(2, r.Links.Single(l => l.IndicatorId == "EMI00100216").Weight);
    }

    /// <summary>0 条规则产出 0 行且不抛——上游据此走"空集合是空操作"，保留上一轮的映射。</summary>
    [Fact]
    public void 没有规则_产出空且不抛()
    {
        var r = WatchIndicatorRuleEngine.Build(CatlBoards(), [], KnownIndicators);

        Assert.Empty(r.Links);
        Assert.Empty(r.Warnings);
    }

    /// <summary>同一板块多只票，每只都要铺到。</summary>
    [Fact]
    public void 板块里多只票_每只都铺()
    {
        List<(string, string)> members =
        [
            ("300750", "BK1303"),
            ("300014", "BK1303"),
            ("002594", "BK1303"),
        ];
        var rules = new List<BoardIndicatorRule>
        {
            new("BK1303", "锂电池", ["EMI00662659"], "碳酸锂"),
        };

        var r = WatchIndicatorRuleEngine.Build(members, rules, KnownIndicators);

        Assert.Equal(3, r.Links.Count);
        Assert.All(r.Links, l => Assert.Equal("EMI00662659", l.IndicatorId));
    }
}
