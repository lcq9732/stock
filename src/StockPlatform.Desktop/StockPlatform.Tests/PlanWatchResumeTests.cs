using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【回购公告进展】的断点续抓判据（<c>PlanWatchTask.ResolveSearchWindow</c>）。
///
/// ════ 这组测试是为一个真实事故写的（2026-09-11）════
/// 首轮在生产上跑到 2026-07-02 被中断。当时的实现是
/// <c>start = today.AddDays(-(水位线有没有 ? 14 : 90))</c>——水位线只决定**回看几天**，
/// 不决定**起点**。于是再点一次会从 today−14 开始，**07-03～08-27 这 56 天永久跳过**，
/// 不报任何错。宁德时代 07-25 那份带「价格上限 573 元」的方案公告正好落在缺口里。
///
/// 这类"不报错、但数据少了一截"是这套设计反复要躲的坑，所以起点逻辑必须被判据钉住。
/// </summary>
public class PlanWatchResumeTests
{
    private static readonly DateOnly Today = new(2026, 9, 11);

    /// <summary>库里空＝首轮，回看 90 天。再往前正文取不到（东财只按该股最近 100 条公告匹配）。</summary>
    [Fact]
    public void 首轮_从今天往回90天()
    {
        var (start, isFirst) = PlanWatchTask.ResolveSearchWindow(null, Today);

        Assert.True(isFirst);
        Assert.Equal(new DateOnly(2026, 6, 13), start);   // 09-11 − 90
    }

    /// <summary>日更：水位线是昨天，退 14 天＝设计里的增量窗口，行为跟修复前一致。</summary>
    [Fact]
    public void 日更_水位线是昨天_退14天()
    {
        var (start, isFirst) = PlanWatchTask.ResolveSearchWindow(new DateTime(2026, 9, 10), Today);

        Assert.False(isFirst);
        Assert.Equal(new DateOnly(2026, 8, 27), start);   // 09-10 − 14
    }

    /// <summary>
    /// ★ 事故场景：首轮跑到 07-02 被中断，隔了 70 天再跑。
    /// 必须从 <b>水位线</b>−14 续，而不是从 today−14——后者会跳过 56 天。
    /// </summary>
    [Fact]
    public void 中断后续抓_从水位线算而不是从今天算()
    {
        var (start, isFirst) = PlanWatchTask.ResolveSearchWindow(new DateTime(2026, 7, 2), Today);

        Assert.False(isFirst);
        Assert.Equal(new DateOnly(2026, 6, 18), start);           // 07-02 − 14，缺口被覆盖
        Assert.True(start < new DateOnly(2026, 7, 2));            // 关键：起点在水位线之前
        Assert.NotEqual(new DateOnly(2026, 8, 28), start);        // 修复前的错误答案
    }

    /// <summary>
    /// 水位线很旧时不能无限往回捞：超出首轮窗口那段正文取不到，
    /// 抓回来只是一堆没有数值的空壳，在库里长得像"这些公司都没在回购"。
    /// </summary>
    [Fact]
    public void 水位线过旧_夹在首轮窗口内()
    {
        var (start, _) = PlanWatchTask.ResolveSearchWindow(new DateTime(2025, 1, 1), Today);

        Assert.Equal(new DateOnly(2026, 6, 13), start);   // 就是首轮起点，不再往前
    }

    /// <summary>水位线正好落在首轮窗口边缘时，退 14 天会越界，同样要夹住。</summary>
    [Fact]
    public void 水位线在首轮窗口边缘_不越界()
    {
        var (start, _) = PlanWatchTask.ResolveSearchWindow(new DateTime(2026, 6, 20), Today);

        Assert.Equal(new DateOnly(2026, 6, 13), start);   // 06-20 − 14 = 06-06 < 06-13，夹回
    }

    /// <summary>水位线在未来（手工塞过数据）也不能把起点推到今天之后，否则循环一天都不跑。</summary>
    [Fact]
    public void 水位线在未来_起点不超过今天()
    {
        var (start, _) = PlanWatchTask.ResolveSearchWindow(new DateTime(2027, 1, 1), Today);

        Assert.True(start <= Today);
    }

    /// <summary>连着跑两轮不该原地踏步：第二轮的起点要比第一轮晚（水位线前进了）。</summary>
    [Fact]
    public void 水位线前进_起点跟着前进()
    {
        var (early, _) = PlanWatchTask.ResolveSearchWindow(new DateTime(2026, 7, 2), Today);
        var (later, _) = PlanWatchTask.ResolveSearchWindow(new DateTime(2026, 8, 20), Today);

        Assert.True(later > early);
    }
}
