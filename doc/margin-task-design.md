# 融资余额迁到新任务框架（2026-09-18 设计）

## 0. 这是什么

`DailyRefetcherFor` 里**最后一个 case**。两融迁完，那个方法整个消失，
"待办归谁补"对所有日频项就只剩一个答案：归它自己
（上一步见 doc/fill-backlog-to-tasks-design.md）。

顺带能删掉的：`RunStepBackfillDailyOneAsync`（龙虎榜 09-17 迁走后，两融是它唯一的用户）、
`FetchMarginRecentAsync`、`RunStepMarginRecentAsync`、`RunStepBackfillMarginAsync`，
以及一个**已经没有任何调用方**的 `RunFetchMarginAsync`。

## 1. 现状：四个入口 + 一个死的

| # | 入口 | 排期从哪来 | 落库 |
|---|---|---|---|
| ① | `RunStepMarginRecentAsync` → `FetchMarginRecentAsync` | 以某天为终点回看 10 个交易日，四道闸过滤 | `InsertOrIgnore` |
| ② | `RunStepBackfillMarginAsync` → `RunStepBackfillDailyOneAsync` → `BackfillDailyAsync` | 数据起点 → 今天，扣掉已有和**已知残缺日** | 同上 |
| ③ | `DailyRefetcherFor` 的 `Margin` 分支（补残缺日） | 待办清单 | 同上 |
| ④ | 【拉取历史区间】里的两融那半边 → `BackfillDailyAsync` | 调用方给的年份区间 | 同上 |
| ⑤ | `RunFetchMarginAsync`（按区间） | — | **没有任何调用方，死代码** |

④ 是**另一个功能**（拉取历史区间同时补 K线/资金流/龙虎榜/两融），不迁、不删，
只把落库换成共用的 writer。⑤ 直接删——留着就是留个陷阱。

## 2. 跟龙虎榜/席位的三点不同（所以不能照抄那两份设计）

**① 落库是 `InsertOrIgnore` 合并，不是整日替换。**
于是那两张表最要命的"抓不全就整天不落库"在这里**不成立**：两融抓到半天也该写，
主键去重，下轮补另一半。`PartialDayRepair` 的进度文案里已经写明这个差别，别改它。

**② 有空日名单**（`IDailyFetchNoDataRepository.MarginDataset`）：
0 行且**是 3 天以前**才敢定案（两所 T+1，当天/昨天的空多半只是还没发布）；
后来有数据了要把结论撤销。这套判据现在**写了两份**（`FetchMarginRecentAsync` 一份、
`BackfillDailyAsync` 一份），实质相同。

**③ 增量有"最近 5 个交易日无条件重抓"。**
两所分批发布，早抓到的可能只是一部分，"有行就跳过"会把残缺状态永久固化
（2026-09-08 定的，目录条目里写着）。这条**必须原样带过去**。

## 3. 形状

### 3.1 `MarginDayWriter`——保持纯粹，不管空日名单

```csharp
public sealed class MarginDayWriter(IMarginProvider provider, IMarginRepository repository, object? dbLock = null)
{
    /// <summary>抓一天、合并落库，返回写入行数。0 行＝那天数据源上没有。</summary>
    public async Task<int> RefetchAsync(DateOnly day, CancellationToken ct = default);
}
```

照 `LhbSeatDayWriter` / `BlockTradeDayWriter` 的先例（可选 `dbLock`：编排器传自己那把，任务传 null）。

⚠ **空日名单的 confirm/remove 不进 writer**。理由：它是**排期**的一部分而不是落库的一部分
（四道闸的闸③读的就是这份名单），而入口④走的是通用的 `BackfillDailyAsync`——
它对龙虎榜也做同一件事。塞进 writer 会让④对两融**做两遍**。

### 3.2 判据抽成纯函数，别再写第三份

两份实质相同的 confirm/remove 判据，抽成 `StockPlatform.Logic.Services` 的纯函数，
跟 `DailyBackfillGate` 同层同风格：

```csharp
public enum NoDataAction { None, Confirm, Revoke }
public static NoDataAction DailyNoDataGate.Evaluate(int rows, DateOnly day, DateOnly today, bool alreadyConfirmed);
```

`BackfillDailyAsync` 和 `MarginTask` 各调它一次。**不迁**龙虎榜那半边的调用
（它在④里，本次不碰），但函数本身是通用的，以后谁用都行。

### 3.3 `MarginTask`

