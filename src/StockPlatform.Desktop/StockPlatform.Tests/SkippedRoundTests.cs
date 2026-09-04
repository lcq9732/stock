using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 「根本没开工」的那一轮不能记成完成（2026-09-04）。
///
/// 踩到的实况：板块抓取时数据源还在限流熔断里，直接返回不开工——界面上却是绿勾
/// 「✔ 09:25 完成」，可它一行数据都没抓。比文案更要命的是
/// <see cref="FetchPlanItem.AlreadyRanOn"/> 只认 <see cref="RunOutcome.Ok"/>，
/// 记成完成之后**今天就再也不会跑了**，等于白等一天。
/// </summary>
public class SkippedRoundTests
{
    /// <summary>
    /// 从默认计划里取真实的那一项来改——裸 new 一个 FetchPlanItem 不行：
    /// AlreadyRanOn 还要问所属组的"应跑时点"（Owner），脱离计划的孤儿项一律算没跑过。
    /// </summary>
    private static FetchPlanItem NewItem(RunOutcome outcome, DateTime when)
    {
        var plan = FetchPlan.CreateDefault();
        plan.Normalize();
        var item = plan.AllItems.First(i => i.Action == FetchActionId.StepBoardList);
        item.Enabled = true;
        item.LastStart = when;
        item.LastEnd = when;
        item.LastOutcome = outcome;
        item.LastNothingToDo = true;   // 分批任务用它表示"这一期做完了"，这里不测分批
        return item;
    }

    [Fact]
    public void 记成完成的轮次今天不会再跑()
    {
        var now = DateTime.Today.AddHours(10);
        // 这是"正常跑完"该有的行为：今天不用再来一遍
        Assert.True(NewItem(RunOutcome.Ok, now.AddMinutes(-30)).AlreadyRanOn(now));
    }

    [Fact]
    public void 记成跳过的轮次今天还有机会补()
    {
        var now = DateTime.Today.AddHours(10);
        // 熔断期跳过的必须还能再来——这正是当初记成 Ok 造成的损失
        Assert.False(NewItem(RunOutcome.Skipped, now.AddMinutes(-30)).AlreadyRanOn(now));
    }

    [Fact]
    public void 失败和被停止的轮次也不算跑过()
    {
        var now = DateTime.Today.AddHours(10);
        Assert.False(NewItem(RunOutcome.Failed, now.AddMinutes(-30)).AlreadyRanOn(now));
        Assert.False(NewItem(RunOutcome.Cancelled, now.AddMinutes(-30)).AlreadyRanOn(now));
    }

    [Theory]
    // 状态列要能把两种 Skipped 分开：前置失败＝这一项没法跑了，限流熔断＝还没轮到、等会儿自己来。
    [InlineData("东财 push2 限流熔断中，预计 09:38 恢复（还有约 14 分钟）", "09:38")]
    [InlineData("东财 push2his 限流熔断中，预计 23:05 恢复（还有约 2 分钟）", "23:05")]
    [InlineData("跳过：前置的【拉取财务报表】今天失败了", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void 从跳过原因里认出恢复时刻(string? message, string? expected)
    {
        // 跟 MainViewModel.PausedResumeAt 同一套判据；认不出来就退回老文案，不至于显示错
        string? actual = null;
        if (message is { Length: > 0 } msg && msg.Contains("熔断中"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(msg, @"预计 (\d\d:\d\d) 恢复");
            actual = m.Success ? m.Groups[1].Value : null;
        }
        Assert.Equal(expected, actual);
    }
}
