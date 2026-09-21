# K线六项迁到新任务框架 · 设计方案

2026-09-21 起草并全部落地（四步，见 §8 落地记录）。

> 按 [Solution 类图 §0.1 分层职责原则](solution-class-map.md) 设计：
> **Remote 取数 / Sqlite 只读写 / Logic 放纯判据 / Tasks 串流程 / Scheduling 管调度**。
> 存量不一次性重构，但这次碰到的每一块都要搬对位置。

迁的是计划里这六项：

| 动作 | 名称 | 现在的入口 |
|---|---|---|
| `StepStockDayBars` | 个股日K·前复权 | `FetchOrchestrator.Steps.cs:129 / 155` |
| `StepStockHfqBars` | 个股日K·后复权 | `Steps.cs:168` → `RunStepStockAdjustedBarsAsync` |
| `StepStockRawBars` | 个股日K·不复权 | `Steps.cs:174` + `RunFetchRawBarsAsync`（整段回补那一路） |
| `StepIndexBars` | 指数日K | `Steps.cs:108` |
| `StepEtfBars` | ETF日K | `Steps.cs:202` |
| `StepDelistedTails` | 退市股收尾 | `Steps.cs:221` |

---

## 0. 这一次跟前面九次迁移不一样在哪

前面迁的那些（龙虎榜、席位、大宗、两融、资金净流入、股东、分红……）都有一个共同点：
**它的抓取逻辑只有它自己在用**，从编排器里搬走之后那段代码就可以删掉。

这六项不是。它们六个共用同一个内核 `ProcessOneStockAsync`（"给一只标的抓一段、按规则写进
Bar 表"），而**这个内核还有另外八处调用方，全都还是老方式、这一轮不迁**：

| 还在用内核的老动作 | 调用点 |
|---|---|
| 【拉取区间数据】`FetchYear` | `FetchOrchestrator.cs:1193/1206`、`2362`、`2495` |
| 【重取前复权】`RepairQfq` | `2056` |
| 【补不复权历史】（退役项，老计划里可能还排着） | `1923` |
| 【重新拉取失败】/【只补待办】 | `2836`、`2907`、`2913` |
| 【全库数据体检】的值回补 | `Steps.cs:500` |

所以这次迁移的第一条判据是：**内核不能复制，也不能删**。

照抄进任务类＝同一段"今天那行要覆盖写""漂移就整段重写""空结果能不能记成水位"的判据在库里
存两份，而这类判据出错**是静默的**（project_intraday_bar_confirmation、
project_bar_value_audit 两次事故都是这么来的）。

---

## 1. 方案：把**判据**抽进 Logic，两边各自编排

遵循 [Solution 类图 §0.1 分层职责原则](solution-class-map.md)：
**Remote 取数 / Sqlite 只读写 / Logic 放纯判据 / Tasks 串流程 / Scheduling 管调度。**

所以这次**不造"内核类"**——`ProcessOneStockAsync` 里真正不能复制的是**判据**，不是那几行胶水。
判据抽进 Logic 之后，老编排层和新任务各写各的编排（都很薄），但判的是同一份规则。

### 1.1 抽进 `StockPlatform.Logic/Services/`（纯计算、零 IO、可单测）

| 新增 | 从哪搬来 | 判什么 |
|---|---|---|
| `BarWritePlanner` | `ProcessOneStockAsync` 的写库那一段 | 抓回来的行 + 库里重叠段的收盘价 → `{要插的, 要覆盖的, 今天那几根, 是否漂移}`；含 `IsDrifted` 那个容差 |
| `QfqRepairPlanner` | `RecordDriftedForRepair` 前半段 | 漂移的票里，哪些"历史比手上那一页更长"才真需要重取 |
| `RawBarCompletenessRule` | `NeedsRawBars` + `RawBarsComplete` | 不复权补齐没有——**两头都要比**（只比尾巴会让 5232 只票永远卡在 3 年） |
| `EtfListGuard` | `FetchEtfBarsAsync` 前半段 | 名单是不是半截（比库里存量少 5%）、该不该改用存量 |
| `HfqProbeGate` | `FetchHfqBarsAsync` 的熔断那一段 | 已完成 ≥30 且失败率 >90% → 中止本轮 |
| `DelistedTailPlanner` | `CatchUpDelistedTailsAsync` 前半段 | 哪些退市股缺尾巴、三个口径各自的窗口 |

已有的 `IncrementalWindowCalculator`（水位线 + 盘中行判定）继续用，不动。

### 1.2 `Data/Sqlite` 只补读接口

写的那几个（`InsertOrRefreshUnconfirmed` / `SqliteBarUpsert.Upsert` / `SqliteStockMetaUpsert`）
现成的够用。可能要加一个"取某段重叠区间的收盘价字典"的读方法，喂给 `BarWritePlanner`——
现在这一句 `Query(...).ToDictionary(...)` 散在编排层里。

### 1.3 两边的编排各自很薄

- **`Data/Orchestration`**：`ProcessOneStockAsync` 留着（八处老调用方要它），但瘦成
  「查库 → `BarWritePlanner` → 写库 → 记 stats/进度」，**行为一字不改**。
