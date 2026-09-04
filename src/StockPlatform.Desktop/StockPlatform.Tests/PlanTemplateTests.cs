using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// **分组、模板和老计划处理**（2026-09-02 分组改造后重写）。
///
/// 这里守的是三件"错了完全没有报错"的事：
///   ① 默认计划的组织方式——三组分别装什么、日常那一串顺序对不对；
///   ② 组的语义：组是总闸、时刻只在组上、「空闲时补」跟重复规则是两个维度；
///   ③ 老计划不迁移但必须备份，模板反复铺不会越铺越多。
/// </summary>
public class PlanTemplateTests
{
    private static FetchPlan Loaded()
    {
        var plan = FetchPlan.CreateDefault();
        plan.MigrateRetired();
        plan.Normalize();
        return plan;
    }

    // ══ 默认计划：三组 ════════════════════════════════════════════════════

    /// <summary>
    /// 三组按"数据更新节奏"分，不是按执行方式分：每日新数据 / 周期更新 / 向前回补。
    /// 执行方式（到点就跑、空闲时补）是组的一个属性，不再拿它当分组依据。
    /// </summary>
    [Fact]
    public void Default_plan_has_three_groups_by_data_cadence()
    {
        var plan = Loaded();

        Assert.Equal(3, plan.Groups.Count);

        var daily = plan.GroupOf(PlanGroupKind.Daily);
        Assert.Equal("每日收盘后", daily.Name);
        Assert.Equal(RepeatKind.EveryWorkday, daily.Repeat);
        // 日更数据有时效，必须到点就跑——等空闲可能永远等不到
        Assert.Equal(RunPacing.Immediate, daily.Pacing);
        Assert.Equal(new TimeOnly(18, 0), daily.NotBefore);

        var periodic = plan.GroupOf(PlanGroupKind.Periodic);
        Assert.Equal("季度定期", periodic.Name);
        Assert.Equal(RepeatKind.Monthly, periodic.Repeat);
        // 这类晚几天没关系，而财务报表每轮只能 300 只、全市场要跨几天才啃得完
        Assert.Equal(RunPacing.WhenIdle, periodic.Pacing);

        var backfill = plan.GroupOf(PlanGroupKind.OnDemand);
        Assert.Equal("按需启动", backfill.Name);
        Assert.Equal(RepeatKind.Manual, backfill.Repeat);
    }

    /// <summary>
    /// **一个都不能少**：日更组装的就是 <see cref="FetchTaskCatalog.DailyOrder"/> 那一串，
    /// 而且必须涵盖老【拉取全部】拆出来的全部 13 步。
    ///
    /// 少一项是**静默**的错误——比如漏掉当日覆盖率体检，从此再没人发现数据源盘后没更新完；
    /// 漏掉退市股收尾，退市那几天的K线就永久缺失。所以这条断言守的是"清单完整"，
    /// 至于**怎么排**是用户的自由，只有下面那几条硬约束才检查。
    /// </summary>
    [Fact]
    public void Daily_group_has_every_daily_action()
    {
        var daily = Loaded().GroupOf(PlanGroupKind.Daily);
        var actions = daily.Items.Select(i => i.Action).ToList();

        Assert.Equal(FetchTaskCatalog.DailyOrder, actions);
        foreach (var step in FetchTaskCatalog.FetchAllSteps)
            Assert.Contains(step, actions);
        Assert.All(daily.Items, i => Assert.True(i.Enabled));
    }

