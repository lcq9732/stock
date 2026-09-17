# 【大宗交易】拆成独立任务 + 按日整日替换（2026-09-17）

## 0. 起因：`DAILY_RANK` 不是稳定标识，主键选错了

界面上「大宗交易性质」那一格显示宁德 2026-08-05 起 **35 笔 / 23.25 亿**，去重后真值是
**27 笔 / 18.48 亿**。顺着查下去发现的是一个表级问题。

### 0.1 现象

同一笔交易，不同时间抓，东财给的 `DAILY_RANK` 不一样：

| 抓取时刻 | daily_rank | 价 / 金额 | 买卖方 |
|---|---|---|---|
| 2026-09-15 23:19 | **22、27** | 316.36 / 316.4万、474.5万 | 机构专用 ↔ 机构专用 |
| 2026-09-16 22:50 | **1、2** | 316.36 / 316.4万、474.5万 | 同上 |

而 `BlockTrade` 的主键是 `(trade_date, code, daily_rank)`。于是本该 UPSERT 覆盖的第二次抓取
变成了 **INSERT 一份新副本**。叠加 `LaggingFieldLookbackDays`（增量回看 30 天补滞后字段），
最近 30 天每跑一次增量就复制一批。

2026-09-04 的 002952 现在躺着 8 行，真值 3 笔：

```
rank 2,4,6   ← 09-16 22:50 批次（121000股 / 113000股 / 110000股）  ← 真值
rank 1,3     ← 09-11 00:12 批次（121000 / 110000 的副本）
rank 10      ← 09-11 23:09 批次（110000 的副本）
rank 35,46   ← 09-07 12:19 批次（113000 / 110000 的副本）
```

### 0.2 `DAILY_RANK` 到底是什么

拿单日查询实抓验证（2026-09-15 全市场 107 行）：**它不是「该股当日第 N 笔」**。

```
002912  5 笔  →  rank = 2, 4, 6, 8, 10      ← 全是偶数
158023  2 笔  →  rank = 20, 26
127025  1 笔  →  rank = 11                  ← 唯一一笔却不是 1
300750  2 笔  →  rank = 1, 2
```

全市场 107 行只有 31 个不同的 rank 值，跨股票大量重复、组内跳号。**语义不明、会变、无信息量。**

补充观察：对**已定稿的老数据** rank 基本是稳定的（09-09 和 09-17 两次抓 2020-01-23 拿到的
rank 完全一致，所以是覆盖而非新增），**只有近期数据不稳定**——东财还在补录重排。这正好解释
破坏的分布。

### 0.3 影响量化

按抓取批次聚类（同一批次内的同内容行是**真实拆单**，必须保留——301380 在 09-16 单批次里就有
两笔完全相同的记录）：

```
全表 583706 行 → 580619 行，应删 3087 行 (0.53%)
金额 144579 亿 → 143995 亿
受影响 735 / 2602 个交易日
  ├─ 2026-08-01 之后   30 天，删 1225 行   ← 重抓窗口，破坏集中在这
  └─ 2026-08-01 之前  705 天，删 1862 行   ← 09-04/09-07 两次早期试抓与 09-09 全量的交叠
```

单日虚高最高 **+106%**（2026-09-08：20.3 亿 → 9.8 亿）。

### 0.4 没有遗漏——这决定了存量是「清理」不是「重建」

挑三天拿接口逐行比对（单日查询页数少，不受深分页干扰）：

| 交易日 | 库中行数 | 去重后 | 接口 `count` | 接口独有 | 库独有 |
|---|---|---|---|---|---|
| 2026-09-15 | 189 | **107** | **107** | 0 | 0 |
| 2026-09-07 | 521 | **270** | **270** | 0 | 0 |
| 2021-12-15（2 页） | 572 | **558** | **558** | 0 | 0 |

**一行不差。历史数据本身是完整的，只是多了副本。**

> 期间出过一次假阳性：2021-12-15 有 2 行对不上，查下来是 09-04 那次早期抓取还没解析
> `buyer_code`/`seller_code`，老行存 NULL、新行存 `'0'`，指纹把同一笔切成了两个。
> 换用席位名做指纹后归零。**比对脚本的问题，不是数据的问题。**

### 0.5 连带：体检和重复在互相喂

`BlockTrade` 的残缺日判据是 `行数 < 前若干日滚动中位数 × ThinRatio`
（`SqliteDailyTableAuditor`）。重复同时抬高**被检日行数**和**基准中位数**：

