# 资金净流入迁到新任务框架（2026-09-18 设计）

## 0. 这是什么

两融迁完之后，`DailyRefetcherFor` 已经消失（见 doc/margin-task-design.md）。
资金净流入是**现在唯一"配了 `OwnerTaskId`（会产生残缺日待办）、却没有按天重抓入口"**的日频项——
实机验证时日志亲口说了这句：

```text
⚠ 资金净流入：有 1 天残缺，但这一项没有"按天重抓"的入口，只能人工处理。
```

迁完它，`FetchOrchestrator.FillPartialDaysAsync` 那条"只能人工处理"的分支就真的没人走了，
可以连同整个方法一起删掉——那才算把上一步的收口做完。

## 1. 现状：四个入口 + 三类待办

| # | 入口 | 目标从哪来 | 窗口 |
|---|---|---|---|
| ① | `RunStepNetInflowAsync`（增量） | 本地全部股票，每只按自己的水位线 | 水位线+1 → 今天；没水位线退 60 天 |
| ② | 同上，`specificDay` 有值（只抓某一天） | 同上 | 精确那一天（`exactDayOnly`） |
| ③ | `RunFillBacklogAsync` 的 `case NetInflow` | `Todos[StepNetInflow].failed` | 同 ① |
| ④ | `FillMissingNetInflowDaysAsync` | `Todos[StepNetInflow].missing_days` | 全市场逐只跑一轮 |
| ⑤ | `FetchNetInflowRangeAsync`（【拉取区间数据】里那半边） | 年份区间 | 起点抬到 `EarliestAvailable`(2010-03-01) |

三类待办：`failed`（逐只失败）、`missing_days`（整天缺失）、
**`partial_day`（残缺日）——现在没有任何人补**。

⑤ 是另一个功能的一部分（跟K线/两融/龙虎榜并排），**不迁**，只在必要时换共用入口。

## 2. 跟前面几项最要紧的不同：它是**逐股**接口

前面迁的四项（龙虎榜、席位、大宗、两融）都是"一天一个请求拿回全市场"，
所以"按天重抓"很便宜，`PartialDayRepair` 那套（逐日重抓 + 复查）天然成立。

资金净流入不是：

- 新浪一次请求返回**整只票的全部历史**，窗口在客户端裁——所以"补几天跟补一天一样贵"；
- 反过来，**补某一天 = 全市场 5,500 个请求 ≈ 1.75 小时**。

这就是为什么 `DailyRefetcherFor` 里从来没有它的分支——不是漏了，是没有便宜的做法。
`FillMissingNetInflowDaysAsync` 补"整天缺失"时就老老实实跑一轮全市场，日志里写明了这个代价。

## 3. ⚠ 要你拍板的一件事：残缺日怎么办

残缺日（那天有行但偏少）现在**写进了待办、却没人补**，只会每轮在日志里喊一句"只能人工处理"。
三条路：

**A. 并进"整天缺失"那条路**（推荐）
残缺日和整天缺失对这张表是同一件事——都得"全市场逐只再跑一轮"（新浪一次返回全历史，
补哪天都一样）。合并之后 `partial_day` 和 `missing_days` 用同一个补法、同一套复查
（`SqliteDailyTableAuditor.CheckDays`，跟体检同判据）。
代价：一轮 1.75 小时，但这个代价 `missing_days` 早就在付了。

**B. 去掉 spec 的 `OwnerTaskId`，改成"只报不补"**
跟分档资金流（`NetInflowDetail`）现在的做法一致——那张表正是因为"补一天要 5500 个请求"
而**故意不配** `OwnerTaskId`。体检照旧报，只是不写待办。
代价：残缺日永远不会被自动补上。

**C. 保持现状**（写待办、没人补、每轮喊一句）
最差：名单里躺着永远不降的条目，跟这个项目一贯的"待办要么能补、要么别记"相悖。

**我推荐 A**：代价跟 `missing_days` 一样、而且那条路已经跑了几个月；B 虽然省事，
但它把"体检查出问题"和"能修"之间的链断开了，而这张表跟分档资金流不同——
它是**因子的输入**（资金流因子），缺一天会直接影响截面。

## 4. 形状

### 4.1 一批 = 一组 N 只（组内并发），照 `DividendTask` 的先例

骨架的 `FetchAsync` 是 `IAsyncEnumerable`，天然串行消费；而现在这条路是
`Task.WhenAll(全市场)` 一把梭。照搬会丢掉并发，一批一只又会让 `MaxItems` 变成"抓几只"。

`DividendTask` 已经解决过同样的问题：**一批 30 只，组内并发，批与批之间串行**。
于是 `MaxItems`＝本轮抓几批、`Deadline`＝到点收尾，都落在批边界上——
全市场 5,500 只、1.75 小时的活，终于能分批跑、能中途停（现在停了就整轮白费）。

### 4.2 模式映射

| 模式 | 含义 |
|---|---|
| `Incremental`（默认） | 全部股票，每只按自己的水位线续抓到今天（没水位线退 60 天） |
| `SpecificDay` | 精确只抓那一天（`exactDayOnly`，原【补指定历史日】的做法） |
| `FillBacklog` | `failed` + `missing_days` + `partial_day`（§3 选 A 的话三类一起补） |
| `FirstBackfill` | **不支持**——这张表没有"整段回补"的语义（一次请求就是全历史） |

`HandlesBacklog => true`。

### 4.3 每只票的水位线判据原样带走

`IsConfirmedFinal`（当天那行可能是盘中抓的，要重抓）、`GetLatestRowInfo`、
`NetInflowInitialLookbackDays = 60` 三条都不动，只是从 orchestrator 挪进任务。

