using StockPlatform.Data.Orchestration;
using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 结果分类的判据（<see cref="RunOutcomeRules"/>）。
///
/// 这些用例存在的理由是 2026-09-21 那笔账：判据在**计划自动跑**和**手动点执行**两条路上各有
/// 一份，手动那份漏了 <see cref="FetchResult.Failed"/>，于是【分档资金流快照】被东财切断、
/// 任务如实报失败，界面上记的却是绿勾「完成」——而 <see cref="FetchPlanItem.AlreadyRanOn"/>
/// 只认 <see cref="RunOutcome.Ok"/>，当天计划就不再回来补了。
/// 判据合成一份之后，这里钉住它的四种取值。
/// </summary>
public class RunOutcomeRulesTests
{
    [Fact]
    public void 整项失败要记失败_不是完成()
    {
        var r = new FetchResult { Failed = true, Errors = { "push2delay 第 1 页被切断" } };

        Assert.Equal(RunOutcome.Failed, RunOutcomeRules.Classify(r));
        Assert.Equal("push2delay 第 1 页被切断", RunOutcomeRules.FailureReason(r));
    }

    [Fact]
    public void 失败但一条错误都没给_也要有句话和一条账()
    {
        var r = new FetchResult { Failed = true };

        Assert.Equal(RunOutcome.Failed, RunOutcomeRules.Classify(r));
        Assert.False(string.IsNullOrWhiteSpace(RunOutcomeRules.FailureReason(r)));
        // 「这一项没干成」本身就是一条账，不能显示成「0 条错误」
        Assert.Equal(1, RunOutcomeRules.FailureErrorCount(r));
    }

    [Fact]
    public void 没开工压过失败_今天还有机会补()
    {
        // 两个字段同时有值时按「本轮没开工」算：那是"数据源熔断中，等会儿再来"，
        // 记成 Failed 会变成一笔要人管的账，而它并不需要人管。
        var r = new FetchResult { Failed = true, SkippedReason = "东财 push2delay 限流熔断中" };

        Assert.Equal(RunOutcome.Skipped, RunOutcomeRules.Classify(r));
    }

    [Fact]
    public void 干完了就是完成_带几条错误也一样()
    {
        Assert.Equal(RunOutcome.Ok, RunOutcomeRules.Classify(new FetchResult()));
        // 逐只抓的任务常有"5500 只里 3 只失败"，那是完成里带几条错误，不该标红
        Assert.Equal(RunOutcome.Ok,
            RunOutcomeRules.Classify(new FetchResult { Errors = { "600000 抓失败" } }));
    }

    [Fact]
    public void 没有结果对象的老任务_行为不变()
    {
        // 老编排层的任务返回 null，按加这个判据之前一样算完成
        Assert.Equal(RunOutcome.Ok, RunOutcomeRules.Classify(null));
    }
}