- 真残缺的日子，被重抓复制几轮后行数达标 → **漏报**
- 刚抓进来还没被重抓过的新日子，行数是真值，对着被吹高一倍的基准显得只有一半 →
  **误判残缺** → 触发重抓 → 又复制一份 → 达标

`manifest.json` 里现挂着两个确认残缺日，拿接口对过：

| | 库中行数 | 去重后 | 接口真值 | 结论 |
|---|---|---|---|---|
| 2020-01-23 | 58 | 58 | **58** | 误判（春节前最后一个交易日，本来就清淡） |
| 2020-02-03 | 57 | 56 | **56** | 误判（疫情开市首日千股跌停） |

2026-09-17 02:01 那次补抓（114 行）什么都没补到，只是又复制了一份。

### 0.6 与既有设计的冲突

`FetchOrchestrator.DailyRefetcherFor` 的类注释写着：

> 写入一律走 InsertOrIgnore/Upsert：**已有的行不动、只补缺的那部分，所以反复跑无害**
> ——这正是残缺日能被修好的前提。

大宗恰恰**反复跑有害**。这个前提对它不成立，是这次要修的根。

---

## 1. 为什么拆成独立任务，而不是就地改

判据用 `doc/full-audit-task-migration-design.md §0`（2026-09-10 用户更正）：
**看迁移成本 + 维护成本，成本可控就该迁，不需要额外理由。**

### 1.1 四张表的节奏根本不同

| 表 | 切片 | 片数 | 形状 |
|---|---|---|---|
| **大宗交易** | 改按日后 | **~2600 片** | 长跑，需要分批 |
| 机构调研 | 按年 | 十几片 | 几分钟跑完 |
| 限售解禁 | 全量重取 | 30 多片 | 几分钟跑完 |
| 股东增减持 | 按年 | 十几片 | 几分钟跑完 |

绑在一起，大宗那 2600 天的历史回补会拖着另外三张陪跑一遍。

### 1.2 体检层面它们早就分开了

`FetchOrchestrator.cs:2698` 已有的注释：

> ⚠【拉取市场事件】是复合任务，但只有大宗进了日频体检，所以这里**只重抓大宗**

体检只认大宗、补抓只补大宗，任务层面还把四张绑着——这个别扭正是要拆掉的。
先例：MoneyFlow 2026-09-12 按同样理由拆成快照 / 补历史两项。

### 1.3 框架能力是白送的

改按日之后，大宗**天然就是 `FetchTaskBase` 的形状**：一批 = 一天。于是

- `MaxItems` = 本轮最多抓几天
- `Deadline` = 空闲窗口到点收尾

2600 天的历史回补正需要这两个，而老框架的 `RunOne` 一个都没有。不迁就得手写一遍——
骨架注释：「每个子类各写一遍就会各写错一遍」。

---

## 2. 批的粒度：一天就是一批

```
FetchAsync      → 逐交易日：抓完该日全部页 → 与接口 count 核对 → yield 该日全部行
SaveBatchAsync  → 整日替换：DELETE 该日 + INSERT 本批（同一事务）
OnCompletedAsync→ 汇总；清理已修好的待办
```

**为什么一天整批 yield、而不是一页一批**：整日替换要求先收齐一整天，否则 DELETE 完只写进半天，
留下的残缺事后完全看不出来（跟 `MoneyFlowSnapshotTask` 的「一批＝一整天」是同一条理由）。

`TItem` 直接用现有的 `BlockTrade`，不引入新模型。

---

## 3. 整日替换与 `count` 校验

### 3.1 流程

```
① 单日查询第 1 页 → 拿 result.count（该日总行数）和 pages
② 翻完所有页，累计收到的行数
③ 收到行数 == count ?
     是 → 按 code 分组、组内按接口返回顺序自赋 daily_rank = 1..N → DELETE 该日 + INSERT
     否 → 跳过该日，不动库，记错误 + 写残缺日待办
```

`EastMoneyDataCenterClient.QueryAsync` 已有 `onTotalCount` 回调，拿 `count` 不用改客户端。

### 3.2 安全阀：`count == 0` 且库里已有行 → 不删

否则接口抽风返回空的那一次会把一整天的数据抹掉。这种情况记警告、跳过。

### 3.3 事务边界

`DELETE + INSERT` 必须在**同一个事务**里。中途崩溃回滚到删除前，不能留下空的一天。

### 3.4 `daily_rank` 改由我们自赋

