# 待办统一格式与任务自治：重取改成调度器（2026-09-13）

## 0. 起因

界面上【重新拉取失败】显示 `09-11日线 1 只`，点下去跑了几个小时。

查 manifest 实际内容：

| 显示的 | 实际要跑的 |
|---|---|
| 09-11日线 1 只 | 09-11日线 1 只 |
| —— | 历史空洞 **1909 段**（1907 只票、合计 **20.8 万个交易日**，顺序抓不并发） |
| —— | 资金净流入缺失日（本次 0 天） |

`FailedRetrySummary.Describe()` 手工列举了 9 类，`MissingBars` 和 `MissingNetInflowDays` 不在其中。

## 1. 现状全图：谁写、存哪、谁用

| 谁发现的 | 写进 manifest 的 | 谁来补 | 摘要显示 |
|---|---|---|---|
| 日常抓取中各步骤失败 | 7 个 `Failed*Codes` | 【重新拉取失败】 | ✅ |
| 日更末尾 `CheckLatestDayCoverage` | `MissingDayCodes` | 【重新拉取失败】 | ✅ |
| **全库体检 `FullAuditTask`** | `MissingBars` | 【重新拉取失败】 | ❌ |
| **全库体检 `FullAuditTask`** | `MissingNetInflowDays` | 【重新拉取失败】 | ❌ |
| 前复权基准变化检测 | `PendingQfqRepairCodes` | 另一个按钮（"待重算 N 只"） | 自己有显示 |

前两行是「抓的时候发现的」，中间两行是「事后体检发现的」——这四类全归【重新拉取失败】。

**为什么只漏了体检那两项**：前八个字段全由 `FetchOrchestrator` 自己写、自己读，七个 `Failed*` 还共用同一个
`ComputeUpdatedFailedCodes`，形成了事实上的约定，加一类绕不开、也就顺手被摘要抄到了。
`MissingBars` / `MissingNetInflowDays` 由 `StockPlatform.Tasks.FullAuditTask` 写——往 manifest 上放个字段就走了，
`FetchOrchestrator` 这边没有任何东西会知道多了一项待办。

manifest 在这里被当成了**共享可变状态**，而不是接口：谁都能加字段，但没有任何地方声明过
「哪些字段代表一件待办、它叫什么、归谁补」。

## 2. 病根

主症是 **manifest 上「哪些字段算待办」这份约定，在四个地方各手抄了一遍**，
加新待办的人不知道要去抄第五遍。

另有两处「写的时候手里有归属、存的时候扔了」，但查证后只有一处真有危害：

### 2.1 体检丢了 `Type`

`FullAuditTask` 按**面**扫，面 = 标的类型 × 口径，五个面连中文标签都定义好了：

```csharp
private static readonly (string Type, string Gran, string Label)[] FetchableScopes =
[
    (TypeStock, Day,    "个股·前复权"),
    (TypeStock, DayHfq, "个股·后复权"),
    (TypeStock, DayRaw, "个股·不复权"),
    (TypeEtf,   Day,    "ETF"),
    (TypeIndex, Day,    "指数"),
];
```

这五个面本来就是照着任务列表定的（类注释：*"把【拉取全部】拆成独立任务之后，后复权、不复权、ETF、
指数各自成了一项——独立就意味着可以被漏排、可以单独失败"*）。**体检的维度就是任务的维度**：

| 体检的面 | 该补它的任务 |
|---|---|
| `(Stock, Day)` | `StepStockDayBars` 【个股日K·前复权】 |
| `(Stock, DayHfq)` | `StepStockHfqBars` 【个股日K·后复权】 |
| `(Stock, DayRaw)` | `StepStockRawBars` 【个股日K·不复权】 |
| `(Etf, Day)` | `StepEtfBars` 【ETF日K】 |
| `(Index, Day)` | `StepIndexBars` 【指数日K】 |
| `MissingNetInflowDays` | `StepNetInflow` 【资金净流入】 |