    /// <summary>
    /// **关键先后**：被依赖的必须排在前面。
    ///
    /// 依赖关系写在目录里（<see cref="FetchActionInfo.DependsOn"/> / <see cref="FetchActionInfo.SoftDependsOn"/>），
    /// 界面上也显示成"依赖"那一列，这里直接拿它校验——不用再手抄一份约束清单，
    /// 以后往目录里加依赖，这条测试自动就守住了。
    ///
    /// 顺序错了全是**静默**的：板块指数用昨天的成分股合成、体检查不出今天漏了谁、
    /// 补漏那几项拿的是昨天的名单——界面上一切正常，数据悄悄就不对。
    ///
    /// ⚠ 只在**同一组内**校验：跨组依赖（比如按需组的项依赖日更组的产物）由组的时刻决定先后，
    /// 不受组内排序影响。
    /// </summary>
    [Fact]
    public void Dependencies_come_before_their_dependents_in_every_group()
    {
        var plan = Loaded();
        foreach (var group in plan.Groups)
        {
            var actions = group.Items.Select(i => i.Action).ToList();
            int At(FetchActionId a) => actions.IndexOf(a);

            foreach (var item in group.Items)
            {
                var info = item.Info;
                var deps = new List<FetchActionId>();
                if (info.DependsOn is { } hard) deps.Add(hard);
                deps.AddRange(info.SoftDependsOn ?? []);

                foreach (var dep in deps)
                {
                    int at = At(dep);
                    if (at < 0) continue;      // 依赖在别的组里，先后由组的时刻决定
                    Assert.True(at < At(item.Action),
                        $"「{group.Name}」里【{FetchTaskCatalog.Info(dep).Name}】要排在"
                        + $"【{info.Name}】前面——后者用的是前者产出的东西");
                }
            }
        }
    }

    /// <summary>
    /// 「按需启动」组装的是想起来才做的一次性活，顺序＝自然工作流：
    /// 先体检看看缺不缺 → 决定要不要往回补 → 补完一大批再建一次索引。
    /// 而且**都不能默认勾选**：它们都是重活（全库扫描、几小时的回补、建索引锁写入），
    /// 自己跑起来会把某天的日更整个挤掉。
    /// </summary>
    [Fact]
    public void On_demand_group_holds_the_one_off_jobs_unchecked()
    {
        var g = Loaded().GroupOf(PlanGroupKind.OnDemand);

        Assert.Equal(
            new[] { FetchActionId.StepFullAudit, FetchActionId.FetchYear, FetchActionId.OptimizeDatabase },
            g.Items.Select(i => i.Action).Take(3).ToArray());
        Assert.All(g.Items, i => Assert.False(i.Enabled));
    }

    /// <summary>
    /// 周期组里【财务报表】要排在【金融监管指标】前面 —— 机构类型（银行/券商/保险）是靠
    /// 财务特征科目认出来的，没有财务数据它一家都识别不出。
    ///
    /// 这条曾经排错过：当时按"最慢的排最后"把财务放到了末尾，而监管指标在它前面。
    /// 财务确实最慢（每轮 300 只、跨几天），但它有冷却——补完一轮歇 20 分钟，
    /// 那段空档后面的小任务照样跑得上，不会被饿死。
    /// </summary>
    /// <summary>
    /// 【指数权重】要排在季度组**最后**：中证 OSS 最容易触发反爬，排末尾的话即使它把自己
    /// 撞进熔断，前面那些数据也早就落库了。
    /// </summary>
    [Fact]
    public void Index_weight_is_last_in_the_periodic_group()
    {
        var periodic = Loaded().GroupOf(PlanGroupKind.Periodic);
        Assert.Equal(FetchActionId.StepIndexWeight, periodic.Items[^1].Action);
    }

    [Fact]
    public void Financials_comes_before_bank_regulatory()
    {
        var items = Loaded().GroupOf(PlanGroupKind.Periodic).Items.Select(i => i.Action).ToList();
        Assert.True(items.IndexOf(FetchActionId.FetchFinancials) < items.IndexOf(FetchActionId.BankRegulatory));
        // 手工回填的是监管指标没解析出来的格子，所以它在最后
        Assert.True(items.IndexOf(FetchActionId.BankRegulatory) < items.IndexOf(FetchActionId.ImportManual));
    }

