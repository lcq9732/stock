# ETF 补不复权与回测口径（2026-09-11，方案待确认）

## 0. 问题

**1659 只 ETF 只有 `day`（源给的前复权），没有 `day_raw`/`day_adj` ⇒ ETF 现在完全不能回测。**

源给的前复权是**减法式**（原价减去此后累计分红），非除权日的收益率也是错的。同一天板块指数
那组实测能说明失真量级：拿东财官方板块指数当标尺，银行板块用前复权算收益率相关系数只有
**0.454**、平均每天差 **5.88%**，换成 `day_adj` 是 0.945 / 0.30%（见
`doc/bar-value-audit-design.md` 和 `BoardIndexSynthesizer` 的类注释）。ETF 分红比个股轻，
失真会小一些，但方向是同一个错。

## 1. 已经查实的事实（全部实测，不是推测）

### ① 腾讯给 ETF 全部三个口径

`newfqkline` 的 `param=<sym>,day,,<end>,640,<qfq|hfq|空>` 对 ETF 都返回数据，字段跟个股一样
（含换手率、成交额）。所以 `day_raw` 直接抓得到，不需要换源。

### ② ETF 的复权**同时有加法和乘法**两种机制

| 票 | `raw − qfq` | 说明 |
|---|---|---|
| 510300 沪深300ETF | 恒 0.2110（近期）/ 恒 0.8470（2013 段） | **加法**：纯现金分红 |
| 510500 中证500ETF | −1.7950 / −1.8230 / −1.8480，随价格变 | **乘法**：做过份额折算 |
| 159915 创业板ETF | **0 天不同** | 从没分红/折算过 |

### ③ 除权事件有现成的本地数据源：`fund_cqcx.db`

`C:\eastmoney\dfcf\config\DataBackUp\fund_cqcx.db`，**标准 SQLite、本地文件、不联网**：

```sql
CREATE TABLE fund_cqcx (RecordID INT64 PRIMARY KEY, Code TEXT, Market int,
  UpdateDate int, UpdateTime int, ExDivType int, NoticeDate int, NvcvtDate int,
  Diviratioa Real, Diviratiob Real, Remark Text, IsValid int)
```

- **5845 条 / 1017 只基金**，日期 1999-04-06 ~ 2026-08-26
- `NvcvtDate` = 除权日；`IsValid=1` 才算
- `ExDivType=1` 现金分红 **3500 条**，金额在 `Diviratioa`
- `ExDivType=2` 份额折算 **2345 条**，比例在 `Diviratiob`

⚠ 注意 `full_cqcx_hs_V3.dat`（那份 105,117 条的沪深除权除息）**一条 ETF 都没有**，全是个股和
新三板。ETF 的在这个 `fund_cqcx.db` 里，两份不要搞混。

### ④ 两个字段的单位都验过了

**`Diviratioa` = 每 10 份现金分红**（跟个股 `Dividend.DividendYuan` 同口径）。
验法是那个恒等式 `raw_t − qfq_t == (此后累计 Diviratioa) / 10`，逐点精确吻合：

| 票 | 日期 | raw | qfq | raw−qfq | 此后累计/10 |
|---|---|---|---|---|---|
| 510300 | 2024-01-19 | 3.2750 | 3.0640 | 0.2110 | **0.2110** |
| 510300 | 2025-05-23 | 3.9880 | 3.7770 | 0.2110 | **0.2110** |
| 510050 | 2024-01-19 | 2.2820 | 2.1470 | 0.1350 | **0.1350** |
| 510050 | 2024-12-10 | 2.7440 | 2.6640 | 0.0800 | **0.0800** |

**`Diviratiob` = 每 10 份折算后的份数**，价格乘数 = `10 / Diviratiob`。
用 510500 那两次折算的真实跳空验：

| 除权日 | `Diviratiob` | 价格乘数 | 理论跳空 | 实际跳空 |
|---|---|---|---|---|
| 2015-04-15 | 2.80325 | 10/2.80325 = **3.567** | +256.7% | **+261.1%** |
| 2022-08-29 | 11.4539 | 10/11.4539 = **0.873** | −12.70% | **−14.13%** |

