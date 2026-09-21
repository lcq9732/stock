using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// **动作目录本身的自洽性**（2026-09-02 随【拉取全部】拆成 13 个原子项新增）。
///
/// 这里测的都是"加东西时最容易忘的那一步"：加了枚举忘了写目录条目、写了模板引用一个不存在的
/// 动作、依赖指向自己。这些在运行时的表现是**计划页整页崩**或者某一项静默不跑，
/// 而单元测试一秒就能拦住。
/// </summary>
public class FetchTaskCatalogTests
{
    /// <summary>
    /// 每个 <see cref="FetchActionId"/> 都必须在目录里有条目。
    /// 漏了的话 <see cref="FetchTaskCatalog.Info"/> 会抛 KeyNotFoundException——
    /// 而它被 FetchPlanItem.Info 属性调用，等于整个计划页一读就炸。
    /// </summary>
    [Fact]
    public void Every_action_has_a_catalog_entry()
    {
        foreach (FetchActionId id in Enum.GetValues<FetchActionId>())
        {
            var info = FetchTaskCatalog.Info(id);
            Assert.Equal(id, info.Id);
            Assert.False(string.IsNullOrWhiteSpace(info.Name), $"{id} 没有名字");
            Assert.False(string.IsNullOrWhiteSpace(info.DataSource), $"{id} 没写数据源");
        }
    }

    /// <summary>目录里不能有重复条目——重复会让 ToDictionary 直接抛。</summary>
    [Fact]
    public void Catalog_has_no_duplicate_actions()
    {
        var dupes = FetchTaskCatalog.All.GroupBy(a => a.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dupes);
    }

    /// <summary>前置（软硬都算）不能指向自己，否则跳过判断会自锁。</summary>
    [Fact]
    public void Dependencies_are_not_self_referential()
    {
        foreach (var info in FetchTaskCatalog.All)
        {
            Assert.NotEqual(info.Id, info.DependsOn);
            Assert.DoesNotContain(info.Id, info.SoftDependsOn ?? []);
        }
    }

    /// <summary>前置指向的动作必须是**还在用**的——指向已退役的等于这条依赖永远不成立。</summary>
    [Fact]
    public void Dependencies_point_at_active_actions()
    {
        foreach (var info in FetchTaskCatalog.Active)
        {
            if (info.DependsOn is { } hard)
                Assert.False(FetchTaskCatalog.Info(hard).Retired, $"{info.Id} 的硬前置 {hard} 已退役");
            foreach (var soft in info.SoftDependsOn ?? [])
                Assert.False(FetchTaskCatalog.Info(soft).Retired, $"{info.Id} 的软前置 {soft} 已退役");
        }
    }

    /// <summary>模板里引用的动作必须都在目录里（模板是硬编码的，写错了要到点击时才发现）。</summary>
    [Fact]
    public void Templates_only_reference_known_actions()
    {
        foreach (var tpl in FetchPlanTemplates.All)
        {
            Assert.NotEmpty(tpl.Items);
            foreach (var item in tpl.Items)
            {
                var info = FetchTaskCatalog.Info(item.Action);   // 不存在就抛
                Assert.Equal(item.Action, info.Id);
            }
        }
    }

    /// <summary>同一份模板里不能出现两次同一个动作——展开时会被去重跳过，等于写了没用。</summary>
    [Fact]
    public void Templates_have_no_duplicate_actions()
    {
        foreach (var tpl in FetchPlanTemplates.All)
        {
            var dupes = tpl.Items.GroupBy(i => i.Action).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(dupes.Count == 0, $"模板【{tpl.Name}】里重复的动作：{string.Join("、", dupes)}");
        }
    }

    /// <summary>
    /// 【每日收盘后】模板必须跟 <see cref="FetchTaskCatalog.DailyOrder"/> **完全一致**，
    /// 并且涵盖老【拉取全部】拆出来的全部 13 步。
    ///
    /// 这是"拆完之后功能还在"的机器化保证：模板是"日更组被删了想恢复"时的唯一途径，
    /// 少一项就等于那一步数据从此不再抓（比如漏掉当日覆盖率体检，静默漏抓就没人发现了）。
    /// 具体怎么排由 DailyOrder 说了算，那边的硬约束由 PlanTemplateTests 守着。
    /// </summary>
    [Fact]
    public void Daily_template_matches_the_daily_order()
    {
        var inTemplate = FetchPlanTemplates.Daily.Items.Select(i => i.Action).ToList();
        Assert.Equal(FetchTaskCatalog.DailyOrder, inTemplate);
        foreach (var step in FetchTaskCatalog.FetchAllSteps)
            Assert.Contains(step, inTemplate);
    }

