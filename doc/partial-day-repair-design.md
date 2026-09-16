# 残缺日检测与修补（Partial Day Repair）设计方案

> 状态：**方案待确认，未动代码**（2026-09-16）
> 起因：做资金面诊断对账时发现 `MarginDetail` 有两天只抓到沪市、深市整天缺失，
> 而程序全程无告警，之后的【首次整段回补】还会把这两天当"已有"跳过、永远补不回来。

---

## 1. 问题

### 1.1 现象

| 交易日 | 沪 | 深 | 总行数 |
|---|---|---|---|
| 2026-08-20（正常） | 1,998 | 2,102 | 4,100 |
| **2026-08-21** | 1,998 | **0** | 1,998 |
| 2026-09-01（正常） | 2,000 | 2,102 | 4,102 |
| **2026-09-02** | 2,000 | **0** | 2,000 |

2025-01-01 以来 413 个数据日里只有这 2 天，属偶发失败。受影响的是所有深市标的
（含创业板），这两天没有融资余额/融券余额/融资买入额。

### 1.2 为什么没被发现

体检 `SqliteDailyTableAuditor` 其实**已经有** `ThinDays` 判据，但两处都不够：

```csharp
/// <summary>行数低于**邻近**中位数的这个比例就算"偏少"。0.2 很宽松——只想抓半拉子轮次，不想天天报噪声。</summary>
private const double ThinRatio = 0.2;
```

1. **阈值 0.2 太松**：这两天是 1,998/4,097 ≈ **49%**，远在 20% 之上，检不出来
2. **只看总行数**：深市整个消失也只体现为"行数掉一半"，看不出是某个市场没了

### 1.3 为什么补不回来

【首次整段回补】的跳过判据是 `SqliteMarginRepository.GetTradeDates()`：

```sql
SELECT DISTINCT trade_date FROM MarginDetail
```

**只看这天有没有行，不看有多少行**。08-21 有 1,998 行 → 落进 `have` 集合 →
`DailyBackfillGate.Evaluate` 判 `DailySkipReason.AlreadyHave` → 跳过。跑多少遍都一样。

唯一能绕过的口子是"最近 5 个交易日无条件重抓"，但时间一过就关上了。
`FetchOrchestrator.cs:4330` 的注释其实已经点明这个风险：

> 最近 5 个交易日无条件重抓：两所 T+1、且当天可能只发了一半，"有行就跳过"会把残缺状态固化

防护只做到了这一层，超出窗口就失效。

### 1.4 手工也补不了

`NeedsDate` 要求模式是 `SpecificDay` 才显示日期框：

```csharp
public bool NeedsDate => Info.Params.HasFlag(FetchActionParams.Date)
                         && Model.EffectiveMode == FetchMode.SpecificDay;
```

而融资余额声明的是 `SupportedModes: Incremental | FirstBackfill`，**不含 SpecificDay**
→ 这一项在任何模式下都没有日期框。龙虎榜同样。

⚠ 连带问题：体检报告给的修复指引「龙虎榜按天重跑（模式选「增量」、日期格填那一天）」
和 catalog 里「日期格**填了**就只抓那一天」，**照着做不到**——UI 不给框，
底层 `ParseOptionalDate(item.DateText)` 永远读到空。这个不一致要一并修。

---

## 2. 为什么选这条路

考虑过两个方案：

| 方案 | 做法 | 结论 |
|---|---|---|
| A. 新增 `FetchMode.RepairThinDays = 32` | 界面多一个下拉选项，人选了才补 | 否决 |
| **B. 修体检判据 + 走现成的「只补待办」** | 体检查出残缺日 → 写进 `Todos` → `FillBacklog` 补 | **采纳** |

否决 A 的理由，用户原话：

> 人来判断是整天缺还是一天中缺一部分，本身就困难的事情。这种事情程序是最擅长的了。

而且项目里早有这条哲学，写在 `PlanItemViewModel.NeedsDate` 的注释里：

> 日常缺了哪天不该靠人去填日期补，那是【全库数据体检】的活——它查出空洞、
> 交给【重新拉取失败】去补，人不用判断缺了哪天

