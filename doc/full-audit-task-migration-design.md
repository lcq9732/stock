# 【全库数据体检】迁到新 Task 框架（2026-09-09）

## 0. 迁移判据（2026-09-10 由用户更正）

> ⚠ 本节原来写的是"破例判据两条：①正要长大 ②卡在流式落账/MaxItems 上，两条须同时成立"。
> **那两条已作废**（2026-09-10）。用户定的判据是：
>
> **看迁移成本 + 以后的维护成本** —— 老方式（堆在 `FetchOrchestrator` 里）是**耦合**的，
> 一处改动影响面大、维护成本高；新方式（独立 Task 类）是**解耦**的。
> 只要迁移成本可控，就该迁，不需要额外理由。
>
> 与之相应，`doc/fetcher-task-refactor-design.md` 里"老任务不迁、只新任务用新形状"那条
> 也不再是硬规矩——它当时的用意是控制一次性改动量，不是说老任务永远留在编排里。

本项（全库数据体检）当时的迁移动机仍然成立，且正是上面判据的典型：它要加 6 条值体检判据
（见 `doc/bar-value-audit-design.md`），继续堆在 `FetchOrchestrator.Steps.cs` 里维护成本只会更高；
而框架的两件能力它恰好都用得上：

| 框架能力 | 体检迁移前的状况 |
|---|---|
| 流式"扫一批落一批" | 原来是扫完一次性 `manifest.MissingBars = ranges` + `Save`——**中途停就全丢**。三遍扫描半小时，停在 29 分钟等于白跑 |
| `MaxItems` / `Deadline` | 完全没有。半小时的重活最该能"空闲窗口补一段、到点收尾" |

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
- `doc/scheduler-redesign.md`（新任务框架）；迁移判据见本文 §0（2026-09-10 已更正）
- `src/StockPlatform.Scheduling/Tasks/FetchTaskBase.cs`、`TradingCalendarTask`（本地为主任务的参照）

---

## 8. 第二个迁过来的：【重算回测序列】（2026-09-10）

§0 那条判据（迁移成本 + 维护成本）第一次拿去衡量别的任务。结论是**这一项比体检还好迁**，
所以当天就做完了。记在这儿是因为它给"以后还要迁哪些"提供了一把尺子。

### 为什么它容易迁

`RunRebuildAdjSeriesAsync` 166 行 + `ReturnCheckText` 20 行，而**依赖只有一个 db 路径**：

| | 【全库数据体检】 | 【重算回测序列】 |
|---|---|---|
| 依赖 | 4 个仓储 + manifest + 空日名单 | `SqliteBarRepository` + `SqliteDividendRepository`，都只吃 db 路径 |
| 落账对象 | manifest（面级语义，落一半会说谎） | `Bar` 表的 `day_adj`（每只票各自独立） |
| 调用点 | 1 个 | 1 个 |
| 数据源 | 不占 | 不占 |

判据（五条）2026-09-09 抽 `SqliteAdjSeriesAuditor` 时就已经从 orchestrator 里搬出来了——
那次是为了体检要用它，这次白捡了一半的工作量。**先把判据抽成类，任务本身就好搬了**，
这个顺序值得复用。

### 批的粒度：一只票 = 一批

跟体检的"面级落账"**不一样，而且必须不一样**：

- 体检的 `MissingBars` 语义是"这个面当前的完整缺口清单"，落一半会把没扫的票当成"没有缺口"——
  安静的错，所以必须整面替换；
- `day_adj` 是逐票独立的，算过的就是对的、没算的下一轮判据照样认出来。所以按只落账是安全的
  ——而且老代码本来就是这么做的（每只票落库后才检查取消）。

于是 `MaxItems`＝本轮最多算几只、`Deadline`＝到点收尾，两个都没写代码。

### 顺带修掉的三处

