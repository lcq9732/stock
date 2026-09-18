# FillBacklog 收口：三项残缺日待办搬进各自任务（2026-09-18）

## 0. 这是什么

2026-09-17 把【龙虎榜】【龙虎榜席位】【大宗交易】迁到新框架时，留了一条尾巴：
**任务迁走了，待办编排还留在 `FetchOrchestrator`**。当时的理由记在
`doc/block-trade-task-design.md` §7.5②：`MainViewModel` 在分派给 registry **之前**就按模式
把 `FillBacklog` 截走了，而改分派顺序会连带动到已经在依赖那条路径的
`StepEtfRawBars` / `FetchMoneyFlowDetail`——不在那次迁移的范围里。

2026-09-18 做分红任务时把**机制**补上了（`doc/dividend-task-design.md` §9）：
`IFetchTask.HandlesBacklog` 能力位 + `ITaskBacklogRunner` 钩子，两个入口都改了。
但那一版的 §9.5 把范围限死在"只让分红一个任务接管"。

本文是**收口**：把这三项的残缺日待办也搬进各自任务，让 `DailyRefetcherFor` 只剩
`Margin`（唯一还没迁成新式任务的日频项）。收口之后，"待办归谁补"这件事对**已迁任务**
只有一个答案：归它自己。

## 1. 前提：这三项的待办只有一类

搬家的前提不是"代码抽好了"，而是**转交之后不会漏掉别的类别的待办**。已核实：

| 证据 | 位置 |
|---|---|
| 三条待办映射只有 `PartialDay` | `RetryBacklog.cs:186-188` |
| 待办的来源只有日频体检的 `OwnerTaskId` | `SqliteDailyTableAuditor.cs:179/189/193` |
| 任务自己写的也只有 `PartialDay` | `LhbSeatTask.SaveIncompleteTodo` / `BlockTradeTask` 同名方法 |
| 产品代码里没有给这三个 taskId 写 `Failed`/`MissingDay`/`Gap`/`Value` 的地方 | 全仓 grep `RetryTaskIds.Lhb*` / `RetryTaskIds.BlockTrade` |

⚠ **这条是搬家的前提，不是顺带结论。** 转交之后 orchestrator 那条路对这三项不再跑——
以后谁给它们加了别的 kind 的待办，就会**静默补不上**。所以 §6 要加一条守卫测试把这个
前提钉死。

## 2. 形状：任务里怎么接

`PartialDayRepair` 自带整段编排（取待办 → 逐日重抓 → 用体检同判据复查 → `Tries` →
满 `MaxTries` 写 `ConfirmedPartialDays`），它不产出"批"。所以 `FillBacklog` 在
`FetchAsync` 开头分流、`yield break`，不走骨架的流式落库：

```csharp
public override bool HandlesBacklog => true;

// FetchAsync 开头
if (args.Mode == FetchMode.FillBacklog)
{
    _backlog = await new PartialDayRepair(paths.CurrentDb, manifestStore)   // 锁传 null，见 §5.2
        .RunAsync(RetryTaskIds.LhbSeat,
                  d => new LhbSeatDayWriter(provider, repository).RefetchAsync(d, ct),
                  ProgressSink, ct);
    yield break;
}
```

`OnCompletedAsync` 按 `_backlog` 拼结果：`null`（没有待办）→ `TaskRunResult.Skipped`；
否则 `Completed` + 汇总句（`PartialDayRepairResult` 里 `Days/Fixed/Rows/Failed/ConfirmedNow`
都有，不用自己数）。

**不把残缺日当普通的天塞进 `PlanDays` 走流式**：复查判据、`Tries`、确认名单是
`PartialDayRepair` 的核心，那样写就是把它复制一遍——正是 09-17 抽出这个类要避免的事。

三个骨架细节：

- `ProgressSink` 是 `FetchTaskBase` 现成的（直调、不异步 post，几十分钟的循环里日志不乱序）。
- `PartialDayRepair` 的日循环开头有 `ct.ThrowIfCancellationRequested()`——**停止停在天的边界**，
  整日替换的事务不会被腰斩，符合既定原则。
- `MaxItems` / `Deadline` 进不去（`PartialDayRepair` 没有这两个参数）。**本轮不加**：
  残缺日是个位数天，而取消已经能在天边界停。真需要时给 `PartialDayRepair` 加可选参数，
  **不要**在任务里另写一个循环——那等于把编排又抄回来了。

## 3. 改动清单