主键 `(trade_date, code, daily_rank)` **保持不变**，不需要 schema 迁移。变的只是 `daily_rank`
的来源：从「东财给的」改成「我们按接口返回顺序、按 code 分组编号 1..N」。

自赋之后它**真的**就是「该股当日第几笔」了，比东财那个还准确。
`SqliteSchema.cs:624` 那行注释（"当日第几笔，同股同日可多笔"）当前是错的，跟着改。

**东财原值不另存**：已证实每次都变、无信息量。要留也只是加一列 `em_daily_rank`，
但没有任何用得上它的场景。

---

## 4. 模式映射

| `FetchMode` | 抓哪些天 |
|---|---|
| `Incremental` | 库里最大日期 − `LaggingFieldLookbackDays`(30) → 今天，逐交易日 |
| `SpecificDay` | 只抓 `args.Day` |
| `FirstBackfill` | 2016-01-01 → 今天，逐交易日（**存量清理就用它**，见 §7） |
| `FillBacklog` | 只抓待办里属于本项的天（接缝见 §6，待定） |

`MaxItems` = 本轮最多抓几天；`Deadline` 到点收尾。两个都是骨架现成的。

**交易日历驱动**：用 `ITradingDayRepository` 跳过非交易日，不发空请求
（大宗数据起点 2016，深交所官方日历 2005 起，覆盖足够）。
接口 `count == 0` 的日子写进 `IDailyFetchNoDataRepository`，`FirstBackfill` 下次跳过。

---

## 5. 请求量

| 场景 | 请求数 |
|---|---|
| 日常增量 | 30 天 × 1~2 页 ≈ **35 个** |
| 残缺日补一天 | 1~2 个 |
| `FirstBackfill` 全量 | 2602 交易日 × 1~2 页 ≈ **3000 个** |

单日 100~600 笔，`pageSize=500` 只需 1~2 页，**永远浅分页**。
相比现在按月切片（每月约 9 页、深分页），请求数从 ~120 涨到 ~3000，但只在回补时发生一次，
换来的是每一天都能用 `count` 逐日校验。

目录里的估时 `TimeSpan.FromMinutes(25)` 要按新形状重估。

---

## 6. 残缺日待办：按 `DayCompletenessTask` 的先例抽共用类

大宗是**第一个既进了残缺日体检、又要迁到新框架的任务**
（`NetInflowDetail` 故意不配 `OwnerTaskId`，只报不补）。但这个问题
`DayCompletenessTask`（2026-09-17）已经解过一次，它的类注释：

> 判据和落账现在都在 `SqliteDayCompletenessAuditor`，这一项和【重新拉取失败】
> 收尾时的那次重建**共用同一份**——不会出现"体检说齐了、重试那边还挂着单子"的分叉。

### 6.1 现状：判据已共用，只有编排还锁在 orchestrator 里

`FillPartialDaysAsync` 拆开看：

| 步 | 做什么 | 现在在哪 |
|---|---|---|
| ① | 按 `OwnerTaskId` 找 spec | `SqliteDailyTableAuditor.DailyTables` ✅ 已共用 |
| ② | 读待办 `Todo(taskId, PartialDay)` | orchestrator |
| ③ | **拿「按天重抓」的入口** | `DailyRefetcherFor` ← **唯一的任务特定部分** |
| ④ | 逐日重抓 + 「整批都失败＝连不上，别白耗 Tries」兜底 | orchestrator |
| ⑤ | 复查 `auditor.CheckDays(spec, ...)` | `SqliteDailyTableAuditor` ✅ 已共用 |
| ⑥ | Tries+1 / `AuditMaxTries` → `ConfirmedPartialDays` / `SetTodo` | orchestrator |

②④⑤⑥ **完全是任务无关的**，唯一的变量是 ③ 那个 `Func<DateOnly, Task<int>>`。
留在 orchestrator 里只是历史位置，不是设计。

### 6.2 做法

抽成 `PartialDayRepair`（`StockPlatform.Data/Orchestration/`，跟 `RetryBacklog` 同层）：

```csharp
public sealed class PartialDayRepair(string dbPath, IManifestStore store)
{
    public Task<PartialDayRepairResult> RunAsync(
        string taskId,
        Func<DateOnly, Task<int>> refetch,
        IProgress<string>? progress,
        CancellationToken ct);
}
```

于是两边共用同一份：

- `FetchOrchestrator.FillPartialDaysAsync` 缩成一行调用，`refetch` 仍来自
  `DailyRefetcherFor`（Margin / Lhb / LhbSeat 三项照旧，一行不动）
