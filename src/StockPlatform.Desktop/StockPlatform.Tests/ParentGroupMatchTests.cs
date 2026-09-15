using StockPlatform.Logic.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 消歧判据 v2（2026-09-15）。钉的是**判据本身**，不是某批样本——
/// 上一版正是因为"32 份样本全绿"而放过了一个会杀掉短品牌名的 bug。
///
/// ════ 这一版修的是什么 ════
/// 后缀正则原来含 <c>集团有限公司$</c> 和 <c>集团$</c>，而区分母公司和上市子公司的
/// 唯一信息就在这两个后缀里。实测 2025 年报 938 条边里 146 条是这么错的（占 17%、
/// 吃掉 44% 的金额）。
/// </summary>
public class ParentGroupMatchTests
{
    private readonly ITestOutputHelper _out;
    public ParentGroupMatchTests(ITestOutputHelper output) => _out = output;

    // 真实数据：全称 / 简称。前三家是踩过雷的
    private static readonly (string Code, string Full, string Abbr)[] Companies =
    [
        ("601600", "中国铝业股份有限公司", "中国铝业"),
        ("000709", "河钢股份有限公司", "河钢股份"),
        ("601669", "中国电力建设股份有限公司", "中国电建"),
        ("000333", "美的集团股份有限公司", "美的集团"),       // ⚠ 简称自带"集团"
        ("300750", "宁德时代新能源科技股份有限公司", "宁德时代"),
        ("601899", "紫金矿业集团股份有限公司", "紫金矿业"),   // ⚠ 全称自带"集团"但是上市主体
        ("601728", "中国电信股份有限公司", "中国电信"),
        ("002594", "比亚迪股份有限公司", "比亚迪"),
        ("600309", "万华化学集团股份有限公司", "万华化学"),   // ⚠ 全称里就带"集团"
        ("600849", "上海医药集团股份有限公司", "上海医药"),   // 同上，被提及 104 次
    ];

    private static (string? Code, string? Type) M(string name,
        IEnumerable<(string Name, string Parent)>? subs = null)
    {
        var index = PartnerNameMatcher.BuildIndex(
            Companies.Select(c => (c.Code, c.Full, (string?)c.Abbr)));
        var sub = subs == null ? null : PartnerNameMatcher.BuildSubsidiaryIndex(subs);
        return PartnerNameMatcher.Match(name, index, sub);
    }

    /// <summary>
    /// ⚠ **这组是最重要的**：非上市母集团不能被当成它旗下的上市公司。
    /// 归到 ParentGroup 而不是丢弃——"贝肯能源 74% 营收来自中石油集团"这句话是真的，
    /// 错的只是把中石油集团贴上 601857 这个代码。
    /// </summary>
    [Theory]
    [InlineData("中国铝业集团有限公司", "601600")]
    [InlineData("河钢集团有限公司", "000709")]
    [InlineData("中国电力建设集团有限公司", "601669")]
    [InlineData("中国电信集团有限公司", "601728")]
    public void 母集团归母集团档_不冒充上市公司(string written, string expectCode)
    {
        var (code, type) = M(written);
        _out.WriteLine($"「{written}」→ {code} [{type}]");
        Assert.Equal(expectCode, code);
        Assert.Equal(PartnerNameMatcher.ParentGroup, type);
    }

    /// <summary>
    /// ⚠ 简称自带"集团"二字的，**绝不能被母集团护栏误杀**。
    /// 这就是简称档必须排在护栏前面的原因。
    /// </summary>
    [Theory]
    [InlineData("美的集团", "000333")]
    [InlineData("宁德时代", "300750")]
    [InlineData("比亚迪", "002594")]
    public void 简称精确命中_不被护栏误杀(string written, string expectCode)
    {
        var (code, type) = M(written);
        _out.WriteLine($"「{written}」→ {code} [{type}]");
        Assert.Equal(expectCode, code);
        Assert.Equal(PartnerNameMatcher.Short, type);
    }

    /// <summary>全称里带"集团"但本身就是上市主体的（判据靠"含股份"），照常走精确档。</summary>
    [Theory]
    [InlineData("紫金矿业集团股份有限公司", "601899")]
    [InlineData("美的集团股份有限公司", "000333")]
    public void 集团股份有限公司是上市主体(string written, string expectCode)
    {
        var (code, type) = M(written);
        _out.WriteLine($"「{written}」→ {code} [{type}]");
        Assert.Equal(expectCode, code);
        Assert.Equal(PartnerNameMatcher.Exact, type);
    }