残缺日正是这句话描述的情况，只是体检的网眼太大漏掉了。B 还不动 `FetchMode` 枚举，
避开"计划文件加新值、旧版 exe 读不懂"的兼容问题（跟 Todos 迁移那次同一性质）。

**`FirstBackfill` 的语义保持不变**——整天缺才补，判据便宜、跑得快。
日内残缺是另一件事，由体检负责发现。

---

## 3. 判据设计（实测定参，不是拍脑袋）

### 3.1 判据一：行数偏少 —— 阈值必须按表分类

复现现有算法（邻近 10 日 trailing median + 清淡日豁免），扫全库各阈值的检出天数：

| 表 | 区间 | 交易日 | ≤0.2 | ≤0.5 | ≤0.7 | ≤0.8 |
|---|---|---|---|---|---|---|
| NetInflow | 2010-03-01~2026-09-15 | 4,022 | 0 | 0 | 0 | 0 |
| **MarginDetail** | 2010-03-31~2026-09-14 | 3,999 | **0** | **2** | **2** | 6 |
| NetInflowDetail | 2026-03-25~2026-09-15 | 120 | 0 | 0 | 0 | 0 |
| Lhb | 2004-06-25~2026-09-15 | 5,403 | 2 | 87 | **420** | 854 |
| LhbSeat | 2016-01-04~2026-09-15 | 2,601 | 0 | 4 | 77 | 264 |
| BlockTrade | 2016-01-04~2026-09-15 | 2,601 | 2 | 57 | **279** | 524 |

**结论：不能全局统一提高阈值。** 同一个 0.7 对 MarginDetail 是完美（正好那 2 天、零误报），
套到龙虎榜就炸成 420 天。0.8 还会误判 2010 年两融刚开市那几天（42 行 vs 邻近 54，
标的正在逐步纳入，是正常增长不是漏抓）。

原因是两类表的性质根本不同：

| 类型 | 表 | 行数特征 | 阈值 |
|---|---|---|---|
| **全市场快照型** | `MarginDetail` `NetInflow` `NetInflowDetail` | 每天覆盖固定的全体标的，行数稳定 | **0.7** |
| **事件型** | `Lhb` `LhbSeat` `BlockTrade` | 行数取决于当天发生多少事件，天然剧烈波动 | **0.2（维持）** |

所以 `ThinRatio` 要从常量改成 `Spec` 上的**按表字段**，而不是全局提高。

### 3.2 判据二：市场缺失 —— 比行数判据更可靠

对每张表，把出现频率 > 90% 的市场认定为"这张表每天都该有的核心市场"，再查哪些天缺了其中之一：

| 表 | 统计区间 | 天数 | 核心市场 | 缺某市场的天数 |
|---|---|---|---|---|
| **MarginDetail** | 2015 起 | 2,844 | 沪、深 | **2（正是那两天，零误报）** |
| NetInflow | 2015 起 | 2,840 | 沪、深 | **0** |
| **Lhb** | 2015 起 | 2,845 | 沪、深 | **0** |
| BlockTrade | 2015 起 | 2,601 | 沪、深 | 3 |
| NetInflowDetail | 2026 起 | 171 | 沪、深、北 | 6 |

**关键发现：龙虎榜在行数判据下误报 420 天，在市场判据下 0 误报。** 两条判据的适用范围
不重合——事件型表的行数会波动，但"沪深两市都有票上榜"这件事每天都成立。

#### 门槛 0.9 不是拟合出来的（2026-09-16 实测）

各交易所的出现频率天然分成两档，中间是巨大空隙：

| 表 | 沪 | 深 | 北 |
|---|---|---|---|
| MarginDetail | 1.000 | 0.999 | — |
| NetInflow | 1.000 | 1.000 | 0.420 |
| Lhb | 1.000 | 1.000 | 0.341 |
| LhbSeat | 1.000 | 1.000 | 0.373 |
| BlockTrade | 0.999 | 1.000 | 0.390 |

所以**门槛取 0.5 到 0.9 之间的任何值，结果完全相同**（MarginDetail 都是那 2 天、
Lhb 都是 0 天）。只有降到 0.3 才会把北交所纳入核心，然后炸出 1,585~1,875 天误报。
取 0.9 是因为它在安全区的中间偏保守端，不是因为它刚好卡住了某个数。