- `BlockTradeTask` 在 `FillBacklog` 模式下调用同一个类，`refetch` 传自己的「抓一天」

**新任务不需要 orchestrator，也不复制任何判据或落账逻辑。**
`DailyRefetcherFor` 里 `RetryTaskIds.MarketEvents` 那个分支直接删掉。

### 6.3 ⚠ 抽的时候要带着的两个细节

- **「整批都失败」的兜底不能丢**（④）：全失败多半是限流/断网而不是"数据源没有"，
  名单和 Tries 必须原样留着。这是最容易在重构里被简化掉的一段。
- **复查绝不能退化成 `COUNT > 0`**（⑤）：残缺日本来就有行，
  拿"有没有行"复查会把没补上的静默划掉（`FillPartialDaysAsync` 原注释里的警告）。

### 6.4 `_dbLock`

`FillPartialDaysAsync` 现在用 orchestrator 的 `_dbLock` 包 manifest 的读改写。抽出去就没这把锁。

`JsonManifestStore` 自身无锁，而 `DayCompletenessTask.SaveBatchAsync` 已经是裸的
`Load → Apply → Save`（09-17 迁移时就这样）——**现状即此**，本项与它保持一致，不额外加锁。
调度侧串行跑任务，并发写 manifest 的窗口只存在于空闲任务并叠的场景，那是既有问题、
不在本次范围内。

---

## 7. 存量清理

已证实**没有遗漏**（§0.4），所以两条都可行：

**(a) 按批次聚类删 3087 行**——快，但要单独写一次性脚本。

**(b) 直接跑一次 `FirstBackfill`**（推荐）——约 3000 个请求，整日替换天然覆盖，
一步到位，顺带把 `buyer_code`/`seller_code` 的历史缺列补上（09-04 那批老行是 NULL）。
而且它同时**验证了新代码**：跑完全表行数应等于逐日 `count` 之和。

清理后 `nearby` 滚动中位数回到真值，残缺日判据恢复正常。

**顺手清掉那两个误判的 `ConfirmedPartialDays`**（2020-01-23、2020-02-03，§0.5 已证数据完整）。
它们的键是字符串 `"FetchMarketEvents"`，任务 id 改名后本来就认领不到——
直接清掉，省掉这次迁移里唯一一处不可逆的键迁移。

---

## 7.5 实现记录（2026-09-17 落地时的偏差）

设计到实现之间调整了四处，都记在这里：

**① 多抽了一个 `BlockTradeDayWriter`。** "抓一天并落库"有两个调用方——任务侧和
`FetchOrchestrator.DailyRefetcherFor`（补残缺日）。各写一份必然漂移，而里面正好藏着
"抓不全就不落库"这条最要命的规矩，所以抽成一个类，跟 `PartialDayRepair` 一样带可选的
`dbLock`（编排器传它自己那把，任务传 null）。

**② `FillBacklog` 仍然走 orchestrator，不走任务。** 原因是 `MainViewModel` 在分派给
新任务**之前**就把 `FillBacklog` 截走了（`RunFillBacklogAsync`），而已有的
`StepEtfRawBars`、`FetchMoneyFlowDetail` 两个新任务也依赖这条路径——改分派顺序会
连带改到它们。所以 `DailyRefetcherFor` 里**加回**了 `RetryTaskIds.BlockTrade` 分支
（设计里写的是"删掉"），只是内容换成了共用的 `BlockTradeDayWriter`。
§6 抽 `PartialDayRepair` 的价值不受影响：编排仍然只有一份，新任务要自己接管待办时直接能用。

**③ 抽了 `IBlockTradeDayFetcher` 接口，`BlockTradeDay` 挪进 `Logic/Models`。**
不然任务类依赖具体的 `EastMoneyMarketEventProvider`，离线测不了排期、`count` 校验、
分批收尾这些编排逻辑。

**④ `UpsertBlockTrades` 直接删了**，没有保留。留着就是留个陷阱——谁用了它，重复就回来了。

另外，**排序键没动**（`TRADE_DATE,SECURITY_CODE,DAILY_RANK`）：`DAILY_RANK` 跨抓取不稳定，
但同一次查询内部是稳的，单日只有 1~2 页，而且 `count` 校验兜在后面，是更强的保证。

