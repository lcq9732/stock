# K线六项迁到新任务框架 · 设计方案

2026-09-21 起草，**等确认后再动产品代码**。

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

## 1. 方案：先把内核抽出来，两边共用

新增 `src/StockPlatform.Data/Orchestration/BarFetchKernel.cs`，把 `ProcessOneStockAsync`
拆成**可以分别调用的两段**（现在是抓完立刻写，糊在一个方法里）：

```csharp
public sealed class BarFetchKernel(string dbPath, SqliteBarRepository repo)
{
    // ① 只发请求，不碰库。失败翻译成 Outcome.Failed，不抛。
    Task<BarFetchOutcome> FetchOneAsync(
        string code, NamedBarSource source, DateTime start, DateTime end,
        string granularity, bool driftCheck, CancellationToken ct);

    // ② 只写库，不联网。今天覆盖 / 旧行 InsertOrRefreshUnconfirmed / 漂移整段覆盖，
    //    判据一字不改地从 ProcessOneStockAsync 搬过来。
    BarWriteOutcome Write(
        string code, string granularity, IReadOnlyList<Bar> bars,
        bool overwrite, bool driftCheck);
}
```

- `ProcessOneStockAsync` 改成 `FetchOneAsync` + `Write` + 原有的 stats/日志/进度心跳，
  **行为一字不改**，那八处老调用方零改动。
- 新任务：`FetchAsync` 里调 ①（yield 出批），`SaveBatchAsync` 里调 ②。
  这样才真的吃到骨架的 `MaxItems`/`Deadline`/"停在批边界"——把整块塞进 `FetchAsync`
  里自己写库的话，那三样全失效。

### 写锁怎么办

编排器用 `_dbLock` 串行化所有写（46 处）。已迁的那些任务（`EtfRawBarTask`、`LhbTask`…）
**都没有拿这把锁**——这是既有事实，不是这次引入的。

建议顺手收口：把编排器的 `private readonly object _dbLock = new()` 改成指向
`BarFetchKernel` 里的一个进程内单例 gate，任务侧用同一个。一行改动，两边从此互斥。

> 不这么做也能跑（调度是串行的，`SourceAdmission` 还挡了同源并发），但"手动点一项 +
> 计划正在跑另一项"这个组合现在没有任何东西挡着。

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

**各项要保住的特有逻辑**（迁移时逐条搬，不合并）：

- **前复权**：漂移检测窗口放宽 400 天（不多花请求）、`RecordDriftedForRepair` 记待重取名单；
  「只抓某一天」那一路的"本地没有这只票就抓 3 年窗口"分支。
- **后复权/不复权**：数据源不支持 `SupportsHfq` 就整项跳过；**起飞前探一只**；
  跑起来之后失败率 >90% 熔断中止（`HfqAbortCheckAfter=30`）。这三条都搬进
  `StockAdjustedBarTask`。
- **不复权·首次整段回补**：目标枚举判据（`RawBarTargetCodes` / `RawBarsComplete`——
  "两头都要比，只比尾巴会让 5232 只票永远卡在 3 年"）抽成 Data 层一个 planner，
  任务和 `RunFetchRawBarsAsync`（退役项还挂着）共用。
- **指数**：写 `StockMeta(type=index)`；整段回补从 1990-12-19 起、跑完逐条报"本地最早到哪天"。
- **ETF**：名单半截护栏（比库里存量少 5% 就改用存量名单跑、记一条 error）。
- **退市股收尾**：刷名单 → `Upsert` + `StockMeta(type=delisted)` → 筛"最后一根早于终止日
  且没试过"的 → 三个口径各补一段 → **只给没失败的打 `tail_fetched_at`**。

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

## 5. 一个顺手能补的欠账（要不要做请拍板）

`Manifest.LastRunByTask`（"数据状态"页那份"最近各项什么时候跑的"）**只有老路的
`FinishFetchRun` 在写**。也就是说已经迁走的那 32 项，从迁走那天起就不再往里记了。

这次要给六项写失败名单收尾，顺手抽一个共享的 `TaskRunManifest.Record(taskId, errors)`
给所有新任务用，代价很小。不做也不影响抓取，只是那一页继续少一半。

---

## 6. 验证计划

1. `dotnet test`：新增 planner/kernel 的纯计算单测（水位线、漂移判据、`RawBarsComplete`
   两头比、ETF 半截名单护栏）。
2. **离线模拟**：用 `MockBarFetcher` 跑六项的完整任务路径（走产品代码，不发请求）——
   验证批边界、`MaxItems`/`Deadline` 收尾、停止后已落库部分有效。
3. **Debug 实机**：按 feedback_verify_by_running_app，起 Debug 实例（数据目录天然隔离）
   逐项点【执行】读日志；重点看三件事：日志密度跟迁移前一致、心跳没被看门狗掐、
   汇总行的"跳过/抓到/返空/失败"四个数跟迁移前同一批数据对得上。
4. **等价性对照**：迁移前后各跑一次增量，比对 Bar 表行数增量与 manifest 失败名单。

---

## 7. 待确认（开工前要你拍板的四条）

1. **一批 30 只**——还是想要更小（停得更快）或更大（日志更少）？
2. **退市股收尾**的后复权/不复权两段，现在会走"探一只 + 熔断"那套；
   `pending` 通常只有 0~2 只，探测等于多发一个请求。保留原样（等价优先），还是去掉？
3. **写锁收口**（§1 末）要不要一起做？
4. **`LastRunByTask` 欠账**（§5）要不要顺手补？
5. 分几次落地？建议顺序：
   **①内核抽取（零行为变更，先单独验一次）→ ②指数+ETF（量小、模式简单）→
   ③个股三口径 → ④退市股收尾**。每一步都能单独编译+实机验证，随时可停。