    /// <summary>
    /// 每个动作都必须支持"增量"——它是 <see cref="FetchPlanItem.Mode"/> 的默认值，
    /// 也是 <see cref="FetchPlanItem.EffectiveMode"/> 在读到不认识的模式时的兜底。
    /// 哪个动作把 Incremental 漏了，那些回退路径就会指向一个它不支持的模式。
    /// </summary>
    [Fact]
    public void Every_action_supports_incremental_mode()
    {
        foreach (var info in FetchTaskCatalog.All)
            Assert.True(info.SupportedModes.HasFlag(FetchMode.Incremental), $"{info.Name} 不支持增量模式");
    }

    /// <summary>模板里指定的模式必须是那个动作真支持的，否则展开出来的行会被静默退回增量。</summary>
    [Fact]
    public void Template_modes_are_supported_by_their_actions()
    {
        foreach (var tpl in FetchPlanTemplates.All)
            foreach (var item in tpl.Items)
                Assert.True(FetchTaskCatalog.Info(item.Action).SupportedModes.HasFlag(item.Mode),
                    $"模板【{tpl.Name}】里的【{FetchTaskCatalog.Info(item.Action).Name}】用了它不支持的模式 {item.Mode}");
    }

    /// <summary>
    /// 支持"只抓某一天"的动作必须同时声明要日期参数——否则界面上根本没有地方填那一天，
    /// 只能落到"默认今天"，模式就白设了。
    /// </summary>
    [Fact]
    public void Specific_day_actions_take_a_date_parameter()
    {
        foreach (var info in FetchTaskCatalog.All.Where(a => a.SupportedModes.HasFlag(FetchMode.SpecificDay)))
            Assert.True(info.Params.HasFlag(FetchActionParams.Date), $"{info.Name} 支持按天抓，却没有日期参数");
    }

    // ── "数据什么时候才齐" / 计划自检（2026-09-02）──

    /// <summary>
    /// 默认计划不该触发自检提醒——它就是"排得对"的样板，自己都过不了的话，
    /// 用户第一次打开就看见一条黄字，只会以为程序坏了。
    /// </summary>
    [Fact]
    public void Default_plan_passes_its_own_schedule_check()
    {
        var plan = FetchPlan.CreateDefault();
        plan.MigrateRetired();
        plan.Normalize();
        Assert.Empty(plan.CheckSchedule());
    }

    /// <summary>要等收盘的项排在收盘前（或整串没设时刻）时，必须提醒。</summary>
    /// <summary>造一个组（含它的任务）——分组之后重复规则和时刻都在组上。</summary>
    private static FetchPlanGroup Group(RepeatKind repeat, TimeOnly? at,
        params (FetchActionId Action, bool On)[] items)
        => Group(repeat, at, RunPacing.Immediate, items);

    private static FetchPlanGroup Group(RepeatKind repeat, TimeOnly? at, RunPacing pacing,
        params (FetchActionId Action, bool On)[] items)
    {
        var g = new FetchPlanGroup { Name = "测试组", Enabled = true, Repeat = repeat, Pacing = pacing, NotBefore = at };
        foreach (var (a, on) in items) g.Items.Add(new FetchPlanItem { Action = a, Enabled = on, Owner = g });
        return g;
    }

    /// <summary>要等收盘的项排在收盘前（或整组没设时刻）时，必须提醒。</summary>
    [Fact]
    public void Warns_when_an_after_close_task_would_start_too_early()
    {
        var plan = new FetchPlan
        {
            Groups = [Group(RepeatKind.EveryWorkday, new TimeOnly(9, 30), (FetchActionId.StepStockDayBars, true))],
        };
        var w = plan.CheckSchedule();
        Assert.Single(w);
        Assert.Contains(FetchTaskCatalog.Info(FetchActionId.StepStockDayBars).Name, w[0]);
    }

    /// <summary>组的时刻管着整组：组头设了 18:00，组里每一项都算"18:00 之后才跑"。</summary>
    [Fact]
    public void The_group_time_covers_every_item_in_it()
    {
        var plan = new FetchPlan
        {
            Groups =
            [
                Group(RepeatKind.EveryWorkday, new TimeOnly(18, 0),
                    (FetchActionId.StepRoster, true), (FetchActionId.StepStockDayBars, true), (FetchActionId.StepLhb, true)),
            ],
        };
        Assert.Empty(plan.CheckSchedule());
    }

