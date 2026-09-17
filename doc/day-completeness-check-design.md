# 当日完整性体检设计方案

> 状态：**A1、B 都已实现并实机验证通过（A1 在 2026-09-16、B 在 09-17）；A2 用户决定不做**——见 §8、§9。
> 用户定的顺序：先做 A1（"分档资金流快照这个，在自己任务最后检查即可"），
> 再做 B（"当日覆盖率体检这个要做"）；A2 独立守卫任务"不用做了"。
> 起因：用户问【当日覆盖率体检】能不能查出"当天分档资金流少了"。查下来：查不出——
> 那一步只看个股日K三个口径，别的日更表一张都不看。而分档资金流是全库唯一
> **过了窗口就永久取不回来**的数据。

---

## 1. 问题

### 1.1 第 13 步只查K线

`FetchOrchestrator.CheckLatestDayCoverage`（FetchOrchestrator.cs:3968）做的全部事情：

1. 以上证指数最新一根日线当交易日锚；
2. 对 `day` / `day_hfq` / `day_raw` 三个口径各查一次 `GetCodesMissingDay`（"上一交易日有、这天没有"）；
3. 并集写进 `StepStockDayBars` 的 `MissingDay` 待办。

**分档资金流、资金净流入、融资余额、龙虎榜、席位、大宗交易——一张都不查。**
今天的分档资金流只入库了 3000 只，这一步照样打印"当天的个股日线是齐的"。

### 1.2 能查"不全"的那套够不着当天

`SqliteDailyTableAuditor` 的残缺日判据（行数 < 邻近中位数×`ThinRatio`、某交易所整天为空）是对的，
但三道门挡着它看当天：

| 门 | 位置 | 后果 |
|---|---|---|
| 只在【全库数据体检】里跑 | `FullAuditTask` | 日更末尾没人调它 |
| `SettleDays = 2` | `FullAuditTask.cs:43` | cutoff 让开最近两天，**当天永远不在范围内** |
| `NetInflowDetail` 不配 `OwnerTaskId` | `SqliteDailyTableAuditor.DailyTables` | 只报不补 |

第三条当时的理由是"补一天要走 push2his 逐股 5500 个请求"。那个理由对**历史**成立，
对**当天**不成立——当天走 push2delay 快照只要 59 个请求、一两分钟。

### 1.3 分档资金流的窗口是会关的

`MoneyFlowSnapshotTask` 自己带了对账（`snap.Total - snap.Suspended - snap.Rows.Count`），
但它只在**这一项真的跑起来并拿到数据**时有效。三个逃逸口：

- 整项没轮到跑，或被 push2delay 限流熔断记成 `Skipped` → 事后没有任何人复查；
- 落库之后没人再看一眼库里实际有多少行（`Upsert` 返回值只进了汇总那句话，没有判据）；
- 2026-09-09 全市场整天缺失就是这么发生的，事后只能靠逐股通道 5,500 个请求换回一天。

**窗口什么时候关**——这一点要说准，因为它决定守卫几点跑：

- 归属交易日由接口的 `f124` 行情时间戳决定（`EastMoneyMoneyFlowSnapshotProvider`），
  **不是本地日期**。所以跨过午夜去补，数据仍然写在正确的那个交易日上。
- 真正的关窗点是**下一个交易日行情开始更新**（集合竞价起 `f124` 翻到新日期），不是 24:00。
  周五晚上漏了，周六周日都还补得回来。
- 但这不改变结论：**当晚必须闭环**。指望"明早开盘前有人开机跑一轮"是把数据押在一个
  没有保证的条件上，而当晚跑一次是确定的。留几小时余量比留几十分钟稳。

### 1.4 其它日更表当天也没人查（实测）

本地库现状（2026-09-16 18:00 查）：

| 表 | 最新一天 | 行数 | 按发布节奏该有 |
|---|---|---|---|
| NetInflowDetail 分档资金流 | 2026-09-16 | 5,550 | 09-16 ✔ |
| NetInflow 资金净流入 | 2026-09-15 | 5,561 | 09-16（当晚该抓） |
| MarginDetail 融资余额 | 2026-09-14 | 4,105 | 09-15（T+1） |
| Lhb 龙虎榜 | 2026-09-15 | 73 | 09-16 |

融资余额落后一个交易日、净流入落后一个交易日，第 13 步对此**零告警**。
`Spec.LagDays` 那档判据本来就是管这个的，只是它没在日更里跑。

---

## 2. 方案：三层，按"能不能省"排

用户 2026-09-16 定的两条边界：**分档资金流单独成任务**（不能跟别的表一起等到第二天），
以及**快照任务跑完就该自己查一下，不足或没有要标红**。这两条不是二选一，是两层：

| 层 | 做什么 | 挡住哪种失效 | 代价 |
|---|---|---|---|
| **A1** 快照任务尾部自检 | 落库后回查当天库里行数，不足→整项红字失败 | 抓了但没拉全、翻页被截断、`Upsert` 没写进去 | 一条 SQL，0.1 秒 |
| **A2** 独立守卫任务 | 22:30 单独跑：不够就**自己重拉**快照 | **整项没跑 / 被熔断跳过**（09-09 事故那一种） | 新任务 + 新组 |
| **B** 第 13 步扩写 | 当天**所有**该有的数据：K线三类标的 + 五张日更表 + 事件型落后，报+记待办 | ETF/指数当天整批没抓到、融资余额/龙虎榜/大宗当天缺或只抓到一半 | 复用现成判据 |

**A1 必做**，它最便宜且覆盖最常见的失效。但**A1 单独做补不完**：它跑在快照任务内部，
快照整项没跑的时候它也不会跑——而 09-09 丢掉一整天正是这一种。
所以 A2 才是真正的兜底，A1 是让问题当场就红、不用等到 22:30。

B 跟 A 的时效性完全不同（B 那些表次日补都来得及），所以它留在日更组原位，不进守卫组。

---

## 3. 判据（实测定参）

### 3.1 期望 = 当天有日K的个股数

分档资金流的正确判据不是"有没有行"，也不是"比邻近中位数少多少"，而是**逐只对齐**：
有K线的那天就该有资金流。这条判据已经在 `MoneyFlowBackfillPlan` 里用过（补历史的排队判据），
A1/A2 复用同一个口径——**同一判据只写一份**，两处各写一份迟早漂移。

