# K线「值体检」设计（2026-09-09）

## 1. 为什么要有它

现有的【全库数据体检】（`FetchOrchestrator.RunStepFullAuditAsync`）查的**全是"行在不在"**：

| 它现在的判据 | 查什么 |
|---|---|
| 5 个可联网补的面（个股前/后/不复权、ETF、指数）逐只对交易日历 | 日历里有、这只票没有 |
| 「整只票一根都没有这个口径」 | 那个 code 在该口径下不存在 |
| 板块指数 / day_adj | 同上，只报数 |
| 覆盖形状 | 起点晚了 / 尾巴停了 |
| 日频表（资金流/融资/龙虎榜/东财三张） | 哪天整天没数据 |

**一条都不查"行里的值对不对"**，于是两个真实事故都从它眼皮下溜过去了：

1. **turnover 整列 NULL**（2026-09-06 东财终端日线导入）——日线不含换手率字段，导入把 `day_raw`
   ≤2015 的 702 万行 turnover 写成 NULL；`SqliteBarRepository.Query` 当时是 `GetDouble(9)` 硬读，
   于是【重算回测序列】对 2886 只票每只都抛 `data is NULL at ordinal 9`，界面"待重算 2886 只"
   永不下降、**潜伏三天**。行一根不缺，体检全绿。
2. **盘中固化 5350 行**（2026-09-01 09:25~10:10 与 2026-07-16 11:25）——任务落在交易时段，抓到的
   当日K线是半天快照（002650 那天在库里是"四价合一 6.04、成交量 23 手"，真实收盘 6.01/20953 手；
   sh000001 2026-07-16 收盘记 3916.84，而次日开盘 3865.32）。行也都在，体检同样全绿。

两次的共同点：**数据是错的，不是缺的**。

## 2. 设计原则

- **纯本地、零请求**。六条判据全是查库，体检本来就是"手动触发的重活"，不引入网络成本。
- **体检只发现和报，纠正交给【重新拉取失败】**。复用现成的两段管道，不新增任务、不新增按钮：

  ```
  【全库数据体检】发现值错 → 写进 Manifest.MissingBars（带 Reason）
          ↓
  【重新拉取失败】按口径逐段重抓（现成逻辑，byGran 那段）
          ↓
  SqliteBarRepository.InsertOrRefreshUnconfirmed / UpdateAmountTurnover 落库
  ```

- **覆盖规则分两类，依据是"这一列会不会随复权变"**：

  | 列 | 能否覆盖已确认的行 | 依据 |
  |---|---|---|
  | OHLC | **不能** | 历史行的价格是当年抓取时的复权基准，重抓可能因其间除权整体平移，覆盖会造成同一序列里新旧基准混杂（这是 `INSERT OR IGNORE` 原本要防的事） |
  | volume / amount / turnover | **能** | 不受复权影响——实测腾讯 qfq 与不复权这三列 640 天 0 差异；`UpdateDayAmountTurnover` 的注释早就是这个论证 |
  | 未确认行（`fetched_at < 交易日16:00`）的所有列 | **能** | 那种行本来就不是最终数据，见 `IncrementalWindowCalculator` |

## 3. 六条判据

### V1 未确认行（盘中固化）

```sql
select code, granularity, period_start from Bar
where fetched_at is not null and fetched_at < datetime(period_start, '+16 hours')
```

- **处置**：进待补名单，重抓，`InsertOrRefreshUnconfirmed` 全列覆盖。
- **例外**：`day_adj` 是本地重算产物，**不进联网名单**——归 V5 处置（提示跑【重算回测序列】）。
- 判据本体已落在 `IncrementalWindowCalculator.IsConfirmedFinal`，此处复用同一常量（16:00）。

### V2 关键列 NULL

```sql
... where open is null or close is null or high is null or low is null
     or volume is null or amount is null or turnover is null
```

- **量额换手 NULL**：先试本地修——同 code 同日其他口径有值就拷过来（三列跨口径同值，见 §2）。
- **OHLC NULL**：进待补名单重抓。
- 写入路径向来写 0 而不是 NULL，所以这一条实际抓的是**批量导入绕过写入路径**那类。

### V3 四口径量额换手不一致

对同 code 同 period_start，比较 `day` / `day_hfq` / `day_raw` / `day_adj` 的三列。

- **依据**：同源、盘后必然逐值相同——实测 50 只票 × 2016 年后 112,473 天，`day` vs `day_hfq`
  **0 差异**；`day` vs `day_raw` 的 25 处差异全部是 2026-09-01 那批盘中行。所以不一致 = 异常。