#### 基线为什么取全区间频率、不取滚动窗口

滚动窗口（跟"偏少日"的 trailing median 一个形状）看着更符合既有风格，实测却在事件型表上炸：

| 表 | 全区间基线 | 滚动窗口基线 |
|---|---|---|
| MarginDetail | **2 天** | 2 天 |
| Lhb | **0 天** | **14 天误报** |
| LhbSeat | **0 天** | **14 天误报** |
| BlockTrade | 2 天 | **78 天误报** |

误报全是"缺北交所"：北交所上榜/大宗本来就稀疏，连着十天有、偶尔一天没有是常态，
滚动窗口会把它当成"每天都该有"。全区间频率下北交所够不到门槛、不算核心，就没有这个问题。

**代价是小样本会自我屏蔽**：某市场要缺超过 10% 的天数，就够不到 0.9、不算核心，判据失灵。
真实的表有几千天（MarginDetail 3,999 天缺 2 天＝99.95%），够不着这个坎；
但写测试时要给足样本——5 天的日历里缺 1 天就是 80%，判据会自己把自己关掉
（实现时就踩到了，所以市场判据的测试单开了一个 40 天日历的类）。
真正的系统性缺失（某张表根本没在抓某个市场）量级完全不同，靠行数判据和空日判据去兜。

**为什么这个粒度恰好合适**：`FetchTaskCatalog.cs:891` 当初否决过更细的粒度——

> 按标的比对做不到——融资余额只有两融标的有、龙虎榜只有上榜的票有，那样必然满屏误报。

这话是对的：逐标的比对必然误报。但"总行数"和"逐标的"之间还有一档——**按市场分组**。
它比总行数细（看得出深市整个没了），又比逐标的粗（不关心具体哪只票在不在），
正好落在会误报的那条线以内。实测 0 误报印证了这一点。

两处需要处理的噪声：

- `BlockTrade` 那 3 天：2016-01-04 / 2016-01-07 是熔断日（13:34 和 9:57 就收市），
  **现有的清淡日豁免能挡掉**；2019-07-19 缺沪市，需人工确认一次
- `NetInflowDetail` 那 6 天：全部落在 2026-01~02，早于该表的有效起点（见 3.5），
  用起点过滤即可

### 3.3 样本量下限：小样本里"缺市场"是噪声（2026-09-16 实现时补）

方案落地后拿真实库一跑，市场判据在两张事件型表上炸了——**验证脚本只从 2015 年起，漏看了早期数据**：

| 表 | 加下限前 | 加下限后 |
|---|---|---|
| 龙虎榜 | **268 天** | 2 天（且都是原有的行数判据报的） |
| 大宗交易 | 4 天 | 2 天（同上） |

挖下去两类误报是同一个根子：

- **龙虎榜 2004 年全年才 223 条、全是深市中小板**（002xxx），沪市 2006 年才进来。
  那时日均 1~2 笔上榜，"这天只有深市"根本没有统计意义
- **大宗 2016-01-04（熔断，13:34 收市）只有 19 行、全是 `112xxx` 企业债**，
  `MarketClassifier` 一行都归不了类 → 沪深两个都判成"缺"。
  ⚠ 这天**挡不住清淡日豁免**：它的成交额比邻近还高 7%（原注释早就写过这件事）

所以市场判据要加一道**样本量下限**：当天能归类到交易所的标的少于 30 个就不判。
取 30 的依据是两边都离得远——出事那两天有 1,998 个可分类标的，而噪声都在个位数到二十几个，
中间那段空白取哪个值都一样。

归不了类的代码（企业债、老 B 股）**不计入样本量**，否则 2016-01-04 会被当成"样本够多但沪深都缺"。

### 3.4 市场判定走 MarketClassifier

验证脚本里用的是临时的前缀规则，**产品代码必须走 `MarketClassifier`**——
920 是北交所，provider 自写前缀规则曾害得 342 只票静默抓不到
（见 `feedback_market_prefix_via_classifier`）。

### 3.5 起点过滤：覆盖未达标期不参与判定