差的 1~4 个百分点是开盘价与理论除权价的正常偏离，落在现有价格校验的容差内
（`AdjustFactorCalculator.Tolerance` = `MAX(0.08, |理论跳空| × 0.35)`）。
`Diviratiob < 10` 是**份额合并**（价格上调）、`> 10` 是拆分（588890 那条是 30.0，即 3 拆 1）。

### ⑤ 只有 323 只 ETF 需要处理

我们 1659 只 ETF 里，`fund_cqcx` 覆盖 **323 只**；剩下 **1336 只从没分红/折算过**
⇒ 对它们 `day == day_raw == day_adj`，三条序列逐值相同（159915 就是实例，raw vs qfq 0 天不同）。

## 2. 方案

### 2.1 除权事件：塞进现有 `Dividend` 表，不新建表

`AdjustFactorCalculator.ExDividend` 的口径正好能容纳：

| fund_cqcx | → | Dividend / ExDividend |
|---|---|---|
| `ExDivType=1`、`Diviratioa` | → | `DividendYuan`（每10份现金），`/10` 后成为 `CashPerShare` |
| `ExDivType=2`、`Diviratiob` | → | `TransferShares = Diviratiob − 10`，`/10` 后成为 `ShareRatio` |
| `NvcvtDate` | → | `ExDate` |

推导：价格乘数要等于 `10/Diviratiob`，而除权参考价公式的分母是 `1 + ShareRatio`，
所以 `1 + ShareRatio = Diviratiob/10` ⇒ **`ShareRatio = Diviratiob/10 − 1`**。

> **⚠ 这里需要改 `AdjustFactorCalculator`**：份额合并时 `ShareRatio` 是**负数**
> （510500 那次是 −0.71968），而现在 `ExDividend.IsEmpty` 判的是 `ShareRatio <= 0 && ...`，
> 负的送转会被当成空事件跳过。要把"负 ShareRatio（份额合并）"作为合法输入接纳，
> 并加一条测试钉住 510500 那两次的因子。这是本方案里**唯一需要碰复权算法**的地方。

**为什么复用 `Dividend` 而不新建表**：`SqliteDividendRepository.GetByCode` 和
`AdjSeriesRebuildTask` 现成可用，ETF 有了事件和 `day_raw` 之后**零改动就能算出 `day_adj`**。

**✅ 污染风险已查清（2026-09-11）：不会污染，复用安全。** 三条证据：

| | |
|---|---|
| ETF 代码形态 | 库里 **1659 只全部带前缀**（`sh510300`），没有一只是 6 位纯数字 |
| `GetAllCodes`（Analyzer 各选股 Tab 的扫描全集） | SQL 里写死 `code GLOB '[0-9][0-9][0-9][0-9][0-9][0-9]'`，实测返回 5879 只 = 个股 5560 + 退市 319，**零 ETF/指数/板块** |
| 那两个全表字典的用法 | `CorePositionAnalysisEngine` 是按 `codes` 逐只**查字典**，不是拿字典当全集——多出来的 key 取不到 |

所以只要 ETF 的分红行**用带前缀的 code 存**（跟它的 `Bar` 行一致），就与个股的 6 位 key 空间
天然不相交，股息率/连续分红年数那些因子一行都不会变。

唯一影响是**纯显示**：`CorePositionScreenTabViewModel` 里那句"已载入 N 只的近一年派息"会把 ETF
计进去，数字变大、不影响筛选。顺手在那句日志里过滤掉非 6 位的 key 即可。

⚠ 但这条结论依赖"ETF 一律带前缀存"这个前提，所以**导入任务必须显式用带前缀的 code**，
并加一条测试钉住：写入 6 位裸码会让 ETF 漏进个股选股全集。

### 2.2 导入任务：【导入基金除权除息】

新任务类 `FundExDividendImportTask`（`StockPlatform.Tasks`，继承 `FetchTaskBase`），
**纯本地读文件，一个请求都不发**：

- 批的粒度：一次全表（5845 条，一批就够，`MaxItems`/`Deadline` 对它没意义 —— 理由同 `IndustryTask`）
- 只导 `IsValid=1 AND NvcvtDate>0`，且只导我们库里有的 ETF 代码（1659 只里的 323 只）
- 幂等：按 `(code, 除权日)` UPSERT，可反复跑
- 文件不存在（没装东财终端）就 `NothingToDo`，不算失败

