using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 把年报里的**交易对手名**还原成股票代码（2026-09-08）。
///
/// 这一步决定的是"产业链图里有没有错边"。**错边比没有边更糟**——没有的时候你知道自己不知道，
/// 有错边的时候你会照着它做判断。所以这里只做两档确定性匹配，测试也主要钉"不该匹配的别匹配"。
/// </summary>
public class PartnerNameMatcherTests
{
    private static PartnerNameMatcher.CompanyIndex Index(
        params (string, string)[] companies)
        => PartnerNameMatcher.BuildIndex(companies);

    [Fact]
    public void 全称一字不差是精确匹配()
    {
        var idx = Index(("300750", "宁德时代新能源科技股份有限公司"));

        var (code, type) = PartnerNameMatcher.Match("宁德时代新能源科技股份有限公司", idx);
        Assert.Equal("300750", code);
        Assert.Equal(PartnerNameMatcher.Exact, type);
    }

    [Theory]
    [InlineData("宁德时代新能源科技 股份有限公司")]      // 半角空格
    [InlineData("宁德时代新能源科技　股份有限公司")]      // 全角空格
    public void 空白不影响精确匹配(string written)
    {
        var idx = Index(("300750", "宁德时代新能源科技股份有限公司"));
        var (code, type) = PartnerNameMatcher.Match(written, idx);

        Assert.Equal("300750", code);
        Assert.Equal(PartnerNameMatcher.Exact, type);   // 去空白之后就是精确的，不算归一化
    }

    [Theory]
    // 后缀写法不一致 —— 归一化之后都是"某某某"
    [InlineData("某某某有限公司", "某某某股份有限公司")]
    [InlineData("某某某有限责任公司", "某某某有限公司")]
    // ⚠ 原来这里还有一行 ("某某某集团有限公司", "某某某集团")，2026-09-15 删掉了。
    //   归一化不再吃掉「集团」二字（那是母集团和上市子公司的唯一区别，见 PartnerNameMatcher
    //   的类注释），所以这种写法现在归 ParentGroup 档——见 ParentGroupMatchTests。
    //   那一行本来也是合成的：真实 A 股没有全称叫「某某某集团」而不带"股份有限公司"的。
    // 括号内容（全角/半角）
    [InlineData("某某某(上海)有限公司", "某某某有限公司")]
    [InlineData("某某某（上海）有限公司", "某某某有限公司")]
    public void 后缀和括号差异走归一化(string written, string registered)
    {
        var idx = Index(("000001", registered));
        var (code, type) = PartnerNameMatcher.Match(written, idx);

        Assert.Equal("000001", code);
        Assert.Equal(PartnerNameMatcher.Normalized, type);
    }

    [Fact]
    public void AB股同名时选A股()
    {
        // ★ 实测踩到过：京东方 A/B 股的 ORG_NAME 一模一样，不处理的话
        //   结果取决于哪条后写入——而我们要的永远是 A 股那个。
        var idx = Index(("200725", "京东方科技集团股份有限公司"),
                           ("000725", "京东方科技集团股份有限公司"));

        var (code, _) = PartnerNameMatcher.Match("京东方科技集团股份有限公司", idx);
        Assert.Equal("000725", code);

        // 反过来插入，结果必须一样 —— 否则依赖输入顺序，不可复现
        var idx2 = Index(("000725", "京东方科技集团股份有限公司"),
                             ("200725", "京东方科技集团股份有限公司"));
        var (code2, _) = PartnerNameMatcher.Match("京东方科技集团股份有限公司", idx2);
        Assert.Equal("000725", code2);
    }

    [Fact]
    public void 沪市B股900开头也让位给A股()
    {
        var idx = Index(("900901", "某某某股份有限公司"), ("600001", "某某某股份有限公司"));
        var (code, _) = PartnerNameMatcher.Match("某某某股份有限公司", idx);
        Assert.Equal("600001", code);
    }

    [Theory]
    [InlineData("第一名")]
    [InlineData("第五名")]
    [InlineData("其余客户")]
    [InlineData("其余供应商")]
    [InlineData("客户1")]
    [InlineData("客户 2")]
    [InlineData("供应商3")]
    [InlineData("客户A")]
    [InlineData("客户一")]
    [InlineData("前五名客户")]
    [InlineData("合计")]
    [InlineData("单位1")]
    public void 匿名披露一律不参与匹配(string anon)
    {
        // 万一真有公司叫"第一名"，也不能让占位符去撞上它 ——
        // 那会凭空造出一堆指向同一家公司的假边。
        var idx = Index(("000001", anon));   // 故意让库里就有这个名字

        var (code, type) = PartnerNameMatcher.Match(anon, idx);
        Assert.Null(code);
        Assert.Null(type);
        Assert.True(PartnerNameMatcher.IsAnonymous(anon));
    }

    [Theory]
    [InlineData("苏州同成化工有限公司")]        // 非上市小公司，永远连不上
    [InlineData("深圳市规划和自然资源局光明管理局")]  // 压根不是公司
    [InlineData("金田物业业主")]
    public void 对不上就返回空_绝不猜(string name)
    {
        var idx = Index(("300750", "宁德时代新能源科技股份有限公司"));
        var (code, type) = PartnerNameMatcher.Match(name, idx);

        Assert.Null(code);
        Assert.Null(type);
    }

    [Fact]
    public void 含简称不算匹配()
    {
        // ★ 刻意不做的那一档。"中国建筑第六工程局有限公司"确实是中国建筑的子公司，
        //   但靠"包含简称"去认，同时也会把一堆不相干的公司认错。
        //   实测加上它命中率只从 7.3% 升到 12.5%，不值这个风险。
        var idx = Index(("601668", "中国建筑股份有限公司"));

        var (code, _) = PartnerNameMatcher.Match("中国建筑第六工程局有限公司", idx);
        Assert.Null(code);
    }

    [Fact]
    public void 空名字和空全称都跳过()
    {
        var idx = Index(("000001", ""), ("", "某公司"), ("000002", "正常公司股份有限公司"));

        Assert.Null(PartnerNameMatcher.Match("", idx).Code);
        Assert.Null(PartnerNameMatcher.Match("   ", idx).Code);
        Assert.Equal("000002", PartnerNameMatcher.Match("正常公司股份有限公司", idx).Code);
    }

    [Fact]
    public void 归一化之后为空的名字不能成为索引键()
    {
        // "有限公司"这种只剩后缀的，归一化之后是空串。空串当键会让所有归一化为空的
        // 名字互相撞上——必须排除。
        var idx = Index(("000001", "有限公司"));
        Assert.False(idx.ByNorm.ContainsKey(""));
    }
}