    [Fact]
    public void Default_plan_has_no_retired_actions_and_passes_self_check()
    {
        var plan = Loaded();
        Assert.DoesNotContain(plan.AllItems, i => FetchTaskCatalog.Info(i.Action).Retired);
        Assert.Empty(plan.CheckSchedule());
    }

    /// <summary>目录里每个还在用的动作都得在计划里有一行，否则它在界面上就凭空消失了。</summary>
    [Fact]
    public void Normalize_places_every_active_action_somewhere()
    {
        var plan = Loaded();
        var have = plan.AllItems.Select(i => i.Action).ToHashSet();
        foreach (var info in FetchTaskCatalog.Active)
            Assert.Contains(info.Id, have);
    }

    // ══ 组的语义 ══════════════════════════════════════════════════════════

    /// <summary>组是总闸：整组没勾，组里勾着的项也不该跑。</summary>
    [Fact]
    public void A_disabled_group_disables_its_items()
    {
        var plan = Loaded();
        var daily = plan.GroupOf(PlanGroupKind.Daily);
        Assert.All(daily.Items, i => Assert.True(i.EffectiveEnabled));

        daily.Enabled = false;
        Assert.All(daily.Items, i => Assert.False(i.EffectiveEnabled));
    }

    /// <summary>
    /// 开始时刻**完全由组决定**（子项不再有自己的「不早于」）。
    /// 组里是串行跑的，给单项设时刻不起作用；真要更晚就把它拆成另一个组。
    /// </summary>
    [Fact]
    public void Start_time_comes_from_the_group_only()
    {
        var plan = Loaded();
        var daily = plan.GroupOf(PlanGroupKind.Daily);
        var day = new DateTime(2026, 9, 2);

        Assert.All(daily.Items, i => Assert.Equal(day.AddHours(18), i.DueTimeOn(day)));
        Assert.All(daily.Items, i => Assert.Null(i.NotBefore));

        daily.NotBefore = new TimeOnly(19, 30);
        Assert.All(daily.Items, i => Assert.Equal(day.AddHours(19).AddMinutes(30), i.DueTimeOn(day)));
    }

    /// <summary>「多久到期」和「到期后怎么跑」是两个维度，改一个不该动到另一个。</summary>
    [Fact]
    public void Repeat_and_pacing_are_independent()
    {
        var g = new FetchPlanGroup { Repeat = RepeatKind.Monthly, Pacing = RunPacing.WhenIdle };
        var item = new FetchPlanItem { Action = FetchActionId.FetchFinancials, Enabled = true, Owner = g };
        g.Items.Add(item);

        Assert.Equal(RepeatKind.Monthly, item.Repeat);
        Assert.Equal(RunPacing.WhenIdle, item.Pacing);

        g.Pacing = RunPacing.Immediate;
        Assert.Equal(RepeatKind.Monthly, item.Repeat);      // 频率没跟着变
        Assert.Equal(RunPacing.Immediate, item.Pacing);
    }

    // ══ 老格式：不迁移，重建 + 备份 ════════════════════════════════════════