- **`StockPlatform.Tasks`**：六个任务类走骨架——`FetchAsync` 里调 Remote 拿数据（yield 出批），
  `SaveBatchAsync` 里调 `BarWritePlanner` + Sqlite 写。
  必须这么切，否则骨架的 `MaxItems` / `Deadline` / "停在批边界"三样全失效。

### 1.4 写锁按原则归 Sqlite 层

编排器用私有 `_dbLock` 串行化写（46 处），已迁的那些任务（`EtfRawBarTask`、`LhbTask`…）
**都没有拿这把锁**——既有事实，不是这次引入的。

按 §0.1 的分层，**本地库的并发控制属于 Sqlite 层**，不该是编排层的私有字段。
建议把 gate 收进 `Data/Sqlite`（一个进程内单例），编排器的 `_dbLock` 指向它，任务侧自然共用。

> 不做也能跑（调度串行、`SourceAdmission` 挡同源并发），但"手动点一项 + 计划正在跑另一项"
> 这个组合现在没有任何东西挡着。

---

## 2. 六个任务类的形状

都放 `src/StockPlatform.Tasks/`，继承 `FetchTaskBase<CodeBars>`，`CodeBars` = (代码, 口径,
抓回来的行, 成功与否)。

| 类 | 动作 | 一批＝ | 支持的模式 |
|---|---|---|---|
| `StockDayBarTask` | StepStockDayBars | 30 只（组内并发） | 增量 / 只抓某一天 |
| `StockAdjustedBarTask`（口径参数化，注册两次） | StepStockHfqBars、StepStockRawBars | 30 只 | 增量（+ 不复权另有整段回补） |
| `IndexBarTask` | StepIndexBars | 1 只（一共十几条） | 增量 / 首次整段回补 |
| `EtfBarTask` | StepEtfBars | 30 只 | 增量 |
| `DelistedTailTask` | StepDelistedTails | 1 只（三口径一起） | 增量 |

**为什么分批**：现在是 `Task.WhenAll` 把 5500 只一次性丢给限流器，中途停＝这一轮白跑一段、
`Deadline` 完全不起作用。改成"批间串行、批内并发 30"之后，真正的并发闸仍然是限流器
（腾讯 3 并发 / 1 秒间隔，行为等价），但**批边界成了可以收尾的点**——跟
`NetInflowTask`、`DividendTask` 已经验证过的形状一致。

**进度与心跳**（照 `NetInflowTask` 的规矩，2026-09-19 那次修的教训）：
每批 `ReportQuiet` 喂看门狗，日志每 10 批（≈300 只）写一行。一批 30 只 K 线约 30~60 秒，
远低于 5 分钟静默上限。

**各项要保住的特有逻辑**（迁移时逐条搬，不合并；判据本体一律落在 §1.1 那几个 Logic 类里，
任务类只负责调用）：

- **前复权**：漂移检测窗口放宽 400 天（不多花请求）→ `BarWritePlanner`；
  待重取名单 → `QfqRepairPlanner`；「只抓某一天」那一路的"本地没有这只票就抓 3 年窗口"分支。
- **后复权/不复权**：数据源不支持 `SupportsHfq` 就整项跳过；**起飞前探一只**；
  跑起来之后失败率 >90% 熔断中止 → `HfqProbeGate`。
- **不复权·首次整段回补**：目标枚举走 `RawBarCompletenessRule`，
  任务和 `RunFetchRawBarsAsync`（退役项还挂着）共用同一份。
- **指数**：写 `StockMeta(type=index)`；整段回补从 1990-12-19 起、跑完逐条报"本地最早到哪天"。
- **ETF**：名单半截护栏 → `EtfListGuard`。
- **退市股收尾**：刷名单 → `Upsert` + `StockMeta(type=delisted)` → `DelistedTailPlanner` 筛
  "最后一根早于终止日且没试过"的 → 三个口径各补一段 → **只给没失败的打 `tail_fetched_at`**。

---

## 3. 数据源怎么交给任务

K线源是运行期可变的（`MainViewModel.ResolveBarSource` 读配置，重载设置时会重算），
而任务注册发生在 `App.xaml.cs` 里。

方案：`App` 建一个 `BarSourceHolder { NamedBarSource Current { get; set; } }`，
注册时传给六个任务，`MainViewModel` 在 `ResolveBarSource` 之后写回它。
比"注册时定死"正确（配置重载能生效），比"每个任务自己读配置"简单。

---

## 4. 这次**不**做的事（有意留着）

1. **待办不接管**：六项一律 `HandlesBacklog = false`，`FetchMode.FillBacklog` 仍走
   `FetchOrchestrator.RunFillBacklogAsync`。
   理由：K线的待办有四类（失败名单 / 当天缺失 / 历史空洞 gap / 值问题 value），
   后两类的复查判据跟【全库数据体检】绑在一起（`FillGapTodoAsync`/`FillValueTodoAsync`），
   连这一起迁的话这次改动面直接翻倍。分派按 `HandlesBacklog` 走，留 false 是安全的
   （见 `FetchTaskRegistry.HandlesBacklog` 的告警注释）。