但 `CommitScope` 落账第一行：

```csharp
var gran = scope.Split(ScopeSep)[1];   // 只取口径，[0] 的类型直接扔掉
```

`MissingBarRange` 没有承载归属的字段，于是类型在落盘时蒸发。

**这一处的价值**：它回答了「体检知不知道该谁补」——**知道，而且体检的维度就是任务的维度**。
所以 §3.2 的统一格式里，体检那边的 `TaskId` 是现成的，不用新造。
（顺带：就算不改格式，`Granularity` 也够现有重试用，见 §2.4。）

### 2.2 失败名单丢了 `fetchKind`

`FinishFetchRun` 同一个方法里，一行之隔：

```csharp
manifest.FailedCodes = ComputeUpdatedFailedCodes(...);        // 全局一份，不分任务
manifest.LastRunByTask[fetchKind] = new TaskRunRecord {...};  // 按任务分域
```

`fetchKind` 就是任务名，27 个调用点全都在传（"个股日K·前复权"、"个股日K·后复权"、"个股日K·不复权"、
"ETF日K"、"指数日K"、"退市股收尾"…）。但 `FailedCodes` 是**一个不分任务的大池子**。

### 2.3 于是消费方只能猜

- **K线重试一律按前复权补**（`FetchOrchestrator.cs:2861`，`GetLatestBarInfo(code, Granularity.Day)` +
  `ProcessOneStockAsync` 默认 `granularity: Day`）。`RunStepStockAdjustedBarsAsync`（后复权/不复权）
  失败的票进的是同一个 `FailedCodes`，走这条路只补得到前复权。
  **严重性**：不是数据丢失——全库体检会把 `day_hfq` / `day_raw` 的缺口报成 `MissingBars`，
  下一轮从那条路补回来。代价是绕一圈、慢一轮。
- **摘要 / `Any` / early-return 各自手抄一份清单**（`Describe()`、`Any`、`FetchOrchestrator.cs:2749`
  那八个 `.Count == 0`），漏一处就是安静的错。这是本设计要治的主症。

### 2.4 不是问题的：ETF / 指数混在 day 组里

`FillAuditedGapsAsync` 按 `Granularity` 分组，`day` 这一组里个股、ETF、指数三个面混在一起。
**查证后确认这没有危害**：`FetchEtfBarsAsync` 和 `FetchIndexBarsAsync` 都直接调用同一个
`ProcessOneStockAsync`、同一个 `source.Fetcher`、同一套 `IncrementalStart` 水位线——
它们跟个股的差别只在「代码从哪来」（ETF 列表 provider / `MarketIndexCatalog` / 本地名册）
和并发方式，跟补空洞这件事无关。

所以补空洞时把它们当个股抓，抓法本来就是对的。
**推论**：`MissingBarRange` 不需要 `Owner` 字段——`Granularity` 已经承载了执行所需的全部信息。
真正缺归属的只有 `FailedCodes`，它连口径都没有。

## 3. 最终形状

### 3.1 三句话

1. **待办统一格式**：日常任务失败的、全库体检查出来的，都按 `(任务Id, 数据)` 写进 manifest。
2. **任务自治**：每个任务认领属于自己的待办——自己补、自己复查、自己移除。
3. **重取退化成调度器**：读清单 → 按任务Id 逐个启动 → 自己不含任何抓取逻辑。

显示（重取那行、体检那行）读的是同一份清单。补完即清，因为清单就是执行的依据。

### 3.2 存储格式

```csharp
/// <summary>一件待办：归谁补、补什么。</summary>
public sealed class RetryTodo
{
    public string TaskId { get; set; } = "";     // FetchActionId 枚举名
    public string Kind { get; set; } = "";       // gap / value / failed / missing-day …
    public List<RetryTarget> Targets { get; set; } = new();
}

/// <summary>要补的一条。区间为空＝整只按任务自己的规则补。</summary>
public sealed class RetryTarget
{
    public string Code { get; set; } = "";
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int Tries { get; set; }
}
```