**⑤ `count == 0` 的日子没有写进 `IDailyFetchNoDataRepository`**（§4 里写了要写）。
大宗 2602 个交易日全都有数据，空日极少；而且 `CheckDays` 早就有收敛路径
（0 行算"没补上"→ Tries → `ConfirmedPartialDays`），再加一套只是多一个依赖。
真遇到空日多的情况再补。

测试抓出来的两个真问题：
- `Deadline` 到点收尾时一天都没抓，原来的"整轮全败判失败"判据会把它判成失败。
  判据改成看**发过请求的天数**而不是计划的天数。
- 目录条目声明了 `SpecificDay` 却没声明 `FetchActionParams.Date`，界面上就没地方填那一天、
  只能落到"默认今天"，模式白设。这条是 `FetchTaskCatalogTests` 里既有的自检抓到的。

## 7.6 实机验证（2026-09-17 11:05~11:10，Debug 实例）

Debug 构建的数据目录在 `bin/.../win-x64/data`，跟生产库天然隔离（起跑时 `BlockTrade` 0 行）。
全程走界面：计划页找到【大宗交易】那一行 → 选模式 → 填日期 → 点「执行」。

| # | 验的是什么 | 结果 |
|---|---|---|
| 1 | 【大宗交易】出现在计划页、模式下拉有四个模式 | ✅ 目录注册与 registry 分派通了 |
| 2 | 选「只抓某一天」后**日期框出现** | ✅ `Params: Date` 的修复生效（不修就没地方填日期） |
| 3 | 抓 2026-09-15 | **107 行**＝接口自报的 count，1.4 秒 |
| 4 | **同一天再抓一次** | 仍是 **107 行** ← 原来的 bug 在这里会变成 214 |
| 5 | 库里 `daily_rank` | 每只股票都是 1..N 连续，300750 两笔为 rank 1/2 |
| 6 | 交易日历为空时的兜底 | ✅ 打出警告并退回按自然日，不漏抓 |
| 7 | 增量模式（水位线 −30 天） | 33 天里 23 天有数据、**10 天数据源本来就没有**（9 个周末＋当天未出），2744 行，39.4 秒 |

**最终比对**：把新抓的 23 天逐行跟生产库**去重后**的结果比：

```
日期范围 2026-08-17 ~ 2026-09-16，23 天
接口独有 0 行 | 生产库去重后独有 0 行 | 总差异 0
```

三方交叉验证成立：**新代码抓出来的 = 去重算法算出的真值 = 接口**。
这也反过来确认了 §7 的清理方案——跑一次 `FirstBackfill` 就等于把库清成真值。

顺手修掉两个日志文案问题：进度写成了「（1/1）（1/1）」（`TaskProgress.ToString()` 会自己拼一次），
以及日历为空时仍称「个交易日」（那批里混着周末）。

## 7.7 存量清理：没跑那 3000 个请求（2026-09-17 11:38）

§7 原定跑 `FirstBackfill`（约 3000 请求 / 50 分钟）。实际用**离线数据源**跑完了，零请求、27 秒。

### 为什么可以

「接口对某一天会返回什么」已经是**已知量**——新代码实机抓的 31 天与「按抓取批次聚类去重」
算出的真值逐行零差异，且跨度覆盖 2016~2026：

| 批次 | 天数 | 差异 |
|---|---|---|
| 单日实测 2026-09-15 / 09-07 / 2021-12-15 | 3 | 0 |
| Debug 实例增量 2026-08-17 ~ 09-16 | 23 | 0 |
| 抽查 2016-03-25 / 2020-01-22 / 2021-05-26 / 2023-02-28 / 2025-08-27 | 5 | 0 |

于是做一个 `IBlockTradeDayFetcher` 的离线实现（从库里按批次聚类算出那天的真值），
喂给**真正的 `BlockTradeTask`** 跑 `FirstBackfill`——走的是完整的产品代码路径，
只是数据不从网上来。

### 结果

```
生产库现状：583706 行 / 144579 亿 / 2602 个交易日
2602 天写入 580619 行，1 天数据源本来就没有，用时 26.8 秒，0 条错误

  行数    583706  →  580619   （删 3087，0.53%）
  金额    144579 亿 → 143995 亿
  交易日    2602  →    2602   （一天没丢）
```

先在一份 `BlockTrade` 的拷贝上预演过一遍，数字与独立脚本算的完全一致，再动生产库。

### 验收