- **处置**：进待补名单 → 重抓 → **只覆盖三列，不动 OHLC**。
- **能力边界（重要）**：抓不出"数据源自己给错"。603999 在 2026-09-08 的 amount 记 6914 万而实际
  2.13 亿（`fetched_at` 21:02，按 16:00 判据已确认），三个口径是同一批抓的，会**一致地错**，
  V3 看不见。那类要跨源比对，需联网，不在体检范围——由 V6 兜一部分。

### V4 OHLC 自洽性

```
high >= max(open, close) 且 low <= min(open, close) 且 low > 0 且 high >= low
```

- **处置**：进待补名单重抓。抓的是脏数据、解析错位、字段串位。

### V5 day_adj 与 day_raw 脱节

行集不同，或同日三列不同。

- **处置**：只报数 + 提示跑【重算回测序列】。跟现有"本地口径只报数"的处置一致（`AuditLocalOnlyScopes`）。
- 当前实例：`day_adj` 2026-09-01 有 1555 行继承了 `day_raw` 的盘中值。

### V6 量额自洽比率

```
ratio = amount / (volume * close)
  ≈ 100  → volume 单位是"手"（主板 3203 只、创业板 1404 只、北交所 342 只）
  ≈ 1    → volume 单位是"股"（科创板 688/689 共 613 只）
  其它   → 量或额本身不对
```

- **阈值**：落在 `[80,125]` 与 `[0.8,1.25]` 两个区间之外算可疑（留 25% 余量，避开收盘价与均价的
  正常偏离）。`volume`/`close` 为 0 的行跳过。
- **处置**：**只报数 + 列样本，不进待补名单**。603999 那种重抓大概率拿回同样的值，要修得先查成因。
- 顺带能量化出 `Bar.volume` 跨板块单位不统一的影响面（科创板会被系统性报成 ratio≈1）。

## 4. 数据结构

`MissingBarRange` 加一个字段：

```csharp
/// 这一段为什么要重抓。原来只有一种（缺行），现在值错也走同一条管道，
/// 复查时必须按它分派回对应判据——否则值错的段会被 FindGaps 一律判成"已补齐"划掉。
public string Reason { get; set; } = MissingBarReason.Gap;
```

`MissingBarReason` 常量：`gap`（缺行，旧行为，缺省值）、`intraday`（V1）、`null_value`（V2）、
`inconsistent`（V3）、`ohlc`（V4）。老 manifest 没有这个字段，反序列化后是缺省值 `gap`，与现有行为一致。

## 5. 复查分派（关键，不改会静默失效）

`FillAuditedGapsAsync` 现在每批补完用 `FindGaps` 复查，而它只看"行在不在"。值错的行**一直都在**，
复查会立刻判成"已补齐"划掉——即使值根本没被覆盖（比如又在盘中跑了一次）。

改成按 `Reason` 分派：

| Reason | 复查用什么 |
|---|---|
| `gap` | `FindGaps`（不变） |
| `intraday` | 重查 V1：那些 (code, day) 还是不是未确认 |
| `null_value` | 重查 V2 |
| `inconsistent` | 重查 V3 |
| `ohlc` | 重查 V4 |

## 6. 白名单策略

「确认没有」白名单（`MissingBarConfirmed`）是给**停牌**用的：补两轮拿不到就认了，往后体检跳过。

**值错类不参与这个机制**——让错值进白名单等于发永久豁免。`Tries` 到上限就**持续报警**，不静默。

## 7. 性能

六条判据合并成 3 遍全表扫描（Bar 表约 7000 万行）：

| 遍 | 判据 | 说明 |
|---|---|---|
| 1 | V1 + V2 + V4 + V6 | 都是单行内判断，一个 SQL 一起算 |
| 2 | V3 | 跨口径 self-join |
| 3 | V5 | day_adj × day_raw join |

实测参考（2026-09-09 生产库 22.2 GB / 7000 万行）：

| | 耗时 |
|---|---|
| **现有的空洞体检**（走索引 join）：五个面 + 板块指数 + day_adj + 覆盖形状 + 七张日频表，整轮 | **3 分 08 秒** |
| **单条值判据** V1（`fetched_at < datetime(period_start,'+16 hours')`，逐行算日期函数、用不上索引），全库一遍 | **878 秒** |

也就是说**瓶颈全在新加的这几条值判据**，是现有体检的十几倍。三遍下来约 40 分钟。

真嫌慢就给值判据加 `period_start >= ?` 下界——历史段一旦体检干净就不会再变，默认只查近一年即可
（用 Mode 表达，别再加 UI 元素，理由同 doc/full-audit-task-migration-design.md §4）。

## 8. 类与接入点

