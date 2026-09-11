# 个股观察项（Watch Item）设计

> 2026-09-11 起草。要解决的问题：**"这只票该盯什么"每支不一样，靠人记会漏也会烂。**
> 相关：`project_core_position_layers`（底仓/主动仓两层）、`feedback_new_task_as_class`、
> `project_scheduler_redesign`、`doc/analysis-app-design.md` 3.5 自选股跟踪。

---

## 1. 三层的职责

分层依据不是粗细，是**知识来源**——谁知道这件事、谁来维护它。

| | L0 兜底 | L1 派生 | L2 手写 |
|---|---|---|---|
| 目标 | **不漏** | **不用维护** | **不噪音** |
| 知识来源 | 法规（披露规则枚举得完） | 结构（库里推得出来） | 私有（只有人知道） |
| 覆盖 | 全市场所有票 | 自选股，按画像 | 少数几只，≤3 项 |
| 谁维护 | 一次写死 | 规则，每次重算 | 人，**永不被规则覆盖** |
| 防的是 | 被机械事件打闷棍 | 环境变了自己不知道 | 论点被证伪了还拿着 |
| 失败模式 | 遗漏 | 清单腐烂 | 变许愿池 |

**为什么不能合并：**

- L0 合进 L1 → **会漏**。L0 是"所有票都有"，做成按画像挂，就会有票的画像没覆盖到、解禁日静默过去。这类事件成本不对称：盯着不烦，漏掉很贵。
- L1 合进 L2 → **会烂**。手工清单没人更新。回购方案实施完毕后就不该再盯，**这个摘除动作人一定会忘**。L1 的核心价值不是"挂上"，是**自动摘掉**。
- L2 合进 L1 → **会瞎**。已验证：`StockIndustryIndicator` 有 658 只票的东财自动映射，但锂电池板块 33 只里只有 1 只挂了指标、碳酸锂指数只挂给 6 只上游矿股。自动派生给不出"中游对上游价格的敏感度"——那是判断，不是结构。

**准入判据**（三条全过才进）：

1. **可判定**——能落成 `(取值器, 表达式, 阈值)`。"关注政策"不行，"存货/营收 > 50%"行。
2. **能改变动作**——触发后加/减/清会变。不改变动作的是噪音。
3. **有归属层**——底仓项和主动仓项分开，否则信号互污（见 `AnalyzerPaths.CorePositionPath` 注释里那三条理由）。

---

## 2. 从三层推出机制：每个新东西各解决哪一层的难点

这一节是设计的主干。下面每一张表、每一个字段，都只为了解决某一层的**一个**具体难点；不对应任何难点的东西不该存在。

| 层 | 它的难点具体是什么 | 机制 | 为什么非得这样 |
|---|---|---|---|
| **L0 不漏** | 已经没有难点了 | **零新增**，只加一个扫描器 | 事件表（`ShareLift` `EarningsForecast` `EarningsSchedule` `Dividend` `HolderChange` `Lhb`…）**已经全市场落库**。L0 要的"所有票都扫一遍"现在就能做，缺的只是读它们的代码（取值器 `EventTable`），不缺数据、不缺表 |
| **L1 不用维护** | ①"该挂什么"要能算出来 | 板块→指标的**配置化规则** | 写死在代码里，改一条要重编译 |
| | ②**"该摘了"要能被看见** | `PlanAnnouncement.stage` | ← **这才是那张表存在的理由**。方案的生命周期不落成结构化状态，程序就永远不知道回购已经结束、该把这条观察项摘掉；而摘除动作人一定会忘。**表不是为了记录回购，是为了让 L1 能自动摘** |
| | ③规则重算会跟东财打架 | `StockWatchIndicator` 独立表 | 东财 `ReplaceLinks()` 是 `DELETE FROM` 整表替换；L1 也要"每轮重算"。**两个都想当这张表的主人**，只能各占一张，查询时 UNION |
| **L2 不噪音** | ①手写的会被规则冲掉 | `WatchItem.Origin` 硬边界 | 跟 ③ 是同一个病的第二次发作：派生项整组重建、手写项任何规则不许碰 |
| | ②不可判定的项无处可去 | `Manual` 取值器 + `notes/{code}.md` | 没有出口，人就会把"盯一下政策"硬塞成自动项，然后它永远不触发、也永远没人发现它坏了 |

**读法**：整个设计只有三个真正的新东西——`PlanAnnouncement`、`StockWatchIndicator`、`Origin`——它们分别是 L1 的"自动摘"、L1 的"重算不打架"、L2 的"不被冲掉"。其余全是已有零件的组装。

### 2.1 四类数据，别混

最容易混的是把"事实"和"待办"当成一张表。它们的增删语义正好相反：

| 类别 | 是什么 | 表 | 增删语义 |
|---|---|---|---|
| **事实** | 客观发生了什么 | `PlanAnnouncement`、`ShareLift`、`Bar`、`IndustryIndicatorValue` | **只增不删** |
| **关系** | 谁该看谁 | `StockWatchIndicator` | 规则重算时整组重建 |
| **待办** | 我要盯什么 | `WatchItem` | 挂 / 摘 |
| **触发** | 盯到了什么 | `WatchHit` | 只增 |