    /// <summary>跟当天收盘无关的项（融资余额是 T+1、公告回看 14 天）早跑不该被提醒。</summary>
    [Fact]
    public void Anytime_tasks_are_never_warned_about()
    {
        var plan = new FetchPlan
        {
            Groups =
            [
                Group(RepeatKind.EveryWorkday, new TimeOnly(7, 0),
                    (FetchActionId.StepMargin, true), (FetchActionId.StepAnnouncements, true)),
                Group(RepeatKind.Monthly, new TimeOnly(9, 0), (FetchActionId.FetchShareholder, true)),
            ],
        };
        Assert.Empty(plan.CheckSchedule());
        Assert.Equal(DataReadiness.Anytime, FetchTaskCatalog.Info(FetchActionId.StepMargin).Readiness);
        Assert.Equal(DataReadiness.AfterClose, FetchTaskCatalog.Info(FetchActionId.StepLhb).Readiness);
    }

    /// <summary>没勾选、以及「空闲时」「手动」的组不参与自检——它们没有固定开始时刻。</summary>
    [Fact]
    public void Disabled_and_idle_items_are_ignored_by_the_check()
    {
        var plan = new FetchPlan
        {
            Groups =
            [
                Group(RepeatKind.EveryWorkday, null, (FetchActionId.StepStockDayBars, false)),
                Group(RepeatKind.EveryWorkday, null, RunPacing.WhenIdle, (FetchActionId.StepLhb, true)),
            ],
        };
        Assert.Empty(plan.CheckSchedule());
    }

    /// <summary>【拉取全部】等价清单里的每一项都得是真动作，而且不能重复。</summary>
    [Fact]
    public void Fetch_all_steps_are_known_and_unique()
    {
        Assert.Equal(FetchTaskCatalog.FetchAllSteps.Count, FetchTaskCatalog.FetchAllSteps.Distinct().Count());
        foreach (var id in FetchTaskCatalog.FetchAllSteps)
            Assert.Equal(id, FetchTaskCatalog.Info(id).Id);
    }

    /// <summary>
    /// ★【回购公告进展】必须占用**全部联网源**，别给它填 Sources（2026-09-11 踩过）。
    ///
    /// 它横跨巨潮全文检索 + 东财 np-anotice/np-cnotice，而 np-* 在 <see cref="DataSourceId"/>
    /// 里没有对应项。原先填了 <c>[EmDataCenter]</c>——一个它根本不打的源——而
    /// <see cref="FetchTaskInfo.EffectiveSources"/> 是"填了就只认填的、没填才退回 AllOnline"，
    /// 于是 <see cref="QuotaGroup.Mixed"/> 的保守兜底被关掉、冲突检查形同虚设：
    /// 实测它跟【拉取分档资金流】并发跑没被拦住，push2his 那边连续 15 只全失败。
    ///
    /// 在 np-* 有自己的枚举项之前，这一项就该保守占满——**填一个错的比不填更糟**。
    /// </summary>
    [Fact]
    public void 回购公告进展_占用全部联网源()
    {
        var info = FetchTaskCatalog.Info(FetchActionId.StepPlanWatch);

        Assert.Null(info.Sources);   // 别"好心"补上，补错就重新打开这个洞
        Assert.Equal(QuotaGroup.Mixed, info.Quota);
        Assert.Equal(DataSourceCatalog.AllOnline.ToHashSet(), info.EffectiveSources.ToHashSet());
        // 具体地：必须跟分档资金流用的那两个源冲突，否则拦不住并发
        Assert.Contains(DataSourceId.EmPush2His, info.EffectiveSources);
        Assert.Contains(DataSourceId.Cninfo, info.EffectiveSources);
    }

    // ─────────── 残缺日修补要用的模式（2026-09-16）───────────
    // 起因：两融 2026-08-21 / 09-02 只抓到沪市，体检查得出来、却没有任何一条路补得回去——
    // 【首次整段回补】按"这天有没有行"跳过，而这几项当时既不支持【只补待办】也不支持
    // 【只抓某一天】（UI 的日期格只在 SpecificDay 模式下才出现，见 PlanItemViewModel.NeedsDate）。

    [Fact]
    public void 上日频体检的那几项_都支持只补待办()
    {
        // 残缺日待办要靠 FillBacklog 分派回各自的任务；不声明这个模式就永远认领不到
        foreach (var id in new[]
                 {
                     FetchActionId.StepMargin, FetchActionId.StepLhb,
                     FetchActionId.FetchLhbSeat,
                     FetchActionId.FetchMoneyFlowDetail,
                     // ⚠ FetchMarketEvents 2026-09-21 从这个名单里去掉了：它原来拥有
                     //    大宗交易那张日频表，而大宗 09-17 拆成了独立任务（RetryTaskIds.BlockTrade）。
                     //    从那以后**没有任何地方**往 FetchMarketEvents 名下记待办
                     //    （RetryTaskIds 里没有它、SqliteDailyTableAuditor 也没有它的 OwnerTaskId），
                     //    那个 FillBacklog 声明只是让下拉框多一个选了没用的选项，同日一并删了。
                 })
            Assert.True(FetchTaskCatalog.Info(id).SupportedModes.HasFlag(FetchMode.FillBacklog),
                $"{id} 少了 FillBacklog");
    }