manifest 上一个 `List<RetryTodo> Todos`。写入方两类，格式相同：

| 写入方 | 怎么定 TaskId |
|---|---|
| `FinishFetchRun` | 已有的 `fetchKind` 反查 catalog（27 个调用点全在传，见 §2.2） |
| `FullAuditTask.CommitScope` | 已有的 `scope` = 类型×口径查表（见 §2.1） |

**两处都已经知道 TaskId，只是现在扔了。**

### 3.3 启动任务：现成的接缝

`MainViewModel.DispatchPlanActionAsync` 是**唯一**的"按 FetchActionId 启动任务"入口：

```csharp
if (_taskRegistry?.Has(item.Action) == true)          // 新式任务
    return _taskRegistry.RunAsync(item.Action, new TaskRunArgs(Mode: item.EffectiveMode, …), …);
switch (item.Action) { … }                            // 老式：也读 item.EffectiveMode
```

新增一个模式即可：

```csharp
FetchMode.FillBacklog   // 「只补待办清单」
```

于是重取变成一个循环：

```csharp
foreach (var taskId in backlog.Actionable.Select(i => i.TaskId).Distinct())
    await DispatchPlanActionAsync(
        new FetchPlanItem { Action = taskId, Mode = FetchMode.FillBacklog }, …);
```

`RunRetryFailedInternalAsync` 那一大套（市值/净流入/指数/股东/分红/当天日线/空洞/资金流缺失日/K线）
全部拆散归还给各任务，重取自己一行抓取代码都不剩。

**额外好处**：每个任务在计划表里可以单独设成「补待办」模式跑，不必非得从重取进去。

### 3.4 传入数据 vs 任务自己读：按任务选

两种都支持，看哪种自然：

| 待办 | 选哪种 | 为什么 |
|---|---|---|
| `MissingBars` 的段 | **任务自己读** | 带区间、`Tries`、复查判据，结构复杂；而且这正是现有 `FillAuditedGapsAsync` / `FullAuditTask` 的风格 |
| 失败代码列表 | 传入或自读都行 | 就是个 `List<string>` |

统一的是**格式和分派**，不是取数方式。`TaskRunArgs` 现在没有"范围"入参，
走「任务自己读」就完全不用动这个契约。

### 3.5 移除判据：各任务自己带，不统一

⚠ 三类的复查方式本来就不一样，合并会造成**安静的数据错**：

| 类 | 怎么才算补上了 | 能否"成功即移除" |
|---|---|---|
| 七类失败名单 | 这轮没失败就移出 | ✅ 本来就是 |
| `MissingBars` 缺行 | 抓完 `FindGaps` 复查，真补上才划掉；补两轮拿不到进白名单 | ❌ 请求成功 ≠ 数据真到了（停牌） |
| `MissingBars` 值问题 | 按 `Reason` 用**对应判据**复查 | ❌ 用 `FindGaps` 复查会一律判成"已补齐"划掉，哪怕值没被覆盖 |

复查逻辑留在各任务内部——本来就该在那里，这也正是"任务自治"的应有之义。

### 3.6 为什么不能直接调任务的日常入口

`RunStepStockRawBarsAsync()` 第一行是 `LocalStockCodes()` 全量，然后按**水位线**跑。
而**水位线只往前抓新日期，历史空洞在水位线之下** ——
`FillAuditedGapsAsync` 存在的理由就是这个（"正常抓取永远不会回头补这些旧行"）。

所以必须有 `FillBacklog` 这个独立模式：同一个任务、同一份抓取代码，
但目标来自待办清单而不是水位线。不加这个模式而直接启动任务，
结果是**跑得更久而且一段也补不上**。

## 4. 显示：两处，一份数据

`RetryBacklog.From(manifest)` 从 `Todos` 派生，每项带一个 `Actionable`：

**判据只有一条：点【重新拉取失败】之后这个数字会不会降。**