| 新增/改动 | 位置 | 内容 |
|---|---|---|
| 新增 | `StockPlatform.Data/Sqlite/SqliteBarValueAuditor.cs` | 六条判据的纯查询，每条一个方法，返回 (code, gran, day) 列表 |
| 新增 | `StockPlatform.Logic/Models/MissingBarReason.cs` | Reason 常量 |
| 改 | `MissingBarRange` | 加 `Reason` |
| 改 | `FetchOrchestrator.Steps.cs` · `RunStepFullAuditAsync` | 现有 5 个面之后，加一段"值体检"，把 V1~V4 写进 `MissingBars`，V5/V6 进 `localHints` 只报数 |
| 改 | `FetchOrchestrator.Steps.cs` · `FillAuditedGapsAsync` | 复查按 `Reason` 分派；V3 那类补完走"只更新三列"的落库路径 |
| 改 | `SqliteBarRepository.UpdateDayAmountTurnover` | 从"只 day 口径、只填 amount=0"扩成"任意口径、值不一致就覆盖" |

## 9. 当前库存污染（实测，2026-09-09）

| 口径 | 行数 | 日期 | 判据 | 怎么清 |
|---|---|---|---|---|
| day_raw | 4,020 | 2026-09-01 | V1 | 体检 → 重拉失败（4020 请求） |
| day | 1,330 | 2026-07-16/17 | V1 | 同上（1330 请求，全是指数/ETF，含 sh000001） |
| day_adj | 1,555 | 2026-09-01 | V5 | 修好 day_raw 后跑【重算回测序列】 |
| day_hfq / week / month | 0 | — | — | 干净 |

东财导入段（`day_raw` ≤2015，702 万行）已专项体检：未确认行 0、`fetched_at` NULL 0、
turnover/amount/volume NULL 各 0 —— 干净，且因 `fetched_at`（2026-09-06/07）远晚于交易日 16:00
而天然判定为"已确认"，新判据不会误报它、UPSERT 也不会覆盖它。

⚠ 09-08 那轮重算日志里"317 天的收益率对不上真实值"很可能就是 `day_adj` 那 1555 行盘中价造成的，
不是算法坏了——清理后重算一次即可确认。

## 10. 测试计划

`BarValueAuditTests`（真 schema、真日期，跟 `DailyTableAuditTests` 同风格）：

- 每条判据各造一条脏数据 + 一条正常数据，断言只报脏的那条；
- V1 的边界：`fetched_at` 15:59 报、16:00 不报；
- V3 的边界：三列全等不报、任一列差报；
- V6 的边界：ratio 100/1 不报，33 报，volume=0 跳过不报；
- 复查分派：值错段补完后**值没变**时不能被划掉（这是 §5 那个坑的回归测试）；
- 白名单：值错段 Tries 到上限不进 `MissingBarConfirmed`。

## 11. 实施状态（2026-09-09）

| # | 内容 | 状态 |
|---|---|---|
| 1 | `SqliteBarValueAuditor`（六条判据的纯查询）+ `BarValueAuditTests` 24 个 | ✅ 完成 |
| 2 | `AuditFindingKind` + `MissingBarRange.Reason`/`EffectiveReason`/`IsValueIssue`（老 manifest 兼容） | ✅ 完成 |
| 3 | `FullAuditTask` 接入：V1~V4 进待补名单（`CommitValueFindings` 值类整体替换）、V5/V6 只报数 | ✅ 完成 |
| 4 | 【重新拉取失败】**不再清掉值类记录**（`SaveProgress` 带上它们） | ✅ 完成 |
| 5 | 值类记录的**补法**：`FillValueIssuesAsync` 按 Reason 分派——多口径不一致走 `UpdateVolumeAmountTurnover`（只覆盖三列、绝不动 OHLC），其余整段重抓靠 UPSERT 覆盖未确认行 | ✅ 完成 |
| 6 | **复查按 Reason 分派**：`SqliteBarValueAuditor` 加了"只查这批 code"的重载（全库扫描不能拿来复查一批 500 段），判定抽成 `ValueIssueRecheck`（Logic 层纯函数 + 单测） | ✅ 完成 |

### 闭环后的流程

```
【全库数据体检】六条判据 → V1~V4 进 MissingBars（带 Reason）、V5/V6 只报数
        ↓
【重新拉取失败】按 Reason 分两条路：
   · Reason=gap          → 整段重抓，复查用 FindGaps（原有逻辑，一行没动）
   · Reason=inconsistent → 抓回来只覆盖 volume/amount/turnover 三列
   · 其余值类            → 整段重抓，靠 InsertOrRefreshUnconfirmed 覆盖未确认行
        ↓
复查：用**对应判据**只查这批 code（不是 FindGaps，值错的行一直都在）
   · 判据不再命中 → 从名单划掉
   · 还命中       → 段收窄到仍命中的那几天、Tries+1，**不进白名单**（那是给停牌用的，
                    值错进去等于发永久豁免；收敛靠真修好，不靠计数到顶）
```