    /// <summary>
    /// 两融和龙虎榜要支持【只抓某一天】——它们的说明文字和体检的修复指引都写着
    /// "日期格填了就只抓那一天"，可 <c>NeedsDate</c> 要求模式是 SpecificDay 才显示日期框。
    /// 不声明这个模式，那两句话就是**照着做不到**的（2026-09-16 修）。
    /// </summary>
    [Fact]
    public void 两融和龙虎榜_支持只抓某一天()
    {
        foreach (var id in new[] { FetchActionId.StepMargin, FetchActionId.StepLhb })
        {
            var info = FetchTaskCatalog.Info(id);
            Assert.True(info.SupportedModes.HasFlag(FetchMode.SpecificDay), $"{id} 少了 SpecificDay");
            Assert.True(info.Params.HasFlag(FetchActionParams.Date), $"{id} 没有日期参数");
        }
    }

    /// <summary>待办里的任务 id 是字符串常量，拼错了就永远认领不到自己的待办——
    /// 每个都得解析得成真实的 <see cref="FetchActionId"/>。</summary>
    [Fact]
    public void 日频表的待办任务id_都对得上枚举()
    {
        foreach (var id in new[]
                 {
                     RetryTaskIds.Margin, RetryTaskIds.Lhb, RetryTaskIds.LhbSeat,
                     RetryTaskIds.BlockTrade, RetryTaskIds.MoneyFlowDetail,
                 })
            Assert.True(Enum.TryParse<FetchActionId>(id, out _), $"{id} 不是有效的 FetchActionId");
    }

    /// <summary>体检里配的归属任务也要对得上——Spec.OwnerTaskId 是残缺日待办的落点。</summary>
    [Fact]
    public void 日频表Spec配的归属任务_都对得上枚举()
    {
        foreach (var spec in SqliteDailyTableAuditor.DailyTables)
        {
            if (string.IsNullOrEmpty(spec.OwnerTaskId)) continue;   // 只报不补的表
            Assert.True(Enum.TryParse<FetchActionId>(spec.OwnerTaskId, out var id),
                $"{spec.Table} 的 OwnerTaskId「{spec.OwnerTaskId}」不是有效的 FetchActionId");
            Assert.True(FetchTaskCatalog.Info(id).SupportedModes.HasFlag(FetchMode.FillBacklog),
                $"{spec.Table} 归属的 {id} 不支持 FillBacklog，残缺日补不了");
        }
    }
    /// <summary>
    /// **有一段长哑期、而且没法喂心跳的任务，必须自己配 MaxQuiet**（2026-09-19）。
    ///
    /// 这几项的静默不是"抓一批慢"，而是一条走全表的大查询/大 UPDATE——没有可切分的心跳单位，
    /// 喂不了狗（按只/按天的循环都改成每单位 ReportQuiet 了，见 QuietWatchdog.IBeatOnlySink）。
    /// 括号里是实测静默，来自 publish 的历史 fetch 日志：
    ///   · 统一成交量单位   17 分 31 秒（Bar 表 2540 万行、granularity 无索引）
    ///   · 金融监管指标     16 分 42 秒（认机构类型要读两次全库财务快照）
    ///   · 融券余额补算     3 分钟（4001 个交易日，"彻底重查"整段 48 分钟）
    /// 谁把这几个 MaxQuiet 删了或调回默认，它们就会在"一路正常跑"的状态下被判成卡死掐断——
    /// 而掐断走的是失败分支，日志上只留一句"最后一句是…"，看不出是误判。
    /// </summary>
    [Fact]
    public void 长哑期任务_必须配够用的MaxQuiet()
    {
        var floors = new Dictionary<FetchActionId, TimeSpan>
        {
            [FetchActionId.StepFixVolumeUnit] = TimeSpan.FromMinutes(25),
            [FetchActionId.BankRegulatory] = TimeSpan.FromMinutes(25),
            [FetchActionId.StepFillShortBalance] = TimeSpan.FromMinutes(10),
        };

        foreach (var (id, floor) in floors)
        {
            var q = FetchTaskCatalog.Info(id).MaxQuiet;
            Assert.True(q.HasValue, $"{id} 有一段长哑期，必须配 MaxQuiet（默认 5 分钟会误杀）");
            Assert.True(q.Value >= floor,
                $"{id} 的 MaxQuiet 是 {q.Value.TotalMinutes:0} 分钟，实测静默已经接近或超过它——"
                + $"至少要 {floor.TotalMinutes:0} 分钟");
        }
    }
}