| 模式 | 这一项的含义 |
|---|---|
| `Incremental`（默认） | 以日期格那天（留空＝今天）为终点回看 10 个交易日，四道闸过滤，**最近 5 个交易日无条件重抓** |
| `SpecificDay` | 只抓那一天，绕过"本地已有"闸（人点名要重查） |
| `FirstBackfill` | 数据起点（2010-03-31）→ 今天，扣掉已有和已知残缺日，幂等可反复跑 |
| `FillBacklog` | `PartialDayRepair` + `MarginDayWriter`，跟上一步那三项同一个形状 |

排期复用 `DailyBackfillGate.Evaluate`（Logic 层纯函数，已经是共享的），
日历走 `ITradingDayRepository` → `TradingCalendar`，跟 `LhbTask` 一样。
`HandlesBacklog => true`。

一批＝**一天**，于是 `MaxItems`（本轮最多抓几天）和 `Deadline`（到点收尾）是骨架白送的——
整段回补约 3900 个交易日，最需要的正是这两件事（现在这条路一跑就是一整轮，停了只能重来）。

⚠ 排期要查库（水位线、日历、空日名单），都是同步 IO，**首个 await 之前必须自己包 `Task.Run`**，
骨架不替子类推线程池。

## 4. 改动清单

### 新增
- `src/StockPlatform.Tasks/MarginTask.cs`
- `src/StockPlatform.Data/Orchestration/MarginDayWriter.cs`
- `src/StockPlatform.Logic/Services/DailyNoDataGate.cs`
- `App.xaml.cs`：注册一行

### 改
- `FetchOrchestrator`：删 `FetchMarginRecentAsync`、`RunFetchMarginAsync`（死代码）、
  `DailyRefetcherFor` **整个方法**（最后一个 case 没了）、`FillPartialDaysAsync` 里对它的调用；
  入口④的落库换成 `MarginDayWriter`；`BackfillDailyAsync` 的 confirm/remove 换成 `DailyNoDataGate`
- `FetchOrchestrator.Steps.cs`：删 `RunStepMarginRecentAsync`、`RunStepBackfillMarginAsync`、
  `RunStepBackfillDailyOneAsync`（没有用户了）
- `MainViewModel`：删 `case FetchActionId.StepMargin`
- `FetchTaskCatalog` 的 `StepMargin` 条目：模式不变（四个都已支持），Note 补一句口径统一

### 不动
- manifest 键 `StepMargin`、空日名单的 dataset 名、`ConfirmedPartialDays` 的键
- `DailyBackfillGate`、`PartialDayRepair`、入口④本身
- 目录里的依赖关系（`SoftDependsOn: StepTradingCalendar`）

## 5. 风险

**5.1 "最近 5 个交易日无条件重抓"最容易在重构里被简化掉**——它看起来像是多余的重复劳动。
丢了它，两所分批发布的残缺会被永久固化（有行就跳过）。单测要专门锁一条。

**5.2 空日名单的 3 天 cutoff 同理**：写成"0 行就定案"的话，当天抓不到就把那天永久钉死，
而两所是 T+1。

**5.3 入口④的行为不能变**：它和任务共用 writer 之后，落库口径一致；
但它的排期、日志、空日处理都还在 `BackfillDailyAsync` 里，不要顺手"统一"过去。

**5.4 `DailyRefetcherFor` 删掉之后**，`FillPartialDaysAsync` 只剩"没有按天重抓入口"这一条路——
它对**所有**未迁任务都会报"只能人工处理"。现在除了两融本来就没有别的 taskId 走到那儿
（体检的 `DailyTables` 里只有这五张表），但删之前要再确认一遍。

## 6. 验证

1. `dotnet test`；新增单测：回看窗口与四道闸（含"最近 5 天无条件重抓"）、空日 cutoff、
   `FillBacklog` 走待办（照 `LhbBacklogTaskTests` 的形状，离线假 provider）。
2. **数据等价**：迁移前后各跑一次增量，`MarginDetail` 行数与内容逐行相同。
3. Debug 实机（数据目录天然隔离）：
   - 四个模式各跑一次，日期格在「只抓某一天」时要出现；
   - `MaxItems` 分两轮跑整段回补，第二轮接得上；
   - 【重新拉取失败】里两融的残缺日显示「（任务自补）」——这是这次改动的直接证据；
   - 【拉取历史区间】跑一小段，确认入口④照旧。
4. 回归：上一步那三项（龙虎榜/席位/大宗）的残缺日行为不变。

## 7. 不做的事

- **不迁入口④**（【拉取历史区间】）——那是另一个功能，牵扯 K线/资金流/公告一整套。
- **不换数据源**：交易所官方源是最权威的，`doc/margin-fields-design.md` 刚把三个字段补全，不动。
- **不改并发**：两融是一天一个请求、串行，现状即此。