`NetInflowDetail` 全表 `MIN(trade_date)` 是 2025-11-14，但那天**只有 1 只票**——
零星数据，不是可用起点。真实的全市场覆盖从 2026-03-13（1,526 只）起，03-16 才 5,464 只。

判定起点应取**「覆盖只数达到全量 80% 的第一天」**，而不是 `MIN`。否则零星期的每一天
都会被两条判据同时判成残缺，报出一大堆无意义的告警。

（这条与 `doc/capital-diagnosis-design.md` 第 2 节是同一个陷阱，两处应复用同一段判定逻辑。）

---

## 4. 程序设计

改动分四层，每层可独立验收。⚠ **前置约束（2026-09-16 查证补充）**：
`RetryTaskIds` 里**没有**两融/龙虎榜/大宗交易，这几张表的任务从没接入过待办体系，
`SupportedModes` 也不含 `FillBacklog`。所以"走现成的只补待办"之前，得先把它们接进去（见 4.3）。

---

### 4.1 判据层 —— `SqliteDailyTableAuditor`

#### Spec 扩展三个字段

```csharp
public sealed record Spec(
    string Table, string DateColumn, string Label, string HowToFill,
    int LagDays = 0, int WindowDays = 0,
    // ↓ 新增
    double ThinRatio = 0.2,        // 行数偏少阈值。快照型 0.7、事件型 0.2（实测依据见 §3.1）
    bool CheckMarkets = false,     // 是否启用市场缺失判据
    string CodeColumn = "code",    // 市场判据要读的代码列
    double CoverageFloor = 0);     // 起点过滤：覆盖率达到这个比例才开始判（0＝不启用）
```

`ThinRatio` 从 `private const double` 改为 Spec 字段，**原常量删除**（避免两个真相）。
`ThinWindowRadius` / `ThinMinSamples` / `QuietRatio` / `QuietWindowRadius` 维持全局常量不动。

#### 表清单配置

| 表 | ThinRatio | CheckMarkets | CodeColumn | CoverageFloor |
|---|---|---|---|---|
| `NetInflow` | 0.7 | true | code | 0 |
| `MarginDetail` | 0.7 | true | code | 0 |
| `NetInflowDetail` | 0.7 | true | code | **0.8** |
| `Lhb` | 0.2 | true | **stock_code** | 0 |
| `LhbSeat` | 0.2 | true | code | 0 |
| `BlockTrade` | 0.2 | true | code | 0 |

⚠ `BlockTrade` 归属的是 **`FetchMarketEvents`（拉取市场事件）**，那是个**复合任务**
（大宗交易 / 机构调研 / 限售解禁 / 股东增减持 四张表）。残缺日待办挂在这个 taskId 名下，
但补的时候只该重抓大宗那一块，不要把另外三张一起拖下水——`fetchOne` 表里给它单配一个
只打大宗接口的委托。另外三张是按公告出的（某天一条都没有很正常），本来就不在体检清单里。

⚠ `Lhb` 的代码列是 `stock_code`。`SafeIdent` 校验要把 `CodeColumn` 也纳入
（现在只校验 `Table` 和 `DateColumn`）。

#### 新增：按日读市场集合

```csharp
/// 每个交易日出现了哪些市场。只有 CheckMarkets 的表才查——多一次全表 GROUP BY。
private static Dictionary<string, HashSet<Market>> ReadMarketsByDay(SqliteConnection conn, Spec spec)
```

SQL 是 `SELECT {DateColumn}, {CodeColumn} FROM {Table}`，逐行过 `MarketClassifier`
归类。**不要在 SQL 里写 `substr(code,1,2)` 之类的前缀规则**——920 是北交所，
provider 自写前缀曾害得 342 只票静默抓不到（`feedback_market_prefix_via_classifier`）。

#### 新增：核心市场基线

```csharp
/// 出现频率 ≥ CoreMarketFreq 的市场，视为"这张表每天都该有"。
private const double CoreMarketFreq = 0.9;
```

基线**从数据自己算**，不写死"沪深"——北交所是后来才有的，写死会让 2021 年以前
全部误报；`NetInflowDetail` 实测核心市场是沪深北三个，而 2015 年的表只有沪深。

#### 新增：起点过滤