⚠ **修正（实现时核实）**：设计阶段把值问题（`IsValueIssue == true`）判成了"补不了"，
依据是 `FillAuditedGapsAsync` 里"值类记录这一轮先原样留着"那句注释。**那是误读**——
那句说的是**缺行那个循环里**先不动它们，等缺行跑完再单独处理；
2026-09-09 加的 `FillValueIssuesAsync` 真的会去抓、去改。所以值问题是可执行的。

于是**目前每一类待办都补得了**，`Actionable` 恒为真。这个机制仍然留着：
以后真挂进"只报不补"的待办时，登记时标一下 `actionable: false`，
显示、`Any`、early-return 三处自动跟上，不用回头再改。

| 显示位置 | 显示什么 |
|---|---|
| 【重新拉取失败】那行 | 只列 `Actionable`。`Any` / early-return / 按钮可点性也只看它 |
| 【全库数据体检】那行 + tooltip | 列**全部**，含值问题，并注明哪些不是重取能补的 |

体检是发现者，问题全貌就该在它那里看；重取是执行者，只显示它能干的。

今天这份数据在重取那行会显示：

```
不复权空洞 1907 段/20.8万交易日 · 09-11日线 1 只 · 前复权空洞 1 段 · 等 1 项
```

段数（≈请求数，决定要跑多久）和交易日数（数据量）都给——
只给"1907 段"看不出这是个三小时的活。

⚠ 另有一种"这一轮不会降"是**运行时**才知道的：数据源不支持某口径时整组跳过
（`!source.Fetcher.SupportsHfq`）。这取决于当前数据源，清单只读 manifest 判不了，
**不做分流**——执行时日志里已有明确一句（"要补请把数据源切到 Tencent"），够了。

### 4.1 tooltip 的绑定坑

计划表的行 DataContext 是 `PlanItemViewModel`，不是 `MainViewModel`；
ToolTip 不在行的可视树里，`RelativeSource AncestorType=Window` 在 ToolTip 内部取不到。
所以这两段文本做成 `PlanItemViewModel` 自己的属性（跟 `StatusText` 一样由外部写入），
绑定写 `{Binding AuditBacklogDetail}`，DataContext 天然正确。

### 4.2 "补完就清"是现成的

`RefreshFailedCodeCount` **每项任务跑完都会触发**（`MainViewModel.cs:722`），
重算的就是同一份 manifest。体检跑完名单重建、重取跑完名单缩小，两处显示一起更新。

## 5. 分期

| 期 | 内容 | 交付 |
|---|---|---|
| **一** | `RetryBacklog` 从**现有字段**派生；统一显示 / `Any` / early-return；体检那行加 tooltip | 漏报当天就好；**零存储变更**、零执行改动 |
| **二** | `Todos` 统一格式 + `FetchMode.FillBacklog` + 重取改成循环分派 | §3 的完整形状 |

二期可以**一个任务一个任务地迁**：迁好的走 `FillBacklog`，没迁的暂时留在
`RunRetryFailedInternalAsync` 老路径里，两套并存到迁完为止。

二期做完，一期那层 `From(Manifest)` 会变薄（直接读 `Todos`），但**接口不变、消费方一行不用改**
——一期不是白做的过渡件。

## 6. 兼容

一期：没有存储变更。

二期：老 manifest 的九个名单按 §1 的表回填成 `Todos`（`MissingBars` 的 TaskId 由
`Granularity` 推，`FailedCodes` 整体归 `StepStockDayBars`，与现状等价）。
老字段留着不删，下一轮自然清空。

## 7. 验证

- 单元测试：`RetryBacklog.From` 对「值问题混排」「空名单」「`Granularity` 为空的老记录」
  「只剩 `MissingBars` 别的全空」四种输入；值问题只进 `Items` 不进 `Actionable`；
  `Any` 与 early-return 结论一致。