**L0/L1/L2 分的不是表，是 `WatchItem` 里每一条的来源。** 三层全部落在 `WatchItem` 一张表里，靠 `Origin` 列区分；`PlanAnnouncement` 和 `StockWatchIndicator` 不是"某一层"，是**支撑 L1 干活所需的两块数据**。

⚠ `PlanAnnouncement` **永不删**。方案结束了要摘掉的是 `WatchItem` 那一条，不是事实表的行——删了就没法回溯"当初方案说的上限是 573"、也没法对账"进展公告拖了几个月"。

### 2.2 拿宁德走一遍

| 步 | 发生什么 | `PlanAnnouncement`（事实） | `WatchItem`（待办） |
|---|---|---|---|
| 1 | 2026-07 发回购方案 | **+1 行** `stage=方案, plan_cap_price=573` | — |
| 2 | L1 规则跑 | 不变 | **挂一条** `Origin=派生, Kind=PlanStage, A 档, AutoExpireOn="stage=完毕"` |
| 3 | 8/4、9/3 进展公告 | **+2 行** `stage=进展, cum_amount=0` | 不变 |
| 4 | 每日求值 | 不变 | 读最新行 → 仍是"进展"且金额 0 → 不触发，进日报 |
| 5 | 某天出现首次回购 | **+1 行** `stage=首次回购` | **触发** → 写 `WatchHit` → A 档推送 |
| 6 | 将来 `stage=完毕` | **+1 行**（前面 5 行一条不删） | L1 下轮重算 → **摘掉这条** |

第 6 步就是 L1"自动摘"的全部含义——**摘的是待办，不是事实**。

### 2.3 顺带解释 Fetcher / Analyzer 那条分界线

分界线不是按层切的，也**不是按"客观 / 主观"切的**——那样会把"宁德该看碳酸锂指数"判给 Analyzer，是错的。

真正的分界线是**这条知识属于标的，还是属于我**：

| | 关于标的 → Fetcher / `current.sqlite` | 关于我的仓位和判断 → Analyzer |
|---|---|---|
| L0 | 事件数据（全市场都抓） | 扫描哪些票 |
| L1 | `PlanAnnouncement`、指标序列、**`StockWatchIndicator`** | 规则触发后挂/摘哪条待办、持仓层判断 |
| L2 | — | 全部 |

**"锂电池厂该看碳酸锂价"是关于标的的领域知识**——它确实是判断不是事实，但换个人来用完全一样，也跟我持不持有宁德无关。这种知识归 `current.sqlite`，还有个实际好处：它引用的两端（`StockIndustryEm`、`IndustryIndicator`）都在那个库里，查询能一条 SQL 做完。

**"我拿着宁德，论点是回购为转折点"才是关于我的**——换个人就全不一样，归 Analyzer。

所以**同一层会横跨两侧**，而且 L1 的重心在 Fetcher 侧，不在 Analyzer 侧。

---

## 3. 架构切分：抓在 Fetcher，判在 Analyzer

| 侧 | 职责 | 读 | 写 |
|---|---|---|---|
| Fetcher | 抓**原始事件数据**（方案类公告、行业指标） | 外部数据源 | `current.sqlite` |
| Analyzer | **求值观察项**、产出触发 | `current.sqlite`（只读）+ 自己的 json | 自己的 json |

两条理由：

1. **Fetcher 不知道自选股**。`watchlist.json` / `core-positions.json` 是 Analyzer 的本地状态，在 `AnalyzerPaths.BaseDir` 下，Fetcher 侧看不到也不该看到。
2. **"该盯什么"天天改，不该引起重抓**。求值放 Analyzer，改一条观察项不碰抓取计划；抓取只管把原始事实存全。

---

## 4. 数据模型

### 4.1 Fetcher 侧：`current.sqlite` 新增两张表

#### `PlanAnnouncement` — 方案类公告进展（回购/定增/重组）

```
code           TEXT    6 位码
plan_kind      TEXT    回购 / 定增 / 重组
announce_date  TEXT    公告日
stage          TEXT    方案 / 首次回购 / 进展 / 达标 / 完毕 / 终止
as_of_date     TEXT    ★ 数据截止的交易日（月度进展是"截至上月末"，≠公告日）
cum_shares     REAL    累计回购股数
cum_amount     REAL    累计回购金额（元）
price_low      REAL    区间最低成交价
price_high     REAL    区间最高成交价
pct_of_capital REAL    占总股本比例
plan_cap_price REAL    方案价格上限（stage=方案 时才有）
title          TEXT
art_code       TEXT
source_url     TEXT    巨潮 PDF 链接
fetched_at     TEXT
PRIMARY KEY (code, plan_kind, announce_date, stage)
```

设计点：