| 项 | 结果 |
|---|---|
| **唯一指纹数** | 536471 → **536471**，一个没多一个没少 ← 删的全是副本，没误删任何独有数据 |
| 5 个抽查日 vs 接口 | 全部 **0 差异** |
| `daily_rank` 按股票 1..N 连续 | 不合规组 **0** |
| 交易日 | 2602 → 2602 |
| 宁德 2026-08-05 起 | **27 笔 / 18.48 亿**，加权 −0.03%，简单平均 −0.69%，平价 99.84% |
| 那笔 −18.71% 折价过户 | 确认**只有 1 笔**（原来是 2 行副本）|
| `buyer_code` 空值 | 10681 → **10138**（补了 543 行，正是 09-04 那批早期试抓的——去重时保留最新批次的行，字段最全）|

备份：`publish/data/local/BlockTrade.backup-20260917-113802.sqlite`（172MB，只备这一张表，
库本身 24GB 没必要整份复制）。manifest 备份：`manifest.json.bak-btclean-20260917-114056`。

**一个诚实的边界**：离线 fetcher 永远 `IsComplete`，所以 `count` 校验那条分支这一跑没走到
（只有单测覆盖）。不影响清理结果——清理不依赖它。

### 顺带清掉的误判残缺日

`ConfirmedPartialDays["FetchMarketEvents"] = [2020-01-23, 2020-02-03]` 已移除。
动手前复核：清理后库里这两天分别是 58 / 56 行，与接口实测真值一致——**本来就是齐的**。
键名是旧任务 id，改名后本来也认领不到，一并清掉省了一次键迁移。

## 7.8 「大宗交易性质」那一格的文案（2026-09-17）

顺手做了最初那个改动请求：这一格在两个口径差超过 0.3 个百分点时会追加一行括号说明，
原来只说「被小额单带偏」，现在**点名差异来源**——判据是「溢价率离加权值超过 2 个百分点、
金额却不到总额 2%」的那几笔，正是拉偏笔数平均的元凶，而它们本身往往值得看一眼。

清理后的实际输出：

```
27 笔 / 18.5亿，金额加权溢价率 -0.03%，平价对倒占金额 99.8% —— 以机构间平价调仓为主，无折价甩卖迹象
（简单平均 -0.69%：差异来自 8-27 那笔 288万，只占总额 0.1%，按笔数平均却被它带偏 —— 不采用）
```

tooltip 里那组过时数字（33 笔 / -1.13% / -0.05% / 两笔 288 万）也一并更新了。

## 7.9 同类问题全库扫描（2026-09-17）

这个 bug 的模式是「拿接口给的序号当主键末列」。趁上下文热，把全库主键含序号列的表都扫了。
判据是**跨抓取批次的同内容重复**——同一批次内的同内容行是真实的多条记录，不算。

| 表 | 序号列 | 跨批次重复 | 判定 |
|---|---|---|---|
| `BlockTrade` | `daily_rank` | 3087 行 | ⚠ 本次已修 |
| `LhbSeat` | `seq` | **46 行**（36 组，只在 2026-09-01~09-04） | ⚠ **同一个病，待修** |
| `OrgSurvey` | `survey_no` | 0 | ✅ 干净 |
| `TopShareholder` | `rank` | 0 | ✅ 干净 |
| `StockCustomerSupplier` | `rank` | 0 | ✅ 干净 |

`ShareLift` / `HolderChange` 主键全是业务字段，不在此列。

**`OrgSurvey` 洗清了**：早先看到它「去掉 `survey_no` 后少 376 行」，一度当成可疑。
实际那 376 行全在同一批次内——同一家机构同一天参与同一公司的多次调研，`survey_no` 正是为此
设计的。跨批次重复是 0。`TopShareholder.rank`、`StockCustomerSupplier.rank` 同理，
它们是**业务语义的排名**（第几大股东、第几大客户），不是接口返回顺序，天然稳定。

**`LhbSeat` 确有同病**。实证：600108 的 2026-09-04，同一席位同一金额，
09-07 抓到 `seq=6/7`、09-16 再抓变成 `seq=4`。量很小（46 / 178 万 = 0.0026%），
但机制相同、会跟着每轮重抓持续累积，而且它是分析游资席位的底表——行数虚高会直接影响
「某个席位上榜多少次」这类统计。

### ⚠ 上面那行 `LhbSeat` 的判定后来被推翻了（2026-09-17 当天，见 doc/lhb-seat-task-design.md §0）

下一轮真去修的时候，本节关于 `LhbSeat` 的三个判断**都不准确**，留在这里是为了记住错在哪：

