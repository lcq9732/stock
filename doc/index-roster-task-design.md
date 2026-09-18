# 指数成分 / 指数权重 / 股票名册与流通市值 迁到新任务框架（2026-09-18 设计）

## 0. 这是什么

资金净流入迁完之后，`FetchOrchestrator.RunFillBacklogAsync` 里只剩三块：
**K线**（四个口径、四类待办）、**指数成分/权重**（`failed`）、**流通市值**（`round`）。

后两块形状简单，一起迁掉之后那个方法里就只剩 K线——K线是另一个量级的工程，单独立项。

## 1. 现状

| 项 | 单项入口 | 待办 | 补待办的路 |
|---|---|---|---|
| 指数成分 `StepIndexCons` | `RunStepIndexConsOnlyAsync` | `failed` | `RetryIndexAsync`（跟权重共用一个方法） |
| 指数权重 `StepIndexWeight` | `RunStepIndexWeightOnlyAsync` | `failed` | 同上 |
| 名册与市值 `StepRoster` | `RunStepRosterAndMarketCapAsync` | `round` | `FetchMarketCapAsync`（就是日常那条） |

`FetchMarketCapAsync` **只有这两个调用方**（单项入口 + Round 分支）——【拉取全部】2026-09-02
拆成原子项之后就没有第三个了，所以可以整项搬走，不用像两融那样留个共用入口。

## 2. 三项各自的"别简化掉"

### 2.1 指数权重：两道筛子 + 一份**不在 Todos 里**的名单

内置指数全集 732 个，而 `closeweight.xls` 只有中证系才有、其余一律 404；权重本身是**月度**更新。
所以每轮先筛两道：本地这一期还新鲜（25 天内）→ 跳过；上次确认没有文件、且没过 30 天 → 跳过。
稳态下真正发出的请求从 732 降到接近 0。

⚠ 第二道筛子读写的是 `Manifest.IndexWeightMissing`（`code → 确认时间` 的字典），
**不是 `Todos`**。搬家时别顺手"统一"成待办格式——那是一次不可逆的键迁移，而且它的语义
（"这个指数没有权重文件，30 天后再问一次"）跟"欠着的活"不是一回事。

### 2.2 指数成分：空结果不算失败

新浪对某些老指数本来就没有成分，返回空**不算失败**（不进失败名单）。
写成"空就记失败"的话，那些指数会永远躺在名单里刷存在感。

### 2.3 流通市值：整轮扫描，"失败"的粒度是**一轮**

这一项不是逐只查——`SinaListMarketCapFetcher` 一次扫回全市场列表，顺带带出每只的流通市值。
于是：

- **`round` 待办的语义**是"有一轮要重来"，名单里那一大批代码只是当时那批的全体，
  不是"这些票各自失败了"。界面上也是按"1 轮"报的，别改成按只数报。
- **一批 ＝ 一轮**，所以 `MaxItems`/`Deadline` 对这一项没有意义（骨架会照常支持，只是用不上）。
- **顺带发现新股**：扫描天然会看到本地还不知道的代码，直接写进名册，不额外发请求。
- **`as_of_date` 记的是"值属于哪个交易日"**，不是"哪天跑的"：快照在盘前/周末的基准价是
  **上一个交易日**的收盘。现在靠抓上证指数日线解析（`ResolveMarketCapAsOfDateAsync`）。

## 3. 形状

### 3.1 三个任务类

| 任务 | 一批 | 模式 |
|---|---|---|
| `IndexConsTask` | 一个指数 | `Incremental`（全量刷新）/ `FillBacklog`（只抓失败名单） |
| `IndexWeightTask` | 一个指数 | 同上 |
| `RosterMarketCapTask` | **一轮**（整批） | `Incremental` / `FillBacklog`（＝重来一轮） |

三项都 `HandlesBacklog => true`；都不支持 `SpecificDay`/`FirstBackfill`——
成分和权重是"当下快照"，市值更是（接口给不出往年的值）。

指数两项一批＝一个指数，于是 732 个的轮次终于能分批跑、能到点收尾（现在中断就是整轮白费）。

### 3.2 市值那项要一个"判交易日"的口子

`ResolveMarketCapAsOfDateAsync` 现在用 `source.Fetcher` 抓上证指数日线判最新交易日，
而新式任务不走 `SelectedSource`（它们注册时自带固定 fetcher，见 `EtfRawBarTask`）。

**选 B（2026-09-18 用户拍板）：改用本地交易日历判，抓指数降级成退路。**

```
if (quotesAreLive == true) return today;                 // 在交易时段，就是今天
① 交易日历：取 ≤ 今天的最后一个交易日 → 用它
② 日历给不出（空 / 过期）→ 退回抓上证指数日线（原来的做法）
```

稳态下**一个请求都不用发**，而原来每轮都要抓一次上证指数日线。

⚠ **日历"给不出"要包括"过期"，不只是"为空"**：日历自己缺哪段就瞎哪段
（project_trading_calendar_pitfall 栽过一次——拿只到 2016 年的指数当日历，
2,360 只老股被判成"缺口里没有交易日"、一个请求都没发就跳过了）。
所以判据是**「最近 15 天内有没有交易日」**：有就取最大的那个；没有就说明日历没跟上，
退回抓指数。15 天足够覆盖春节这种最长连休。

退路仍需要一个 `IBarDataFetcher`，注册时给固定的 `TencentBarFetcher`（同 `EtfRawBarTask`）。

### 3.3 失败名单的语义原样带走

`SetFailedTodo` 那套"本轮碰过、这次没失败的移出；没碰到的原样留着"三项都要保持。
⚠ 权重那项的 `attempted` 是**本轮真问过的** `targets`，不是全集 732——
被两道筛子跳过的不该被清出名单，也不该被记进去。