⚠ 排期要查库（每只票的水位线），**首个 await 之前必须包 `Task.Run`**。

## 5. 改动清单

### 新增
- `src/StockPlatform.Tasks/NetInflowTask.cs`
- `App.xaml.cs` 注册一行

### 改
- `FetchOrchestrator`：删 `FetchNetInflowAsync`、`FillMissingNetInflowDaysAsync`、
  `RunFillBacklogAsync` 里的 `case NetInflow` 和 `MissingDays` 分支；
  **删 `FillPartialDaysAsync` 整个方法**（没有调用方了）；⑤ 保留
- `FetchOrchestrator.Steps.cs`：删 `RunStepNetInflowAsync`
- `MainViewModel`：删 `case FetchActionId.StepNetInflow`
- `FetchTaskCatalog`：`SupportedModes` 去掉不支持的、Note 补一句分批可停
- 选 A 的话：`PartialDayRepair` 不用动（复查判据共用），补法在任务里

### 不动
- manifest 键 `StepNetInflow`、三类待办的 kind
- `SqliteDailyTableAuditor` 里 `NetInflow` 那条 spec（选 A 时 `OwnerTaskId` 保留）
- 入口⑤【拉取区间数据】

## 6. 风险

**6.1 最容易丢的是"当天那行要重抓"**（`IsConfirmedFinal`）：盘中抓到的行是不完整的，
判成"已有"就永久固化了（跟 `project_intraday_bar_confirmation` 同一类坑）。

**6.2 失败名单的语义**：`FetchNetInflowAsync` 内部自己维护 `failedNetInflowCodes`，
收尾时"这轮碰过、这次没失败的移出名单"。搬进任务后要保持——不是"清空重写"。

**6.3 1.75 小时的活第一次有了 `MaxItems`/`Deadline`**，等于行为变了（以前跑不完就整轮白费）。
这是改进，但计划里那一项的预计耗时要跟着调。

## 7. 验证

1. `dotnet test`；新增单测：水位线排期（含 `IsConfirmedFinal` 那条）、分批与熔断、
   `FillBacklog` 三类待办的认领。
2. **离线模拟已经有了**（`MockNetInflowFetcher`，见 doc/offline-mock-design.md），
   所以整条链能零请求实机跑：四个模式各一遍、`MaxItems` 分两轮接得上、
   【重新拉取失败】里显示「（任务自补）」、残缺日补完复查清空。
3. 数据等价：迁移前后各跑一次增量，`NetInflow` 表逐行比对。

## 8. 不做的事

- **不迁入口⑤**（【拉取区间数据】），理由同两融那轮。
- **不换数据源**：东财那条（f52 主力净流入）口径跟新浪的 `netamount` 不同，换源要重抓全表。
- **不动分档资金流**（`NetInflowDetail`，另一张表、另一个任务）。

---

## 9. 实现记录（2026-09-18 落地时的偏差）

**① 给 `PartialDayRepair` 加了 `RunBatchAsync`**（设计里没写）。
逐天那条 `RunAsync` 对这一项是灾难：十天残缺就是十轮全市场（十个 1.75 小时）。
新方法一次把欠着的天全喂给调用方，**复查、Tries、"确认就这些"名单跟逐天那条完全共用**
（抽成了私有的 `Finish`）——那几段才是最不能各写一份的。

"是否被限流"的信号由调用方给：判据因项而异（这一项是"过半只数失败"），但后果一样——
一个 Tries 都不加、名单原样留着。

**② 顺带删掉了 `FillPartialDaysAsync`**：两融迁走后它就只剩"转发给 `PartialDayRepair`
并报一句只能人工处理"的空壳，唯一还会走到它的资金净流入这次也自己补了，于是没有调用方。

**③ 进度行里不报写入行数。** 骨架是"先 yield、再 `SaveBatchAsync`"，`_rows` 要等这一批
存完才涨——在进度里读永远差一批，第一批会显示"写入 0 行"（实机第一次跑就看见了）。
行数留给收尾那句汇总。

**④ `FetchNetInflowRangeAsync` 没动**（【拉取区间数据】里的资金流那半边），同两融那轮。

## 10. 实机验证（2026-09-18，Debug 实例，**全程离线模拟、零网络请求**）

`MockNetInflowFetcher` 上一轮已经有了，所以这一轮一个请求都没发。

| # | 验的是什么 | 结果 |
|---|---|---|
| 1 | 增量 | 12 只写入 540 行；日志"每只按自己的水位线续抓；从没抓过的回看 60 天" |
| 2 | **失败名单那一类** | 只抓名单里那一只，不是全市场 |
| 3 | **整天缺失那一类** | 全市场逐只跑一轮、写入 12 行，复查"补上 1/1 天"后待办清空 |
| 4 | **残缺日走新的批量口子** | 日志"…**这一项按天重抓很贵，所以一轮把这些天一起补**"＝`RunBatchAsync` 的文案 |
| 5 | 残缺日复查仍走体检同判据 | "资金净流入残缺日补齐完成：补上 1/1 天" |
| 6 | 【重新拉取失败】的转交回路 | 收尾行 `资金净流入（任务自补）` |
| 7 | 待办清空、确认名单没误加 | ✅ |

第 4、6 条合起来是这轮的直接证据：**那句"⚠ 资金净流入：有 N 天残缺，但这一项没有『按天重抓』
的入口，只能人工处理"再也不会出现了**——它正是上一轮（两融）实机验证时抓出来的洞。

### 收尾

验证用的临时配置已从 Debug 的 `fetcher-settings.json` 撤掉（生效键只剩 `BoardMemberChannel`），
模拟源写进 Debug 库的假数据、为跑通名册临时塞的 12 只"模拟X"股票全部清空。