---

## 8. 实现记录（2026-09-18 落地时的偏差）

**① 整段回补的起点从"本地K线最早日"改成数据源起点。**
原来走通用的 `RunStepBackfillDailyOneAsync`，它拿 `GetOverallEarliestPeriodStart(Granularity.Day)`
当起点（本地还没有K线时直接抛异常），再由 `BackfillDailyAsync` 上提到 `EarliestAvailable`。
两融跟K线没有任何关系，这个耦合是"沿用通用回补器"的副作用——目录条目里写的本来就是
"从 2010-03-31 一路补到今天"。生产库K线从 1990 年就有，两者等价；但空库不再抛异常。

**② 顺带删了 `PartialDaysOf`**：它唯一的用户是两融整段回补，跟着搬进了任务
（"整段回补要把已知残缺日从 have 里扣掉，否则那些天会被当成'已有'永远跳过"这条规矩原样带过去了）。

**③ `FillPartialDaysAsync` 的 `refetch` 固定传 null。**
`DailyRefetcherFor` 删掉之后，**所有**有"按天重抓"能力的日频项都自己补待办了，
分派在转交那一步就走掉、到不了这儿。还能走到的只剩**资金净流入**——它配了 `OwnerTaskId`
（会产生残缺日待办），但从来就没有按天重抓的入口（`DailyRefetcherFor` 里原本也没有它的分支），
所以 `PartialDayRepair` 会报一句"只能人工处理"。那正是要的。

> ⚠ 这一条最初写的是"分档资金流"，**举错了例子**（2026-09-18 实机验证时发现并改正）：
> `NetInflowDetail` 那条 spec **故意不配 `OwnerTaskId`**（"这张表只报不补"，补一天要走
> push2his 逐股 5500 个请求），所以它根本不会产生残缺日待办；手工塞一条进去，
> `PartialDayRepair` 也是因为 `spec == null` 静默返回，什么都不报。
> 真正会走到那条路、并且确实报出警告的是**资金净流入**（`StepNetInflow`）。

**④ 差点误删 `RunStepFillProbeFloorAsync`**：它在 `Steps.cs` 里正好夹在
`RunStepMarginRecentAsync` 和 `RunStepBackfillMarginAsync` 之间。按"从这个方法删到那个方法"
的范围一刀切会把它带走，编译期才发现。两融的两段要分别删。

## 9. 实机验证（2026-09-18，Debug 实例，**全程离线模拟、零网络请求**）

数据目录在 `bin/.../win-x64/data/local`，跟生产库天然隔离；起跑前把 `MarginDetail` 和
空日名单清空当干净起点。全程走界面（UIAutomation 点按钮）。

### 9.1 先补齐了离线模拟源

> 这套设施的完整说明书见 **`doc/offline-mock-design.md`**（开关、现有模拟源、
> 哪些源还没有、怎么加新的、验证前后的注意事项）。这里只记这一轮补了什么。

`MockBarFetcher` 只覆盖K线（`BarSource: "Mock"`），别的源不受它影响。照同一先例补了三个：

| 模拟源 | 顶替 | 造出来的数据 |
|---|---|---|
| `MockMarginProvider` | 交易所两融 | 每个工作日 10 行，沪深各一半，余额 111111 起 |
| `MockNetInflowFetcher` | 新浪资金净流入 | 区间里每个工作日 1 行，净流入 1111111 |
| `MockLhbProvider` | 东财/新浪龙虎榜 | 每个交易日 4 行，沪深各一半 |

开关是**一个总开关** `"OfflineMock": "true"`，不是每类一个——逐类去配必然漏，
2026-09-18 验【拉取历史区间】时就漏过、当场发出了真请求。K线仍走既有的 `BarSource: "Mock"`。

**两道闸跟 `MockBarFetcher` 完全一样**：只在 DEBUG 构建里认这个值（Release 里那段代码不存在），
加上 Debug 数据目录天然隔离。

两融和龙虎榜的模拟源都默认**当天返回 0 行**（模拟 T+1 / 当晚发布）——
不这样的话"今天的空不该定案"那条判据根本验不到。

`MockLhbProvider` **故意不实现 `ILhbRangeProvider`**：实现了 `LhbTask` 就会走按月切片，
而【拉取历史区间】和补残缺日走的都是逐日路径，要模拟的正是后者。

它默认模拟 **T+1**（今天返回 0 行）——不这样的话"今天的空不该定案"那条判据根本验不到。