- **`stage` 必须分开存**。"方案"的金额上限和"进展"的累计金额是两个量纲，混在一列对不上账。
- **`as_of_date` 独立于 `announce_date`**。按 `feedback_trading_date_not_write_date`，月度进展公告说的是"截至上月末"，值所属交易日不是公告日。
- **失败语义＝累积**（不是快照）。一只票抓失败不影响其余，没有"整项放弃"的必要——跟 `IndustryIndicatorTask` 的"序列"那一步同类。

#### `StockWatchIndicator` — 我们自己的个股→行业指标映射

```
code         TEXT
indicator_id TEXT    指向 IndustryIndicator.indicator_id
weight       INTEGER 展示序，越小越相关
origin       TEXT    rule / manual
reason       TEXT    为什么挂（人读）
created_at   TEXT
PRIMARY KEY (code, indicator_id)
```

**⚠ 这是本设计里最容易踩的坑：绝对不能往 `StockIndustryIndicator` 里补映射。**
`SqliteIndustryIndicatorRepository.ReplaceLinks()` 里是 `DELETE FROM StockIndustryIndicator;` 后整表重写——东财目录是**快照替换**，下一轮 `IndustryIndicatorTask` 跑完，手工补的映射会被静默清空，而且界面上看不出来（只是某只票"恰好没有指标"）。

所以：**东财那张表保持原样不动**（维持快照语义），我们的映射单独一张表，查询时两张 UNION、我们的优先。

### 4.2 Analyzer 侧：`BaseDir/watch/` 目录

跟 `watchlist.json` / `core-positions.json` / `notes/` 并列，理由同 `AnalyzerPaths` 类注释——这是 Analyzer 自己的状态，不是 Fetcher 的共享只读数据。

| 文件 | 内容 |
|---|---|
| `watch/items.json` | 观察项定义（L1 派生结果缓存 + L2 手写） |
| `watch/hits-{yyyy}.json` | 触发记录，按年切 |

用 json 不用新开 sqlite：A 档触发一年也就几十条，量级跟 `trade-fees.json` 同级；Analyzer 侧已有一整套 json store 的惯例（`JsonWatchlistStore`），不值得为此引入第二个数据库文件。

`WatchItem` 字段：

```
ItemId       Guid
Code         string
Layer        string   L0 / L1 / L2
Origin       string   兜底 / 派生 / 手写      ← 边界，见下
PositionKind string   主动仓 / 底仓 / 无仓位
Kind         string   取值器类型，见 4.3
Expr         string   取值参数（如 indicator_id、metric_key）
Op           string   lt / gt / cross_down / stage_change / ...
Threshold    double?
Priority     string   A / B / C
Enabled      bool
AutoExpireOn string?  自动摘除条件（如"方案 stage=完毕"）
Thesis       string?  L2 才有：一句话论点
CreatedDate  string
```

**`Origin` 是这套东西能不能长期用下去的关键**：派生项（`origin=派生`）每轮规则重算时整组重建，手写项（`origin=手写`）**任何规则都不许碰**。这条边界不划清，规则跑一次就把人写的冲掉了——跟 3.1 那个 `ReplaceLinks` 坑是同一个病。

### 4.3 取值器（`Kind`）

| Kind | 数据来源 | 例子 |
|---|---|---|
| `PlanStage` | `PlanAnnouncement` | 回购 stage 跃迁到"首次回购" |
| `IndustryIndicator` | `IndustryIndicatorValue` | 碳酸锂指数跌破 N |
| `FinMetricQoQ` | `FinancialReport` | 单季毛利率环比再降 |
| `FinMetricRatio` | `FinancialReport` | 存货/营收 > 50% |
| `PriceMA` | `Bar` | 跌破 MA20 |
| `MarginBalance` | `MarginDetail` | 融资余额环比 |
| `HolderCount` | `ShareholderCount` | 股东户数环比降 |
| `EventTable` | `ShareLift` / `HolderChange` / `EarningsSchedule` / `EarningsForecast` / `Dividend.progress` | 解禁日临近、预告发布 |
| `Manual` | **无数据源**，到点提醒人去查 | 月度装车份额 |

#### ⚠ `IndustryIndicator` 取值器的口径坑：指标值是**自然日序列**，不是交易日序列

实测 `EMI00662659`（碳酸锂指数）：

```
09-04 周五  377.07
09-05 周六  377.07   ← 沿用
09-06 周日  377.07   ← 沿用
09-07 周一  356.69
09-09 周三  351.59
09-10 周四  351.59   ← 工作日里也常连续同值（生意社不是每天出新数）
```

所以 **"值没变" ≠ "没更新"**。求值必须：① 按交易日过滤（拿 `TradingDay` 当锚）；② 环比跟**上一个不同值**比，不是跟上一行比。直接写"较上一日跌 X%" 会在周末恒等于 0，且分不清"真没动"和"没发布"。

**`Manual` 是必要的诚实出口**，不是偷懒：有些关键变量库里确实没有（见 §7）。让它显式存在，好过假装能自动判、或者把它排除在系统之外让人忘掉。`Manual` 项只产出"该去查了"的提醒，不产出"已触发"的结论。