- 二期另需：`FillBacklog` 模式下任务只碰属于自己的待办；复查后 `Tries` 收敛正确。
- 改完跑 `dotnet test`（`StockPlatform.Tests`）。
- 实机：Debug 起 Fetcher，读【重新拉取失败】那行字、以及【全库数据体检】那行的 tooltip，
  且**不点执行**——1909 段真跑要几小时（Debug 数据目录天然隔离）。
- 回归点：别的名单全清空、只剩体检查出的空洞时，那行字必须仍然显示它
  （`Any` 为真 → 重取入口的 early-return 不拦、自动重试照排）。这是今天那个 bug 的正面回归。
  ⚠ 表述更正：计划表那行的「执行」按钮**本来就一直可点**（XAML 没绑 `HasFailed`），
  `Any` 影响的是 early-return 和自动重试排程两处，不是按钮灰不灰。

---

## 8. 落地记录（2026-09-13）

两期都已实现、测试通过、实机验证过。与设计的出入都记在这里。

### 一期

| 做了什么 | 在哪 |
|---|---|
| `RetryBacklog` / `RetryItem` 契约，唯一派生入口 `From(Manifest)` | `StockPlatform.Data/Orchestration/RetryBacklog.cs`（新） |
| 删掉 `FailedRetrySummary`（那份手抄清单） | 已删 |
| 重取入口 early-return 改读 `backlog.Any`，八个 `.Count == 0` 全删 | `FetchOrchestrator.RunRetryFailedInternalAsync` |
| 界面那行字、自动重试计数改读 backlog | `Fetcher/ViewModels/MainViewModel.cs` |
| 【全库数据体检】那行加"待补 N 段"+ tooltip 列全部明细 | `MainWindow.xaml`、`PlanItemViewModel`、`MainViewModel.PushBacklogToPlanItems` |

### 二期

| 做了什么 | 在哪 |
|---|---|
| 统一待办格式 `RetryTodo` / `RetryTarget`（TaskId + Kind + Targets） | `Logic/Models/RetryTodo.cs`（新） |
| `Manifest.Todos` + `MigrateLegacyTodos()`（九个老名单读一次就地迁移、清空） | `Logic/Models/Manifest.cs` |
| `JsonManifestStore.Load` 读完即迁移 → 内存里永远是新格式 | `JsonManifestStore.cs` |
| 写入方全部改写：`FinishFetchRun`（带 taskId）、七类失败名单、`CheckLatestDayCoverage`、`FullAuditTask.CommitScope` / `CommitValueFindings` / `QueueMissingNetInflowDays` | 共 11 处 |
| 体检落账**不再丢 Type**：`TaskIdOfScope(type, gran)`，ETF / 指数各归各的任务 | `FullAuditTask` |
| `FetchMode.FillBacklog` + 模式下拉里的「只补待办」 | `FetchTaskCatalog`、`PlanItemViewModel.ModeOption` |
| 重取退化成调度器：读清单 → 按 TaskId 分派 | `RunRetryFailedInternalAsync` + `RunFillBacklogAsync` |
| 补空洞/值问题改成"认领自己那一条" | `FillGapTodoAsync` / `FillValueTodoAsync`（原 `FillAuditedGapsAsync`） |
| K线失败按**自己的口径**重抓（§2.3 那个缺陷） | `RefetchFailedBarsAsync` |
| 离线模拟数据源（只在 DEBUG 注册，用来端到端跑执行链而不发请求） | `Data/Remote/MockBarFetcher.cs`（新） |

### 与设计的出入

1. **值问题是补得了的**——见 §4 那段修正。设计阶段照一句过时注释判成了"补不了"。
2. **"按钮会变灰"是错的**：`HasFailed` 只喂给自动重试，XAML 没绑它，计划表那行的「执行」
   一直可点。`Any` 真正影响的是重取入口的 early-return 和自动重试排程。
3. **`MissingBarRange.Owner` 没有加**，理由见 §2.4（`Granularity` 已够执行用）；
   二期改成统一格式之后，归属由 `RetryTodo.TaskId` 承载，这个字段更没必要了。

### 验证