| | 迁移前 | 迁移后 |
|---|---|---|
| 分批的依据 | 调用点 `DeadlineToCount(deadline, 0.2 秒/只)` **预估**只数，然后不管实际时间跑完。而一只从几毫秒（增量）到几百毫秒（整段）都有 | 框架的 `Deadline`，每批之后看**真实钟点** |
| 收尾那句"还剩 N 只" | 又调一次 `GetPendingAdjRebuildCount()`，为一个数字对 1300 万行 GROUP BY 一遍、几十秒 | 减法：落了账的票五条判据必然不成立，剩下的就是"没轮到的 + 算不出来的" |
| `FormatElapsed` | orchestrator（34 处调用）和 `FullAuditTask` 各一份一样的实现，再迁一个就是第三份 | 提到 `ElapsedText.Format`，两处旧的只留转发（不动 34 个调用点） |

### 新增的能力：「首次整段回补」＝全量重算

框架的 `TaskRunArgs.Mode` 白送的。catalog 里加一行
`SupportedModes: Incremental | FirstBackfill`，界面的模式下拉自动就有了；任务里把它解释成
**忽略五条判据、有不复权日线的票全部整段重来**。

以前没有这个入口：改过复权算法之后想让全库重算，只能手工删掉 `day_adj` 逼判据认出来，
而"该怎么删"没有任何地方记着。

### 迁的时候真正要小心的三件事

**① CPU 密集的活必须自己扔线程池。** 老代码是整个方法包一层 `Task.Run`；框架的 `FetchAsync`
是 async 迭代器、跑在调用者线程上。这一层不能丢——丢了就是 UI 卡死几十分钟。现在是每只票
一次 `Task.Run`（每只 50 毫秒以上，线程池调度的开销可以忽略）。

**② `_dbLock` 会消失，而这一条不能靠"反正串行"糊过去。** 占用表里**本地项不占数据源、
永远可并发**（`FetchAction.EffectiveSources`），所以这一项完全可能跟正在写 `day_raw` 的
【个股日K】同时跑。撑住它靠的是：库是 WAL（读写互不阻塞）＋ **写事务的粒度是一只票一次**
（毫秒级），撞上 `SQLITE_BUSY` 时 Microsoft.Data.Sqlite 默认有 30 秒重试窗口。
推论：**别把落账合并成大事务**，那会把毫秒级冲突变成分钟级互等。

**③ 空批被框架跳过。** 不复权不足两根的票产出空批，`SaveBatchAsync` 不会被调、不占
`MaxItems` 额度、不计入 `items`——语义正好，但得写明白。`FullAuditTask` 在这上面栽过两次
（反过来：以为空批会落账）。

### 验证

单元测试 `AdjSeriesRebuildTaskTests` 13 条（增量/整段的四种分岔、整段先删后写、空批不占额度、
`MaxItems`/`Deadline` 收尾、全量重算、收尾报告）。全套 1029 条通过。

Debug 实例实跑（造 3 只票：一只没算过、一只只多一天、一只当天除权）：

```
重算回测序列：3 只待算（本地计算，不联网）...
回测序列更新完成：2 只（其中 1 只只追加了新K线、1 只整段重算），…，待算名单里还有 1 只没轮到
```

第三只当时因为**造数据的日期格式不对**（`announce_date` 存成了带时分秒的）抛在读分红那一步，
正好验到两件事：逐票的异常不会中断整轮，而且失败的票**留在待算名单里**（"还剩 1 只"）。
改对数据重跑：

```
回测序列更新完成：1 只（0 只只追加了新K线、1 只整段重算），应用除权 1 次，收益率自检全部通过（共比 4 天）
600003  day_raw 09-07 = 9.00  →  day_adj 09-07 = 10.00（因子 10/9），除权前四天保持 10.00
```

因子从除权日往后乘、除权前不动——乘法式复权该有的形状。再把模式换成「首次整段回补」，
界面明明写着"已是最新"，照样 3 只全部整段重来。