2. **老方法不删**：`FetchStockDayBarsAsync` 等六个私有方法删掉，`Steps.cs` 里六个
   `RunStepXxxAsync` 删掉；但 `ProcessOneStockAsync`、`FetchHfqBarsAsync`、
   `RunFetchRawBarsAsync`、`RunRepairQfqAsync`、`RunFetchYearAsync` 全部留着——它们是别的
   老动作的实现。编排器行数大概减 300~400 行，不会一下子瘦下来。
3. **周/月线**：本来就已经不落库（2026-09-11 改的），迁移不涉及。

---

## 5. 骨架的一处漏项：`LastRunByTask`

**这不是"按新框架做就有"的东西，恰恰是新框架漏做的一件横切小事。**

`Manifest.LastRunByTask` 是 manifest.json 里"各项上次什么时候跑完、有几个错误"的字典，
Fetcher【数据状态】页那份"最近任务运行"列表读的就是它。老路每项跑完都会写一条
（`FinishFetchRun`），而 `FetchTaskBase` 没有这一步——所以**已经迁走的 32 项，
从迁走那天起就不再往里记了**；这六项迁完也会一起从那一页上消失。

补的位置不是每个任务，而是**骨架的统一入口** `FetchTaskRegistry.RunAsync`：
所有新任务（含【只补待办】那条转交路径）都从这儿过，那里写一行
`manifest.LastRunByTask[目录里的中文名] = new TaskRunRecord{...}`，
**一处改动，34 项一起恢复**，键名跟老路一致（都是 `FetchTaskCatalog.Info(id).Name`）。
registry 要多拿一个 `IManifestStore`，App 里加一个构造参数。

> 2026-09-21 决定：作为骨架修复一并做。它是纯附加的，任何任务都不用改。

---

## 6. 验证计划

0. **分层验收**（按 §0.1 原则逐条过）：新增的判据类在 `Logic/Services` 里、不含任何
   `SqliteConnection`/`File`/`HttpClient`；任务类里没有 SQL；`Data/Sqlite` 里没有新增判据；
   同一条判据全 Solution 只有一份（老编排层改成调它，不留副本）。
1. `dotnet test`：新增那六个判据类的纯计算单测（写入方案、漂移、`RawBarsComplete`
   两头比、ETF 半截名单护栏、熔断门限、退市尾巴筛选）。
2. **离线模拟**：用 `MockBarFetcher` 跑六项的完整任务路径（走产品代码，不发请求）——
   验证批边界、`MaxItems`/`Deadline` 收尾、停止后已落库部分有效。
3. **Debug 实机**：按 feedback_verify_by_running_app，起 Debug 实例（数据目录天然隔离）
   逐项点【执行】读日志；重点看三件事：日志密度跟迁移前一致、心跳没被看门狗掐、
   汇总行的"跳过/抓到/返空/失败"四个数跟迁移前同一批数据对得上。
4. **等价性对照**：迁移前后各跑一次增量，比对 Bar 表行数增量与 manifest 失败名单。

---

## 7. 已拍板（2026-09-21）

1. **一批 30 只**。
2. **退市股收尾保持原样**——后复权/不复权两段继续走"探一只 + 熔断"，等价优先。
3. **写锁统一，收进 Sqlite 层**（§1.4）。
4. **`LastRunByTask` 补在骨架入口**（§5）。
5. 分四步落地，每步单独编译 + 实机验证、随时可停：

| 步 | 内容 | 验收 |
|---|---|---|
| ① ✅ | 共享判据抽进 Logic（`BarWritePlanner`、`QfqRepairPlanner`）+ 写锁归位 + 骨架补 `LastRunByTask` | **零行为变更**：编译 + 单测 + 老路跑一轮增量，日志与库行数跟改前一致 |
| ② ✅ | 指数日K、ETF日K（`EtfListGuard` 在这一步抽） | 两项实机跑通，量小好核 |
| ③ ✅ | 个股三口径（`HfqProbeGate`、`RawBarCompletenessRule` 在这一步抽） | 含整段回补那一路 |
| ④ ✅ | 退市股收尾（`DelistedTailPlanner` 在这一步抽） | `tail_fetched_at` 只给没失败的打 |

---

## 8. 落地记录

### ① 共享判据抽取 —— 2026-09-21 完成

改了什么：

| 文件 | 改动 |
|---|---|
| `Logic/Services/BarWritePlanner.cs` | 新增。写入决策 + `IsDrifted` + `DriftCheckLookbackDays` |
| `Logic/Services/QfqRepairPlanner.cs` | 新增。"哪些漂移的票真需要重取更早历史" |
| `Data/Sqlite/SqliteWriteGate.cs` | 新增。本地库的进程内写闸，归 Sqlite 层 |
| `Data/Orchestration/FetchOrchestrator.cs` | 写库那段改成调 planner；`IsDrifted`/`DriftCheckLookbackDays` 改成转发；`_dbLock` 指向 `SqliteWriteGate.Local`（46 处 `lock` 一行没动） |
| `Scheduling/Tasks/FetchTaskRegistry.cs` | 收尾写 `LastRunByTask`（骨架漏项，见 §5） |
| `Tests/BarWritePlannerTests.cs` | 新增 17 个纯函数单测 |