实测最近 25 个交易日（`Bar.granularity='day'` ∩ `StockMeta.type='stock'` vs `NetInflowDetail`）：

| 差值 | 天数 |
|---|---|
| 0 | 9 |
| +1 | 16 |
| 其它 | **0** |

差值只在 0 和 +1 之间。所以：

- **容差 2 只**。差 3 只及以上就报，实测离误报还很远。
- 为什么不用 `ThinRatio 0.7`：少 200 只 = 3.6%，那条判据连眉毛都不会动一下。
- 为什么不只信服务端自报的 `total`：那是**同一次请求**里的数，接口口径变了、或整项根本没跑，
  它就不存在。这条判据查的是**库里到底有没有**，是唯一不依赖抓取过程的证据。
- 两条对账互补、都留着：快照那条查"这一轮翻页丢没丢"，这条查"今天库里齐不齐"。

### 3.2 前置健康检查：期望值自己得可信

同向失效必须挡掉：**个股日K今天也没抓完**的话，期望值跟着变小，两边一起少、判据会说"齐了"。

判据：当天个股日K行数 ≥ 在市名册（`StockMeta.type='stock'`，现 5,565）的 **99%**（≈5,509）。
不满足就**不下结论**，报"无法判定：今天的个股日K只有 N 行、名册 M 只，先把日K补齐再跑本项"。

实测当天 5,550 / 5,565 = 99.7%，停牌股不会把它压到线下——停牌当天本来就没K线、也本来就没资金流，
两边同时缺正好抵消，这也是 3.1 里差值恒为 0/+1 的原因。

### 3.3 交易日判定走 `TradingDay` 表

不用上证指数K线当锚（那是第 13 步的做法）：A2 要在 22:30 独立跑，**不能依赖日更跑到哪一步**。
本地 `TradingDay` 是深交所官方日历，已覆盖到 2026-10-30，一次主键查询。

今天不是交易日 → 整项 `NothingToDo`，不报错、不重拉。

### 3.4 B：当天**所有**该有的数据，按三类判据分

用户 2026-09-16 定的范围：【当日覆盖率体检】继续扩，**把当天该有的数据项全查一遍**，
不是只查那几张表。但"全查"不等于"一套判据套所有表"——期望的确定性差着量级，
硬套只会天天误报，那比不报还糟。所以按**当天期望有多确定**分三类：

**类 1｜全市场逐只必有 —— 按只数对齐（最强判据）**

| 项 | 期望 | 现状 |
|---|---|---|
| 个股日K `day`/`day_hfq`/`day_raw` | 上一交易日有的都该有 | ✔ 已在查 |
| **ETF 日K**（1,665 只） | 同上 | ✘ **漏检**，见下 |
| **指数日K**（9 条） | 同上 | ✘ **漏检** |
| 分档资金流 | = 当天个股日K只数 | 归 A |

⚠ **ETF 和指数当天缺了现在没有任何告警**，这一版必须补上。原因在
`SqliteBarRepository.GetCodesMissingDay` 的这一行：

```sql
AND b.code GLOB '[0-9][0-9][0-9][0-9][0-9][0-9]'
```

ETF 和指数在本地是**带前缀存**的（`sh510300`、`sh000001`），六位纯数字这条过滤把它们
全挡在外面。实测 2026-09-15 库里 ETF 日K 1,663 条、指数 9 条——它们要是哪天一条没抓到，
体检报的仍然是"当天的个股日线是齐的"。

改法：判据从"代码长什么样"换成**`StockMeta.type`**（`stock` / `etf` / `index` 各查一次、
各自记进各自任务的待办：`StepEtfBars`、`StepIndexBars`）。这跟"市场前缀走 `MarketClassifier`"
是同一条教训——**别靠代码字面猜标的类型**，920 那次已经吃过一回。
`delisted` 不查（数据源不再更新，归【退市股收尾】）；`board` 不查（本地合成，见类 3）。

**类 2｜每交易日必有一批、但只数不定 —— 空日 / 极端偏少 / 缺交易所**

复用 `SqliteDailyTableAuditor` 的现成判据，只把范围缩成一天：

| 表 | 该查哪天 | 判据 |
|---|---|---|
| NetInflow 资金净流入 | 最新交易日 | 空 / thin(0.7) / 缺交易所 |
| MarginDetail 融资余额 | 最新交易日往前 `LagDays=1` 天 | 同上 |
| Lhb 龙虎榜 | 最新交易日 | 空 / thin(0.2) / 缺交易所 |
| BlockTrade 大宗交易 | 最新交易日 | 同上 |
| LhbSeat 龙虎榜席位 | **上一个交易日** | 同上 |

LhbSeat 要往前挪一天：它在 `DailyOrder` 里排在**整组最末**（首轮几小时），
体检跑的时候它今天那批还没抓，查当天必然误报。

⚠ **NetInflow 不能用 3.1 那条严格对账**：实测 2026-09-15 它比当天个股日K多 14 只
（000016、002731、301139…），新浪那个源跟本地K线名册天然对不齐。它只能用 thin + 缺交易所。

**类 3｜按事件/公告出的 —— 不判当天空，只判"最新一天落后太久"**

公告、业绩预告、财报预约日、行业景气指标、回购公告进展、股东增减持、机构调研、限售解禁、
板块行情与板块指数合成——这些**某天一条都没有很正常**（`SqliteDailyTableAuditor` 的表清单
注释里写着这条，是实测出来的：放进空日判据只会天天误报）。

它们只判一条：**最新一行比交易日历落后超过 N 天**（沿用 `Spec.LagDays` 的形状，按项配）。
这条能挡住真正的失效模式——"这一项已经连着一周没抓到东西了，而没人发现"。
板块那两项还有个额外理由要往后挪一天：它们在 `DailyOrder` 里排在体检**之后**。

类 3 这一版**先只报、不记待办**：各项的补法差别太大（有的按天重抓、有的整段回补），
统一塞进待办既补不对也说不清——这跟 `SqliteDailyTableAuditor` 当初"只报不补"的取舍一致。

### 3.5 性能：带日期下界之后全是亚秒级

`SqliteDailyTableAuditor` 现在的两个读取（`ReadRowCounts` 全表 `GROUP BY`、
`ReadMarketsByDay` 全表 `DISTINCT`）在当日体检里必须带日期下界，否则 `NetInflow` 一张表就顶不住。