- `dotnet test`：1333 项全过（新增 `RetryBacklogTests` 11 项、`RetryDispatchTests` 6 项）。
- 拿**真实 manifest** 跑迁移核对：空洞/值问题/当天日线/资金流缺失日/失败名单五类数量前后一致，
  老字段清空、`MissingBars` 条数 == 迁移后 gap + value 之和。
- 实机（Debug 实例，数据目录天然隔离，**没点执行、没发请求**）：
  - 造 8 类待办 → 重取那行显示
    `不复权空洞 1907 段/33.4万交易日 · 净流入 3 只 · 09-11日线 1 只 · 等 5 项`，
    体检那行显示 `待补 1910 段`；
  - tooltip 逐行列出全部待办（一期已截图确认绑定生效，二期未改绑定方式）；
  - 模式下拉里出现「只补待办」；
  - 回归点：只剩体检空洞时那行字仍然显示它（`Any` 为真）。
### 执行链：离线模拟源跑通了

真跑一轮是几千个网络请求、几小时，还会跟正在抓数据的正式实例抢配额；
单测又只覆盖得到纯函数，覆盖不了"界面点下去之后整条链对不对"。
所以加了一个**离线模拟数据源** `MockBarFetcher`（`Data/Remote/`）：
一个请求都不发，按请求区间凭空造K线，代码里含 `999` 的一律抛异常（用来验失败那条路）。

⚠ 两道闸，别拆：① 只在 `#if DEBUG` 里注册进数据源列表（Release 的 `BarSource` 选不到它）；
② Debug 实例的数据目录在 `bin\Debug\...\data` 下，跟正式实例的 `publish\data` 天然隔离。

**造的场景**（每条对应链上一个分支）：

| 标的 | 状况 | 待办 |
|---|---|---|
| 600001 | 前复权缺 3 天 | `StepStockDayBars` / gap |
| 600002 | 不复权缺 3 天 | `StepStockRawBars` / gap |
| 600003 | 后复权缺 3 天 | `StepStockHfqBars` / gap |
| 600999 | 前复权缺 3 天，模拟源必抛异常 | `StepStockDayBars` / gap |
| 600004 | 当天日线没到位 | `StepStockDayBars` / missing_day |

**第一轮**（点【执行】）：

```
执行前：前复权空洞 2 段/6交易日 · 不复权空洞 1 段/3交易日 · 后复权空洞 1 段/3交易日 · 等 1 项
执行后：前复权空洞 1 段/3交易日 · 前复权K线失败 1 只
```

- 600001/600002/600003 的待办**各自消失**，库里各补上 3 天、**写在自己的口径上**
  （`day` / `day_raw` / `day_hfq` 分别对得上）→ 按 TaskId 分派 + 各用各的口径，成立；
- 600004 的当天日线补上，顺带把它 `day_hfq`/`day_raw` 的水位线缺口补齐（各 783 根，
  真实行为，只是模拟源返回满区间所以显得多）；
- 600999 抓取失败 → 留在 gap 待办里 `Tries` 0→1，同时进 `StepStockDayBars` 的 **failed** 待办
  → 失败名单按任务分域记账，成立；
- 全程日志里只有 `[模拟源] … 未发任何请求`。

**第二轮**（再点一次，验收敛）：

```
执行前：前复权空洞 1 段/3交易日 · 前复权K线失败 1 只
执行后：前复权K线失败 1 只
```

600999 的 `Tries` 1→2 到顶，从 gap 待办移除、写进库里的 `MissingBarConfirmed`
（`600999/day: 3 天`）——判定"数据源确实没有"、以后体检不再报。收敛链成立。

### 还没做的

- **真实数据源的一轮**没跑（几千个请求）。模拟源覆盖的是分派/口径/复查/收敛这些逻辑，
  真实源特有的东西（限流、翻页、各家口径换算）不在其中。
- 分派顺序另有纯函数测试 `FetchOrchestrator.DispatchOrder` + `RetryDispatchTests`。