| 本节写的 | 实际 |
|---|---|
| `seq` 是东财给的序号、跨抓取会变 | `seq` **从接入那天起就是本地自赋的**（`AssignSeq`，按净额降序）。改不动任何东西 |
| 重复 46 行 | **3579 行**。46 只是"跨抓取批次"那一小撮；**3537 行是单次抓取内部**的跨页重复 |
| 判据是"看是否跨抓取批次，同批次内的同内容行是真实多行" | 这条**对席位表不成立**。同批次内的同内容行多数是跨页副本 |

根因也不是主键选错，而是**按月切片（40~60 页）+ 非唯一排序键 `TRADE_DATE,SECURITY_CODE`**
造成的跨页错位——它同时制造**重复和遗漏**，实测每多一份副本就挤掉一行真数据。
因此存量**不能**走 §7.7 的离线模拟（缺的行本地算不出来），只能真跑一次整段回补。

⚠ 顺带一条通用教训：**`count` 校验抓不住跨页错位**——实测 12 天里库中行数全部等于接口自报的
count，错的是内容。大宗那轮"行数 == 逐日 count 之和"的验收判据不能照搬到别的表。

以下是当时（错误的）调研记录，保留原样：

### 修 `LhbSeat` 之前先知道这几件事（2026-09-17 调研，留给下一轮）

**① 不能靠「把主键末列换成 `seat_code`」绕过去。** 这是最容易想到的省事修法，但走不通：

```
seat_code = '0' 共 191159 行（10.7%），全是「机构专用」
——机构席位不披露具体营业部，代码统一是 0，一张榜上可以并列好几个
```

实测同榜（`trade_date, code, is_buy, explanation`）同 `seat_code` 出现多行的有 **54554 组**，
样例 000823 的 2026-09-16 卖方榜上三行都是「机构专用」、`seat_code` 都是 0，净额各不相同——
它们是**三个不同的机构席位**，不是重复。`trade_id` 也不行，它是**榜的 id**，同一张榜内全一样。

⇒ 所以只能走跟大宗一样的路：**按日整日替换 + `seq` 由落库时自赋**（按净额降序，
正好就是 `seq` 原本声称的语义，比接口给的还准确）。

**② 分页比大宗深一些。** 单日行数中位数 685、最多 6170（2024-02-07），
按 `pageSize=500` 算是 **2~13 页**——仍远离深分页区，但不像大宗那样「永远 1~2 页」。
`count` 对账因此更重要。

**③ 一张榜通常 5 个席位**（买前 5 / 卖前 5），`seq` 取值 0~9。
「5 个席位的榜」35 万张，其余 1~8 个的合计不到 9 千张。

**④ 完全同内容（席位＋买卖额全等）的组有 3260 组**——修之前要先判定这些是真重复还是真实多行，
判据跟大宗一样：**看是否跨抓取批次**，同批次内的同内容行不能删。

**⑤ 该顺手迁到新 Task 框架**，理由跟大宗一致：既然抓取方式要从「按月切片」改成
「按日整日替换」，2600 天就是长跑，正需要 `MaxItems` / `Deadline`；而老框架的 `RunOne`
一个都没有。`BlockTradeTask` / `BlockTradeDayWriter` / `PartialDayRepair` 三个都是现成模板，
`LhbSeat` 也已经在日频体检里（`OwnerTaskId: RetryTaskIds.LhbSeat`），接缝完全一样。

**⑥ 存量清理同样可以走离线模拟**（见 §7.7），46 行的量几秒钟就完。

⚠ 判据要点：**光看「去掉序号列后少了多少行」会误伤**——`OrgSurvey` 那 376 行、
`TopShareholder` 那 499 万行都是正常的多条记录。必须看**跨批次**。

## 8. 改动清单

### 新增

| 文件 | 内容 |
|---|---|
| `src/StockPlatform.Tasks/BlockTradeTask.cs` | 主体，参考 `MoneyFlowSnapshotTask`（201 行），估 200 行上下 |
| `src/StockPlatform.Data/Orchestration/PartialDayRepair.cs` | 残缺日待办的编排，从 `FillPartialDaysAsync` 抽出（§6），orchestrator 与新任务共用 |
| `FetchTaskCatalog.cs` | `FetchActionId.FetchBlockTrade` 枚举一项 |
| `FetchTaskCatalog.cs` | 目录条目（`FetchActionInfo`）、`PlanGroupKind.Daily`、日频清单 —— 3 处 |
| `App.xaml.cs` | `taskRegistry.Register(...)` 一行 |

