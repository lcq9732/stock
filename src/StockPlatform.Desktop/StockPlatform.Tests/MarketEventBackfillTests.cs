using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【拉取市场事件】的「首次整段回补」（2026-09-19）。
///
/// 这个模式是给**改了排序键之后**用的：排序键排不到主键末列时，深分页会跨页重复 + 遗漏——
/// 重复那半会被批内主键去重自检喊出来（见 <see cref="MarketEventDedupeTests"/>），
/// **遗漏那半一声不吭**，只能整段重取一遍才补得回来。机构调研 2026-09-19 补了 tieBreaker
/// （<c>RECEIVE_OBJECT,RECEIVE_START_DATE,NUM</c>），这一轮回补就是给它用的。
///
/// ⚠ 在此之前，回填那条路是**通的但没接线**：<c>RunFetchMarketEventsAsync</c> 声明了
/// <c>forceStart</c> 参数却没有任何调用方传值，目录里也没声明这个模式，于是界面上根本点不到。
/// 这一组就是钉住"点得到"。
/// </summary>
public class MarketEventBackfillTests
{
    /// <summary>目录里没声明的话，界面上那个下拉框里就没有这一项，功能等于不存在。</summary>
    [Fact]
    public void 市场事件声明了首次整段回补模式()
    {
        var info = FetchTaskCatalog.All.Single(x => x.Id == FetchActionId.FetchMarketEvents);
        Assert.True(info.SupportedModes.HasFlag(FetchMode.FirstBackfill), "少了「首次整段回补」");
        Assert.True(info.SupportedModes.HasFlag(FetchMode.Incremental), "日常那条不能丢");
        Assert.True(info.SupportedModes.HasFlag(FetchMode.FillBacklog), "补待办那条不能丢");
    }

    /// <summary>
    /// ⭐ 选了模式却**静默回落**是这个项目踩过的坑：<see cref="FetchPlanItem.EffectiveMode"/> 读到
    /// 不在 <c>SupportedModes</c> 里的值会悄悄退回「增量」，于是用户以为在整段回补、其实早就停了
    /// （分档资金流那次：门槛从 1 行跳回 3 行，谁都没发现）。所以声明和生效要分别钉。
    /// </summary>
    [Fact]
    public void 选了整段回补不会被静默回落成增量()
    {
        var item = new FetchPlanItem
        {
            Action = FetchActionId.FetchMarketEvents,
            Mode = FetchMode.FirstBackfill,
        };

        Assert.Equal(FetchMode.FirstBackfill, item.EffectiveMode);
    }

    /// <summary>默认仍是增量——回补是手工跑一次的活，不该因为加了这个模式就天天整段重取。</summary>
    [Fact]
    public void 默认模式还是增量()
    {
        var plan = FetchPlan.CreateDefault();
        plan.Normalize();

        var item = plan.AllItems.Single(i => i.Action == FetchActionId.FetchMarketEvents);
        Assert.Equal(FetchMode.Incremental, item.EffectiveMode);
    }
}