**实机验证**（Debug 实例 + `BarSource: Mock` + `OfflineMock: true`，零联网）：
造了四只票——长历史带漂移(600000)、短历史无漂移(600001)、短历史带漂移(600002)、
必失败(600999)——跑一轮【个股日K·前复权】，五条判据全部按预期落地：

- 漂移那天（2026-06-12，库里 9.00）被按新基准覆盖成 11.11；
- 窗口外的老行（600000 的 2025-05-09）原样没动；
- "今天"那根三只都写进去了（覆盖写那一路）；
- `PendingQfqRepairCodes = ["600000"]`——600002 同样漂移但历史短，**正确地没进名单**
  （这正是 `QfqRepairPlanner` 那条判据）；
- 600999 进了失败待办；
- `LastRunByTask` 里出现了【当日完整性体检】（新框架任务）——骨架那个漏项真的补上了。

### ② 指数日K + ETF日K —— 2026-09-21 完成

| 文件 | 改动 |
|---|---|
| `Logic/Services/EtfListGuard.cs` | 新增。名单半截那道闸 |
| `Logic/Services/FailedTodoRule.cs` | 新增。失败名单"只动本轮碰过的"——**原来编排器和 `NetInflowTask` 各写了一份**，两边都改成调它 |
| `Logic/Services/IncrementalWindowCalculator.cs` | `AShareMarketOpen` 挪进来（整段回补的起点，两边都要用） |
| `Tasks/BarFetchTaskBase.cs` | 新增。K线六项的共同骨架：抓一只/落一批/四个计数/失败名单/批级心跳 |
| `Tasks/BarSourceHolder.cs` | 新增。运行期可变的K线源，由 `MainViewModel.SelectedSource` 写回 |
| `Tasks/IndexBarTask.cs`、`Tasks/EtfBarTask.cs` | 新增 |
| `Scheduling/Tasks/FetchTaskContracts.cs` | `TaskRunArgs` 加 `LookbackYears` |
| `Data/Orchestration/*` | 删 `RunStepIndexBarsAsync`/`RunStepEtfBarsAsync`/`FetchIndexBarsAsync`/`FetchEtfBarsAsync` 与 `EtfListMinKeepRatio` |
| `Fetcher/App.xaml.cs` | 注册两项；**ETF 名单补进离线总开关**（见下） |

**实机验证**（Debug + Mock + OfflineMock，三轮，零联网）：

- 首轮：ETF 3 只里 2 只要抓、1 只（今天已确认）跳过；有历史的只续抓 3 根、无历史的按回看 3 年抓 783 根；
  指数 9 条各 783 根共 7047 行；窗口外的老行没动；`LastRunByTask` 里出现【ETF日K】【指数日K】。
- 末轮（都已是最新）：两项各报一次「完成」，一个请求都不发。

**两处在验证里才暴露的问题，已修**：

1. **`Skipped` 用错了地方**。「标的都已是最新」我一开始返回 `TaskRunResult.Skipped`，
   而 Skipped 的语义是「这轮**没开工**、今天恢复了还该再来」——于是计划引擎立刻再排一次，
   条件又不会变，空转到被"连着 5 轮瞬间跑完"那道护栏拦下。
   已改成 `Completed + NothingToDo`；真正该用 Skipped 的只剩"名单接口不可达且库里没存量"那一支。
   > ⚠ 同一个用法在别的已迁任务里也有（「没有欠着的残缺日」「所有标的都已抓到最新」），
   > 见 `LhbTask` / `LhbSeatTask` / `MarginTask` / `BlockTradeTask` / `NetInflowTask`。**这次没动**。
2. **ETF 名单不在离线总开关里**。`etfListProvider` 一直是裸的 `SinaEtfListProvider`，
   于是"零联网验证"里【ETF日K】照样会真去新浪要一次名单——正是那段注释自己说的
   "逐类去配必然漏"。已补进 `offlineMock` 分支（模拟版直接读库里 type='etf' 的存量）。

### ③ 个股三口径 —— 2026-09-21 完成

| 文件 | 改动 |
|---|---|
| `Logic/Services/RawBarCompletenessRule.cs` | 新增。不复权"补齐了没有"（**两头都要比**）+ `HfqProbeGate`（熔断门限） |
| `Tasks/StockDayBarTask.cs` | 新增。前复权：增量 / 只抓某一天；唯一开漂移检测的一路 |
| `Tasks/StockAdjustedBarTask.cs` | 新增。后复权/不复权**同一个类注册两次**（口径参数化）；探一只 + 熔断；不复权的整段回补 |
| `Tasks/BarFetchTaskBase.cs` | 加 `LocalStockCodes` / `RecordDriftedForRepair` |
| `Data/Orchestration/*` | 删三个 `RunStepStockXxxAsync`；`RawBarsComplete` 改成转发，`NeedsRawBars`+手写 SQL 换成 `GetByTypes` |

**实机验证**（Debug + Mock + OfflineMock，两轮，零联网）：

- 增量轮：三项各跑一遍。前复权触发漂移 → `PendingQfqRepairCodes=["600000"]`（短历史那只正确地没进）；
  不复权按**自己的**水位线只补 14 根（前复权是最新的也不影响它）；后复权无历史 → 回看 3 年 783 根；
  必失败那只（600999）在**三个 taskId 下各记一条**失败待办——2026-09-13 二期"失败名单按口径分域"那件事保住了。