### 4.4 L2 与个股笔记的关系

`notes/{code}.md` 已经在承担"下次要盯什么"这类判断的留档（见 `AnalyzerPaths.NotesDir` 注释："数据能重算，判断不能"）。**不另造一套论点编辑 UI**：

- 笔记 `.md` 里写**论点全文**（给人读，能进 git、能贴表格）
- `watch/items.json` 里存**可判定条件**（给机器读），`Thesis` 只存一句话摘要
- UI 上同屏显示，不做双向同步——双向同步会立刻产生"以哪边为准"的问题

---

## 5. L1 派生规则（M1 的具体内容）

规则表配置化（JSONC，按 `feedback_config_self_documenting`）：

| 触发条件 | 挂什么 | 摘除条件 |
|---|---|---|
| 存在 `PlanAnnouncement.stage=方案` 且未完毕 | `PlanStage` 进展监控，A 档 | stage 到"完毕/终止" |
| 属于板块 BK1303（锂电池）等 | 对应 `IndustryIndicator`，B 档 | 不再属于该板块 |
| 在 `core-positions.json` 里 | 分红方案变化、股息率，B 档 | 移出底仓 |
| 在 `watchlist.json` 里 | 跌破 MA5/MA20、融资余额、龙虎榜，B 档 | 移出自选 |
| 存货/营收 连续两期上升 | `FinMetricRatio` 存货比，B 档 | 连续两期回落 |
| 行业属于银行/券商/保险 | `BankRegulatoryMetric` 相关项 | — |

板块→指标的映射表先写成配置，**不写死在代码里**——它会随认识变化频繁调整，改一条要重编译不可接受。

---

## 6. 类设计与改动清单

### 6.1 M1 的类图

**M1 全部落在 Fetcher 一侧，Analyzer 一行不改**（理由见 §2.3：这是关于标的的知识）。

```mermaid
classDiagram
  direction LR

  class WatchIndicatorLink {
    <<record · Logic.Models>>
    +string Code
    +string IndicatorId
    +int Weight
    +string Origin
    +string Reason
  }
  class WatchIndicatorOrigin {
    <<static · 硬边界>>
    +Rule = "rule"
    +Manual = "manual"
  }
  class BoardIndicatorRule {
    <<record · 一条配置规则>>
    +string BoardCode
    +string BoardName
    +IReadOnlyList~string~ IndicatorIds
    +string Reason
  }
  class WatchIndicatorRuleEngine {
    <<static · 纯计算>>
    +Build(成员, 规则, 已知指标) WatchIndicatorRuleResult
  }
  class WatchIndicatorRuleResult {
    <<record>>
    +Links
    +Warnings
  }
  class IWatchIndicatorRepository {
    <<interface>>
    +EnsureSchema()
    +ReplaceRuleLinks(links) int
    +GetLinks(code)
    +GetCounts()
  }
  class SqliteWatchIndicatorRepository {
    <<Data.Sqlite>>
    只删 origin='rule'
  }
  class WatchIndicatorRuleStore {
    <<Data.Orchestration>>
    +Read(path) rules
    +EnsureTemplate(path)
  }
  class WatchIndicatorRuleTask {
    <<Tasks · FetchTaskBase>>
    编排这一轮
  }

  WatchIndicatorRuleEngine ..> BoardIndicatorRule : 读规则
  WatchIndicatorRuleEngine ..> WatchIndicatorRuleResult : 产出
  WatchIndicatorRuleResult *-- WatchIndicatorLink
  WatchIndicatorLink ..> WatchIndicatorOrigin : Origin 取值
  SqliteWatchIndicatorRepository ..|> IWatchIndicatorRepository
  WatchIndicatorRuleTask ..> WatchIndicatorRuleEngine : 调
  WatchIndicatorRuleTask ..> WatchIndicatorRuleStore : 读配置
  WatchIndicatorRuleTask ..> IWatchIndicatorRepository : 落库
```

### 6.2 各类职责与"为什么是它"

| 类 | 项目 | 职责 | 为什么单独存在 |
|---|---|---|---|
| `WatchIndicatorLink` | Logic.Models | 一行"这票该看这指标" | 跟东财的 `StockIndicatorLink` **是两个类型**，不能复用——两者归属不同的表、不同的主人 |
| `WatchIndicatorOrigin` | Logic.Models | `rule` / `manual` 常量 | 这两个字符串散在各处写字面量，早晚拼错一个，而拼错的后果是**手挂的行被当成规则行删掉** |
| `BoardIndicatorRule` | Logic.Models | 一条配置规则 | 配置的形状要能被单测直接构造，不能只存在于 json 解析里 |
| `WatchIndicatorRuleEngine` | Logic.Services | 板块规则 → 个股行，**带校验** | 纯计算无依赖 ⇒ 可单测。**校验失败要告警不能静默跳过**，理由见下 |
| `IWatchIndicatorRepository` | Logic.Abstractions | 存取契约 | 任务只依赖接口，测试不必起 SQLite |
| `SqliteWatchIndicatorRepository` | Data.Sqlite | 实现 | **`ReplaceRuleLinks` 只删 `origin='rule'`**——整个设计的关键一行 |
| `WatchIndicatorRuleStore` | Data.Orchestration | 读 JSONC 配置 + 首次写自带说明的模板 | 按 `feedback_config_self_documenting`，跟 `FetcherSettings` 同一套路数 |
| `WatchIndicatorRuleTask` | Tasks | 编排：读板块成员 + 读指标字典 + 读规则 → 引擎 → 落库 | 按 `feedback_new_task_as_class`，不堆进 `FetchOrchestrator` |