    /// <summary>
    /// ⚠ 「集团」必须在**末尾**才算母集团。
    /// 「中国电建集团华东勘测设计研究院有限公司」的集团在中间，那是 601669 的真子公司——
    /// 2026-09-15 我拿"含集团"当判据，把这类 8 条误判成了错边。
    /// </summary>
    [Fact]
    public void 集团在名字中间的是子公司_不是母集团()
    {
        const string sub = "中国电建集团华东勘测设计研究院有限公司";
        Assert.False(PartnerNameMatcher.IsParentGroup(sub));

        var (code, type) = M(sub, [(sub, "601669")]);
        _out.WriteLine($"「{sub}」→ {code} [{type}]");
        Assert.Equal("601669", code);
        Assert.Equal(PartnerNameMatcher.Subsidiary, type);
    }

    /// <summary>括号写法也要认出来：「申能(集团)有限公司」。</summary>
    [Theory]
    [InlineData("中国铝业(集团)有限公司")]
    [InlineData("中国铝业集团有限公司(合并)")]
    [InlineData("中国铝业集团")]
    public void 括号与尾部附注不影响母集团判定(string written)
    {
        Assert.True(PartnerNameMatcher.IsParentGroup(written), written);
    }

    /// <summary>全称 + 限定词：合并口径和分支机构，语义上都还是那家公司本人。</summary>
    [Theory]
    [InlineData("宁德时代新能源科技股份有限公司及其子公司", "300750")]
    [InlineData("中国电信股份有限公司宁夏分公司", "601728")]
    [InlineData("比亚迪股份有限公司及其关联方", "002594")]
    public void 全称加限定词仍是本人(string written, string expectCode)
    {
        var (code, type) = M(written);
        _out.WriteLine($"「{written}」→ {code} [{type}]");
        Assert.Equal(expectCode, code);
        Assert.Equal(PartnerNameMatcher.Qualified, type);
    }

    /// <summary>
    /// ⚠ 限定词是**白名单**，宁可漏不可错。
    /// 「…股份有限公司、另一家公司」这种多主体串不能算，顿号后面是别人。
    /// </summary>
    [Theory]
    [InlineData("比亚迪股份有限公司、宁德时代新能源科技股份有限公司")]
    [InlineData("比亚迪股份有限公司1")]
    [InlineData("比亚迪股份有限公司的竞争对手")]
    public void 限定词白名单之外一律不认(string written)
    {
        var (code, type) = M(written);
        _out.WriteLine($"「{written}」→ {code ?? "(不匹配)"} [{type ?? "-"}]");
        Assert.NotEqual(PartnerNameMatcher.Qualified, type);
    }

    /// <summary>
    /// ⚠ **公司自己名字就带「集团」时，不能把它判成自己的母集团。**
    ///
    /// 600309 全称是「万华化学集团股份有限公司」，对手写「万华化学集团有限公司」只是漏了"股份"，
    /// 那是本人。而 601600 全称是「中国铝业股份有限公司」不含"集团"，所以
    /// 「中国铝业集团有限公司」是**另一个实体**，是真母集团。区别就在公司自己的全称里。
    ///
    /// 实测 185 个母集团候选里 66 个属于前者，上海医药（被提及 104 次）就在其中。
    /// </summary>
    [Theory]
    [InlineData("万华化学集团有限公司", "600309")]
    [InlineData("万华化学集团", "600309")]
    [InlineData("上海医药集团有限公司", "600849")]
    [InlineData("上海医药(集团)有限公司", "600849")]
    public void 自己名字带集团的公司_不判成自己的母集团(string written, string expectCode)
    {
        var (code, type) = M(written);
        _out.WriteLine($"「{written}」→ {code} [{type}]");
        Assert.Equal(expectCode, code);
        Assert.NotEqual(PartnerNameMatcher.ParentGroup, type);
    }

    /// <summary>
    /// 归一化档只剩"写法差异"这一种用途了：有限公司 vs 股份有限公司。
    /// ⚠ 它**不该**再把"集团"当可去后缀——那正是 v1 的病根。
    /// </summary>
    [Fact]
    public void 归一化不再吃掉集团二字()
    {
        Assert.NotEqual(PartnerNameMatcher.Normalize("中国铝业集团有限公司"),
                        PartnerNameMatcher.Normalize("中国铝业股份有限公司"));
        _out.WriteLine(PartnerNameMatcher.Normalize("中国铝业集团有限公司") + " ≠ "
                     + PartnerNameMatcher.Normalize("中国铝业股份有限公司"));
    }

    /// <summary>匿名占位一律不参与匹配，加了新档次也不能破这条。</summary>
    [Theory]
    [InlineData("第一名")]
    [InlineData("客户1")]
    [InlineData("供应商一")]
    [InlineData("其余客户")]
    public void 匿名占位不匹配(string written)
    {
        var (code, _) = M(written);
        Assert.Null(code);
    }

    /// <summary>改判据就要改版本号，否则历史数据不会重算——修复落不了地。</summary>
    [Fact]
    public void 版本号必须大于初版()
    {
        Assert.True(PartnerNameMatcher.MatcherVersion >= 2,
                    "判据改了就要 +1，任务靠它决定要不要全量重匹");
    }
}
