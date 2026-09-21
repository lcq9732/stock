using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 「这次拿回来的 ETF 名单能不能用」（2026-09-21 随【ETF日K】迁移抽出来，见 <see cref="EtfListGuard"/>）。
///
/// ⚠ 这道闸挡的是**静默**的错：名单接口被限流时不报错、只少给，拿半截名单跑的结果是
/// 没在名单里的 ETF 整轮不被处理，而日志看起来一切正常。
/// </summary>
public class EtfListGuardTests
{
    [Fact]
    public void 名单完整_用拿回来的()
        => Assert.Equal(EtfListGuard.Decision.UseFetched, EtfListGuard.Judge(fetchedCount: 1659, localCount: 1655));

    /// <summary>少一点点（还在 5% 容差内）不算半截——ETF 本来就有摘牌的。</summary>
    [Fact]
    public void 少一点点_仍用拿回来的()
        => Assert.Equal(EtfListGuard.Decision.UseFetched, EtfListGuard.Judge(1600, 1655));

    [Fact]
    public void 少超过5个百分点_改用库里存量()
        => Assert.Equal(EtfListGuard.Decision.UseLocal, EtfListGuard.Judge(1000, 1655));

    [Fact]
    public void 接口整个没给_库里有存量_改用存量()
        => Assert.Equal(EtfListGuard.Decision.UseLocal, EtfListGuard.Judge(0, 1655));

    /// <summary>两边都空：这一轮真的没得跑，记成「跳过」而不是「完成」——
    /// 记成完成的话 AlreadyRanOn 会认为今天已经跑过，接口恢复了也不会再试。</summary>
    [Fact]
    public void 两边都空_整项跳过()
        => Assert.Equal(EtfListGuard.Decision.Abort, EtfListGuard.Judge(0, 0));

    /// <summary>头一回跑（库里没存量、接口给了）——当然用拿回来的。</summary>
    [Fact]
    public void 首次跑_库里没存量_用拿回来的()
        => Assert.Equal(EtfListGuard.Decision.UseFetched, EtfListGuard.Judge(1659, 0));
}

/// <summary>
/// 「这一轮跑完，失败名单该变成什么样」（<see cref="FailedTodoRule"/>）。
/// 规则一句话：**只动本轮碰过的**。它的错法是静默的——简化成"清空重写"的话，
/// 任何一次部分跑都会把没轮到的票从名单里抹掉，界面显示"没有待办"而数据一直缺着。
/// </summary>
public class FailedTodoRuleTests
{
    [Fact]
    public void 碰过且这次成了_移出名单()
        => Assert.Empty(FailedTodoRule.Update(["600000"], attempted: ["600000"], failedThisRun: []));

    [Fact]
    public void 碰过且又失败_留在名单()
        => Assert.Equal(["600000"], FailedTodoRule.Update(["600000"], ["600000"], ["600000"]));

    /// <summary>这一条是重点：分批到点收尾、中途停止时，没轮到的那些不能被抹掉。</summary>
    [Fact]
    public void 没碰到的_原样留着()
        => Assert.Equal(["600001"], FailedTodoRule.Update(["600001"], attempted: ["600000"], failedThisRun: []));

    [Fact]
    public void 新失败的_加进名单并排序()
        => Assert.Equal(["000001", "600000"],
            FailedTodoRule.Update(["600000"], ["600000", "000001"], ["600000", "000001"]));

    [Fact]
    public void 写回manifest_按taskId分域()
    {
        var m = new Manifest();
        FailedTodoRule.SetFailed(m, RetryTaskIds.EtfBars, attempted: ["sh510300"], failedThisRun: ["sh510300"]);
        FailedTodoRule.SetFailed(m, RetryTaskIds.IndexBars, attempted: ["sh000001"], failedThisRun: []);

        Assert.Equal(["sh510300"],
            m.Todo(RetryTaskIds.EtfBars, RetryTodoKind.Failed)!.Targets.Select(t => t.Code));
        // 空名单不留空壳（SetTodo 的约定）
        Assert.Null(m.Todo(RetryTaskIds.IndexBars, RetryTodoKind.Failed));
    }
}