实测（本机 25GB 库）：

| 查询 | 全表 | 带 `>= '2026-08-01'` 下界 |
|---|---|---|
| NetInflow（1,389 万行）`GROUP BY` | 12.3 秒 | **0.11 秒** |
| MarginDetail（664 万行） | — | 0.01 秒 |
| LhbSeat（178 万行） | — | 0.06 秒 |
| NetInflowDetail `DISTINCT(日,码)` | — | 0.78 秒 |
| Bar 当天个股日K计数（走 `ix_bar_gran_date`） | — | 0.07 秒 |

下界取"要查那天往前 25 个交易日"：`TrailingMedian` 要 10 个有效样本、`IsQuietDay` 要前后各 10 天，
25 天足够，且判定结果跟全区间版一致（当天的邻近中位数只由它**前面**的日子决定）。

---

## 4. 程序设计

### 4.1 A1：快照任务尾部自检

改 `MoneyFlowSnapshotTask.OnCompletedAsync`（落库之后、汇总之前）：

```
expect = 当天个股日K行数（3.1）
have   = NetInflowDetail 当天行数
日K健康检查不过（3.2） → 汇总里加一句"无法核对"，不判失败
差 ≤ 2  → 汇总照旧，加一句"已核对 have/expect"
差 > 2  → _errors.Add(...)，整项 TaskState.Failed，日志红字：
          "⚠ 分档资金流当天只有 have/expect 只，差 N 只——这一项漏一天就永久补不回来，
            请立刻重跑本项（模式不限，整天 upsert 去重）"
```

三点讲究：

- **查库而不是查抓取过程**：现有那条 `snap.Total - snap.Suspended - snap.Rows.Count` 只证明
  "这轮请求收全了"，证明不了"写进库了"。两条都留。
- **标红要能真标红**：⚠ 写这一节时以为"返回 `TaskState.Failed` 就会红"，实现时发现**不会**——
  `PlanRunner` 只认"抛异常"那条路，新式任务的失败一律显示成绿色的"完成，但有 N 条错误"。
  这条得先修（见 §8.1）。不新增"警告"级别——这个项目里"红=要人管"的语义已经建立了，
  再加一档只会让人分不清哪档该管。
- **不在这里自动重拉**：任务自己重拉自己，失败语义会变糊（跑了两轮算一轮？红还是绿？）。
  重拉归 A2，那是它存在的理由。

### 4.2 A2：新任务 `MoneyFlowDayGuardTask`

按"新任务写成独立类"的规矩放 `StockPlatform.Tasks`，继承 `FetchTaskBase`，
**不往 `FetchOrchestrator` 里堆**。

```
FetchAsync：
  1. TradingDay 查今天是不是交易日 → 不是：Skipped("今天不是交易日")
  2. 健康检查（3.2）不过 → 失败（"无法判定，先补日K"），不重拉
  3. 数差（3.1）：差 ≤ 2 → NothingToDo，报"分档资金流当天 5,550/5,550 齐"
  4. 不齐 → 调 EastMoneyMoneyFlowSnapshotProvider.FetchAllAsync 重拉整天（整批 upsert）
     最多 2 轮，轮间按 RateLimiter 的节奏；熔断中则直接进 5
  5. 复查（回到 3）：齐了 → Completed；仍不齐 → 整项失败（红字）
     + 记 PartialDay 待办给 FetchMoneyFlowSnapshot，日期＝今天
```

- **守卫自己重拉，不记待办等别人**。这是它跟 B 的根本区别：`RetryFailed` 排在日更组里，
  守卫 22:30 跑的时候那一项早跑完了，记待办等于等到明天——而明天就没了。
- **重拉走快照通道**：59 个请求 vs 逐股 5,500 个。
- **盘中保护照旧**：provider 的 `IsIntraday`（`f124 < 15:00`）仍然生效，22:30 撞不上，
  但手工在盘中点了它，拿到的半天数据一样会被拒收。

### 4.3 A2 的排期：新组，排在计划最上面

```
组【收盘前守卫】 每工作日 22:30 Immediate
  └ 分档资金流当日守卫
```

- 为什么要独立组：`NotBefore` 是**组**的属性，日更组是 18:00，守卫要 22:30。
- 为什么排在计划**最上面**：`PlanRunner.FindPending` 是"同频次档内按表格顺序"挑，
  守卫跟日更同属 `EveryWorkday` 档。排最上面，22:30 一到它就是下一个被挑中的，
  而它只跑一分钟，对日更没有可感知影响。
- ⚠ 调度严格串行、**不打断进行中的项**。22:30 若正卡在【龙虎榜席位】首轮那种几小时的活里，
  守卫得等它结束。缓解有三：席位增量只有几分钟；真关窗是次日开盘不是午夜；守卫幂等，
  什么时候跑起来什么时候判。**不为它引入插队/截止机制**——那是给整套调度加一个新概念，
  代价远大于收益。

### 4.4 B：第 13 步扩写成三段

名字也该改：【当日覆盖率体检】→ **【当日完整性体检】**（覆盖率只说了K线那一件事）。

```
第一段·类 1（K线，按只数对齐）
  foreach type in {stock, etf, index}:
      foreach gran in 该类型该有的口径（stock 三套，etf/index 只有 day）:
          GetCodesMissingDay(type, gran, latest, previous)
          → 记进各自任务的 MissingDay 待办（StepStockDayBars / StepEtfBars / StepIndexBars）

第二段·类 2（日更表，空/偏少/缺交易所）
  foreach spec in DailyTables（排除 NetInflowDetail，那是 A 的）:
      day = spec == LhbSeat ? 上一个交易日 : 最新交易日 - spec.LagDays
      带下界查这一天
      有问题 → 报一行（带 spec.HowToFill）+ 按 spec.OwnerTaskId 记 PartialDay 待办

第三段·类 3（事件型，只判落后）
  foreach 项 in 事件型清单:
      表里最新一天比日历落后 > 该项允许的天数 → 报一行（不记待办）
```

三段的产出合成一条汇总：**"今天：K线齐 / ETF 缺 12 只 / 融资余额落后 1 天 / 龙虎榜齐 …"**，
让人一眼看完当天数据全貌——这正是这一项该有的样子，而不是只报K线。