### 2.3 抓 `day_raw`：分两条路，省掉八成请求

| ETF | 做法 | 请求数 |
|---|---|---|
| **323 只有事件的** | 腾讯抓 `day_raw`（多页，最长 20 年 ≈ 5000 天 ≈ 8 页） | 约 **2600** 个 |
| **1336 只没事件的** | **从 `day` 直接复制**（三条序列逐值相同） | **0** |

复制那条路要有个**验证闸门**：复制前先抓一页（最近 640 天）核对 `day` 与源的 `day_raw` 是否
逐值相同，不同就退回"老老实实抓"。否则一旦 `fund_cqcx` 漏记了某只 ETF 的事件，就会静默地把
错的序列复制进去 —— 这类错**没有任何地方会报**。

实现上挂在现有 ETF 日K那一项里按口径扩展，还是新加一个计划项，两种都行；倾向**新加一项**
（`StepEtfRawBars`），跟个股的 `StepStockRawBars` 对称，也方便单独重跑。

### 2.4 `day_adj`：不用写新代码

【重算回测序列】按"有 `day_raw` 的票"枚举（`SqliteAdjSeriesAuditor`），ETF 有了 `day_raw`
自然进名单；事件从 `Dividend` 表按 code 取。所以 2.1 + 2.3 做完，**点一次【重算回测序列】
就有 ETF 的 `day_adj` 了**。

唯一要确认的：`AllCodesWithRawBars`/`BuildPlan` 不会因为 ETF 代码带前缀（`sh510300`）而漏掉
——它是按 `Bar.code` 枚举的，不做格式过滤，应该没问题，但要有测试。

### 2.5 不做的事

- **不抓 ETF 的 `day_hfq`**。它是源的混合式口径（送转乘、分红加），回测不用它；个股有它是
  历史原因。少 1659 只 × 8 页的请求。
- **不改 `day`**。前复权那条序列照旧抓、照旧存，看盘用它没问题。

## 3. 验收

1. **恒等式回归**：对纯分红的 ETF，`raw_t − qfq_t` 必须等于"此后累计 `Diviratioa` / 10"。
   这是 §1④ 用过的检验，做成测试（510300、510050 各取 5 个采样点）。
2. **折算因子**：510500 的 2015-04-15 和 2022-08-29 两次，因子必须是 3.567 和 0.873。
3. **收益率自检**：复用 `AdjustFactorCalculator.VerifyReturns` —— 非除权日 `day_adj` 的收益率
   必须精确等于 `day_raw` 的；除权日的假跳空必须被抵掉。
4. **覆盖对账**：1659 只 ETF 都要有 `day_raw` 和 `day_adj`；其中 323 只的 `day_adj` 应与
   `day_raw` 不同，1336 只应逐值相同。
5. **不污染个股**：跑完之后，个股的股息率/分红类因子输出与跑之前逐值相同（这一条对应 2.1 的风险）。

## 4. 顺序

1. ~~查清 `Dividend` 表复用是否污染个股分析~~ —— **已查清（2026-09-11）：不污染，见 §2.1**
2. `AdjustFactorCalculator` 接纳负 `ShareRatio` + 测试（510500 那两次）
3. `FundExDividendImportTask` + 导入 323 只的事件
4. `StepEtfRawBars`：323 只抓、1336 只带闸门复制
5. 【重算回测序列】跑一轮，出 ETF 的 `day_adj`
6. 按 §3 验收

## 5. 相关

- `doc/bar-value-audit-design.md`（值判据；ETF 现在缺口径这件事最初是从体检的覆盖形状里看出来的）
- `AdjustFactorCalculator`（乘法式复权，为什么不用源的后复权）
- `BoardIndexSynthesizer`（同一天量化过前复权收益率的失真：银行板块相关系数 0.454 → 0.945）
- [[project_eastmoney_terminal]]（东财本地数据清单；`fund_cqcx.db` 这次新解出来）
- [[project_etf_board_kline]]（ETF 走新浪取名单、带前缀存）