`SupportedModes`：`Incremental | SpecificDay | FirstBackfill | FillBacklog`。
`DataReadiness`：`AfterClose`（大宗当晚才全）。
`SupportsPartialRun`：`true`（按天分批，天然支持）。

### 改指向

| 位置 | 改成 |
|---|---|
| `RetryTodo.cs:31` `RetryTaskIds.MarketEvents` | → `BlockTrade = "FetchBlockTrade"`（⚠ 值必须等于 `FetchActionId` 枚举名） |
| `SqliteDailyTableAuditor.cs:177` `OwnerTaskId` | → `RetryTaskIds.BlockTrade` |
| `RetryBacklog.cs:188` 显示映射 | → 新 id |
| `MainViewModel.cs:2275` dispatch | 老 case 保留给剩下三张表 |
| `FetchOrchestrator.FillPartialDaysAsync` | 缩成一行调 `PartialDayRepair`（Margin/Lhb/LhbSeat 行为不变） |
| `FetchOrchestrator.cs:2700` `DailyRefetcherFor` 的 `MarketEvents` 分支 | **删掉**——大宗自己管自己的待办 |

### 改

| 位置 | 改什么 |
|---|---|
| `EastMoneyMarketEventProvider.FetchBlockTradesAsync` | 改成「抓一天」的方法，回传 `count` 供校验；切片交给任务 |
| `EastMoneyDataCenterClient.ByDay` | 新增切片器（或任务侧直接按日循环，不用切片器） |
| `SqliteMarketEventRepository` | 新增 `ReplaceBlockTradesForDay(day, rows)`（DELETE + INSERT 同事务）；`UpsertBlockTrades` 保留给别处 |
| `SqliteSchema.cs:624` | `daily_rank` 注释改掉 |
| `FetchTaskCatalog.cs:1071` | 「拉取市场事件」说明里删掉大宗那一段，估时重估 |

### 删

- `RunFetchMarketEventsAsync` 里大宗那一支（`FetchOrchestrator.cs:1744`）
- `LaggingFieldLookbackDays` 若只剩大宗在用，跟着迁进任务

---

## 9. 测试

| 用例 | 锁住什么 |
|---|---|
| 整日替换：同一天抓两次，行数不变 | **本次的根因**，回归的第一道闸 |
| `count` 不一致 → 不写库、记待办 | 残缺数据永远不覆盖完整数据 |
| `count == 0` 且库里有行 → 不删 | §3.2 安全阀 |
| `daily_rank` 自赋：同 code 内 1..N 连续 | 主键唯一性 |
| 同一天同价同量同席位两笔（301380 2026-09-04 实抓样本） | 真实拆单不能被去重掉 |
| `MaxItems` = 3 → 只抓 3 天，水位线停在第 3 天 | 分批跑可续 |
| 非交易日不发请求 | 日历驱动 |
| `PartialDayRepair`：整批都失败 → 名单和 Tries 原样留着 | §6.3 最容易被重构掉的一段 |
| `PartialDayRepair`：补上的划掉、没补上的 Tries+1、满 `AuditMaxTries` 进 `ConfirmedPartialDays` | 抽取前后行为一致 |
| 抽取前后对 Margin/Lhb/LhbSeat 行为不变 | 共用类不能改到另外三项 |

`MarketEventExtraFieldsTests` 里那两份实抓 JSON 继续用（解析层没变）。

---

## 10. 分步

1. **先抽 `PartialDayRepair`**（§6），orchestrator 改成调它，**行为一行不变**。
   跑测试确认 Margin / Lhb / LhbSeat 三项照旧——纯搬，不掺新东西
   （`doc/full-audit-task-migration-design.md §3③`：一次改两件事，出问题分不清是搬坏了还是新逻辑的 bug）
2. 再写 `BlockTradeTask` + 测试，不碰数据
3. Debug 实例实跑 `SpecificDay` 抓几天，对 `count`（数据目录天然隔离，见 `project_debug_run_verify`）
4. 备份 `current.sqlite`
5. 跑 `FirstBackfill` 清存量（§7b），跑完对账：全表行数 == 逐日 `count` 之和
6. 清掉那两个误判的 `ConfirmedPartialDays`
7. 重跑一次「大宗交易性质」诊断，确认宁德那格变成 27 笔 / 18.48 亿

⚠ 第 4 步是 3000 个请求、跑得久，要在不影响日常抓取的窗口做。