- **待办闭环现成**：`RetryFailed` 在 `DailyOrder` 里就排在 `StepDayCoverage` 之后，
  `FillPartialDaysAsync` 会按 `OwnerTaskId` 认领、重抓、用 `CheckDays` 复查、写 `Tries`。
  也就是说 B 记下的账**当轮就补**。
- **空日也按 `PartialDay` 记**：当天这个场景下"整天没有"和"有一半"的补法完全一样（重抓那天），
  不新增待办类别，待办带日期就够。⚠ 但 `CheckDays` 对 `n == 0` 的天是 `continue`
  （当空日、不算残缺），照搬会把"补不上的空日"判成"已补齐"静默划掉——见 §6.3。
- `SqliteDailyTableAuditor` 只加**一个带日期下界的重载**，判据代码一行不改。
  这是硬要求：判据两处各写一份迟早漂移（`SqliteAdjSeriesAuditor` 的由来就是这教训）。

### 4.5 改动文件清单

| 文件 | 改动 | 属于 |
|---|---|---|
| `src/StockPlatform.Data/Sqlite/SqliteDailyCompletenessReader.cs` | **新增**：交易日、当天日K只数、当天资金流只数、名册数四条查询 | A1+A2 |
| `src/StockPlatform.Tasks/MoneyFlowSnapshotTask.cs` | `OnCompletedAsync` 加尾部自检 | A1 |
| `src/StockPlatform.Tasks/MoneyFlowDayGuardTask.cs` | **新增** | A2 |
| `src/StockPlatform.Scheduling/FetchTaskCatalog.cs` | 新枚举 + 目录条目 + `PlanGroupKind` 归类；第 13 步说明改写 | A2+B |
| `src/StockPlatform.Scheduling/FetchPlan.cs` | `CreateDefault` 加【收盘前守卫】组，插在 `Groups[0]` | A2 |
| `src/StockPlatform.Scheduling/FetchPlanTemplates.cs` | 加对应模板（否则"按模板恢复"会静默少这一项） | A2 |
| `src/StockPlatform.Desktop/StockPlatform.Fetcher/App.xaml.cs` | 注册任务 A2 | A2 |
| `src/StockPlatform.Data/Sqlite/SqliteDailyTableAuditor.cs` | 三个读取方法加日期下界参数；加"只查一天"入口；类 3 的"落后"清单 | B |
| `src/StockPlatform.Data/Sqlite/SqliteBarRepository.cs` | `GetCodesMissingDay` 的 `GLOB` 六位数字换成按 `StockMeta.type` 过滤（ETF/指数漏检） | B |
| `src/StockPlatform.Data/Orchestration/FetchOrchestrator.cs` | `CheckLatestDayCoverage` 扩成三段；改名【当日完整性体检】 | B |
| `doc/data-platform-design.md`、`doc/solution-class-map.md` | 补这两项 | — |

---

## 5. 验收标准

### 单元测试（`StockPlatform.Tests`）

A1：

1. 落库后 have == expect → `Completed`、无 error；
2. have 比 expect 少 500 → **整项失败**、error 文案含实际数字；
3. 日K只有名册的 90% → 汇总写"无法核对"，**不判失败**（不能让日K的锅算到快照头上）。

A2：

4. 非交易日 → `Skipped`，不发请求；
5. 日K不健康 → 失败且**不重拉**；
6. 差 1 只 → `NothingToDo`，不重拉；
7. 差 2,550 只 → 触发重拉；Mock provider 返回全量 → 复查通过、`Completed`；
8. 同上但 Mock 仍返回不全 → 整项失败 + manifest 里有 `FetchMoneyFlowSnapshot` 的
   `PartialDay` 待办、日期＝当天；
9. 当天 0 行（快照整项没跑）→ 走 7 的路径——**这是 09-09 事故的回归测试**；
10. push2delay 熔断中 → 不重拉，直接失败 + 记待办（不吞掉）。

B：

11. 造一天 MarginDetail 只有沪市 → 体检报出、记进 `StepMargin` 的 `PartialDay`；
12. LhbSeat 查的是**上一个交易日**（今天没抓不算缺）；
13. 带下界的读取跟全表版在同一天上给出**相同判定**（防两套判据漂移）；
14. `FetchTaskCatalogTests` 既有守卫（每个 `OwnerTaskId` 都支持 `FillBacklog`）对新枚举仍通过；
15. **ETF 少 12 只 → 报出并记进 `StepEtfBars` 的待办**；指数少 1 条 → 记进 `StepIndexBars`
    （这两条是本次的漏检回归测试，`GLOB` 那版必然测不过）；
16. `delisted` 类型的票当天缺 → **不报**（数据源不再更新，归【退市股收尾】）；
17. 事件型某天 0 行 → **不报**；同一项连续落后超过阈值 → 报一行且**不记待办**。

### 实机验证（Debug 实例，数据目录天然隔离）

18. 手工点【分档资金流快照】：库齐 → 绿、汇总带"已核对 5,550/5,550"；
19. 删掉当天 1,000 行、再点一次快照 → 这次抓回来应当自动补齐（upsert），仍为绿；
    再删 1,000 行后**只点守卫** → 日志显示重拉 → 复查通过 → 行数回到 5,550；
20. 把当天整天删空、点守卫 → 重拉 → 补齐；
21. 断网点守卫 → 红字 + 待办，不静默过去；
22. 计划页确认【收盘前守卫】组在最上面、22:30、每工作日，【执行】能单独跑。

---

## 6. 风险与未决

1. **22:30 撞上长任务**：见 4.3。不引入插队机制，靠"真关窗是次日开盘"兜底。
   若实际用下来确实会被顶到午夜以后，再讨论"给守卫一个更高的频次档"这种小改动。
2. **容差 2 是按 25 天的样本定的**。将来接口纳入新板块、或名册口径变了可能要放宽。
   判据把"期望/实有/差值"三个数都打进日志，出问题时一眼看得出是不是判据本身过严。
3. **空日复查会被静默划掉**（§4.4 那条 ⚠）：`CheckDays` 对 0 行的天返回"不残缺"。
   实现时必须让当天场景下 `n == 0` 算"仍然不齐"，否则重演"补不上却被划掉"那个 bug。
4. **要不要给 `NetInflowDetail` 配 `OwnerTaskId`**（让全库体检也能补历史）：不做。
   历史缺口只能走逐股通道 5,500 个请求，代价没变。A 只管当天。