### 6.3 数据流

```mermaid
flowchart LR
  A[("StockIndustryEm<br/>个股→板块")] --> T
  B[("IndustryIndicator<br/>指标字典 116 个")] --> T
  C["data/watch-indicator-rules.json<br/>板块→指标 规则"] --> T
  T["WatchIndicatorRuleTask<br/>+ RuleEngine"] --> D[("StockWatchIndicator<br/>只覆盖 origin=rule")]
  E[("StockIndustryIndicator<br/>东财给的 1190 行")] -.M3 查询时 UNION.-> F["Analyzer"]
  D -.M3 查询时 UNION.-> F
```

**零网络请求**：三个输入全在本地库和本地配置里。这也是 M1 能零联网验证的原因。

### 6.4 三个关键决定

**① 引擎校验失败 = 逐条跳过 + 告警，不是整项放弃。**
配置里把 `EMI00662659` 写错一个字母，结果是这条规则一行都不产出；而"没产出"跟"这个板块本来就没成分股"在库里长得一模一样——**没人会发现配置坏了**。所以指向不存在的板块/指标一律进 `Warnings` 并打进日志。

这跟【行业景气指标】那边"目录拿不全就整项放弃"是**有意的不同**：那边是快照，半批入库等于凭空少票；这边是派生，一条规则坏掉不该连累其余。

**② 同一只票被多条规则命中，保留先命中的。**
宁德同时在"电池(BK1033)"和"锂电池(BK1303)"里。配置顺序即优先级，**越靠前越具体**，后面的不覆盖前面的。

**③ `ReplaceRuleLinks` 是"只删自己那部分"的替换。**
不是 `DELETE FROM StockWatchIndicator`——那会连人手挂的一起删。这跟东财 `ReplaceLinks` 的全表替换是**故意的不同**，因为这张表有两个主人（规则和人），而东财那张只有一个。

---

### 6.5 改动清单

### Fetcher 侧

| 文件 | 动作 | 说明 |
|---|---|---|
| `SqliteSchema.cs` | 改 | 加 `PlanAnnouncement`、`StockWatchIndicator` 两张表 |
| `StockPlatform.Logic/Models/PlanAnnouncement.cs` | 新增 | 模型 |
| `StockPlatform.Logic/Abstractions/IPlanAnnouncementRepository.cs` | 新增 | |
| `StockPlatform.Data/Sqlite/SqlitePlanAnnouncementRepository.cs` | 新增 | |
| ~~`CninfoStockAnnouncementProvider.cs`~~ | **撤销** | 见 §6.6：两条现成通道够用，不新写 provider |
| `StockPlatform.Logic/Services/PlanAnnouncementExtractor.cs` | 新增 | 从正文抽 stage / 累计数 / 价格区间，参照 `OrderWinExtractor` |
| `StockPlatform.Tasks/PlanWatchTask.cs` | 新增 | 继承 `FetchTaskBase<PlanAnnouncement>` |
| `FetchActionId` | 改 | 加 `StepPlanWatch` |
| `FetchTaskCatalog.cs` | 改 | 注册一行 + `PlanGroupKind.Daily` + 执行顺序 |
| `App.xaml.cs` | 改 | DI 一行 |

**为什么新写 provider 而不复用现有那个**：按 `feedback_datasource_per_class`，一个通道一个类。两者形状不同——现有的是全市场关键词检索（一次覆盖所有票、适合日频发现），新的是按股票拉公告列表（几十个请求、可一天跑多次）。共享的 HTTP/限流/解析抽基类。

**任务形状**（对照 `IndustryIndicatorTask` 的两条铁律）：

- 一批 = 一只票的公告解析结果；水位线 = 每只票自己的 `MAX(announce_date)`。**水位线粒度（票）细于骨架截断粒度（批＝票）**，所以不需要额外的完成度表。
- 失败语义 = **累积**。单票失败不影响其余，不做"整项放弃"。

**调度**：`PlanGroupKind.Daily`，跑两次——08:15（抓前一晚+早间公告，赶在开盘前）和收盘后随日更兜底。非交易日不跑（准入归调度侧）。

### Analyzer 侧