## 4. 改动清单

### 新增
- `src/StockPlatform.Tasks/IndexConsTask.cs`、`IndexWeightTask.cs`、`RosterMarketCapTask.cs`
- `App.xaml.cs` 注册三行

### 改
- `FetchOrchestrator`：删 `RetryIndexAsync`、`FetchMarketCapAsync`、`ResolveMarketCapAsOfDateAsync`；
  `RunFillBacklogAsync` 删 `Round` 分支和 `IndexCons`/`IndexWeight` 两个 case
- `FetchOrchestrator.Steps.cs`：删三个 `RunStep*` 入口
- `MainViewModel`：删三个 case
- `FetchTaskCatalog`：三项的 `SupportedModes` 补 `FillBacklog`（现在缺）、Note 补一句分批可停

### 不动
- `Manifest.IndexWeightMissing`（见 §2.1）、三项的 manifest 待办键
- `IndexCatalog`（内置指数清单）、`BuildEtfIndexMap`（ETF指数映射是另一项）

## 5. 风险

**5.1 权重的两道筛子最容易在重构里丢**：丢了就是每轮拿四五百个注定 404 的请求去撞中证的反爬。

**5.2 `round` 待办别改成按只数**：改了界面会显示"5500 只票丢了数据"，而事实是"有一轮要重来"。

**5.3 名册刷新的回退路径**：默认实现自带全市场名单（`rosterRefreshed=true`），
只有逐只查询式的市值实现才需要单独取一次名册。这条分支照搬，别因为"默认走不到"就删掉。

## 6. 验证

1. `dotnet test`；新增单测：权重两道筛子的取舍、成分空结果不算失败、失败名单三态。
2. 实机（离线模拟）：⚠ 这三项的源**都还没有模拟版**（见 doc/offline-mock-design.md §6），
   所以要先补三个：`MockIndexConsProvider` / `MockIndexWeightProvider` / `MockMarketCapFetcher`。
   补完之后三项的四条路（日常、只补待办、分批、中断续跑）可以零请求跑一遍。
3. 回归：【重新拉取失败】里三项都显示「（任务自补）」。

## 7. 不做的事

- **不动 K线**（那是 `RunFillBacklogAsync` 剩下的唯一一块，单独立项）。
- **不动 ETF指数映射**（`StepEtfIndexMap`，本地计算、不联网，跟这三项无关）。

---

## 8. 实现记录（2026-09-18 落地时的偏差）

**① 市值那项多注入了两样**：`ITradingDayRepository`（判交易日的主路径，选 B）和
`IStockListProvider`（名册回退路径原来从 `NamedBarSource` 拿，新式任务不走 `SelectedSource`）。
判交易日的退路 `IBarDataFetcher` 注册时给固定实例——但**它也要跟着离线模拟总开关走**，
见 §9 的第一个坑。

**② 进度行里不报累计数**（三项都是）。骨架是"先 yield、再 `SaveBatchAsync`"，
`_ok` 要等这一批存完才涨，在进度里读永远差一批（实机日志里看到"732/732（成功 731）"）。
计数留给收尾那句汇总。跟 `NetInflowTask` 那次是同一个坑。

## 9. 实机验证（2026-09-18，Debug 实例）

### 9.1 先踩了两个坑

**① 漏接模拟源，白发了一轮真请求。** `marketCapFetcher` 和判交易日用的
`TencentBarFetcher` 都没挂到 `OfflineMock` 总开关上，第一次跑【股票名册与流通市值】
真去扫了新浪的全市场列表（约 55 个请求、46 秒）。
补模拟源时**要连"退路"一起补**——日历为空时那个 anchor fetcher 是真会发请求的。

**② `round` 待办清不掉（真 bug，已修）。** 我用"当前本地名册"当作本轮碰过的代码，
而待办里那批可能早就不在名册里了，于是 `ExceptWith` 一个都移不掉、待办永远清不空。
原逻辑用的是**待办里那批代码**。已修并补了单测（待办里故意放一个不在名册里的代码）。

### 9.2 验证结果（全程离线模拟、零请求）

| # | 验的是什么 | 结果 |
|---|---|---|
| 1 | 指数成分一轮 | 732 个全抓到，0 个无成分、0 失败 |
| 2 | 指数权重一轮 | 313 个有权重、419 个没有权重文件（记进独立名单） |
| 3 | **权重两道筛子**（第二轮） | `本轮要问 0 个（313 个本地已是最新一期、419 个确认没有权重文件）` |
| 4 | 市值：日历为空 | 走退路并明说 `⚠ 本地交易日历最近 15 天里没有交易日…改用上证指数日线` |
| 5 | **市值：日历有数据**（选 B 的主路径） | `快照归到上一个交易日 2026-09-15（本地交易日历）`——没抓指数 |
| 6 | 三项的转交回路 | `本轮重试完成：流通市值（任务自补）、指数成分（任务自补）、指数权重（任务自补）` |
| 7 | 待办复查后清空 | ✅（修完 ② 之后） |

第 3 条是这一项的命根子——丢了那两道筛子就是每轮拿四五百个注定 404 的请求去撞中证的反爬。
第 5 条是选 B 的直接证据：稳态下判交易日一个请求都不用发。

### 9.3 收尾

验证用的临时配置已从 Debug 的 `fetcher-settings.json` 撤掉（生效键只剩 `BoardMemberChannel`），
模拟源和那一轮真请求写进 Debug 库的数据（名册、市值、指数成分/权重、交易日历）全部清空。
