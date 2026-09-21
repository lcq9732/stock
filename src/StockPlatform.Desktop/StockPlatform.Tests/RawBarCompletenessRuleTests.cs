using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 「不复权日线补齐了没有」（2026-09-21 随个股三口径迁移抽出来，见 <see cref="RawBarCompletenessRule"/>）。
///
/// ⚠ 这条判据只比尾巴的话会**静默丢掉前面几年**：日更那根按回看年数先把最近 3 年填上，
/// 判据一归零就显示「已补齐」，2026-09-01 实测 5781 只里有 5232 只这么卡住。
/// </summary>
public class RawBarCompletenessRuleTests
{
    private static readonly DateTime DayEarliest = new(2016, 1, 4);
    private static readonly DateTime DayLatest = new(2026, 9, 18);

    [Fact]
    public void 两头都追上_算齐()
        => Assert.True(RawBarCompletenessRule.IsComplete(DayEarliest, DayLatest, DayEarliest, DayLatest));

    /// <summary>这一条就是那次事故：尾巴追上了，开头只有 3 年。</summary>
    [Fact]
    public void 只有尾巴追上_不算齐()
        => Assert.False(RawBarCompletenessRule.IsComplete(
            DayEarliest, DayLatest, rawEarliest: new DateTime(2023, 9, 1), rawLatest: DayLatest));

    [Fact]
    public void 开头够早但尾巴落后_不算齐()
        => Assert.False(RawBarCompletenessRule.IsComplete(
            DayEarliest, DayLatest, DayEarliest, rawLatest: new DateTime(2026, 8, 1)));

    [Fact]
    public void 一根都没有_不算齐()
        => Assert.False(RawBarCompletenessRule.IsComplete(DayEarliest, DayLatest, null, null));

    /// <summary>开头晚几天是数据源口径差异，留 30 天容差——否则这些票每轮都被当成"没补齐"反复重抓。</summary>
    [Theory]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void 开头容差30天(int laterDays, bool complete)
        => Assert.Equal(complete, RawBarCompletenessRule.IsComplete(
            DayEarliest, DayLatest, DayEarliest.AddDays(laterDays), DayLatest));

    /// <summary>尾巴比前复权还新（补历史时抓到了更新的一天）当然算齐。</summary>
    [Fact]
    public void 尾巴更新_算齐()
        => Assert.True(RawBarCompletenessRule.IsComplete(
            DayEarliest, DayLatest, DayEarliest, DayLatest.AddDays(1)));
}

/// <summary>
/// 后复权/不复权那道熔断闸（<see cref="HfqProbeGate"/>）——接口挂了就别对着几千只票空跑几小时。
/// </summary>
public class HfqProbeGateTests
{
    /// <summary>样本太少不判：偶发失败会把正常的一轮误判成"接口挂了"。</summary>
    [Theory]
    [InlineData(10, 10)]
    [InlineData(29, 29)]
    public void 没到门槛_不中止(int finished, int failed)
        => Assert.False(HfqProbeGate.ShouldAbort(finished, failed));

    [Fact]
    public void 到门槛且几乎全失败_中止()
        => Assert.True(HfqProbeGate.ShouldAbort(finished: 30, failed: 30));

    /// <summary>90% 是**严格大于**：刚好九成不中止（边界上宁可多跑一批）。</summary>
    [Fact]
    public void 刚好九成_不中止()
        => Assert.False(HfqProbeGate.ShouldAbort(finished: 100, failed: 90));

    [Fact]
    public void 超过九成_中止()
        => Assert.True(HfqProbeGate.ShouldAbort(finished: 100, failed: 91));

    [Fact]
    public void 失败率不高_不中止()
        => Assert.False(HfqProbeGate.ShouldAbort(finished: 300, failed: 30));
}
