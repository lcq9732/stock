using StockPlatform.Data.Remote;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 页面通道截下来的那段响应怎么解析（2026-09-05）。
///
/// 为什么值得专门钉：这条路上的数据是从**页面自己发的 JSONP** 里剥出来的，
/// 剥壳剥错、字段取错都**不会报错**，只会让名单少几只——而上游是快照语义，
/// 少几只就等于把那几只从板块里删掉，且全程无声。
/// </summary>
public class EastMoneyClistPageTests
{
    private const string Payload =
        """{"rc":0,"data":{"total":198,"diff":[{"f12":"000016","f14":"*ST康佳A"},{"f12":"600183","f14":"生益科技"}]}}""";

    [Fact]
    public void JSONP外壳要剥掉()
    {
        // 页面用的就是这个形态：cb=jQuery371..._178...
        var body = "jQuery37103340495740046191_1788618938368(" + Payload + ");";

        var p = EastMoneyClistPage.Parse(body);

        Assert.NotNull(p);
        Assert.Equal(198, p!.Value.Total);
        Assert.Equal(["000016", "600183"], p.Value.Codes);
    }

    [Fact]
    public void 裸JSON也要认()
    {
        // 我们自己拼 URL（不带 cb）时拿回来的就是这个
        var p = EastMoneyClistPage.Parse(Payload);

        Assert.NotNull(p);
        Assert.Equal(198, p!.Value.Total);
        Assert.Equal(2, p.Value.Codes.Count);
    }

    [Fact]
    public void 股票名里带括号也不能把壳剥坏()
    {
        // 用 LastIndexOf 找右括号就是为了这个：板块名/股票名里带括号很常见
        var body = """cb({"rc":0,"data":{"total":1,"diff":[{"f12":"000333","f14":"美的集团(白电)"}]}})""";

        var p = EastMoneyClistPage.Parse(body);

        Assert.NotNull(p);
        Assert.Equal(["000333"], p!.Value.Codes);
    }

    [Fact]
    public void f12是数字形态时要补回前导零()
    {
        // 东财同一个字段有时给字符串有时给数字，数字会把 000001 吃成 1
        var body = """{"rc":0,"data":{"total":1,"diff":[{"f12":1,"f14":"平安银行"}]}}""";

        var p = EastMoneyClistPage.Parse(body);

        Assert.Equal(["000001"], p!.Value.Codes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html>验证页</html>")]
    [InlineData("jQuery123(")]                    // 被截断
    [InlineData("jQuery123({\"rc\":0,\"data\"")]  // JSON 截断
    public void 解析不了要返回null而不是空页(string body)
    {
        // 这条最要紧：返回"空页"的话，上游会把它当成"这一页没数据"接着往下走，
        // 最后拼出一份半截名单；返回 null 才会走重试/对账那条路。
        Assert.Null(EastMoneyClistPage.Parse(body));
    }

    [Fact]
    public void data为null是空页而不是解析失败()
    {
        // 限流时东财会返回合法 JSON 但 data 为 null；也可能真是空板块。
        // 这里只如实报告"0 条"，怎么处理交给上游的 total 对账。
        var p = EastMoneyClistPage.Parse("""{"rc":0,"data":null}""");

        Assert.NotNull(p);
        Assert.Equal(0, p!.Value.Total);
        Assert.Empty(p.Value.Codes);
    }
}