跳过的两种：数据源不支持该口径（后复权/不复权只有腾讯给）、`day_adj`（本地重算的，抓不来，
等【重算回测序列】）——两种都 **Tries 一动不动**，免得空跑两轮被误判。

### 实施中被测试抓出来的两个真 bug（都很安静）

1. **面级落账连带删值类记录**：`CommitScope` 的 `kept` 过滤原来只看 `code + gran`，于是扫完
   "个股·不复权"这个面就把同一只票同一口径的**值类**记录删了。加 `!m.IsValueIssue` 修掉，
   回归测试是 `值类记录的Tries按Reason分别继承`。
2. **值判据零发现时落账不被调用**：批里只剩 `Scope=null` 的汇总行，`SaveBatchAsync` 找不到
   scope 就不落账 ⇒ "这些值问题已经修好了"永远写不回名单。让值体检的汇总行也带
   `ValueScope` 修掉，回归测试是 `值问题修好后_旧的值类记录被清掉_缺行记录不受影响`。
   （跟 §1 里"每个面必须至少产出一条汇总行"是同一个坑的第二次出现。）

## 12. 相关

- `IncrementalWindowCalculator`（16:00 确认判据的本体）、`IntradayBarConfirmationTests`
- `doc/fetch-plan-atomic-tasks-design.md`（体检与重拉失败在任务矩阵里的位置）
- memory：`project_intraday_bar_confirmation`、`project_bar_volume_unit_bug`、`project_eastmoney_terminal`

## 13. 生产实测暴露的四个缺陷（2026-09-09，全是判据"适用范围"没想清楚）

第一轮生产体检（22.2 GB 库）报出 22514 段，其中大部分是误报或修不了的。**四个缺陷有一个共同
形状：判据本身的算法没错，错在没界定"它对哪些列、哪些口径、哪些标的成立"。**

| # | 现象 | 根因 | 修法 |
|---|---|---|---|
| ① | V4 报 5 万行（打满上限），244 只票全是 `day` | `low <= 0` 撞上**前复权减法式的负价**（万科 1997 年前复权价 −8.17，是已知失真不是脏数据） | "价格 ≤ 0" 只对 `day_raw` 判 |
| ② | V6 报 2621 万行，一只票 8420 行＝全部历史 | 比值 `amount/(volume×close)` 里**只有 close 随复权变**，而 amount 永远是真实成交额——拿复权价去除必然对不上 | V6 只对「个股 × 不复权」判（第一次只排除了指数/板块，不够） |
| ③ | 69 段 `inconsistent` 重抓也修不掉 | `turnover` 是数据源**算出来的派生值**（量÷流通股本），各口径不同时刻抓，期间股本一变就重算成另一个数（000153 的 08-27：量额完全一致、turnover 7.39 vs 7.36，相隔 5 天抓） | 三列分开：量额保持 1e-6，turnover 放宽到 2% 或绝对 0.05 |
| ④ | 5621 段 `day_adj` 永远被跳过、每轮重报 | `day_adj` 是本地重算产物、**抓不来** | 它的值问题只报数、不进待补名单（跟 V5 同处置） |

### 还有一个不属于判据、但更严重的功能缺陷

**`intraday` 那 5282 段一段都没修上**：我原本让它走 `ProcessOneStockAsync`（"整段重抓，靠
`InsertOrRefreshUnconfirmed` 覆盖未确认行"），但那个方法在 `overwrite=false` 时**只把"库里
没有的行"放进 `toInsert`**——值错的行是"**存在**但值错"，压根到不了 UPSERT 那一步，覆盖条件
没机会生效。002650 跑完一轮后 OHLC 还是四价合一 6.04。

改成抓回来**直接交给 `InsertOrRefreshUnconfirmed`**，让它的 WHERE 去裁决。

> ⚠ 这条值得单独记住：**`ProcessOneStockAsync` 是"补缺"的工具，不是"改错"的工具**。
> 任何"行在但值错"的修复都不能指望它。

### 附带的效率问题

同一 (票, 口径) 常有多条不同 Reason 的记录（2026-09-01 那批盘中行既是 `intraday`、量额也
`inconsistent`），逐段抓的话 9398 段里 4020 段是白发的请求。改成按 (票, 口径) 分组、取日期
并集抓一次；混着多种 Reason 时走整段重抓（那条路顺带也修好量额），全是 `inconsistent` 才走
"只覆盖三列"。

### 判据"上限"是个反面教材

最初给每类判据加了 5 万条返回上限（防内存）。后果是 V4/V6 都打满，日志显示"50000 行"——
**把截断伪装成了精确数字**，真实规模从此不可知。正确做法是流式聚合成段（内存里只留段、
行数只累加计数），上限根本不需要。用户一句"为什么要 50000 上限"点出了这个问题。
