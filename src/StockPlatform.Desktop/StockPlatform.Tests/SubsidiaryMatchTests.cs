using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 实体消歧的第三档：子公司归并到母公司（2026-09-11）。
///
/// 这一档跟前两档性质不同——前两档断言"这两个名字指同一个法人"，它断言"这两个法人有控制关系"。
/// 所以要钉住的不只是"能匹配上"，更是**它不能越位**：本身就是上市公司的名字绝不能被归并走，
/// 有歧义的名字宁可不连。
/// </summary>
public class SubsidiaryMatchTests
{
    private static PartnerNameMatcher.CompanyIndex Listed()
        => PartnerNameMatcher.BuildIndex(
        [
            ("601668", "中国建筑股份有限公司"),
            ("002594", "比亚迪股份有限公司"),
        ]);

    [Fact]
    public void 子公司名归并到母公司()
    {
        var idx = Listed();
        var subs = PartnerNameMatcher.BuildSubsidiaryIndex(
        [
            ("中国建筑第六工程局有限公司", "601668"),
            ("中国建筑第八工程局有限公司", "601668"),
        ]);

        var (code, type) = PartnerNameMatcher.Match("中国建筑第六工程局有限公司", idx, subs);
        Assert.Equal("601668", code);
        Assert.Equal(PartnerNameMatcher.Subsidiary, type);
    }

    [Fact]
    public void 上市公司本尊不许被归并走()
    {
        var idx = Listed();
        // 恶意构造：有人把"比亚迪股份有限公司"错登记成了别家的子公司
        var subs = PartnerNameMatcher.BuildSubsidiaryIndex(
        [
            ("比亚迪股份有限公司", "601668"),
        ]);

        // 前两档先命中，轮不到第三档——它是它自己
        var (code, type) = PartnerNameMatcher.Match("比亚迪股份有限公司", idx, subs);
        Assert.Equal("002594", code);
        Assert.Equal(PartnerNameMatcher.Exact, type);
    }

    [Fact]
    public void 一个名字落到两个母公司_整个丢掉()
    {
        var idx = Listed();
        // 两家公司各自把"某某科技有限公司"写进了自己的子公司名单——无从判断该算谁的
        var subs = PartnerNameMatcher.BuildSubsidiaryIndex(
        [
            ("某某科技有限公司", "601668"),
            ("某某科技有限公司", "002594"),
        ]);

        var (code, type) = PartnerNameMatcher.Match("某某科技有限公司", idx, subs);
        Assert.Null(code);     // 宁可不连，也不能连错
        Assert.Null(type);
    }

    [Fact]
    public void 归一化后也能命中()
    {
        var idx = Listed();
        var subs = PartnerNameMatcher.BuildSubsidiaryIndex(
        [
            ("中国建筑第六工程局（上海）有限公司", "601668"),
        ]);

        // 对手名少了括号里那段，归一化之后应该还能对上
        var (code, type) = PartnerNameMatcher.Match("中国建筑第六工程局", idx, subs);
        Assert.Equal("601668", code);
        Assert.Equal(PartnerNameMatcher.Subsidiary, type);
    }

    [Fact]
    public void 匿名占位符不参与第三档()
    {
        var idx = Listed();
        var subs = PartnerNameMatcher.BuildSubsidiaryIndex([("第一名", "601668")]);

        foreach (var anon in new[] { "第一名", "前五名客户", "客户1", "其余供应商" })
        {
            var (code, _) = PartnerNameMatcher.Match(anon, idx, subs);
            Assert.Null(code);
        }
    }

    [Fact]
    public void 不传子公司索引时行为不变()
    {
        var idx = Listed();
        // 老调用点一个参数都没改，必须还是老行为
        var (code, type) = PartnerNameMatcher.Match("中国建筑第六工程局有限公司", idx);
        Assert.Null(code);
        Assert.Null(type);
    }
}