| 文件 | 动作 |
|---|---|
| `AnalyzerPaths.cs` | 改：加 `WatchDir` |
| `Analyzer/Watchlist/WatchItem.cs`、`WatchHit.cs` | 新增：模型 |
| `Analyzer/Watchlist/JsonWatchItemStore.cs` | 新增：参照 `JsonWatchlistStore` |
| `StockPlatform.Logic/Services/WatchRuleEngine.cs` | 新增：L1 派生（挂 + 摘） |
| `StockPlatform.Logic/Services/WatchEvaluator.cs` | 新增：按 `Kind` 分发求值 |
| `ViewModels/` + 新页 | 新增：观察项列表 / 触发提醒 |

---

### 6.6 M2 修正：不新写 provider，复用两条现成通道

原计划新写一个"按股票查公告列表"的 provider。**撤销**，改用已有的两条（`OrderWinAnnouncement` 那条管线在用，已验证）：

| 步 | 复用 | 为什么 |
|---|---|---|
| 发现 | `IAnnouncementSearchProvider`（巨潮全市场标题检索，关键词「回购」） | **一次分页覆盖全市场**，比按股票查几十个请求更省，而且**更全**——自选股之外新冒出来的回购方案也能捕获 |
| 正文 | `IAnnouncementDetailFetcher`（东财，直接给纯文本） | 绕开 PDF 解析；已有实现 |

原方案基于"要一天跑多次、只盯自选股"的假设。但既然全市场一次就够，按股票查就没有存在理由了；而且新接口要沙箱验证，现成这两条已经在生产里跑着。

**⚠ 复用带来的一条硬约束：必须按天切片搜索**（2026-09-11 实机跑出来的）

`CninfoAnnouncementSearchProvider` 有 `MaxPages = 30` 的硬上限。而「回购」是高频关键词——
实测 14 天一次搜**正好撞满 30 页（285 条）就 break，剩下的静默丢掉、没有任何告警**。

```
[12:31:04] [巨潮全文检索] 关键词"回购" 第 30 页，累计 285 条命中   ← 正好停在上限
```

漏掉的公告就是漏掉的信号，而这一项的全部价值就是不漏掉「首次回购」那一条。
所以 `PlanWatchTask` 自己**按天循环调 SearchAsync**，单日只有几页、撞不到上限；
总请求数反而差不多（页数是一样的）。单日命中数接近上限时还要**再告警一次**。

这跟 `project_em_sort_key_must_be_unique` 记的是同一类病：**分页截断不报错**，
少掉的那半没有任何迹象。中标公告那条管线用的是低频关键词所以没暴露，不代表上限不存在。

### 6.7 抽取规则（拿真实公告归纳，不是猜的）

取了 4 家 5 份真实公告正文作为依据（宁德 300750、美的 000333、格力 000651）。

**`stage` 判定：标题定类型，正文定数值**

| 标题特征 | stage | 实例 |
|---|---|---|
| 含「方案」「报告书」 | `方案` | 宁德《关于回购公司股份方案的公告暨回购股份报告书》 |
| 含「首次回购」 | `首次回购` | 格力《关于以集中竞价方式首次回购股份暨回购股份进展的公告》 |
| 含「达总股本」「达到总股本」 | `达标` | 美的《…回购A股股份达总股本1%的进展公告》 |
| 含「进展」 | `进展` | 宁德/美的《…回购…进展情况的公告》 |
| 含「完毕」「实施完成」「期限届满」 | `完毕` | |
| 含「终止」 | `终止` | |

**⚠ 必须排除的干扰项**：标题形如《关于回购股份事项**前十名股东和前十名无限售条件股东持股情况**的公告》——
宁德和格力都有。它标题含「回购」、也含不了「进展」，但内容是股东名册，跟回购进度毫无关系。
不排除的话会被解析成一条各字段全空的"进展"，**看起来像回购停滞**。这是本项唯一会产生**错误结论**（而非漏数据）的坑。

**正文句式**（三种，都实测过）：

```
① 已实施（美的 2026-09-03）
截至2026年8月31日，公司通过回购专用证券账户，以集中竞价交易方式累计回购公司A股股份
数量为99,797,967股，占公司目前总股本的1.31%，最高成交价为87.71元/股，
最低成交价为73.66元/股，支付的总金额为8,019,722,850元（不含交易费用）

② 未实施（宁德 2026-09-03）
截至2026年8月31日，公司尚未实施股份回购。

③ 首次回购（格力 2026-08-19）——注意数字里有空格
公司于 2026 年 8 月 17 日至 2026 年 8 月 18 日以集中竞价方式累计回购股份数量为
495,600 股，占公司目前总股本的 0.0088%，最高成交价为 40.10 元/股，…
```

**两条从真样本才看得出来的实现约束**：

1. **数字和日期里会有空格**（格力那份：`495,600 股`、`2026 年 8 月 17 日`）——PDF 转文本的产物。
   所有正则必须 `\s*` 容忍，否则会**只对一部分公司生效**，而失败的那些看起来就像"这家没回购"。
2. **`as_of_date` 有两种写法**：「截至X日」直接取；「公司于 A 至 B」取区间末 B。

