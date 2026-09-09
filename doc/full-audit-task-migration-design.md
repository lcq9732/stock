# 【全库数据体检】迁到新 Task 框架（2026-09-09）

## 0. 为什么破"老任务不迁"那条规矩

`doc/scheduler-redesign.md` 定的是**老任务不迁、新任务按新形状写**。这一项是第一个例外，判据是
两条同时成立：

1. **它正要长大**——要加 6 条值体检判据（见 `doc/bar-value-audit-design.md`），与其往已经很大的
   `FetchOrchestrator.Steps.cs` 再堆两百行，不如借这次搬出去；
2. **它恰好卡在框架能给的两件事上**：

   | 框架能力 | 体检现在的状况 |
   |---|---|
   | 流式"扫一批落一批" | 现在是扫完一次性 `manifest.MissingBars = ranges` + `Save`——**中途停就全丢**。三遍扫描半小时，停在 29 分钟等于白跑 |
   | `MaxItems` / `Deadline` | 完全没有。半小时的重活最该能"空闲窗口补一段、到点收尾" |

往后再有老任务想迁，得同样拿出这两条，否则维持"不迁"。

## 1. 批的粒度：一个"面"就是一批

体检的天然分块是**面**（标的类型 × 口径），正好映射成框架的批：

```
FetchAsync  →  每扫完一个面，yield 该面的发现列表
SaveBatchAsync →  按面落账：删掉该面的旧记录 + 写入新结果（沿用旧 Tries）
OnCompletedAsync → 汇总报告
```

于是 `MaxItems`＝本轮最多扫几个面，`Deadline`＝到点收尾，两个都不用额外写代码。

**为什么不是每 500 只票一批**：`MissingBars` 的语义是"这个面当前的完整缺口清单"。若按 500 只落账，
中途停下来时这个面的名单就只有跑过的那部分，剩下的票被当成"没有缺口"——比全丢更糟（安静的错）。
面级落账则是"扫过的面已更新、没扫的面原样保留"，中途停是安全的。

`TItem`：

```csharp
public sealed record AuditFinding(
    string Kind,              // gap / intraday / null_value / inconsistent / ohlc / note
    string? Code, string? Granularity,
    DateTime? From, DateTime? To, int Days,
    string? Note);            // Kind=note 的只报数行（板块指数、day_adj、覆盖形状、日频表、V5、V6）
```

## 2. 依赖迁移清单

| 依赖 | 现在 | 迁移后 |
|---|---|---|
| `SqliteMissingBarRepository` / `SqliteBarRepository` / `SqliteBarProbeFloorRepository` | `new(_paths.CurrentDb)` | 构造注入 |
| `IManifestStore` | `_manifestStore` | 构造注入 |
| `IDailyFetchNoDataRepository` | `_dailyNoDataRepository`（可空） | 构造注入（可空，语义不变） |
| `SqliteDailyTableAuditor` | 方法内 `new` | 方法内 `new`（只吃 db 路径） |
| `SqliteStockMetaUpsert.GetAllInstruments` / `CoverageShapeAuditor` / `MarketIndexCatalog` | 静态 / Logic 纯函数 | 不动 |
| **`GetPendingAdjRebuildCount()`** | **FetchOrchestrator 私有方法** | **抽成 `SqliteAdjSeriesAuditor`**（连 `CodesWithStaleAdjEvents` 一起），orchestrator 与新任务都用它 |
| `FormatElapsed` | orchestrator 私有 | 用框架的 `TaskRunStats.Elapsed` + 现有格式化 |
| `BeginStep` / `FinishFetchRun` | orchestrator 私有（收错误、写日志） | 框架的 `TaskRunResult` + `Report` |
| **`_dbLock`** | orchestrator 私有锁 | **不带**，见 §3 |

## 3. 三个真问题的处置

### ① `_dbLock` 没了

体检现在靠 orchestrator 的私有锁跟抓取串行。迁出去就没这把锁。