    /// <summary>
    /// 2026-09-02 之前的老计划（顶层是一串平铺的 Items）**不做映射迁移**——新的三组划分是按
    /// "数据更新节奏"重新想过的，硬套过来只会带进别扭的组合。但用户排过的东西不能无声无息地没了，
    /// 所以原文件必须先备份，并且要能从 MigrationNotes 里看到备份在哪。
    /// </summary>
    [Fact]
    public void Legacy_plan_is_rebuilt_and_the_old_file_is_backed_up()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "fetch-plan.json");
        try
        {
            File.WriteAllText(file, """
                {
                  "Items": [
                    { "Action": "FetchAll", "Enabled": true, "Repeat": "EveryWorkday", "NotBefore": "18:00" },
                    { "Action": "FetchFinancials", "Enabled": true, "Repeat": "WhenIdle" }
                  ],
                  "AutoStartOnLaunch": true
                }
                """);

            var plan = new FetchPlanStore(file).Load();

            Assert.Equal(3, plan.Groups.Count);
            Assert.Equal("每日收盘后", plan.GroupOf(PlanGroupKind.Daily).Name);
            Assert.DoesNotContain(plan.AllItems, i => i.Action == FetchActionId.FetchAll);
            Assert.True(File.Exists(Path.ChangeExtension(file, ".bak.json")));
            Assert.Contains(plan.MigrationNotes, n => n.Contains("备份"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// 重建的是**结构**，不是**事实**：老计划里每一项"上次什么时候跑的、成没成、跑了多久"
    /// 必须按动作承接过来。
    ///
    /// 不承接的后果很实在：当天已经跑完的任务会被当成没跑过、再来一遍（【拉取全部】那种就是白花两小时），
    /// 耗时自学的样本也要从头学起。
    ///
    /// 老 json 里还有 <c>"Repeat": "WhenIdle"</c> 这种已经删掉的枚举值——它不能把整份文件读挂，
    /// 否则连这些记录都拿不到（所以那几个老字段是按 string 接的）。
    /// </summary>
    [Fact]
    public void Run_records_carry_over_when_the_plan_is_rebuilt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "fetch-plan.json");
        try
        {
            File.WriteAllText(file, """
                {
                  "Items": [
                    {
                      "Action": "StepStockDayBars", "Enabled": true, "Repeat": "EveryWorkday",
                      "NotBefore": "18:00:00",
                      "LastStart": "2026-09-02T18:05:00", "LastEnd": "2026-09-02T19:10:00",
                      "LastOutcome": "Ok", "LastErrorCount": 3, "LastMessage": "完成，但有 3 条错误",
                      "RecentDurationsSec": [ 3900, 3720, 4010 ]
                    },
                    { "Action": "FetchFinancials", "Enabled": true, "Repeat": "WhenIdle",
                      "LastOutcome": "Failed", "LastEnd": "2026-09-02T11:00:00" }
                  ]
                }
                """);

            var plan = new FetchPlanStore(file).Load();

            var day = plan.AllItems.First(i => i.Action == FetchActionId.StepStockDayBars);
            Assert.Equal(RunOutcome.Ok, day.LastOutcome);
            Assert.Equal(new DateTime(2026, 9, 2, 18, 5, 0), day.LastStart);
            Assert.Equal(3, day.LastErrorCount);
            Assert.Equal([3900, 3720, 4010], day.RecentDurationsSec);   // 耗时样本也要跟过来

            var fin = plan.AllItems.First(i => i.Action == FetchActionId.FetchFinancials);
            Assert.Equal(RunOutcome.Failed, fin.LastOutcome);

            Assert.Contains(plan.MigrationNotes, n => n.Contains("运行记录"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// 老文件里某个字段格式对不上（这里是 <c>"NotBefore": "18:00"</c> 少了秒，
    /// System.Text.Json 转不了 TimeOnly）时，强类型反序列化会整份作废——
    /// 但运行记录得靠 JsonDocument 逐项**捞回来**，不能跟着结构一起丢。
    /// </summary>
    [Fact]
    public void Run_records_are_rescued_even_when_the_file_fails_to_parse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "fetch-plan.json");
        try
        {
            File.WriteAllText(file, """
                {
                  "Items": [
                    {
                      "Action": "StepStockDayBars", "NotBefore": "18:00",
                      "LastStart": "2026-09-02T18:05:00", "LastEnd": "2026-09-02T19:10:00",
                      "LastOutcome": "Ok", "RecentDurationsSec": [ 3900, 3720 ]
                    }
                  ]
                }
                """);

            var plan = new FetchPlanStore(file).Load();

            var day = plan.AllItems.First(i => i.Action == FetchActionId.StepStockDayBars);
            Assert.Equal(RunOutcome.Ok, day.LastOutcome);
            Assert.Equal(new DateTime(2026, 9, 2, 19, 10, 0), day.LastEnd);
            Assert.Equal([3900, 3720], day.RecentDurationsSec);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>读不出来的文件（半个 json、手改坏）同样走"备份 + 重建"，不能让程序开不起来。</summary>
    [Fact]
    public void Broken_plan_file_is_rebuilt_too()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "fetch-plan.json");
        try
        {
            File.WriteAllText(file, "{ \"Groups\": [ { \"Name\": \"半个");
            var plan = new FetchPlanStore(file).Load();

            Assert.Equal(3, plan.Groups.Count);
            Assert.True(File.Exists(Path.ChangeExtension(file, ".bak.json")));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>新格式存了再读要一模一样（含 Pacing 和组内顺序）——存取不对称是最难查的那种 bug。</summary>
    [Fact]
    public void New_format_survives_a_save_and_load_round_trip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "fetch-plan.json");
        try
        {
            var store = new FetchPlanStore(file);
            var saved = Loaded();
            saved.GroupOf(PlanGroupKind.Daily).NotBefore = new TimeOnly(19, 0);
            store.Save(saved);

            var back = store.Load();

            Assert.Equal(3, back.Groups.Count);
            Assert.Equal(new TimeOnly(19, 0), back.GroupOf(PlanGroupKind.Daily).NotBefore);
            Assert.Equal(RunPacing.WhenIdle, back.GroupOf(PlanGroupKind.Periodic).Pacing);
            Assert.Equal(saved.GroupOf(PlanGroupKind.Daily).Items.Select(i => i.Action).ToList(),
                         back.GroupOf(PlanGroupKind.Daily).Items.Select(i => i.Action).ToList());
            // Owner 必须回填，否则每一项都会当成"没有组"而按手动处理、整份计划一动不动
            Assert.All(back.AllItems, i => Assert.NotNull(i.Owner));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ══ 模板 ══════════════════════════════════════════════════════════════

    [Fact]
    public void Templates_only_reference_active_actions_with_supported_modes()
    {
        foreach (var tpl in FetchPlanTemplates.All)
        {
            Assert.NotEmpty(tpl.Items);
            foreach (var item in tpl.Items)
            {
                var info = FetchTaskCatalog.Info(item.Action);
                Assert.False(info.Retired, $"模板【{tpl.Name}】用了已退役的 {item.Action}");
                Assert.True(info.SupportedModes.HasFlag(item.Mode),
                    $"模板【{tpl.Name}】让 {item.Action} 用了它不支持的模式 {item.Mode}");
            }
        }
    }

    /// <summary>
    /// 【每日收盘后】模板必须跟默认计划的日更组**完全一致**：模板是"组被删了想恢复"时的兜底，
    /// 两边不一致就意味着恢复出来的东西跟原来不一样。
    /// </summary>
    [Fact]
    public void Daily_template_matches_the_default_daily_group()
    {
        var daily = Loaded().GroupOf(PlanGroupKind.Daily);
        Assert.Equal(daily.Items.Select(i => i.Action).ToList(),
                     FetchPlanTemplates.Daily.Items.Select(i => i.Action).ToList());
    }

    [Fact]
    public void Applying_a_template_fills_the_matching_group()
    {
        var plan = Loaded();
        plan.GroupOf(PlanGroupKind.Daily).Items.Clear();
        plan.LinkOwners();

        var (group, items, _) = FetchPlanTemplates.Apply(plan, FetchPlanTemplates.Daily);

        Assert.Equal("每日收盘后", group.Name);
        Assert.Equal(FetchPlanTemplates.Daily.Items.Select(i => i.Action).ToList(),
                     items.Select(i => i.Action).ToList());
        Assert.All(items, i => Assert.Same(group, i.Owner));
    }

    /// <summary>反复铺同一个模板不该越铺越多，也不该把顺序搅乱。</summary>
    [Fact]
    public void Applying_twice_is_idempotent()
    {
        var plan = Loaded();
        var (g1, items1, _) = FetchPlanTemplates.Apply(plan, FetchPlanTemplates.Daily);
        int countAfterFirst = plan.AllItems.Count();
        var orderAfterFirst = g1.Items.Select(i => i.Action).ToList();

        var (g2, items2, movedIn2) = FetchPlanTemplates.Apply(plan, FetchPlanTemplates.Daily);

        Assert.Same(g1, g2);
        Assert.Empty(movedIn2);
        Assert.Equal(items1.Count, items2.Count);
        Assert.Equal(orderAfterFirst, g2.Items.Select(i => i.Action).ToList());
        Assert.Equal(countAfterFirst, plan.AllItems.Count());
    }

    // ══ 模式 ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 整段回补的项在空闲窗口里可以只跑一部分；同一个动作在增量模式下不行
    /// （日常只有一根K线的量，没有"跑一半"这回事）。
    /// </summary>
    [Fact]
    public void Partial_run_follows_the_mode_not_just_the_catalog_flag()
    {
        var item = new FetchPlanItem { Action = FetchActionId.StepStockRawBars };

        item.Mode = FetchMode.Incremental;
        Assert.False(item.SupportsPartialRunNow);

        item.Mode = FetchMode.FirstBackfill;
        Assert.True(item.SupportsPartialRunNow);

        var fin = new FetchPlanItem { Action = FetchActionId.FetchFinancials };
        Assert.True(fin.SupportsPartialRunNow);
    }

    /// <summary>json 里存了这个动作不支持的模式时退回增量，不能把整份计划卡住。</summary>
    [Fact]
    public void Unsupported_mode_falls_back_to_incremental()
    {
        var item = new FetchPlanItem { Action = FetchActionId.StepEtfBars, Mode = FetchMode.SpecificDay };
        Assert.Equal(FetchMode.Incremental, item.EffectiveMode);
    }
}

/// <summary>
/// 东财四项数据在计划里的归属（2026-09-03）。
///
/// 用户对这批任务的要求很明确：**不要用户自己往计划里加**——升级后打开程序就该自动出现在
/// 对应的组里。这靠 <see cref="FetchPlan.Normalize"/> 的"缺的动作补进默认组"实现，
/// 而进哪个组由 <see cref="FetchTaskCatalog.DefaultGroupOf"/> 决定。这组用例把那个归属钉死。
/// </summary>
public class EastMoneyTaskGroupingTests
{
    private static FetchPlan Fresh()
    {
        var p = FetchPlan.CreateDefault();
        p.Normalize();
        return p;
    }

    [Theory]
    [InlineData(FetchActionId.FetchEarningsForecast)]   // 业绩预告随时公告
    [InlineData(FetchActionId.FetchLhbSeat)]            // 龙虎榜盘后发布
    [InlineData(FetchActionId.FetchMarketEvents)]       // 大宗/调研/增减持都是按日出的
    public void 按日产生的三项归日更组(FetchActionId id)
    {
        Assert.Equal(PlanGroupKind.Daily, FetchTaskCatalog.DefaultGroupOf(id));
        Assert.Contains(id, Fresh().GroupOf(PlanGroupKind.Daily).Items.Select(i => i.Action));
    }

    [Fact]
    public void 分档资金流归定期组_因为它每轮都要重抓全市场()
    {
        // 接口是 120 天滚动窗口、没有增量入口，每轮 5500 只约 3 小时——放日更会把每晚占满。
        // 定期组是"空闲时补"，正适合这种没有时效压力但耗时长的活。
        Assert.Equal(PlanGroupKind.Periodic,
            FetchTaskCatalog.DefaultGroupOf(FetchActionId.FetchMoneyFlowDetail));
        Assert.Contains(FetchActionId.FetchMoneyFlowDetail,
            Fresh().GroupOf(PlanGroupKind.Periodic).Items.Select(i => i.Action));
    }

    [Fact]
    public void 分档资金流不能排在指数权重后面()
    {
        // 指数权重必须是定期组最后一项（中证 OSS 最容易触发反爬，排末尾则熔断也不影响前面的）
        var items = Fresh().GroupOf(PlanGroupKind.Periodic).Items.Select(i => i.Action).ToList();
        Assert.True(items.IndexOf(FetchActionId.FetchMoneyFlowDetail)
                  < items.IndexOf(FetchActionId.StepIndexWeight));
    }

    [Fact]
    public void 龙虎榜席位必须排在日更组最末()
    {
        // 它是日频数据、本该跟 StepLhb 挨着，但**首轮 264 万行要跑几小时**。
        // 日更组是「定时项」：跑多久算多久、后面顺延、不受时间窗约束——排中间的话，
        // 首轮那几小时会把后面的公告/板块/板块指数/覆盖率体检全堵住，当天核心数据就废了。
        // 排最末则最坏也只是它自己跑到半夜，前面该落库的早落完了。
        var items = Fresh().GroupOf(PlanGroupKind.Daily).Items.Select(i => i.Action).ToList();
        Assert.Equal(FetchActionId.FetchLhbSeat, items[^1]);
    }

    [Fact]
    public void 首轮很久的项不能排在核心日更之前()
    {
        // 通用判据：会跑几小时的项一律不能挡在"当天必须拿到的数据"前面。
        var items = Fresh().GroupOf(PlanGroupKind.Daily).Items.Select(i => i.Action).ToList();
        int lhbSeat = items.IndexOf(FetchActionId.FetchLhbSeat);
        foreach (var core in new[] { FetchActionId.StepAnnouncements, FetchActionId.StepBoards,
                                     FetchActionId.StepBoardIndex, FetchActionId.StepDayCoverage })
            Assert.True(items.IndexOf(core) < lhbSeat,
                $"{core} 是当天必须拿到的，不能排在首轮要跑几小时的【龙虎榜席位】后面");
    }

    [Fact]
    public void 升级到新版时四项会自动补进计划且默认不启用()
    {
        // 模拟老用户的计划文件：完全没有这四项
        var plan = FetchPlan.CreateDefault();
        foreach (var g in plan.Groups)
            g.Items.RemoveAll(i => i.Action is FetchActionId.FetchEarningsForecast
                or FetchActionId.FetchLhbSeat or FetchActionId.FetchMarketEvents
                or FetchActionId.FetchMoneyFlowDetail);

        plan.Normalize();

        foreach (var id in new[] { FetchActionId.FetchEarningsForecast, FetchActionId.FetchLhbSeat,
                                   FetchActionId.FetchMarketEvents, FetchActionId.FetchMoneyFlowDetail })
        {
            var item = plan.AllItems.SingleOrDefault(i => i.Action == id);
            Assert.NotNull(item);                       // 自动补进来了，不用用户手工加
            Assert.False(item!.Enabled,                 // 但不自作主张跑起来
                $"{id} 补进计划时不该自动启用——首次全量很久，得由用户决定什么时候跑");
        }
    }
}

/// <summary>
/// 日更组的排序原则（2026-09-03 随板块换源梳理时定的）。
///
/// 日更组是「定时项」——跑多久算多久、后面顺延、不受时间窗约束。所以**排在前面的慢活会把
/// 后面全堵住**，顺序不是审美问题而是"当天能不能拿到数据"的问题。这组用例把几条硬约束钉住。
/// </summary>
public class DailyOrderRationaleTests
{
    private static List<FetchActionId> Daily()
    {
        var p = FetchPlan.CreateDefault();
        p.Normalize();
        return p.GroupOf(PlanGroupKind.Daily).Items.Select(i => i.Action).ToList();
    }

    [Fact]
    public void 覆盖率体检不该被板块拖住()
    {
        // 体检只吃指数日K+个股日K，跟板块无关。
        // "今天漏了谁"是当天最该早知道的结论，不能排在一个跟它无关的活后面。
        var d = Daily();
        Assert.True(d.IndexOf(FetchActionId.StepDayCoverage) < d.IndexOf(FetchActionId.StepBoardList));
    }

    [Fact]
    public void 板块列表与板块指数合成必须挨着()
    {
        // 合成不只是算板块指数，还负责把板块涨跌幅/成交额回填进 Board 表
        // （2026-09-03 起这两个值不再从接口取）。中间插别的项，一旦那项失败/超时，
        // 热度页的涨跌幅就会一直是 0。
        //
        // 2026-09-04 拆分后盯的是**列表**而不是成分股：成分股挪去了"空闲时补"那一组
        // （2500 个请求、7 天一轮），本来就不保证当天跑；合成用的是库里已有的成分，
        // 真正要紧的是列表刚更新完就把行情算出来回填。
        var d = Daily();
        Assert.Equal(d.IndexOf(FetchActionId.StepBoardList) + 1,
                     d.IndexOf(FetchActionId.StepBoardIndex));
    }

    [Fact]
    public void 板块成分股归空闲补而不是日更()
    {
        // 2500 个请求、push2 限流又紧（实测连发 16~35 个就被切），放日更会把每晚占满；
        // 而它 7 天内抓过就算新鲜，没有当天必须拿到的压力。
        Assert.Equal(PlanGroupKind.Periodic,
            FetchTaskCatalog.DefaultGroupOf(FetchActionId.StepBoardMembers));
        Assert.DoesNotContain(FetchActionId.StepBoardMembers, Daily());
    }

    [Fact]
    public void 老的板块行情与成分会自动拆成两项()
    {
        // 老计划 json 里排着已退役的 StepBoards，加载时要原地换成【板块列表】+【板块成分股】，
        // 而不是留个排不动也删不掉的幽灵行。
        var plan = FetchPlan.CreateDefault();
        plan.GroupOf(PlanGroupKind.Daily).Items.Insert(
            0, new FetchPlanItem { Action = FetchActionId.StepBoards, Enabled = true });
        plan.LinkOwners();

        var notes = plan.MigrateRetired();
        plan.Normalize();

        var all = plan.AllItems.Select(i => i.Action).ToList();
        Assert.DoesNotContain(FetchActionId.StepBoards, all);
        Assert.Contains(FetchActionId.StepBoardList, all);
        Assert.Contains(FetchActionId.StepBoardMembers, all);
        // 拆出来的要**启用**——原来那行是启用的，拆完不跑就等于悄悄停了一项
        Assert.True(plan.AllItems.First(i => i.Action == FetchActionId.StepBoardList).Enabled);
        Assert.True(plan.AllItems.First(i => i.Action == FetchActionId.StepBoardMembers).Enabled);
        Assert.NotEmpty(notes);
    }

    [Fact]
    public void K线必须排在所有消费它的项之前()
    {
        var d = Daily();
        int bars = d.IndexOf(FetchActionId.StepStockDayBars);
        foreach (var consumer in new[] { FetchActionId.StepBoardIndex,      // 要用当天个股K线算板块指数
                                         FetchActionId.StepDayCoverage,     // 要查今天谁没抓到
                                         FetchActionId.RebuildAdjSeries })  // 要用K线重算复权序列
            Assert.True(bars < d.IndexOf(consumer), $"{consumer} 消费个股日K，必须排在它后面");
    }

    [Fact]
    public void 名册排第一_因为逐只抓的项都要先知道有哪些股票()
    {
        Assert.Equal(FetchActionId.StepRoster, Daily()[0]);
    }
}