5. **A1 和 A2 的判据必须同一份代码**（`SqliteDailyCompletenessReader`）。
   两处各写一份的话，会出现"快照说齐了、守卫说不齐"这种谁也不信谁的状态。

---

## 7. 怎么复查

- 当晚：快照那一项的汇总里应有"已核对 N/M"；守卫应有"齐"或"重拉后补齐"；
- 次日：`SELECT COUNT(*) FROM NetInflowDetail WHERE trade_date='昨天'` 应等于
  当天个股日K行数（±2）；
- 长期：【全库数据体检】里 `NetInflowDetail` 的 `EmptyDays` 应保持为空。

---

## 8. 实现结果：A1（2026-09-16）

只做了 A1（快照任务尾部自检 + 界面标红）。A2（独立守卫任务）和 B（第 13 步扩写）没动。

### 8.1 顺手修掉的一个真问题：新式任务失败时界面是绿的

实现时发现：`FetchTaskBase` 把异常**吞在骨架里**、翻译成 `TaskState.Failed` 返回，
而 `PlanRunner` 只有"抛异常"那条路才记 `RunOutcome.Failed`——于是**任何新式任务失败，
状态列显示的都是绿色的"完成，但有 1 条错误"**。【分档资金流快照】抓不到一行时正是如此，
而它恰恰是全库最不能静默失败的一项。

修法：`FetchResult` 加 `Failed` 字段，`TaskRunResult.ToFetchResult()` 带过去，
`PlanRunner` 据此记失败。老编排层的任务不设这个字段，行为完全不变。

### 8.2 落地的文件

| 文件 | 改动 |
|---|---|
| `src/StockPlatform.Data/Sqlite/SqliteMoneyFlowDayAudit.cs` | **新增**：判据本体 + `MoneyFlowDayStatus`（含 `IsAlert` 和那句 `Text`） |
| `src/StockPlatform.Tasks/MoneyFlowSnapshotTask.cs` | 收尾回查库；不齐→`TaskState.Failed`；"没开工"的轮次也查 |
| `src/StockPlatform.Data/Orchestration/FetchResult.cs` | 加 `Failed` |
| `src/StockPlatform.Scheduling/Tasks/FetchTaskContracts.cs` | `ToFetchResult` 带上 `Failed` |
| `src/StockPlatform.Scheduling/PlanRunner.cs` | `result.Failed` → 记 `RunOutcome.Failed` 并红字日志 |
| `src/StockPlatform.Data/Orchestration/FetchOrchestrator.cs` | `GetMoneyFlowDayStatus()` |
| `.../Fetcher/ViewModels/MainViewModel.cs` | `MoneyFlowDay` / `MoneyFlowDayText` / `MoneyFlowDayAlert`，并进那轮后台刷新 |
| `.../Fetcher/ViewModels/PlanItemViewModel.cs` | `IsMoneyFlowSnapshot` |
| `.../Fetcher/MainWindow.xaml` | 参数列加一格，`MoneyFlowDayAlert` 为真时红字加粗 |
| `.../Fetcher/App.xaml.cs` | 注册任务时把 audit 传进去 |
| `.../StockPlatform.Tests/MoneyFlowSnapshotTaskTests.cs` | +6 个用例 |

### 8.3 验证

- 单测：`MoneyFlowSnapshotTaskTests` 13 个全过；全量 1,543 个全过（0 失败）。
- 实机（Debug 实例，数据目录在 bin 下、跟生产库天然隔离）：
  - 造"当天日K 10 只、资金流 3 只" → 参数列显示**红色加粗**
    `⚠ 09-16 差 7 只（有日K 10 只、资金流只有 3 只）`；
  - 补齐到 10 只重启 → 变灰字 `09-16 已齐（10/10 只）`；
  - 验证完把 Debug 库里造的假数据删干净了。

### 8.4 A2 不做（用户 2026-09-16 决定）

独立守卫任务能挡住的是"这一项整项没被调起来/被熔断跳过"——A1 挡不住这种，因为它跑在任务内部。
这条缺口仍然开着，唯一的缓解是：跳过的轮次现在也会回查库并报错（见 §4.1），
但前提仍是"这一项被调起来了"。

---

## 9. 实现结果：B（2026-09-17）

【当日覆盖率体检】→ **【当日完整性体检】**（改的是显示名，`FetchActionId` 不变，计划文件不受影响）。

### 9.1 跟 §3.4 的设计有两处出入

**类 3 收窄了**。设计里写"事件型只判最新一天落后太久"，实现时没这么做——公告、业绩预告、
股东增减持这些是**季节性**的（非财报季整月没有预告是常态），落后判据照样会误报，
而真正想挡的"这一项连着几天没抓成"，本来就该由计划的运行记录回答，不该靠数据反推。
所以类 3 只留**覆盖式快照**两张：`Board`（板块行情）和 `FundamentalMetric`（总股本/流通市值）——
它们库里只留最新一份、每个交易日都该刷新，判"停在哪天"零误报。

**多做了一件**：体检判"这天齐了"时会**把对应的残缺日待办撤掉**。
不撤的话待办一直挂着，人白跑一轮【重新拉取失败】（龙虎榜那种按天重抓是真发请求的）。
体检既是发现者也是复查者，判据就是同一个方法，撤单是自洽的。

### 9.2 顺手修掉的第二个静默 bug

`CheckDays`（补完残缺日之后的复查）对"这天 0 行"返回的是**不算残缺**。
当日体检接进来之后这条就危险了：它会把"今天整天没有"记成残缺日待办（龙虎榜、融资余额都可能），
而复查一看"0 行不算残缺"就判成补齐、**静默划掉**——补没补上都过关。
改成"0 行＝仍然不齐"，补不上的靠 `Tries` 收敛进 `ConfirmedPartialDays`。
这正是设计 §6.3 事先标出来的那条，实现时确认它真的会触发。

### 9.3 落地的文件