**分析**：体检写的只有 manifest（文件）和白名单/水位（SQLite，各自带事务 + `busy_timeout`），
而 SQLite 的 WAL 本身就是单写者。所以那把锁是**降低 `SQLITE_BUSY` 的优化，不是正确性依赖**。

**处置**：不带锁。迁完必须实测一次"体检与抓取同时跑"（Debug 实例，数据目录独立）。

### ② `MissingBars` 从"整体替换"改成"按面落账"

现在是扫完 `manifest.MissingBars = ranges`（整体替换，顺便沿用旧 `Tries`）。改成面级落账后：

```
本面新结果 = 扫出来的段（Tries 从旧记录里按 (Code, Gran) 继承）
manifest.MissingBars = 其它面的旧记录 ⊕ 本面新结果
```

**这是整个迁移里最容易写错的一处**。写错的表现是"名单越跑越少"（其它面被误删）或"`Tries` 被清零、
永远确认不了"，两种都很安静。专门写测试锁住（见 §5）。

### ③ 迁移与增强分两步

先**纯搬 + 流式化**，用旧版输出做基线对比（报出来的段数/天数/汇总行必须完全一致）；再在新类里加
V1~V6。一次改两件事，出问题分不清是搬坏了还是新判据的 bug。

## 4. `thorough` 与查询范围收成 Mode

`TaskRunArgs` 只有 `Mode` / `Day` / `Deadline` / `MaxItems` / `Manual`，没有 `thorough`。而
`FetchMode` 本来就是项目里"模式是参数、不是任务"这个既有概念，`catalog` 的 `SupportedModes`
天然能声明支持哪几个模式，界面复用现成的模式下拉。

| Mode | 含义 |
|---|---|
| `Incremental`（缺省） | 常规体检 |
| `Thorough`（新增） | 忽略并清空「确认没有」白名单 + 「无更早数据」水位 + 日频表空日名单（＝现在那个勾） |

于是 `PlanItemViewModel.ThoroughAudit` 和界面那个「彻底体检」勾**退役**，`MainViewModel` 里
`case StepFullAudit` 整支删掉（走 registry 总分支）。

范围参数（"只查近一年"）先不做：面级落账 + `MaxItems`/`Deadline` 已经能把半小时的活切开，
真需要再加一个 Mode。

## 5. 测试

`FullAuditTaskTests`（真 schema、真日期，跟 `DailyTableAuditTests` 同风格）：

- **面级落账**：扫完面 A 后停，manifest 里面 A 已更新、面 B 的旧记录**原样保留**；
- **Tries 继承**：旧记录 `Tries=1` 的段重新扫出来后仍是 1（不能清零）；
- `MaxItems=1` 只扫一个面就收尾，`Deadline` 已过时立刻收尾；
- `Thorough` 模式确实清了三张名单，非 Thorough 不清；
- 取消（`ct`）时已落账的面不回滚；
- 与旧版的等价性：同一个库跑新旧两条路，`MissingBars` 逐段相同。

## 6. 顺序

1. `SqliteAdjSeriesAuditor` 抽取（`GetPendingAdjRebuildCount` + `CodesWithStaleAdjEvents`），orchestrator 改成委托 —— 纯重构，跑现有测试
2. `AuditFinding` + `FullAuditTask`（纯搬 + 流式化），旧方法暂时留着做基线
3. 注册一行 + 删 `case StepFullAudit` + 实测并发（问题①）
4. `FetchMode.Thorough` + 界面勾退役
5. 等价性验证通过后，删掉 orchestrator 里的旧体检方法
6. 在新类里加 V1~V6（`doc/bar-value-audit-design.md` §3）

## 7. 相关

- `doc/bar-value-audit-design.md`（要加的 6 条判据）
- `doc/scheduler-redesign.md`（新任务框架、"老任务不迁"原则）
- `src/StockPlatform.Scheduling/Tasks/FetchTaskBase.cs`、`TradingCalendarTask`（本地为主任务的参照）