```csharp
/// 覆盖只数达到全量 CoverageFloor 的第一天。0 表示不启用，退回 tableMin。
private static string CoverageLowerBound(SqliteConnection conn, Spec spec, string tableMin)
```

`NetInflowDetail` 全表 `MIN` 是 2025-11-14 但那天只有 1 只票。实测 2026-03-13 才 1,526 只、
03-16 才 5,464 只。不过滤的话零星期每天都会被两条判据同时判成残缺。

与 `WindowLowerBound` 的关系：两者都抬下界，取**较晚**的那个。

#### Result 扩展

```csharp
public enum PartialReason { ThinRows, MissingMarket }   // 可同时命中，用 Flags

public sealed record PartialDay(
    DateTime Day, int Rows, int Nearby, Market[] MissingMarkets, PartialReason Reason);

public sealed record Result(
    Spec Spec, DateTime From, DateTime To, int TradingDays,
    List<DateTime> EmptyDays,
    List<PartialDay> PartialDays,        // ← 取代原 List<(DateTime, int)> ThinDays
    int MedianRows, List<DateTime> TailMissingDays);
```

`ThinDays` 直接改名为 `PartialDays` 并换类型，不保留旧字段——它只有两个使用点
（`FullAuditTask.cs:479` 和 `:493`），没有兼容包袱。

#### 判定循环

现有的 `for` 循环里，`thin.Add(...)` 那段改成：

```
空日  → empty，continue（不变）
否则：
  reason = 0
  if (nearby > 0 && n < nearby * spec.ThinRatio && !IsQuietDay(...)) reason |= ThinRows
  if (spec.CheckMarkets && 缺核心市场 && !IsQuietDay(...))            reason |= MissingMarket
  if (reason != 0) partial.Add(new PartialDay(day, n, nearby, missing, reason))
```

**清淡日豁免两条判据都要过**——熔断日（2016-01-04 13:34 收市、01-07 9:57 收市）
既会行数偏少，也可能整个市场没有大宗交易，实测 `BlockTrade` 那 3 天里有 2 天是这种。

---

### 4.2 报告层 —— `FullAuditTask`

`CheckDailyTables` 里那段（约 470-500 行）：

```csharp
// 旧
if (r.ThinDays.Count > 0)
    parts.Add($"{r.ThinDays.Count} 天行数明显偏少、疑似只抓了一半（{FormatDays(...)}）");

// 新：分因由报，人才知道要不要去查数据源
if (r.PartialDays.Count > 0)
{
    var mkt  = r.PartialDays.Where(d => d.Reason.HasFlag(MissingMarket)).ToList();
    var thin = r.PartialDays.Where(d => d.Reason == ThinRows).ToList();
    if (mkt.Count  > 0) parts.Add($"{mkt.Count} 天缺整个市场（{...}，缺 {市场名}）");
    if (thin.Count > 0) parts.Add($"{thin.Count} 天行数明显偏少（{...}）");
}
```

`r.EmptyDays.Count == 0 && r.ThinDays.Count == 0 && ...` 那个"齐"的判断同步改字段名。

---

### 4.3 待办层

#### 新增 Kind

```csharp
/// 这一天有行但不全（某个市场整天没有，或行数明显偏少）。
/// ⚠ 不能并进 MissingDays：那条的复查判据是 COUNT == 0，残缺日本来就有行，
///   一复查就被判"已补齐"静默划掉（理由详见本节上文）。
public const string PartialDay = "partial_day";
```

#### 新增 TaskId

`RetryTaskIds` 补四个常量（值＝`FetchActionId` 的枚举名，`FillBacklog` 分派就是靠这个串起来的）：

```csharp
public const string Margin       = "StepMargin";
public const string Lhb          = "StepLhb";
public const string LhbSeat      = "FetchLhbSeat";      // ⚠ 是 Fetch 前缀不是 Step
public const string MarketEvents = "FetchMarketEvents"; // 大宗交易归这一项
```

#### Spec → TaskId 映射

放 `SqliteDailyTableAuditor.Spec` 上（多一个 `OwnerTaskId` 字段）还是放 `FullAuditTask`
的查表里，二选一。**建议放 Spec** ——它已经承载了 `HowToFill` 这种"该跑哪一项"的信息，
两处放会分叉。

