using StockPlatform.Data.Sqlite;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 全库体检里"日频表有空日"那几句话的措辞（2026-09-13）。
///
/// 守的是一个真实的误导：2026-09-13 那轮体检报了「资金净流入 9 天一行都没有」，
/// 后面跟着一句写死的"补法：空日已记进待补名单，跑一次【重新拉取失败】即可（约 1.75 小时）"。
/// 可那 9 天**一天都没进名单**——它们早就补满两轮拿不到、进了 ConfirmedNetInflowDays 白名单，
/// QueueMissingNetInflowDays 会把它们全过滤掉。照那句话去跑，白等 1.75 小时。
///
/// 讽刺的是旁边"其中 N 天已记入待补名单"那半句是带条件的、正确地没出现，
/// 于是同一行里两句话自相矛盾。
/// </summary>
public class DailyTableReportTests
{
    private static readonly SqliteDailyTableAuditor.Spec NetInflow =
        new("NetInflow", "period_start", "资金净流入", "空日已记进待补名单，跑一次【重新拉取失败】即可");

    private static readonly SqliteDailyTableAuditor.Spec Lhb =
        new("Lhb", "trade_date", "龙虎榜", "【龙虎榜】按天重跑");

    [Fact]
    public void 空日一天都没进名单_不能说跑一次重取就能补()
    {
        var how = FullAuditTask.HowToFill(NetInflow, emptyCount: 9, queued: 0);

        Assert.DoesNotContain("已记进待补名单", how);
        Assert.Contains("不会", how);
        Assert.Contains("数据源确实没有", how);
        // 得告诉人怎么推翻这个结论，否则这条信息是个死胡同
        Assert.Contains("彻底重查", how);
    }

    [Fact]
    public void 空日一天都没进名单_那句提示也要说清楚()
    {
        var note = FullAuditTask.EmptyDaysNote(NetInflow, emptyCount: 9, queued: 0);

        Assert.Contains("数据源确实没有", note);
        Assert.DoesNotContain("已记入待补名单", note);
    }

    [Fact]
    public void 空日全进了名单_照旧告诉人跑重取()
    {
        var how = FullAuditTask.HowToFill(NetInflow, emptyCount: 9, queued: 9);
        var note = FullAuditTask.EmptyDaysNote(NetInflow, emptyCount: 9, queued: 9);

        Assert.Equal(NetInflow.HowToFill, how);
        Assert.Contains("9 天已记入待补名单", note);
    }

    [Fact]
    public void 只进了一部分_两半都要交代()
    {
        var note = FullAuditTask.EmptyDaysNote(NetInflow, emptyCount: 9, queued: 4);

        Assert.Contains("4 天已记入待补名单", note);
        Assert.Contains("其余 5 天已判定「数据源确实没有」", note);
    }

    [Fact]
    public void 别的表不记待补名单_不该冒出这套说辞()
    {
        // 只有资金净流入这一张表会把空日记进待补名单，别的表只报不记、各有各的补法。
        Assert.Equal("", FullAuditTask.EmptyDaysNote(Lhb, emptyCount: 244, queued: 0));
        Assert.Equal(Lhb.HowToFill, FullAuditTask.HowToFill(Lhb, emptyCount: 244, queued: 0));
    }

    [Fact]
    public void 没有空日时不改写补法()
    {
        Assert.Equal(NetInflow.HowToFill, FullAuditTask.HowToFill(NetInflow, emptyCount: 0, queued: 0));
    }
}