- 整段回补轮：判据只挑出 600000（另两只的 `day_raw` 开头本来就比 `day` 早），
  窗口从前复权最早那天 2025-05-09 起补 357 根，收尾复查报「已全部齐了」。

**一处有意的行为变更**：迁移前**不复权**那一路会逐只打"正在抓取 xxx"（后复权不打，因为
`isHfq` 判的是 `== DayHfq`），全市场一轮几千行、还都走 UI 线程。现在两路统一按批打
（每 10 批一行）、心跳每批一次，跟新框架其它任务一致。

### ④ 退市股收尾 —— 2026-09-21 完成（六项迁完）

| 文件 | 改动 |
|---|---|
| `Logic/Services/DelistedTailPlanner.cs` | 新增。挑"哪几只缺尾巴"（三条条件）+ 后复权/不复权各补哪一段 |
| `Tasks/DelistedTailTask.cs` | 新增。刷名单 → 三个口径各补一段 → 只给没失败的打 `tail_fetched_at` |
| `Data/Remote/MockBarFetcher.cs` | 加 `MockDelistedListProvider`（离线名单，同 ETF 那个漏洞） |
| `Data/Orchestration/*` | 删 `RunStepDelistedTailsAsync` 和 `CatchUpDelistedTailsAsync` |

**实机验证**（Debug + Mock + OfflineMock，两轮，零联网）：造了三只退市股——
缺尾巴的、已补到终止日的、已打过标记的——判据只挑出第一只；前复权从本地最后一根次日补到终止日，
后复权/不复权先探一只再从终止日往前回看 3 年；跑完只有那一只拿到 `tail_fetched_at`，
另两只一根都没动。第二轮（没有可补的）一次报完成、不空转。

**一个已知的脆弱点（原样保留，只是记下来）**：后复权/不复权那两条线的"起飞前探一只"
探的是**最近一年**。退市久了的票那一年本来就没有数据 → 探测判失败 → 整条线跳过。
实际影响小（这里挑出来的基本都是刚退市的），但这是个真实的边界，已写在
`DelistedTailTask` 的注释里。

---

## 9. 迁完之后的账

- 计划里 **38 项新框架 / 16 项老方式**（迁移前是 32 / 22）。
- 编排器从 4,818 行降到 **4,484 行**（`FetchOrchestrator.cs` 3,789 + `Steps.cs` 695）。
  没有一下子瘦下来是意料之中的：`ProcessOneStockAsync`、`FetchHfqBarsAsync`、
  `RunFetchRawBarsAsync`、`RunRepairQfqAsync`、`RunFetchYearAsync` 全都留着——
  它们是【拉取区间数据】【重取前复权】【重新拉取失败】【全库体检值回补】的实现。
- 新增的 Logic 判据共 **7 个**，全部有单测：`BarWritePlanner`、`QfqRepairPlanner`、
  `FailedTodoRule`、`EtfListGuard`、`RawBarCompletenessRule`、`HfqProbeGate`、`DelistedTailPlanner`。
  其中 `BarWritePlanner`、`FailedTodoRule`、`RawBarCompletenessRule` 是**两边共用**的
  （老编排层改成调它们，不留副本）——这是这次迁移的第一条判据，见 §0。
- 顺带补上的两个离线开关漏洞：ETF 名单、退市名单。它们原来都会在"零联网验证"里真发请求。

**六项都没有接管待办**（`HandlesBacklog = false`，见 §4）：K线的四类待办仍走
`FetchOrchestrator.RunFillBacklogAsync`。那是下一个可以单独做的题目。

---

## 10. 下一题：待办接管 · 设计（2026-09-21，**等确认**）

§4 里有意留着的那件事：六项目前 `HandlesBacklog = false`，K线的待办仍走
`FetchOrchestrator.RunFillBacklogAsync`。这一节说怎么把它接过来。

### 10.1 现状：四类待办、谁在补

| 类 | 谁产生 | 现在谁补 | 补法 |
|---|---|---|---|
| `failed` 逐只失败 | 各任务自己 | `RefetchFailedBarsAsync` | 按该口径水位线重抓 |
| `missing_day` 当天缺着 | 【当日完整性体检】 | `RunFillBacklogAsync` 里那段 | 窗口＝缺的那天→今天，按标的类型决定补哪几个口径 |
| `gap` 历史空洞 | 【全库数据体检】 | `FillGapTodoAsync` | 按**区间**抓，抓完复查；补满 2 轮还缺就进「确认没有」白名单 |
| `value` 值问题 | 【全库数据体检】 | `FillValueTodoAsync` → `FillValueIssuesAsync` | 按 Reason 分抓法，复查用对应判据，**不进白名单** |

判据其实**已经在 Logic 里**了（`ValueIssueFixPlan`、`ValueIssueRecheck` 有单测），
Sqlite 那侧有 `SqliteMissingBarRepository.FindGaps/Confirm`、`SqliteBarValueAuditor`。
留在编排层的是**编排**——正好是任务该管的那层。

### 10.2 方案：抽一个 `BarBacklogFiller`（Tasks 层），七个任务共用