#### 写待办

`QueueMissingNetInflowDays` 现在写死只服务 NetInflow 的 EmptyDays。推广成：

```csharp
/// 把体检查出的残缺日记进待办。各表记到各自的任务名下。
/// thorough=true 时连"确认没有"名单一起清空重来（跟现有语义一致）。
private int QueuePartialDays(SqliteDailyTableAuditor.Spec spec, List<PartialDay> days, bool thorough)
```

保留原 `QueueMissingNetInflowDays` 不动（它管的是 EmptyDays，是另一条线）。

#### 显示标签

`RetryBacklog.Describe` 现在是 `(RetryTodoKind.MissingDays, _) => (..., "资金流缺失日", ...)`
——**标签写死**。新增分支必须按 TaskId 分，否则两融的残缺日会显示成"资金流缺失日"：

```csharp
(RetryTodoKind.PartialDay, RetryTaskIds.Margin)     => (RetryKind.PartialDay, "两融残缺日", "天", true),
(RetryTodoKind.PartialDay, RetryTaskIds.Lhb)        => (RetryKind.PartialDay, "龙虎榜残缺日", "天", true),
(RetryTodoKind.PartialDay, _)                       => (RetryKind.PartialDay, "残缺日", "天", true),
```

---

### 4.4 补数层

#### catalog 开模式

`StepMargin` / `StepLhb` / 市场事件那几项的 `SupportedModes` 加上 `FetchMode.FillBacklog`。
不加的话界面上选不到，`RunFillBacklogAsync` 也不会被这几项调用。

#### 分派

`RunFillBacklogAsync` 末尾，现有的 MissingDays 分支旁边加一条：

```csharp
// ── 整天缺失的日子（资金净流入）──
if (manifest.Todo(taskId, RetryTodoKind.MissingDays) is { Targets.Count: > 0 })
    await FillMissingNetInflowDaysAsync(done, progress, ct);

// ── 残缺日（有行但不全）──                                    ← 新增
if (manifest.Todo(taskId, RetryTodoKind.PartialDay) is { Targets.Count: > 0 })
    await FillPartialDaysAsync(taskId, done, progress, ct);
```

`RunFillBacklogAsync` 的 `source` / `currentRepo` 两个参数用不上，跟现有
`FillMissingNetInflowDaysAsync` 一样不传即可。

#### `FillPartialDaysAsync` 的形状

```csharp
private async Task FillPartialDaysAsync(
    string taskId, List<string> done, IProgress<string>? progress, CancellationToken ct)
```

1. 认领 `Todo(taskId, PartialDay)` 的 Targets，取 `Day` 去重排序
2. 按 taskId 找到"按天重抓"的委托——**复用 `RunStepBackfillDailyOneAsync` 里那个
   `fetchOne` 的形状** `Func<DateOnly, Task<int>>`，抽成一张
   `taskId → fetchOne` 的表，两处共用，避免两份重抓逻辑漂移
3. 逐天重抓，写入走各 repository 的 `InsertOrIgnore`（已有行不动、只补缺的行，天然幂等）
4. **复查（关键）**——见下

#### 复查必须复用体检判据

这是本方案存在的理由，写错就等于没做：

```csharp
// ✗ 绝对不能这样（这正是 MissingDays 的做法，残缺日会被立刻判"已补齐"）
var stillBad = days.Where(d => repo.CountRowsByDay(d) == 0);

// ✓ 重新跑一次体检的两条判据，只对这几天
var recheck = auditor.CheckDays(spec, days);        // 新增一个只查指定日期的轻量入口
var stillBad = recheck.Where(d => d.Reason != 0).ToList();
```

为此 `SqliteDailyTableAuditor` 要开一个 `CheckDays(Spec, IEnumerable<DateTime>)` 入口：
逻辑跟 `Check` 里的判定循环完全一样，只是不扫全表、只算这几天（邻近中位数仍需取
它们前面 10 个交易日，所以还是要读一段行数序列，但不用全量）。

#### 收敛

沿用 `RetryTarget.Tries` + `AuditMaxTries`：补满仍不齐就判"数据源确实没有"、不再报
（`BlockTrade` 2019-07-19 缺沪市那种，可能源上当天就没有）。