### 改
| 文件 | 改什么 |
|---|---|
| `src/StockPlatform.Tasks/LhbSeatTask.cs` | 加构造参数 `FetchPaths paths`（复查要 dbPath）；`HandlesBacklog => true`；`FetchAsync` 加 `FillBacklog` 分支；`OnCompletedAsync` 认这条结局；删掉 `PlanDays` 里那段"压根到不了这儿"的兜底和注释 |
| `src/StockPlatform.Tasks/BlockTradeTask.cs` | 同上（`BlockTradeDayWriter`） |
| `src/StockPlatform.Tasks/LhbTask.cs` | 同上，但**已有 `dbPath`**，不用加构造参数（`LhbDayWriter`） |
| `src/StockPlatform.Data/Orchestration/FetchOrchestrator.cs` | `DailyRefetcherFor` 删 `Lhb` / `LhbSeat` / `BlockTrade` 三个 case，只剩 `Margin`；删 `_lhbSeatProvider` / `_lhbSeatRepository` 两个字段和对应构造参数——删掉分支后它们只剩赋值，是死字段 |
| `src/StockPlatform.Desktop/StockPlatform.Fetcher/App.xaml.cs` | 三处 `Register` 补参数；`FetchOrchestrator` 构造去掉那两个实参 |

⚠ `_lhbRepository` / `_lhbProvider` / `_marketEventProvider` / `_marketEventRepository`
**不能删**：前两个还在 `RunStepBackfillLhb`（`LhbDayWriter`）那条路上，后两个还在跑
机构调研/股东增减持/限售解禁。

### 不动
- manifest 的键、待办格式、`ConfirmedPartialDays` 的键（改键踩过坑）
- `DispatchOrder`：这三项本来就不在顺序表里，排末尾按字典序，行为不变
- `RunRetryFailedInternalAsync` 收尾的 `FinishFetchRun(checkDayCoverage: true)`；
  转交分支"立刻返回空 attempted"那条铁律已经在（否则会拿转交前 `Load` 的 manifest
  覆盖任务刚写进去的名单）
- `PartialDaysOf(taskId)`（整段回补要扣掉已知残缺日）仍在 orchestrator，
  用的是同一个 `PartialDayRepair`，不受影响
- `SqliteDailyTableAuditor` 的 spec 和 `OwnerTaskId`
- orchestrator 的 `RunFillBacklogAsync` 私有重载里那一堆分支：**留着**，它还要服务
  `Margin` 和所有没迁的老任务

## 4. 收口之后的分工

| 谁 | 补谁的待办 |
|---|---|
| 各自任务（`HandlesBacklog = true`） | 分红（`Failed`）、龙虎榜/席位/大宗（`PartialDay`） |
| `FetchOrchestrator.RunFillBacklogAsync` | `Margin` 的残缺日 + K线/资金流/股东/指数那一整套（`Round`/`Failed`/`MissingDay`/`MissingDays`/`Gap`/`Value`） |

`DailyRefetcherFor` 从四个 case 缩到一个。等 `Margin` 也迁成新式任务，这个方法整个消失。

## 5. 风险

**5.1 漏补别的 kind**——见 §1，靠守卫测试钉死。

**5.2 任务侧没有 `_dbLock`**：`PartialDayRepair` 的 manifest 读改写在任务里是裸的
`Load → Apply → Save`。**现状即此**（`DayCompletenessTask`、`DividendTask` 都这样），
调度侧串行跑任务，并发窗口只存在于空闲任务并叠的场景——既有问题，不在本次范围。

**5.3 口径漂移**：`RefetchAsync` 两边共用同一个 Writer，这次只是少了一个调用方，
不可能漂。但 §6.4 仍然要做一次反向对照，证明"走任务"和"走 orchestrator"结果一样。

## 6. 验证

1. `dotnet test`（改了任务/目录，必跑）。
2. **守卫测试（新增）**：对 `Lhb`/`LhbSeat`/`BlockTrade` 三个 taskId，断言
   `RetryBacklog` 能产出的 kind 只有 `PartialDay`——以后谁加了别的类别，这条先红。
3. **单测（新增，三项各一条）**：照 `BlockTradeTaskTests` / `LhbSeatTaskTests` 现成的形状，
   注入假 provider/repository，往 manifest 塞两天残缺日 → 跑 `FillBacklog` → 断言
   重抓发生、待办按复查结果更新。纯本地、不发请求。
4. **Debug 实机**（数据目录天然隔离；UI 链和调用环路单测测不出，这一步不能省）：
   - 计划页三项各设成【只补待办】→ 日志应显示**任务自己**在补，不是 orchestrator；
   - 点【重新拉取失败股票】→ 三项的残缺日要真被抓、名单要复查后更新
     （这条走的是 `ITaskBacklogRunner` 那条回路，跟上一条是两个入口，漏一个就漏一半）；
   - 回归：`Margin` 的残缺日仍走 orchestrator，日志和结果跟改动前一模一样。
5. **反向对照**：临时把某一项的 `HandlesBacklog` 改回 false 再跑一次同样的待办，
   两边的"补上几天/写入几行/剩几天"应完全一致。

## 7. 不做的事

- **不动 `StepEtfRawBars` / `FetchMoneyFlowDetail`**：它们欠的是 `MissingDay` 和K线那套，
  编排深在 orchestrator 里（按任务分流口径、并发、水位线窗口），搬动是另一件事。
- **不给 `PartialDayRepair` 加 `MaxItems`/`Deadline`**（§2）。
- **不动 `Margin`**：它还没迁成新式任务，迁它是另一件事。
