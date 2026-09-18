using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 「抓完一天要不要动『确认没有数据』名单」的判据（2026-09-18 从 <c>FetchOrchestrator</c>
/// 里那两份重复实现抽出来，见 <see cref="DailyNoDataGate"/>）。
///
/// ⚠ 这套判据错一处就是**永久漏数据**：名单是一次定案的，闸③直接跳过，
/// 那天往后一个请求都不会再发。所以它跟 <see cref="DailyBackfillGate"/> 一样是纯函数、单独测。
/// </summary>
public class DailyNoDataGateTests
{
    private static readonly DateOnly Today = new(2026, 9, 18);

    private static NoDataAction Eval(int rows, int daysAgo, bool confirmed = false) =>
        DailyNoDataGate.Evaluate(rows, Today.AddDays(-daysAgo), Today, confirmed);

    [Fact]
    public void 抓到数据_什么都不做() => Assert.Equal(NoDataAction.None, Eval(rows: 100, daysAgo: 10));

    /// <summary>之前定过案、现在有数据了 → 撤销（源后来补上了）。</summary>
    [Fact]
    public void 抓到数据且之前定过案_撤销() =>
        Assert.Equal(NoDataAction.Revoke, Eval(rows: 100, daysAgo: 10, confirmed: true));

    [Fact]
    public void 够旧的空_定案() => Assert.Equal(NoDataAction.Confirm, Eval(rows: 0, daysAgo: 5));

    /// <summary>
    /// **当天的空不能定案**——两所是 T+1、龙虎榜当晚才出，当天拿到 0 行多半只是"还没发"。
    /// 定了案那天就被永久钉死了。这是这套判据存在的全部理由。
    /// </summary>
    [Theory]
    [InlineData(0)]   // 今天
    [InlineData(1)]   // 昨天
    [InlineData(2)]   // 前天
    public void 太新的空_不定案(int daysAgo) => Assert.Equal(NoDataAction.None, Eval(rows: 0, daysAgo));

    /// <summary>边界：正好第 3 天该定案（cutoff 是"不晚于 today-3"）。</summary>
    [Fact]
    public void 边界_第三天定案() => Assert.Equal(NoDataAction.Confirm, Eval(rows: 0, daysAgo: 3));

    /// <summary>已经在名单里的空，不用重复写。</summary>
    [Fact]
    public void 已在名单里的空_不重复定案() =>
        Assert.Equal(NoDataAction.None, Eval(rows: 0, daysAgo: 10, confirmed: true));
}