**方案公告另抽**（宁德 2026-07-25）：`回购价格上限为573元/股` → `plan_cap_price`；
`不低于人民币200亿元…不超过人民币400亿元` → 金额区间。

#### ⚠ 三家样本归纳不出全市场句式（2026-09-11 实机打脸）

上面那版正则拿宁德/美的/格力三家归纳，跑全市场 103 条真实进展公告**只抽到 18 条（17%）**，
而且 `art_code` 都有值——**正文是取到了的，是正则不匹配**。实测到的变体：

| 真实原文 | 原正则要求 | 差在哪 |
|---|---|---|
| 公司**尚未开始实施**股份回购 | `尚未(实施\|开始\|进行)(股份)?回购` | 中间多一个词 |
| **回购公司股份**206,000**股** | `累计回购…股份数量为N股` | 没有「累计…数量为」 |
| **成交总金额**为**人民币**1,863,178元 | `支付的?总金额为N元` | 换了动词、多了「人民币」 |

改法是**每条按「骨架词 + 可选修饰」写**，不把某一家的措辞当通例：
`回购(公司)?([A-Za-z]股)?股份(的)?(数量)?(为|共计|合计)?\s*N\s*股`。

**这条教训比那几条正则本身重要**：中文公告没有统一模板，靠少数样本归纳出来的抽取规则
会**静默地只对一部分公司生效**——而失败的那些在库里长得就像"这家没在回购"。
所以这类抽取必须**先跑全市场、用覆盖率当验收指标**，不能测几条样例就算过。

---

## 7. 已知缺口（设计阶段就说清，不留到实现时才发现）

| 观察项 | 状态 |
|---|---|
| 回购进展 | ✅ 可自动，本设计覆盖 |
| 碳酸锂指数 | ✅ 数据已在库（`EMI00662659`，生意社，日频），**只差映射**，零新增请求 |
| 单季毛利率环比、存货/营收 | ✅ `FinancialReport` 够算 |
| **月度动力电池装车份额** | ❌ **库里没有**。来源是中国汽车动力电池产业创新联盟月度数据，现有任何表都不含。要么新增数据源（另立设计），要么降级为 `Manual` |
| 锂电池消费税等政策节点 | ❌ 无结构化源，只能 `Manual` |

讨论阶段把"装车份额 < 45%"列成 A 档自动观察项是不成立的——**现阶段它只能是 `Manual`**。

**已排查**：`IndustryIndicator` 里中汽协那一路 12 个指标（`EMI00100216` 全国汽车销量、`EMI00225489/225497/225508/225522/1633900/1633912` 当月汽车产销量、`EMI00225598/225610` 客车产销量、`EMI00225624/225648` 汽车总产销量）**全是整车产销口径，没有一个是电池装车**。整车销量是行业总需求，跟"宁德在电池里占多少份额"是两件事——份额掉了而整车销量涨着，这个指标一点信号都不给，不能拿来顶替。

**另一条能力边界**：月频指标只有 30 行（2024-03 起），**历史只有两年半**。当环境监控够用，**做回测不够**——别拿它进 FactorLab。

### 现状确认（2026-09-11 查）

| 表 | 现状 |
|---|---|
| `IndustryIndicator` | 116 个指标：月 55 / 日 45 / 周 14 / 旬 1 / 半年 1 |
| `IndustryIndicatorValue` | 30230 行，2024-03-01 ~ 2026-09-10，116 个指标全有值 |
| `StockIndustryIndicator` | 1190 行 / 658 只票，由 `IndustryIndicatorTask` 日更 |
| `EMI00662659` 碳酸锂指数 | 891 行，生意社，日频，最新 2026-09-10 = 351.59 |

**M1 要做的只是补 `300750 → EMI00662659` 这类关系行，零新增请求。**

---

## 8. 分期与验证

| 期 | 内容 | 验证 |
|---|---|---|
| **M1** | `StockWatchIndicator` 表 + L1 板块→指标映射规则 + 配置 | 宁德挂上碳酸锂指数；**再跑一次 `IndustryIndicatorTask`，映射仍在**（证明没被 `ReplaceLinks` 冲掉）；`dotnet test` |
| **M2** | `PlanAnnouncement` + `PlanWatchTask` | Debug 数据目录（天然隔离）跑一轮，能抓到宁德 2026-07 回购方案 + 8/4、9/3 两份进展，`stage` 分得对、`as_of_date` 是"截至上月末"不是公告日 |
| **M3** | Analyzer 侧求值 + UI | 实机跑（`feedback_verify_by_running_app`）：Debug 起 Analyzer + UIAutomation 点按钮读日志 |

M1 不依赖 M2，可以单独上——数据已在库，是三期里唯一零联网就能验证的。

### M1 已完成（2026-09-11）

单测 9/9 通过（全量 1093 全过）。Debug 实机验证（UIAutomation 点【观察指标映射】的【执行】，
零联网；Debug 数据目录灌入真库的 `StockIndustryEm` 16935 条 + `IndustryIndicator` 116 个）：