验证期间 `BarSource` 也一并切成 `Mock`：点错行时不至于发出真请求（第一次就点错过一次，
跑成了【指数日K】、发了 9 个真请求，随后才补上这道保险）。

### 9.2 验证结果

| # | 验的是什么 | 结果 |
|---|---|---|
| 1 | 增量跑一轮（空库） | 15 天里抓 14 天、140 条，用时 0.0 秒；日志首行 `K线数据源：Mock` |
| 2 | **今天的 0 行不定案**（§2②） | ✅ `DailyFetchNoData` 始终为空；日志"两所 T+1，明天这轮会补上" |
| 3 | **最近 5 个交易日无条件重抓**（§2③） | ✅ 第二轮只抓 09-14~09-18 共 5 天，更早的被"本地已有"闸挡掉 |
| 4 | 合并落库、反复跑不翻倍 | ✅ 两轮之后仍是 140 行 / 14 天 |
| 5 | **整段回补的起点**（§8①） | ✅ "共 4288 天（2010-03-31 ~ 2026-09-18）"，`Bar` 表为空也没抛"请先跑个股日K" |
| 6 | **扣掉已知残缺日**（§8②） | ✅ 库满之后再跑整段回补，只抓 6 天＝残缺日 09-08 ＋ 最近 5 天；不扣的话 09-08 会被"已有"闸跳过 |
| 7 | **没有重抓入口的项会报出来**（§8③） | ✅ `⚠ 资金净流入：有 1 天残缺，但这一项没有"按天重抓"的入口，只能人工处理。` |
| 8 | 【重新拉取失败】的转交回路 | ✅ 收尾行 `StepMargin（任务自补）`，补上 1/1 天 |
| 9 | **只抓某一天** | ✅ 选中后日期框出现；填 2026-09-02（本地已有）→ 只抓那一天、绕过"已有"闸 |

第 8 条是这次改动的直接证据：`DailyRefetcherFor` 已经不存在，残缺日照样补上了。
第 9 条上一轮没验成，是 UIAutomation 枚举 WPF 虚拟化下拉项的等待时间不够（400ms → 1500ms 后
4 个模式全拿到）——产品侧 `SupportedModes` 一直是对的。

### 9.3 入口④【拉取历史区间】——补齐模拟源后也验了

补上那三个模拟源之后整条路离线跑通（2026-09-18 11:07~11:11，2026 年区间，
公告关键词留空即跳过那一段，全程零真请求）：

| # | 验的是什么 | 结果 |
|---|---|---|
| 1 | 整轮离线跑通 | 27 秒跑完，日志里每一条抓取都带 `[模拟源] …未发任何请求` |
| 2 | **两融那半边落库换成 `MarginDayWriter`** | 187 个交易日、1860 行，全是模拟数据（余额 111111 起） |
| 3 | **空日判据换成 `DailyNoDataGate`** | 两融和龙虎榜当天都返回 0 行，**空日名单仍为空**——没有把"还没发布"定案 |
| 4 | 幂等 | 第二轮融资余额"新抓 5 个交易日、跳过本地已有 182"，行数不变（1860/186） |
| 5 | 第二轮只抓最近 5 天 | ✅ 跟增量那条路一致 |

第 3 条是这次唯一碰过入口④的判据改动，第 2 条是唯一碰过的落库改动——两处都验到了。

### 9.4 收尾

验证用的临时配置（`OfflineMock` / `BarSource`）已从 Debug 的 `fetcher-settings.json` 撤掉
（生效键只剩 `BoardMemberChannel`），模拟源写进 Debug 库的假数据——两融、龙虎榜、资金净流入、
K线，以及为跑通名册临时塞的 4 只"模拟X"股票——全部清空。

### 9.5 顺带踩的两个坑（都已修）

**① 点错行发了真请求。** 计划表的行序会变（依赖重排、状态变化），按上一轮记住的行号点
会点到别的项——第一次就点成了【指数日K】、发出 9 个真请求。之后把 `BarSource` 也切成
`Mock` 当保险，并且每次点之前重新枚举一遍行。另外待办里残留的龙虎榜/大宗残缺日也会被
【重新拉取失败】连带跑掉（它们没有模拟源，又发了 2 个真请求）——验证前要先清干净待办。

**② 行尾被脚本改掉。** `App.xaml.cs` 的 44/12 改动一度显示成 **853/821**：一条 `sed -i`
把整份文件的 CRLF 吃成了 LF，而我用 `grep -c $'\r'` 判行尾——它对 LF 文件也返回
"每行都有 CR"，所以没发现。四个文件的行尾都修回了 HEAD 的风格，
细节和检查脚本记进了 memory（project_file_encoding_crlf）。