| 文件 | 改动 |
|---|---|
| `src/StockPlatform.Data/Sqlite/SqliteBarRepository.cs` | `GetCodesMissingDay` 加 `type` 参数，判据从"六位纯数字"换成 `StockMeta.type` |
| `src/StockPlatform.Data/Sqlite/SqliteDailyTableAuditor.cs` | 新增 `CheckOneDay`/`DayCheck`、`SnapshotTables`/`SnapshotSpec`/`LatestDayOf`；三个读取方法加日期下界；`CheckDays` 的 0 行判据 |
| `src/StockPlatform.Data/Orchestration/FetchOrchestrator.cs` | `CheckLatestDayCoverage` 重写成三段 + 待办的加/撤；`FillBacklog` 的当天日线分支按任务分流（ETF/指数只跑前复权） |
| `src/StockPlatform.Data/Orchestration/FetchOrchestrator.Steps.cs` | 第 13 步改名、`NothingToDo` 改看"处数" |
| `src/StockPlatform.Scheduling/FetchTaskCatalog.cs` | 条目改名 + 说明重写；`SoftDependsOn` 加 `StepEtfBars` |
| `src/StockPlatform.Desktop/StockPlatform.Tests/DayCompletenessTests.cs` | **新增** 14 个用例 |

### 9.4 验证

- 单测：新文件 14 个全过；全量 **1,557 个全过，0 失败**。
- 实机（Debug 实例，数据目录在 bin 下、跟生产库隔离），造场景跑三轮：

  **第一轮**（故意造 5 处缺口）日志：

  ```
  当日完整性体检：查 2026-09-17（K线三类标的 → 日更表 → 覆盖式快照）...
  ⚠ 个股日K(三口径)：2026-09-17 还有 1 只没有日线——…已记入待重试名单…
  ⚠ ETF日K：2026-09-17 还有 1 只没有日线——…
  ⚠ 融资余额：09-16 只有 100 行、邻近水平是 200 行，缺深市整天的数据——…
  ⚠ 龙虎榜：09-17 一行都没有——【龙虎榜】按天重跑…
  ⚠ 板块行情：库里停在 2026-09-10，比最新交易日 2026-09-17 落后 5 个交易日——…
  当日完整性体检：2026-09-17 有 5 处不齐 —— 个股日K(三口径)缺 1 只；ETF日K缺 1 只；
  指数日K齐；资金净流入齐；融资余额不齐；龙虎榜不齐；板块行情落后 5 天；总股本/流通市值齐
  ```

  manifest 里待办分别落到 `StepStockDayBars`(600000)、**`StepEtfBars`(sh510300)**、
  `StepMargin`(09-16)、`StepLhb`(09-17)——ETF 单独一条正是这次要的能力。
  大宗交易和龙虎榜席位那两张表是空的，判不了，**没报**（判不了不是告警）。

  **第二轮**（把数据补齐）：`2026-09-17 全齐 —— …八项全齐`。

  **第三轮**（验证撤单）：待办清空为 `[]`。

- 验证完把 Debug 库里造的假数据删干净了。

---

## 10. 迁到新任务框架（2026-09-17）

用户看完 §9 问"当日完整性体检有没有按新框架做"——没有，它还是老形状。当天迁了。

### 10.1 为什么这次该迁

跟【全库数据体检】那次破例是同一条判据：**它正要长大**。09-16 从"只查个股K线"扩成三段之后，
编排从 40 行涨到 150 行，而且后面还会继续加判据段（类 3 那批收窄掉的迟早要回来）。
再堆在 `FetchOrchestrator` 里只会让那个文件更难拆。

至于"恰好卡在框架能给的能力上"那半条——它其实不卡：纯查库，几秒就跑完，不需要流式落账，
也用不上 MaxItems/Deadline。所以迁的理由只有"长大"这一条，但这一条够了。

### 10.2 关键是判据只留一份

体检有**两个调用方**：计划里的那一项，和【重新拉取失败】收尾时的那次重建
（补过一轮之后名单必须重算，`FinishFetchRun(checkDayCoverage: true)`）。
所以迁移不是"把代码搬进任务类"，而是先把判据和落账抽成 `SqliteDayCompletenessAuditor`，
两边共用：

- `RunSegments(out tradingDay)` —— 一段一批产出 `DayFinding`（只查库，不碰 manifest）；
- `Apply(manifest, findings)` —— 把结论写进 manifest 对象（不落盘，落盘时机归调用方）。

这样不会出现"体检说齐了、重试那边还挂着单子"的分叉。
（这个项目在"同一判据两处各写一份"上栽过，见 `SqliteAdjSeriesAuditor` 的由来。）

### 10.3 一批＝一段

① K线 / ② 日更表 / ③ 覆盖式快照各 yield 一次。不是为了好看：段与段之间才是能停下来的地方
（取消只在单元之间生效，不打断进行中的单元），而且前一段的待办先落盘——后面两段扫得再久，
K线那份名单已经稳稳记下了。

`SaveBatchAsync` 写的是 manifest 而不是业务表：体检本身不产生数据，它的产出就是"谁欠着什么"。

### 10.4 一个刻意的决定：不齐**不算这一项失败**

体检查出问题 = 它干成了该干的活。判成失败的话状态列会红在体检这一行，
而真正该动手的是【重新拉取失败】和日志里点名的那几项。
（跟【分档资金流快照】那条正相反——那一项红是因为**它自己**没把数据拿全，而且过期就没了。）

### 10.5 落地的文件

| 文件 | 改动 |
|---|---|
| `src/StockPlatform.Data/Sqlite/SqliteDayCompletenessAuditor.cs` | **新增**：三段判据 + `DayFinding` + `Apply` |
| `src/StockPlatform.Tasks/DayCompletenessTask.cs` | **新增**：`FetchTaskBase<DayFinding>`，一批一段 |
| `src/StockPlatform.Data/Orchestration/FetchOrchestrator.cs` | `CheckLatestDayCoverage` 从 240 行缩成 40 行的薄封装（只剩【重新拉取失败】在用） |
| `src/StockPlatform.Data/Orchestration/FetchOrchestrator.Steps.cs` | 删掉 `RunStepDayCoverageCheckAsync` |
| `.../Fetcher/App.xaml.cs` | 注册一行 |
| `.../Fetcher/ViewModels/MainViewModel.cs` | 删掉 switch 里那个 case（走 registry 总分支） |
| `.../StockPlatform.Tests/DayCompletenessTaskTests.cs` | **新增** 6 个用例 |

### 10.6 验证

