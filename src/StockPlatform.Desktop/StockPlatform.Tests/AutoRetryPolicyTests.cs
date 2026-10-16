using StockPlatform.Data.Orchestration;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 自动重试要不要排（2026-10-16 抽出 <see cref="AutoRetryPolicy"/>）。
///
/// 起因：【重新拉取失败】补完、名单已清零（日志上一行刚写"失败名单已全部清零"），界面还是排了一次 21:00 的
/// 自动重试，括号里报的是这一轮开始前的名单——判的时候用的是还没刷新的缓存。现在界面排程前先现读 manifest，
/// 判断交给这个纯函数；这里钉住判断本身，以及"现读的空名单就不排"。
/// </summary>
public class AutoRetryPolicyTests
{
    private static RetryBacklog Backlog(params (string Task, string Kind, int N)[] todos)
    {
        var m = new Manifest();
        foreach (var (task, kind, n) in todos)
            m.SetTodo(task, kind, Enumerable.Range(0, n).Select(i => new RetryTarget
            {
                Code = $"60{i:D4}", Gran = Granularity.DayRaw,
                From = new DateTime(2023, 8, 1), To = new DateTime(2023, 8, 30), Days = 1,
            }).ToList());
        return RetryBacklog.From(m);
    }

    /// <summary>2026-10-16 的原样：补完之后 manifest 里一条待办都没有——现读的名单是空的，就不该排。</summary>
    [Fact]
    public void 补完名单清零_不排()
    {
        var before = Backlog((RetryTaskIds.StockRawBars, RetryTodoKind.Gap, 26));
        Assert.Equal(AutoRetryDecision.Schedule, AutoRetryPolicy.Decide(before, 0, 0, 3));   // 用旧缓存就会排

        var fresh = Backlog();                                                              // 补完现读
        Assert.Equal(AutoRetryDecision.Cleared, AutoRetryPolicy.Decide(fresh, 0, 0, 3));
    }

    [Fact]
    public void 还有待办_第一次就排()
        => Assert.Equal(AutoRetryDecision.Schedule,
            AutoRetryPolicy.Decide(Backlog((RetryTaskIds.StockDayBars, RetryTodoKind.Gap, 3)), 0, 0, 3));

    /// <summary>自动重试过一轮、剩下的没减少：多半是停牌或数据源确实没有，再试也白试。</summary>
    [Fact]
    public void 一轮之后没减少_停()
    {
        var b = Backlog((RetryTaskIds.StockDayBars, RetryTodoKind.Gap, 3));
        Assert.Equal(AutoRetryDecision.NoProgress, AutoRetryPolicy.Decide(b, 1, b.ActionableCount, 3));
        Assert.Equal(AutoRetryDecision.Schedule, AutoRetryPolicy.Decide(b, 1, b.ActionableCount + 1, 3));
    }

    [Fact]
    public void 满了轮数_停()
    {
        var b = Backlog((RetryTaskIds.StockDayBars, RetryTodoKind.Gap, 3));
        Assert.Equal(AutoRetryDecision.MaxRounds, AutoRetryPolicy.Decide(b, 3, b.ActionableCount + 5, 3));
    }
}