⚠ **白名单不能共用 `manifest.ConfirmedNetInflowDays`**——那是资金流专用的。
按表各存一份，或改成 `Dictionary<taskId, List<DateTime>>`。

---

### 4.5 整段回补要能识别残缺日

`RunStepBackfillDailyOneAsync` 的 `haveDates` 扣除已知残缺日：

```csharp
// 旧：ct2 => _marginRepository.GetTradeDates()
// 新：
ct2 => _marginRepository.GetTradeDates()
        .Except(PartialDaysOf(RetryTaskIds.Margin))
        .ToHashSet()
```

不改的话，补完之后再跑【首次整段回补】，这些天仍会被 `DailySkipReason.AlreadyHave` 跳过
——两条路径对残缺日的判断必须一致。

---

### 4.6 顺手修掉的不一致

`MarginDetail` / `Lhb` 的 catalog 说明和体检修复指引都写着「日期格填了就只抓那一天」，
但 `NeedsDate` 要求 `SpecificDay`、而这两项不声明该模式 → **UI 不给框，照着做不到**。
二选一（见 §7 未决问题 3）：给这两项加 `SpecificDay`，或改掉那些文字。

---

### 4.7 改动文件清单

| 文件 | 改什么 |
|---|---|
| `SqliteDailyTableAuditor.cs` | Spec 加 4 字段、删 `ThinRatio` 常量、加 `ReadMarketsByDay` / 核心市场基线 / `CoverageLowerBound` / `CheckDays`、`Result.ThinDays`→`PartialDays`、判定循环加第二条判据 |
| `FullAuditTask.cs` | 报告分因由；`QueuePartialDays` 推广写待办 |
| `RetryTodo.cs` | 加 `RetryTodoKind.PartialDay`、`RetryTaskIds` 四个常量 |
| `RetryBacklog.cs` | `Describe` 加 PartialDay 分支（标签按 TaskId 分） |
| `Manifest.cs` | 残缺日的 confirmed 名单（不复用 `ConfirmedNetInflowDays`） |
| `FetchOrchestrator.cs` | `RunFillBacklogAsync` 加分支、`FillPartialDaysAsync`、`taskId → fetchOne` 表、`RunStepBackfillDailyOneAsync` 的 have 扣除残缺日 |
| `FetchTaskCatalog.cs` | 四项加 `FillBacklog` 模式；§4.6 那条二选一；**【全库数据体检】那段说明文字要改**——现在写的是"行数不到平时两成、疑似只抓了一半"，判据变了 |
| `StockPlatform.Tests` | 见 §5 |

按 `feedback_new_task_as_class`：本次**不新增任务**，全是在既有判据/待办/分派链路上扩展，
所以不涉及"新任务写成独立类"那条。

---

## 5. 实现结果（2026-09-16）

产品代码已实现，`dotnet build` 全绿、单测 1471 通过。拿**真实库**（只读探针，见 §8）跑一遍：

| 表 | 空日 | 残缺日 | 判定 |
|---|---|---|---|
| 资金净流入 | 9 | 0 | — |
| **融资余额** | 0 | **2** | **2026-08-21 / 09-02「行数1998/邻近4097，缺深市」** ✅ 目标命中 |
| 龙虎榜 | 244 | 2 | 2005-11-14/18「行数1/邻近6」——原有行数判据，与改动前一致 |
| 资金流明细(东财) | 0 | 0 | CoverageFloor 生效 |
| 龙虎榜席位(东财) | 0 | 0 | — |
| 大宗交易(东财) | 0 | 2 | 2020-01-23/02-03（疫情初期）——原有行数判据，与改动前一致 |

**市场判据新增的检出就是融资余额那两天，其余表一天没多。**

---

## 6. 验收标准

1. 体检能报出 `MarginDetail` 的 2026-08-21 和 2026-09-02，且**不新增误报**
   （各表检出天数应与第 3 节的表格一致）
2. 龙虎榜维持 0.2 阈值，检出天数不因本次改动而变化
3. 跑一次【重新拉取失败】能把这两天的深市数据补齐，补后行数达到 4,100 量级
4. 补齐后再跑体检，这两天不再被报
5. 补齐后跑【首次整段回补】，不再把这两天算作"已有"而跳过（§4.5 生效）