```
Tasks/BarBacklogFiller.cs
  ├ FillFailedAsync   (failed)      —— 按本任务口径重抓，复用任务自己的排期
  ├ FillMissingDayAsync(missing_day)—— 窗口＝那天→今天
  ├ FillGapAsync      (gap)         —— 区间抓 + 每批落账 + FindGaps 复查 + 白名单收敛
  └ FillValueAsync    (value)       —— ValueIssueFixPlan 抓 + ValueIssueRecheck 复查
```

- `BarFetchTaskBase` 里加一个 `FillBacklogAsync(...)`，六项的 `FetchAsync` 在
  `Mode == FillBacklog` 时转进去，`HandlesBacklog => true`。
- **每批落账**那条规矩必须原样保住：2026-09-07 踩过——跑了 1 小时 52 分的 Tries 只在内存里，
  中途一停全白费，那 8103 段永远收敛不进白名单。
- **数据源不支持该口径时 Tries 一动不动**：让后复权/不复权空跑两轮会把几千只票永久打进
  「确认没有」白名单。

### 10.3 顺带修一个现成的 bug

`RefetchFailedBarsAsync` 按 taskId 推口径：

```csharp
RetryTaskIds.StockHfqBars => DayHfq, RetryTaskIds.StockRawBars => DayRaw, _ => Day
```

`EtfRawBars` 落到 `_` ⇒ 它的失败票被按**前复权**重抓，`day_raw` 那条线补不回来
（`missing_day` 和 `gap` 两条路都正确分了口径，只有 `failed` 这条漏了）。
任务自己接管之后这个错不可能再犯——口径是任务自己的属性，不用从 taskId 猜。

所以建议把 **`EtfRawBarTask` 一起接管**（它现在也是 `HandlesBacklog = false`）。

### 10.4 接管之后编排层能删掉什么

七项全接管后，`RunFillBacklogAsync` 里除了「转交」那一段就没有别的用户了
（其余有待办的任务早就 `HandlesBacklog = true`），可以连同
`RefetchFailedBarsAsync` / `FillGapTodoAsync` / `FillValueTodoAsync` / `FillValueIssuesAsync`
一起删掉，编排器再减约 400 行。

【重新拉取失败】那个元动作**不动**：它本来就是遍历待办 taskId、经 `ITaskBacklogRunner` 转交。

### 10.5 分两步落地

| 步 | 内容 | 验收 |
|---|---|---|
| ⑤ ✅ | 七项接上、`HandlesBacklog = true`（落在 `BarFetchTaskBase` 的两个 partial 文件里，不另造类） | 离线造四类待办各跑一遍，复查/Tries/白名单的账对得上 |
| ⑥ ✅ | 删编排层那四个方法 + `RunFillBacklogAsync` 瘦身 | 编译 + 全量测试 + 【重新拉取失败】实机跑一轮 |

### 10.6 要拍板的三条

1. **`EtfRawBars` 一起接管**（§10.3 那个 bug 跟着修）——建议要。
2. **`gap` / `value` 这次一起搬**，还是先只搬 `failed` + `missing_day`？
   建议一起：只搬一半的话编排层那块还得留着，等于同一件事两套编排。
3. 顺带发现、**本次不处理**：`MoneyFlowBackfillTask`（`FetchMoneyFlowDetail`）也是
   `HandlesBacklog = false`，而它的残缺日待办在 2026-09-18 删掉那两个分支之后
   **可能已经没人补了**——要不要单独查一下？

---

## 11. 待办接管 · 落地记录（2026-09-21）

### 实现落在哪

没另造 `BarBacklogFiller` 类——四类待办的编排直接做成了 `BarFetchTaskBase` 的两个 partial 文件
（`BarFetchTaskBase.Backlog.cs` 管 failed/missing_day + 结局翻译，`.Audit.cs` 管 gap/value）。
理由：这些编排要用的东西（`Source`/`Bars`/`Report`/`FetchOneAsync`/`SaveBatchAsync`/失败名单）
全在基类上，抽成独立类就得靠七八个委托把它们递进去，没有换来任何解耦。

`EtfRawBarTask` 不是 `BarFetchTaskBase` 的子类，给了它一个**只管 missing_day** 的小实现——
它也只会有这一类（失败名单它从来不写；空洞和值问题按类型/口径归属，落在别的 taskId 上）。

### 顺带改掉的两处

1. **`Skipped` 用错的 5 处**（用户要求一起改）：`LhbTask` / `LhbSeatTask` / `MarginTask` /
   `BlockTradeTask` / `NetInflowTask` 的「没有欠着的待办」「都已抓到最新」全部改成
   `Completed + NothingToDo`，配套 5 个单测跟着改。还留 `Skipped` 的只剩真"没开工"的那几处
   （熔断、盘中、探测失败、名单不可达）。
2. **`RefetchFailedBarsAsync` 按 taskId 猜口径的错**：`StepEtfRawBars` 落进 `_ => day`，
   它的失败票被按前复权重抓。删掉那个方法就没了——口径现在是任务自己的属性。

### 实机验证（Debug + Mock + OfflineMock，两轮，零联网）

在 manifest 里造齐四类待办、并在库里挖了一个真空洞，跑【个股日K·前复权】的「只补待办」：

