using StockPlatform.Logic.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 成交归因码（2026-09-14）。
///
/// ════ 这个字段解决什么 ════
/// 软件里目前只有 12 笔成交，做不了归因统计。但**原因必须现在就记**——
/// 等半年后数据攒够再回头补，人已经想不起来那笔是"到 +2% 止盈走的"还是
/// "看着不对提前跑的"，而这两者正是「纪律 vs 判断」的分界。
///
/// 所以这组用例盯的不是"填得对不对"（那是人的事），而是**这个字段能不能支撑最终那次归因**：
/// 每个码都归得进三组之一，老数据不会因为多了字段而读不出来。
/// </summary>
public class TradeReasonTests
{
    private readonly ITestOutputHelper _out;
    public TradeReasonTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// ⚠ **最重要的一条**：每个码都必须能归进「纪律/判断/外部」之一。
    ///
    /// 归不进去的码等于白记——半年后拆收益时那些笔只能扔掉。
    /// 加新码的人如果没想清楚它属于哪组，这条会红。
    /// </summary>
    [Fact]
    public void 每个码都要能归组()
    {
        var all = TradeReasons.For(isSell: false).Concat(TradeReasons.For(isSell: true));
        foreach (var (code, label) in all)
        {
            var g = TradeReasons.GroupOf(code);
            _out.WriteLine($"{code,-12} {g,-4} {label}");
            Assert.False(string.IsNullOrEmpty(g), $"「{code}」({label}) 归不进任何一组");
        }
    }

    [Fact]
    public void 三组都要有码_不能空着()
    {
        var all = TradeReasons.For(isSell: false).Concat(TradeReasons.For(isSell: true))
                              .Select(x => TradeReasons.GroupOf(x.Code)).ToList();
        foreach (var g in new[] { "纪律", "判断", "外部" })
            Assert.Contains(g, all);
    }

    [Fact]
    public void 买卖的选项不串台()
    {
        var buy = TradeReasons.For(isSell: false).Select(x => x.Code).ToHashSet();
        var sell = TradeReasons.For(isSell: true).Select(x => x.Code).ToHashSet();

        // 卖出的理由不该出现在买入里，反之亦然——串台会让"卖出笔挂着方法给出信号"这种记录出现
        Assert.DoesNotContain(TradeReasons.Target, buy);
        Assert.DoesNotContain(TradeReasons.Stop, buy);
        Assert.DoesNotContain(TradeReasons.Signal, sell);
        Assert.Empty(buy.Intersect(sell));
    }

    [Fact]
    public void 不认识的码归组返回null_不抛()
    {
        Assert.Null(TradeReasons.GroupOf(""));
        Assert.Null(TradeReasons.GroupOf(null));
        Assert.Null(TradeReasons.GroupOf("手改过的JSON里的乱码"));
    }

    // ⚠ TradeLot 的序列化用例去掉了：那个类在 Analyzer（自包含单文件 exe），
    //   测试项目引用它会撞 NETSDK1151（见 csproj 里 Fetcher 那段注释）。
    //   "JSON 里没有的字段取默认值" 是 System.Text.Json 的标准行为，不值得为它引一个 exe。
}
