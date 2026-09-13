using StockPlatform.Data.Orchestration;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【重新拉取失败】的**分派**（2026-09-13 二期）——它现在自己不抓任何东西，
/// 只是读待办清单、按 <see cref="RetryTodo.TaskId"/> 挨个调对应任务的"补待办"入口。
///
/// 这里测的是"该调哪些任务、按什么顺序"。真正的执行一跑就是几千个网络请求，测不了，
/// 所以把顺序抽成了 <see cref="FetchOrchestrator.DispatchOrder"/> 这个纯函数。
/// </summary>
public class RetryDispatchTests
{
    private static RetryBacklog Backlog(Manifest m)
    {
        m.MigrateLegacyTodos();
        return RetryBacklog.From(m);
    }

    private static MissingBarRange Gap(string code, string gran) => new()
    {
        Code = code, Granularity = gran,
        From = new DateTime(2026, 9, 4), To = new DateTime(2026, 9, 10), Days = 5,
    };

    [Fact]
    public void 没有待办就一个任务都不调()
    {
        Assert.Empty(FetchOrchestrator.DispatchOrder(Backlog(new Manifest())));
    }

    [Fact]
    public void 三个口径的空洞_分派给三个不同的任务()
    {
        // 这正是二期要的：以前所有空洞按口径归堆、由重取自己抓，
        // 现在归属在存储里，谁的活谁干。
        var m = new Manifest();
        m.MissingBars.Add(Gap("000001", Granularity.Day));
        m.MissingBars.Add(Gap("000001", Granularity.DayHfq));
        m.MissingBars.Add(Gap("000001", Granularity.DayRaw));

        Assert.Equal(
            new[] { RetryTaskIds.StockDayBars, RetryTaskIds.StockHfqBars, RetryTaskIds.StockRawBars },
            FetchOrchestrator.DispatchOrder(Backlog(m)));
    }

    [Fact]
    public void 便宜的整轮扫描排在重活前面()
    {
        var m = new Manifest();
        m.MissingBars.Add(Gap("000001", Granularity.DayRaw));   // 最重：可能上千段、几小时
        m.FailedMarketCapCodes.Add("000002");                   // 最轻：一次请求拿全市场
        m.FailedNetInflowCodes.Add("000003");

        var order = FetchOrchestrator.DispatchOrder(Backlog(m));

        Assert.Equal(RetryTaskIds.Roster, order[0]);
        Assert.Equal(RetryTaskIds.NetInflow, order[1]);
        Assert.Equal(RetryTaskIds.StockRawBars, order[^1]);
    }

    [Fact]
    public void 同一个任务有好几类待办_也只调它一次()
    {
        // 个股日K·前复权同时欠着：抓取失败的、当天日线没到位的、体检查出的历史空洞。
        // 分派按任务去重，进去之后由 RunFillBacklogAsync 一次把几类都补掉。
        var m = new Manifest { MissingDayDate = new DateTime(2026, 9, 11) };
        m.FailedCodes.Add("000001");
        m.MissingDayCodes.Add("000002");
        m.MissingBars.Add(Gap("000003", Granularity.Day));

        var order = FetchOrchestrator.DispatchOrder(Backlog(m));

        Assert.Equal(new[] { RetryTaskIds.StockDayBars }, order);
    }

    [Fact]
    public void 分派到的每个任务都得是真实存在的()
    {
        var m = new Manifest { MissingDayDate = new DateTime(2026, 9, 11) };
        m.MissingDayCodes.Add("000001");
        m.FailedMarketCapCodes.Add("000002");
        m.FailedNetInflowCodes.Add("000003");
        m.FailedIndexConsCodes.Add("000300");
        m.FailedIndexWeightCodes.Add("000300");
        m.FailedShareholderCodes.Add("000004");
        m.FailedDividendCodes.Add("000005");
        m.MissingNetInflowDays.Add(new MissingDayRetry { Day = new DateTime(2026, 9, 9) });
        m.MissingBars.Add(Gap("000006", Granularity.DayHfq));

        foreach (var id in FetchOrchestrator.DispatchOrder(Backlog(m)))
            Assert.True(Enum.TryParse<FetchActionId>(id, out _), $"\"{id}\" 不是一个有效的 FetchActionId");
    }

    [Fact]
    public void 支持只补待办的那几项_catalog里要声明这个模式()
    {
        // 没声明的话界面模式下拉里选不到，用户就没法单独跑"把这一项欠的补上"。
        foreach (var id in new[]
                 {
                     FetchActionId.StepStockDayBars, FetchActionId.StepStockHfqBars,
                     FetchActionId.StepStockRawBars, FetchActionId.StepEtfBars,
                     FetchActionId.StepIndexBars, FetchActionId.StepNetInflow,
                 })
        {
            var action = FetchTaskCatalog.All.Single(a => a.Id == id);
            Assert.True(action.SupportedModes.HasFlag(FetchMode.FillBacklog),
                $"【{action.Name}】没声明 FillBacklog 模式");
        }
    }
}
