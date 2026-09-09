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

实测参考：单条 V1 判据（带 `datetime()` 计算）全库扫一遍 878 秒。体检是手动触发的重活，可接受；
若要提速，可给这几条加 `period_start >= ?` 的下界参数（默认全history，界面可选"只查近一年"）。

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

## 11. 实施顺序

1. `SqliteBarValueAuditor` + 单测（纯新增，无风险）
2. `MissingBarReason` + `MissingBarRange.Reason`（纯新增字段，老 manifest 兼容）
3. `RunStepFullAuditAsync` 接入
4. `FillAuditedGapsAsync` 复查分派 + `UpdateDayAmountTurnover` 扩展
5. 全套测试 + Debug 实例实机跑一次【全库数据体检】，核对报出来的数字跟 §9 一致

## 12. 相关

- `IncrementalWindowCalculator`（16:00 确认判据的本体）、`IntradayBarConfirmationTests`
- `doc/fetch-plan-atomic-tasks-design.md`（体检与重拉失败在任务矩阵里的位置）
- memory：`project_intraday_bar_confirmation`、`project_bar_volume_unit_bug`、`project_eastmoney_terminal`