### 单元测试（`StockPlatform.Tests`）

现有 `DailyTableAuditTests.cs` 要扩，新增这几条：

| 用例 | 断言 |
|---|---|
| 行数判据按表取阈值 | 同一组数据，`ThinRatio=0.7` 的 Spec 报出、`0.2` 的不报 |
| 市场缺失判据 | 造一天只有沪市的数据 → 命中 `MissingMarket`，`MissingMarkets` 含深 |
| 核心市场从数据算 | 早期只有沪深的表，不因为没有北交所就误报 |
| 清淡日豁免对两条判据都生效 | 熔断日那种低成交额日，行数少且缺市场，都不报 |
| 起点过滤 | `CoverageFloor=0.8` 时，覆盖未达标那段不参与判定 |
| **复查不能用 COUNT>0** | 残缺日重抓后仍缺深市 → `CheckDays` 仍判命中，**不得**被划掉 |
| Kind 不混用 | `PartialDay` 与 `MissingDays` 各自独立收敛，白名单不串 |

最后一条是本方案的核心，必须有测试守住——它正是不复用 `MissingDays` 的理由。

按 `project_tests`：改了 FetchPlan / PlanRunner 要跑 `dotnet test`。本次改到了
`FetchTaskCatalog`（SupportedModes）和分派链路，属于要跑的范围。

### 实机验证

按 `feedback_verify_by_running_app`，单测测不出调用环路和 UI 链，必须：

1. Debug 起 Fetcher（数据目录天然隔离，见 `project_debug_run_verify`）
2. UIAutomation 点【全库数据体检】，读日志确认报出那两天且措辞正确
3. 确认【融资余额】那一行的模式下拉里出现了「只补待办」
4. 点它，确认真的去抓了、补完复查没有被误划掉

---

## 7. 影响面与风险

- **同类问题不止两融**：`LhbSeat` / `BlockTrade` / `NetInflowDetail` 共用同一套判据，
  都是"有行就算有"，本次一并覆盖
- **事件型表的市场判据**要留人工确认口子：`BlockTrade` 2019-07-19 缺沪市这类，
  可能是数据源当天真没有，补两轮拿不到应移进"确认没有"名单收敛
  （复用 `RetryTarget.Tries` 的既有机制，避免年复一年重复报）
- **不改 `FirstBackfill` 语义**，风险集中在体检侧和待办侧，抓取主流程不动

---

## 8. 未决问题

1. ~~`BlockTrade` 2019-07-19 缺沪市~~ —— 已消解：那天 182 行里 138 个是债券代码，
   去重后可分类的标的不足 30 个，被样本量下限挡掉了（见 §3.3）
2. ~~新增 `RetryTodoKind.PartialDay` 属不可逆迁移~~ —— 用户 2026-09-16 确认接受
3. ~~`SpecificDay` 二选一~~ —— 用户 2026-09-16 定为**加模式**，已实现

---

## 9. 怎么复查

定阈值时用过一批一次性脚本（复现判据扫全库、对照不同阈值/基线的检出量），**跑完即弃、不留仓库**。
它们产出的结论已经固化在三处，要复查看这三处就够：

1. **§3 各表的实测数据** —— 阈值和基线的选择依据，含"0.5~0.9 取哪个都一样"这类敏感性结论
2. **单元测试** —— `DailyTableMarketGapTests`（市场判据 7 条，40 天日历）、
   `DailyTableAuditTests`（行数判据与起点过滤）、`FetchTaskCatalogTests`（模式与 taskId 对得上枚举）。
   这才是长期守着的那道防线
3. **§5 真实库的检出结果** —— 拿真库跑一遍 `SqliteDailyTableAuditor.Check` 的输出

要在真实库上再跑一次第 3 项：写个十几行的控制台程序引用 `StockPlatform.Data`，
遍历 `SqliteDailyTableAuditor.DailyTables` 调 `Check(spec, MarketIndexCatalog.ShanghaiCompositeSymbol, cutoff)`
打印 `PartialDays` 即可（纯查询、不写库）。**写在 scratchpad，别落进 publish 或项目根。**