- 单测：任务级 6 个 + 判据级 14 个全过；全量 **1,563 个全过，0 失败**。
- 实机（Debug 实例）：同一套造好的缺口场景，迁移前后日志**逐字一致**（只多了"用时 0.1 秒"），
  manifest 待办也一致（`StepStockDayBars` / `StepEtfBars` / `StepMargin` / `StepLhb`）；
  补齐后再跑 → `全齐`，待办清空为 `[]`。造的假数据已删干净。

---

## 11. 收尾：文档与两处硬编码（2026-09-17）

拆【拉取市场事件】那四项之前能做的都做了。

### 11.1 两处硬编码表名变成 Spec 上的配置

体检里原来写着两个 `spec.Table == "…"`：

| 原来 | 现在 | 为什么值得改 |
|---|---|---|
| `== "NetInflowDetail"` 跳过 | `Spec.SkipDayCheck` | 语义是"当天归它自己的守卫管"，写在表清单上一眼能看见 |
| `== "LhbSeat"` 往前挪一天 | `Spec.RunsAfterDayCheck` | 语义是"它在日更里排在体检之后"——**日更顺序是会变的** |

后一条正是拆分会碰的：四项拆出来后要是有哪一项排到了体检后面，就得标上这个。
所以顺手加了一条测试拿 `DailyOrder` 直接核对：

```
排在体检之后的日更表_必须标RunsAfterDayCheck
```

谁改了日更顺序却忘了改 Spec，这条会红——而不是等到某天看见一条莫名其妙的"龙虎榜席位不齐"。
（它顺带也校验了每个 `OwnerTaskId` 都解析得成 `FetchActionId`，
所以大宗交易改挂新动作时拼错也会被挡住。）

### 11.2 文档

- `doc/data-platform-design.md` 那段"体检"还停在 2026-08 的样子（只讲个股K线、写的是已经废弃的
  `Manifest.MissingDayCodes`）。改写成现在的三段 + 分档资金流那道单独的守卫。
- `doc/solution-class-map.md` 补上 `DayCompletenessTask`、`SqliteDayCompletenessAuditor`、
  `SqliteMoneyFlowDayAudit` 三个类和它们的连线。

历史设计文档（`fetch-plan-atomic-tasks-design.md`、`fetch-plan-group-design.md`、
`retry-backlog-design.md`）里那些"当日覆盖率体检"**没动**：它们记的是当时那一版的样子，改了反而丢掉沿革。

### 11.3 等拆分的那一条

`BlockTrade` 的 `OwnerTaskId` 现在还挂在复合任务 `FetchMarketEvents` 上，`HowToFill` 写的是
"【市场事件】重跑一次"。拆成四项之后这两处要改成大宗交易那一项自己的动作——
不改的话体检照样记待办，但【重新拉取失败】认领不到，静默补不上。§11.1 那条测试会挡住拼错，
但**挡不住"忘了改"**（旧 id 消失时 `Enum.TryParse` 才会失败，所以只要旧动作还在就不会红）。

---

## 12. 跟上拆分：大宗交易 + ETF 不复权（2026-09-17 晚）

### 12.1 §11.3 那条不用我做了

【拉取市场事件】拆出【大宗交易】时，那边已经把体检这侧一并改好了：
`RetryTaskIds.BlockTrade = "FetchBlockTrade"`、`DailyTables` 里 BlockTrade 的 `OwnerTaskId`
和 `HowToFill`、`DailyRefetcherFor` 的按天重抓入口（共用 `BlockTradeDayWriter`）全都到位。
实机验证时大宗交易的残缺日待办确实落在 `FetchBlockTrade` 名下。

顺带确认：`FetchBlockTrade` 在 `DailyOrder` 里排在体检**之前**（第 12 位，体检第 17 位），
所以不用标 `RunsAfterDayCheck`——§11.1 那条测试也是这么核对的。

### 12.2 顺着日更清单发现的新缺口：ETF 不复权

同一天新增的【ETF日K·不复权】（`StepEtfRawBars`，ETF 的 `day_raw`，是 ETF 回测序列的输入）
**不在体检范围内**——第①段只查 ETF 的 `day`。这跟当初 ETF 整个漏在体检之外是同一类缺口：
`day_raw` 哪天整批没抓到，体检照样报"ETF日K齐"。

补法是把它当**独立的一条线**：

- 体检第①段加一次 `GetCodesMissingDay(DayRaw, …, TypeEtf)`，记进新的
  `RetryTaskIds.EtfRawBars`。不跟前复权合并——两个任务、两条水位线，缺一个不代表另一个也缺。
- 【重新拉取失败】那边原来是个 `onlyQfq` 布尔（ETF/指数只跑前复权），现在按任务展开成三个口径开关：

  | 待办归属 | 跑哪些口径 |
  |---|---|
  | 个股（三口径并成一份名单） | day + day_hfq + day_raw |
  | ETF / 指数（前复权） | 只 day |
  | **ETF·不复权** | **只 day_raw** |

  少了最后一行的话，ETF 的 `day_raw` 待办会被当成前复权补——补完那条线还是缺的，
  而复查会说"已补齐"。

### 12.3 验证

- 单测：新增一条"ETF不复权是独立的一条线_单独记单独补"（造一只**前复权齐、不复权缺**的 ETF）；
  全量 **1,671 个全过**。
- 实机（Debug 实例）：造出"ETF 前复权缺 sh510300、不复权缺 sh510500"，体检报
  `ETF日K缺 1 只；ETF日K·不复权缺 1 只`，待办分别落在 `StepEtfBars` / `StepEtfRawBars`。
- 补数分流用**离线模拟数据源**（`Mock`，零请求）跑了【重新拉取失败】，日志逐条对上：

  ```
  补 2026-09-17 还缺的个股·前复权，共 1 只 … 抓到新数据 3 只   ← 三个口径
  补 2026-09-17 还缺的ETF，共 1 只         … 抓到新数据 1 只   ← 只有前复权，没有"开始抓不复权日K"
  补 2026-09-17 还缺的ETF·不复权，共 1 只  … 开始抓不复权日K … 抓到新数据 1 只
  ```

  补完后体检自动重建名单：K线三条全齐。验证用的假数据和临时改的 `BarSource: "Mock"` 都已还原。

---

## 13. 真实库上的预演：零误报，以及各判据的余量（2026-09-17）