- **failed** 2 只 → 一只成功移出名单、一只（模拟必失败的）留着；
- **missing_day** 1 只 → 前复权从缺的那天补到今天；后复权/不复权按各自水位线判定后**不发请求**；
- **gap** 1 段 4 天 → 抓回 4 根、`FindGaps` 复查通过、从名单划掉，库里那 4 行确认回来了；
- **value** 1 段 → 抓 → 按 Reason 复查不再命中 → 判定修好。

汇总行：`个股日K·前复权·补待办：前复权K线 2 只、09-18 当天 1 只、空洞 1 段、值问题 修好 1/1 段…`

**第一轮暴露了一个真 bug，已修**：失败名单里那些"已经被别的路补到最新"的票，窗口是空的、
不发请求，于是从来不算"本轮碰过"，**永远移不出失败名单**（老编排层是把整份名单都当作碰过的，
所以没这个问题）。修法是给基类加一个 `MarkAttempted`，窗口为空时记一笔"碰过"再跳过。
第二轮验证名单正确收敛。

### 编排层的账

`FetchOrchestrator` 3,660 行 + `Steps.cs` 359 行 = **4,019 行**（接管前 4,484，再减 465）。
`RunFillBacklogAsync` 只剩"转交给任务"那一段，外加一句"这个任务没声明 HandlesBacklog 却挂着待办"
的告警——那是配置错，不该静默。

### 一个之前的判断纠正

§10.6 第 3 条担心【分档资金流·补历史】的残缺日没人补——**不成立**。
`SqliteDailyTableAuditor` 里 `NetInflowDetail` 那条 spec **故意不配 `OwnerTaskId`**
（"这张表只报不补"：补一天要走 push2his 逐股 5500 个请求，而加了 `CoverageFloor` 之后实测残缺 0 天），
所以它根本不产生残缺日待办。

### 【重新拉取失败】实机补跑（2026-09-21）

造了五个任务的失败名单（个股三口径 + ETF + 指数，每项都掺一只"必失败"和一只"已是最新"），
点【重新拉取失败】跑一轮：

- 五项**按 `DispatchOrder` 的固定顺序**挨个被调用，每项日志都标着「（任务自补）」——
  转交那条路（`ITaskBacklogRunner` → registry → 任务的 `FillBacklog` 模式）通了；
- 名单正确收敛：能成的（600001 / sh510300 / sh000001）全部移出，只剩三只必失败的；
- 收尾那次当日完整性体检照常跑。

**顺手修掉两处日志毛病**：

1. **每条状态打两遍**：编排层的 `RunFillBacklogAsync` 还订着 `source.Fetcher.OnStatus`，
   而任务开跑时自己也订一份（`ForwardSourceStatus`）。待办既然全由任务补了，编排层那份就该撤——
   已删。
2. **ETF/指数被说成"前复权K线"**：失败名单那句日志原来按口径叫。它们只有前复权一路，
   按标的类型叫才读得顺——加了个 `BacklogLabel`，三个非个股任务各 override 一行。

3. **待办清单那一层也按口径叫**（同日一并改掉）：开头那句「本轮要补：前复权K线失败 2 只 ·
   前复权K线失败 2 只 · …」把 `StepStockDayBars` 和 `StepEtfBars` 都说成"前复权K线"，看着像重复了一行。
   `RetryBacklog` 里的 `GranLabelOf` 只分前/后/不复权三档、其余一律"前复权"——改成 `BarLabelOf`
   按任务叫（ETF / ETF不复权 / 指数 / 退市股），跟任务那侧的 `BacklogLabel` 同一套措辞；
   兜底仍是"前复权"（老记录里 taskId 认不出来时按前复权算）。补了 8 条单测把七个任务的名字钉住。
   实机确认：「仍有待重试：后复权K线失败 1 只 · 不复权K线失败 1 只 · **ETFK线失败 1 只**」。

---

## 12. 真库验证（2026-09-21）

前面每一步的实机验证都跑在 Debug 那个几 MB 的小库上（四只造出来的票）。这一轮换成
**生产库的完整拷贝**再跑一遍，看的是"规模"这个维度。

**环境**：`publish/data/local/current.sqlite` 拷进 Debug 数据目录（23.8 GB，`Bar` 表 8,250 万行，
5,564 只个股、1,675 只 ETF、339 只退市股），42 秒拷完；生产库全程只读、没动过。
数据源仍是 `Mock` + `OfflineMock`，**零联网**。

### 结果

| 轮 | 项 | 规模 | 耗时 | 写入 |
|---|---|---|---|---|
| A | 个股日K·不复权 | 5,564 只 | 1:52 | 5,645 行 |
| A | 个股日K·后复权 | 5,564 只 | 4:13 | 5,645 行 |
| A | ETF日K | 1,675 只 | 1:50 | 1,733 行 |
| A | 指数日K | 9 条 | 0 秒 | 9 行 |
| A | 退市股收尾 | 名单 339 只 | 44 秒 | 无尾巴要补 |
| B | 【重新拉取失败】走**真待办** | 见下 | 42 秒 | — |
| C | 个股日K·前复权 | 5,564 只 | 3:59 | **1,587,051 行** |

