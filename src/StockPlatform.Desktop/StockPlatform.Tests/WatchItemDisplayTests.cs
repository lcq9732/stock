using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 左表那一列的拼法（2026-09-11）。目标形状是用户给的样例：
/// <code>2026-11-08（还有 58 天） 限售解禁 100 万股，占流通 2%</code>
/// 日期排最前——左表是待办，先看"什么时候"再看"什么事"。
/// </summary>
public class WatchItemDisplayTests
{
    [Fact]
    public void 日期提到事项名之前()
    {
        var s = WatchItemDisplay.Compose("限售解禁", "2026-11-08（还有 58 天），100 万股，占流通 2%");

        Assert.Equal("2026-11-08（还有 58 天） 限售解禁 100 万股，占流通 2%", s);
        Assert.StartsWith("2026-11-08", s);   // 日期必须在最前
    }

    [Fact]
    public void 业绩预告_同样是日期在前()
    {
        var s = WatchItemDisplay.Compose("业绩预告",
            "2026-09-10（1 天前），2026-09-30 报告期，预增，净利同比 54.0~58.89%");

        Assert.StartsWith("2026-09-10（1 天前） 业绩预告", s);
        Assert.Contains("预增", s);
        Assert.Contains("54.0~58.89%", s);
    }

    /// <summary>只有日期、没有别的数据时也不能丢事项名。</summary>
    [Fact]
    public void 只有日期()
        => Assert.Equal("2026-08-31（11 天前） 定期报告预约披露",
            WatchItemDisplay.Compose("定期报告预约披露", "2026-08-31（11 天前）"));

    /// <summary>
    /// 不是日期开头的保持 "事项：内容"——回购那一路是
    /// 「进展（已回购 8.5 亿元）」这种，硬拆会拆坏。
    /// </summary>
    [Fact]
    public void 非日期开头_保持原格式()
        => Assert.Equal("回购方案进行中：进展（已回购 8.52 亿元）",
            WatchItemDisplay.Compose("回购方案进行中", "进展（已回购 8.52 亿元）"));

    /// <summary>★ 取不到数据的事项只显示名字，不编——这是用户明确要求的。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 没有数据就只有事项名(string? detail)
        => Assert.Equal("跌破 MA20", WatchItemDisplay.Compose("跌破 MA20", detail));

    [Fact]
    public void 日期后面是空的_不留尾巴()
        => Assert.Equal("2026-11-08 限售解禁", WatchItemDisplay.Compose("限售解禁", "2026-11-08，"));
}