前面所有验证都是 Debug 实例里**构造**的数据——那只能证明"该报的报得出来"，
证不了"不该报的不会报"。误报比漏报更能毁掉一个体检：天天喊狼来了，人第三天就不看了。

所以拿生产库（25GB，只读、带日期下界的轻查询，不做全表扫）把三段判据各预演一遍。
锚是当时的最新交易日 **2026-09-16**（09-17 的指数日K还没抓到，体检就查 09-16——
这正是"以指数最新一根为锚"该有的行为）。

### 13.1 第①段 K线：六个面全齐

| 面 | 09-16 有 | 缺 |
|---|---|---|
| 个股·前复权 / 后复权 / 不复权 | 各 5,549 | 0 |
| ETF·前复权 / 不复权 | 各 1,667 | 0 |
| 指数 | 9 | 0 |

### 13.2 第②段 日更表：全齐，而且余量很大

| 表 | 查哪天 | 行数 | 邻近中位 | 阈值 | 触发线 | 结论 |
|---|---|---|---|---|---|---|
| NetInflow | 09-16 | 5,564 | 5,560 | 0.7 | < 3,892 | 齐 |
| MarginDetail | 09-15（T+1） | 4,105 | 4,103 | 0.7 | < 2,872 | 齐 |
| Lhb | 09-16 | 73 | 67 | 0.2 | < 14 | 齐 |
| LhbSeat | 09-15（排体检之后） | 730 | 681 | 0.2 | < 137 | 齐 |
| BlockTrade | 09-16 | 97 | 118 | 0.2 | < 24 | 齐 |

两类表的余量差别正是当初按表分阈值的理由：快照型（前两行）行数稳定到几乎不动，
0.7 都嫌松；事件型（后三行）本身就在 97~300 之间晃，所以 0.2 意味着"只抓到 1/5 才报"——
故意的，否则 BlockTrade 这种 09-16 比邻近低 18% 的正常波动天天都会报。

### 13.3 第③段 覆盖式快照：齐

| 表 | 停在 | 锚 | 允许落后 | 结论 |
|---|---|---|---|---|
| Board（板块行情） | 09-16 | 09-16 | 1 个交易日 | 齐 |
| FundamentalMetric（总股本/流通市值） | **09-17** | 09-16 | 0 | 齐（比锚还新，`behind=0`） |

### 13.4 留着这张表干什么

以后要是有人想动阈值，先回来看这里的"触发线"那一列——它告诉你现在离误报有多远。
真实数据的余量不是拍出来的，改阈值前得有新的实测数字顶上。

---

## 14. 第④段：日频景气指标停更（2026-09-17 晚）

§3.4 的类 3 当初把景气指标一起收窄掉了（"事件型，某天没有很正常"）。那个判断对公告类成立，
**对景气指标不成立**：116 个指标里 45 个是日频的，它们每个交易日都该有新值。用户要求加上。

### 14.1 判据：跟它自己的历史节奏比

判"落后几天"要有基准，而这里有三个陷阱，全是从真实数据里撞出来的：

| 陷阱 | 实例 | 后果 |
|---|---|---|
| 东财的 `frequency` 标注不准 | 涤纶 DTY/FDY/POY 标成"日"，实际每周发两三次、最长停 6 天 | 写死"日频就该每天有"→ 这三个天天误报 |
| **日期不是交易日** | 国内汽油/柴油供应价的值落在 2026-09-12（**周六**） | 拿交易日历数"落后几个交易日"→ 直接算不出来 |
| 正常停更长度差一个量级 | 期货类 3~4 天、涤纶 6 天、国内油价 15 天 | 统一阈值要么天天误报、要么什么都检不出 |

所以基准从**每个指标自己**近 90 个自然日的数据算：相邻值之间最长隔了多少天（`maxGap`），
现在停得比那还久（再加 1 天宽限）才报。语义是"它从来没停这么久"。

这条判据自带对第一个陷阱的免疫——某个指标要是被标成"日"、其实是周频，它的 `maxGap`
自然就是 7~8 天，判据跟着宽容，不需要我们去纠正东财的标注。

### 14.2 实测定参（锚 2026-09-16，45 个日频指标全部有足够样本）

`落后天数 − maxGap` 的分布：

| 差值 | 指标数 | 代表 |
|---|---|---|
| −4 | 31 | 期货价格类（当天就有值） |
| −1 | 8 | 涤纶 ×3（停 5 天 / 最长 6 天）、国际原油天然气 |
| −2 / −3 | 4 | 国际汽油等 |
| −11 | 2 | 国内汽油/柴油供应价（15 天一发） |

**最大 −1**，也就是零误报，而最紧的那个离触发线还差 1 天——`GraceDays = 1` 就是为它留的。
这类数据不在关键路径上（景气指标是传统行业分析的输入，晚几天不影响选股），
所以宁可保守：漏报一天没代价，天天误报会让人从此不看这一行。

### 14.3 落地

- `src/StockPlatform.Data/Sqlite/SqliteIndicatorStalenessAuditor.cs` **新增**——判据自己一个类，
  跟 `SqliteDailyTableAuditor` / `SqliteMoneyFlowDayAudit` 平级。
  （第一版写在 `SqliteDayCompletenessAuditor` 里、测试拿反射调私有方法，随即改掉：
  判据该能被直接测，反射壳是味道不是办法。）
- `SqliteDayCompletenessAuditor` 加第④段，只把结论包成一条 `DayFinding`：
  45 个指标不能各报一行，汇总成一句 + 明细列前 5 个。
- 只报不补：停更可能是数据源下线了这个指标、改了编号、或者接口挂了，三种处理完全不同。
- 测试 6 条（正常不报 / 停更要报 / 本来就隔几天发的不报 / 日期落在周末也判得了 /
  样本不足不判 / 月频不掺进来）；全量 **1,682 个全过**。

### 14.4 实机验证

Debug 实例造 5 个日频 + 1 个月频，其中一个平时每天一值、停了 8 天：

```
⚠ 行业景气指标(日频)：1 个指标停得比自己平时最长的间隔还久——全国水泥价格指数
（停在 09-09、已 8 天没更新，它平时最多停 1 天）。跑一次【行业景气指标】…
汇总：…；行业景气指标(日频) 1/5 项停更
```

6 天一发的涤纶没报，月频那个没被算进来（`1/5` 而不是 `1/6`）。假数据已清干净。