- **排期在规模下不是瓶颈**：5,564 只逐只查水位线 **2 秒**算完。
- **B 轮跑的是生产 manifest 里真实存在的待办**：前/后/不复权空洞各 2 段（10 个交易日）→
  抓回后 `FindGaps` 在 8,250 万行上复查、全部划掉；**值问题 151 段 / 647 交易日** →
  `ValueIssueFixPlan` 分组抓 → 只覆盖量额换手三列 → 真 auditor 复查 → 判定修好，**4 秒**；
  两融残缺日 1 天转交 `MarginTask`（任务自补）。分派顺序、「（任务自补）」标记、收尾体检全正常。
- **`RecordDriftedForRepair` 那次全表 GROUP BY**（`GetEarliestPeriodStartByCode`，8,250 万行）
  实测 **14 秒**——C 轮里 3:45 是抓+写、最后 14 秒是它。
- **落库吞吐**：C 轮 158.7 万行 / 3:45 ≈ **6,600 行/秒**（走 `BarWritePlanner` + upsert 的完整路径）。

### 两处只能算"机制验证"、不能当数据结论

1. **C 轮判了 5,390 只"复权基准漂移"**——模拟源固定返回 11.11，跟真实价一比当然全是漂移。
   它验证的是这条路在 5,564 只 × 400 天窗口下跑得动，不是真有这么多票除权。
2. **B 轮"值问题修好 151 段"**——`ValueIssueFixPlan` 对「多口径不一致」会连基准 `day` 一起抓，
   模拟源给两个口径写了同一组假值，复查自然就不再不一致（抽查 `sh510050` 确认）。
   链路和判据都真跑了，但"修好"是模拟源造成的必然结果。

### 顺带量到的一个数

C 轮覆盖 158 万行把 WAL 撑到 **15 GB**（进程是被强杀的，没走 checkpoint）。真实场景里漂移不会是
全市场，但这给了个量级参考——跟 2026-09-11 板块指数合成那次 WAL 162 GB 是同一类账。

验完把 23.8 GB 的拷贝连同 15 GB 的 WAL 一起删了，Debug 的小测试库、manifest、设置、计划全部还原。

### 12.1 真联网验证（2026-09-21 11:05，用户授权）

前面都是模拟源。这一轮用**真腾讯**再跑一遍——规模那一维已经验过，真联网要验的是**数据本身**。

做法：再拷一份生产库，在**拷贝里**把名册裁到 40 只个股 + 15 只 ETF（`Bar` 一行没动，
所以水位线、历史、复权基准全是真的），设置不动（默认就是 Tencent、`OfflineMock` 没开）。

| 项 | 规模 | 耗时 | 结果 |
|---|---|---|---|
| 个股日K·不复权 | 40 只 | 17 秒 | 写入 40 行 |
| 个股日K·前复权 | 40 只 | 45 秒 | 抓到数据 40 只、**写入 0 行** |
| 个股日K·后复权 | 40 只 | 46 秒 | 返空 40 只、写入 0 行 |
| ETF日K | 1,674 只 | 27 分钟 | 写入 1,310 行、返空 364 只 |
| 指数日K | 9 条 | 40 秒 | 写入 9 行 |
| 退市股收尾 | 名单 319 只 | 37 秒 | 没有要补尾巴的 |

**六项零失败。** 限流器按预期工作（每 50 个请求主动歇 30 秒，中途打的
"仍在主动暂停中，预计还需 1 秒恢复（不是卡死）"没被看门狗误判）。

#### 两条模拟源验不出来的结论

1. **前复权一次漂移都没报。** 模拟源那轮判了 5,390 只"基准漂移"（假价 11.11 vs 真实价的必然），
   真数据下是 **0**——`BarWritePlanner` 拿腾讯当前基准的 400 天窗口跟库里逐根比，全部对得上，
   所以写 0 行、`PendingQfqRepairCodes` 没涨。这条判据最容易在重构里改错，而错了是静默的。

2. **盘中行会被重抓覆盖**（2026-09-01 事故那条防线）。11:05 那轮把 09-21 的盘中 `day_raw`
   写了进去（600000：收 9.02 / 量 378235 / 抓于 11:05）；11:54 再跑一次同一项，报的是
   「40 只里有 40 只要抓、**0 只本地已是最新**」，那 40 行被覆盖成
   （600000：收 8.99 / 量 433411 / 抓于 11:54）。
   `IsConfirmedFinal` + `BarWritePlanner` 的 Today 桶 + `Upsert` 三者合起来生效。

#### 顺带量到的数据源行为（盘中 11:05，三个口径给的东西不一样）

| 口径 | 请求窗口 | 腾讯给了什么 |
|---|---|---|
| `day_raw` | 短窗（水位线→今天） | **有当天那根**（盘中值） |
| `day_hfq` | 短窗 | **整段返空** |
| `day`（开漂移检测） | 400 天窗 | 给历史，**不给当天那根** |

三种都被正确处理：没有乱写、没有报错、计数对得上。ETF 和指数走的也是 `day` 但不开漂移检测
（短窗），它们**拿到了**当天那根——所以"前复权不给当天"更像是长窗口请求的行为，不是口径本身。

验完把 23.8 GB 的拷贝删了，Debug 的小测试库、manifest、设置、计划全部还原；生产库全程只读。