| 验证 | 结果 |
|---|---|
| 规则 2 条 → 算出 **107 条映射覆盖 107 只票** | 跟离线跑真库数据的结果一字不差 |
| 宁德时代挂上 `EMI00662659` | `weight=1, reason="锂电池：碳酸锂是主要原材料成本"` |
| 一票多规则（BK1303 vs BK1033） | BK1303 赢——配置顺序即优先级 |
| 故意写错指标码 `EMI_TYPO_999` | **日志逐条告警**，且不连累其余 107 条 |
| ★ 重跑后手挂的 `origin=manual` 行 | **2 条全部活下来** |
| ★ 规则撞上手挂的同一条 | 保留手挂的（`rule` 重建为 106） |
| ★ 东财 `ReplaceLinks` 跑完我们的映射还在 | `rule=1→1`、`manual=1→1`（临时库交叉验证） |
| ★ 我们的 `ReplaceRuleLinks` 不动东财那张表 | `1→1` |
| 首次跑（模板全注释） | 产出 0 行，不改动映射 |
| 配置坏文件 | 0 条 + 告警，不抛、不清空 |

打 ★ 的四条是这套设计要躲的那个坑的实证。推理上两张表动不了彼此，
但"推理上不会"正是这类静默事故的标准开场白，所以都真跑了一遍。

### M2 / M3 已完成（2026-09-11）

单测 1162 全过（新增 69 条）。端到端实机：Debug Fetcher 抓真实公告 → Debug Analyzer 求值。

**M2 抓取**（UIAutomation 点【回购公告进展】，真连巨潮 + 东财）：

| 指标 | 结果 |
|---|---|
| 单日命中 → 真回购公告 | 61 → 10（四轮收紧：48 → 13 → 10） |
| 取到正文 | 95% |
| 进展类字段抽取率 | 股数 90% / 金额 80% / 占比 100% / 价格 80% |
| 单位换算 | 九洲药业「123.25 万股」「1,524.73 万元」→ 1,232,500 股 / 15,247,300 元 ✅ |

**M3 求值**（UIAutomation 点【观察项】→【重算并求值】）：

```
观察项 15 条（新挂 14、摘掉 0）；求值 15 条，命中 3 条

[A] 2026-08-31 宁德时代  回购方案进行中（2026-07-25 公告，价格上限 573 元）
                         → 现状：进展（尚未实施）
[A] 2026-09-11 宁德时代  【需人工核对】月度装车份额跌破 45%（库里没有这个数据源）
[B] 2026-09-10 宁德时代  碳酸锂：356.69 → 351.59（-1.4%）
```

三条正好覆盖三层：L1 派生（回购 stage）、L2 手写（Manual 诚实出口）、L1 派生（行业指标）。
触发日期是**值所属交易日**（08-31）而非公告日（09-03），口径正确。

**★ 自动摘除实机验证**——往库里插一条「完毕」公告再重算：

```
观察项 14 条（新挂 0、摘掉 1）
  PlanStage 观察项: 0 条   ← 方案完毕，自动撤下
  手写项:     1 条         ← 没被误伤
  行业指标:   1 条         ← 没被误伤
  触发记录:   3 条         ← 只增不删，摘待办不动事实
```

这是 L1 存在的全部理由：**摘的是待办，不是事实**，而且人一定会忘的那个动作由规则代劳了。

### 实机才暴露、单测测不出的四个问题（都已修）

| 问题 | 后果 | 修法 |
|---|---|---|
| 巨潮 `MaxPages=30` 静默截断 | 14 天一次搜正好撞满 285 条就 break，**剩下的没有告警** | 按天切片搜索 + 接近上限时告警 |
| 三家样本归纳的正则 | 跑全市场只抽到 **17%**，失败的在库里像"这家没在回购" | 按"骨架词+可选修饰"重写，覆盖率当验收指标 |
| 数量带「万」单位 | 抽出小 10000 倍**但看起来完全正常**的数 | `NU` 模式 + `ScaledNum` 换算 |
| `ClassifyStage` 兜底当「进展」 | 土地回购/提议回购/贷款承诺全落进进展类，**看起来像回购停滞** | 认不出明确 stage 的一律不收 |

**共同点：四个全是"不报错但结论错"的坑**，单测按自己想象的样本写永远测不到——
必须跑全市场、拿覆盖率当指标。

---

## 9. 不做什么

- **不做盘中实时**。回购没有法定盘中披露，资金流/大宗/席位都识别不出回购盘口。最快就是 T+1 开盘前，再快是假的。
- **不按事件频次反推该盯什么**。宁德近 2 年 `BlockTrade` 414 条、`Lhb` 和 `HolderChange` 各 0 条——高频的恰好最没用。
- **不做自由文本观察项**。不满足 §1 三条准入判据的写进 `notes/{code}.md`，不进系统。
- **不动 `StockIndustryIndicator`**，见 §4.1。
- **不合并 `watchlist.json` 和 `core-positions.json`**，理由见 `AnalyzerPaths.CorePositionPath` 注释。
