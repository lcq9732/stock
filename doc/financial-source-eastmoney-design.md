# 【拉取财务报表】换东财 + 迁新框架（设计，2026-09-10）

上游结论见 `doc/datasource-eastmoney-migration.md`，这是那份评估里两个"要做"的第二个（第一个是行业分类，已落地，见 `doc/industry-source-eastmoney-design.md`）。

迁框架的判据按 2026-09-10 更正后的版本（迁移成本 + 后续维护成本，见 `doc/full-audit-task-migration-design.md` §0）。

---

## 1. 为什么换

按价值排序，**字段多不是主要理由**：

1. **摆脱中文行名匹配**。现在是拿"营业总收入"/"业务及管理费用"这类中文行名去 TSV 里找行，报表版式一改就静默丢科目——之前漏配银行的行名，42 家银行的成本收入比整类算不出来。东财是固定英文列名 + 显式 null。
2. **机构类型不用再猜**。东财每行带 `ORG_TYPE`（通用/银行/券商/保险），直接决定该读哪张表；现在是靠"这家有没有'已赚保费'这一行"反推。
3. **退市股照样有**（300104 乐视网 71 期，最早 2007-12-31）。
4. 字段 40 → 203/319/254 列。**近期兑现不了**，`FinancialKeys` 只有 60 个 key，要先扩 key 才用得上。算期权。

## 2. 字段映射表（不是猜的，是数值证明的）

方法：取库里某只票某期的 60 个科目值，去东财三张表的**所有数值列**里找相等的列（容差 1e-9 相对误差）。值对上了才算映射成立。茅台覆盖不到的科目（值为 0 / 机构专有）换银行、券商、保险、重资产公司再跑。

用到的样本：600519 茅台、600036 招行、600030 中信证券、601318 平安、000651 格力、600019 宝钢、000002 万科、600941 中国移动。全部为 2026-06-30 期。

### 2.1 利润表（`RPT_F10_FINANCE_GINCOME`）

| FinancialKeys | 东财列 | FinancialKeys | 东财列 |
|---|---|---|---|
| revenue | `TOTAL_OPERATE_INCOME` | oper_profit | `OPERATE_PROFIT` |
| total_cost | `TOTAL_OPERATE_COST` | total_profit | `TOTAL_PROFIT` |
| oper_cost | `OPERATE_COST` | income_tax | `INCOME_TAX` |
| tax_surcharge | `OPERATE_TAX_ADD` | net_profit | `NETPROFIT` |
| sell_exp | `SALE_EXPENSE` | np_parent | `PARENT_NETPROFIT` |
| admin_exp | `MANAGE_EXPENSE` | minority_pl | `MINORITY_INTEREST` |
| fin_exp | `FINANCE_EXPENSE` | eps_basic | `BASIC_EPS` |
| rd_exp | `RESEARCH_EXPENSE` | impair_loss | `ASSET_IMPAIRMENT_LOSS` |
| fv_gain | `FAIRVALUE_CHANGE_INCOME` | other_oper_exp | `OTHER_BUSINESS_COST` |
| invest_income | `INVEST_INCOME` | | |

### 2.2 资产负债表（`RPT_F10_FINANCE_GBALANCE`）

| FinancialKeys | 东财列 | FinancialKeys | 东财列 |
|---|---|---|---|
| cash | `MONETARYFUNDS` | advance_recv | `ADVANCE_RECEIVABLES` |
| note_recv | `NOTE_RECE` | cur_liab | `TOTAL_CURRENT_LIAB` |
| ar | `ACCOUNTS_RECE` | lt_loan | `LONG_LOAN` |
| prepay | `PREPAYMENT` | bond_pay | `BOND_PAYABLE` |
| inventory | `INVENTORY` | liab | `TOTAL_LIABILITIES` |
| cur_assets | `TOTAL_CURRENT_ASSETS` | share_capital | `SHARE_CAPITAL` |
| assets | `TOTAL_ASSETS` | equity_parent | `TOTAL_PARENT_EQUITY` |
| st_loan | `SHORT_LOAN` | minority_equity | `MINORITY_EQUITY` |
| note_pay | `NOTE_PAYABLE` | equity_total | `TOTAL_EQUITY` |
| ap | `ACCOUNTS_PAYABLE` | undist_profit | `UNASSIGN_RPOFIT` ⚠ |

⚠ `UNASSIGN_RPOFIT` 是东财那边的**拼写错误**（RPOFIT），照抄，别"修正"成 PROFIT。

歧义列的取舍（多列同值时）：`ar` 取 `ACCOUNTS_RECE` 不取 `NOTE_ACCOUNTS_RECE`（后者是票据+应收合计，样本票据为 0 才相等）；`ap` 同理；`assets` 取 `TOTAL_ASSETS` 不取 `TOTAL_LIAB_EQUITY`。

### 2.3 现金流量表（`RPT_F10_FINANCE_GCASHFLOW`）

| FinancialKeys | 东财列 | FinancialKeys | 东财列 |
|---|---|---|---|
| sales_cash | `SALES_SERVICES` | impair_provision | `ASSET_IMPAIRMENT` |
| ocf | `NETCASH_OPERATE` | depreciation | `FA_IR_DEPR` |
| icf | `NETCASH_INVEST` | amort_intangible | `IA_AMORTIZE` |
| fcf | `NETCASH_FINANCE` | amort_lt_prepaid | `LPE_AMORTIZE` |
| capex | `CONSTRUCT_LONG_ASSET` | fv_loss | `FAIRVALUE_CHANGE_LOSS` |
| cash_end | `END_CCE` | inv_decrease | `INVENTORY_REDUCE` |
| recv_decrease | `OPERATE_RECE_REDUCE` | pay_increase | `OPERATE_PAYABLE_ADD` |

`ocf` 取 `NETCASH_OPERATE` 不取 `NETCASH_OPERATENOTE`（后者是附注间接法的结果，正常相等但口径不同）；`depreciation` 取 `FA_IR_DEPR` 不取 `OILGAS_BIOLOGY_DEPR`。

### 2.4 机构专表：银行/券商是**整套换表**，不是"通用表 + 补两个科目"

> ⚠ 本节 2026-09-10 全量比对后重写过。第一版只对利润表分了 G/B/S 表，资产负债表和现金流量表
> 一律用通用表——**平安银行因此缺了 9566 格**（`assets`/`liab`/`equity_total`/`ocf`/`icf`/`fcf`
> 这些核心科目全没），而且一个错都不报。

两条实测事实：

1. **银行/券商的 `GBALANCE` / `GCASHFLOW` 是空表**（不是列为 null，是整张表没有这只票）。
   只有 `BBALANCE`/`BCASHFLOW`、`SBALANCE`/`SCASHFLOW` 才有数据。
   （`GINCOME` 是例外：它对四类机构都有数据，所以用它探 `ORG_TYPE`。）
2. **专表的列名跟通用表不一样**，不能只换前缀：

   | 科目 | 通用表 | 银行/券商专表 |
   |---|---|---|
   | revenue | `TOTAL_OPERATE_INCOME` | `OPERATE_INCOME` |
   | total_cost | `TOTAL_OPERATE_COST` | `OPERATE_EXPENSE` |
   | admin_exp | `MANAGE_EXPENSE` | `BUSINESS_MANAGE_EXPENSE`（业务及管理费） |
   | ar | `ACCOUNTS_RECE` | 券商是 `RECEIVABLES` |

#### 映射怎么定的：跨票交集

单票数值匹配会被"两个科目恰好等值"骗到——平安银行没有少数股东权益，`equity_total` 和
`equity_parent` 完全相等，只看它根本分不出哪列是哪个。所以每一类都用 **3 只票取交集**，
三只都指向同一列才采纳：银行 600036/000001/601398，券商 600030/000776/601688。

交集里仍多义的四项，按跟通用表相同的原则取舍：`assets` 取 `TOTAL_ASSETS`（不取恒等的
`TOTAL_LIAB_EQUITY`）、`eps_basic` 取 `BASIC_EPS`（不取 `DILUTED_EPS`）、`net_profit` 取利润表的
`NETPROFIT`（不取现金流量表的同名列）、`ocf` 取 `NETCASH_OPERATE`（不取 `FBNETCASH_OPERATE`）。

完整映射见 `EastMoneyFinancialProvider` 里的 `BankIncomeFullMap` / `BankBalanceMap` /
`BankCashFlowMap` / `BrokerIncomeFullMap` / `BrokerBalanceMap` / `BrokerCashFlowMap`。

### 2.4.1 ⚠ `ORG_TYPE` 的字面值要实测，不能凭语感写

| 机构 | 值 | 实测样本 |
|---|---|---|
| 一般企业 | `通用` | 600519、300750 |
| 银行 | `银行` | 600036、601398、000001 |
| **券商** | **`证券`** | 601377、002926、600030… 共 10 只 |
| 保险 | `保险` | 601318、601601、601628、601336、601319 |

**这里踩过**：券商想当然写成了"券商"，结果所有券商都被当成通用票、去读对券商为空的 G 表，
整只票科目全缺——不报错，只在比对里表现为"某只票缺 104 格"。**字面值错一个字，一整类公司的数据就没了。**

### 2.5 保险公司的支出科目东财整体不填 → 这 5 家走新浪

查实（2026-09-10，三家保险 × 三张表 × 2026-06-30）：

| | 新浪（库里） | 东财 |
|---|---|---|
| 平安 601318 | 221,773,000,000 | `NET_COMPENSATE_EXPENSE` = **null** |
| 中国人寿 601628 | 70,442,000,000 | **null** |
| 中国太保 601601 | 119,071,000,000 | **null** |

**列在、数据不在**：`GINCOME` 有 `NET_COMPENSATE_EXPENSE` 这一列，保险专表 `IINCOME` 有
`COMPENSATE_EXPENSE`，连 `RPT_DMSK_FN_INCOME` 摘要表也有——三处全是 null，三家公司全空。
东财把它并进营业支出了（平安 `OPERATE_EXPENSE` 444,310,000,000 含着这 221,773），不拆。
同一组的 `SURRENDER_VALUE`（退保金）、`POLICY_BONUS_EXPENSE`（保单红利）、
`REINSURE_EXPENSE`（分保费用）**也全是空的**——不是丢一个字段，是保险公司的支出结构整组没有。

### 2.5.1 为什么不能丢：它有真实消费方，而且替代品覆盖不了

`BrokerInsurerHealthCheckBuilder` 第 03 条用它算**赔付率**（赔付支出 ÷ 已赚保费）并给同比。
同一份体检里第 02 条有从年报 PDF 解析来的综合成本率（权威口径），但两者覆盖差得远：

| 指标 | 来源 | 覆盖 |
|---|---|---|
| 赔付支出/已赚保费（粗口径） | 新浪利润表 `claim_expense` | **5 家 × 392 期，1997 年起** |
| 综合成本率（权威口径） | 年报 PDF `combined_ratio` | 3 家 × 10 期，2024 年起 |
| 车险综合成本率 | 同上 | 1 家 × 2 期 |

粗口径那条代码注释自己写着"只适合自比趋势"——而自比趋势正需要长历史，恰恰是 PDF 那条没有的。
**所以 PDF 替代不了它。**

### 2.5.2 决定：保险整只票走新浪（2026-09-10 用户拍板，数据质量第一）

`ORG_TYPE = 保险` 的票（A 股 5 家：601318 平安、601601 太保、601628 国寿、601336 新华、
601319 人保）**整只票**由 `SinaFinancialProvider` 抓，其余全部走东财。

- 代价：5 家 × 3 请求，占全量 1.7 万请求的万分之三；判断逻辑就一个分支。
- **不是字段级拼接**：一只票的所有科目仍来自同一个源，`FinancialReport` 里不会出现
  "利润表东财、赔付新浪"这种混源行。
- 因此 `SinaFinancialProvider` **不退役**，从"备用退路"变成"这 5 家的常用路径"。
- ⚠ 这 5 家仍吃着中文行名匹配的风险（版式一改就静默丢科目），所以要加针对性护栏：
  抓完这 5 家后 `premium_earned` / `claim_expense` 为空就**告警**，不能静默通过。

怎么判断是保险：先用东财的 `ORG_TYPE`（一次 G 表请求就带回来），拿不到时退回内置的 5 只代码名单
——名单会过时，`ORG_TYPE` 才是主判据。

## 3. 接口与请求量

```
GET https://datacenter.eastmoney.com/securities/api/data/v1/get
    ?reportName=RPT_F10_FINANCE_{G|B|S|I}{INCOME|BALANCE|CASHFLOW}
    &columns=ALL&pageSize=500&pageNumber=1
    &filter=(SECUCODE="600519.SH")&sortColumns=REPORT_DATE&sortTypes=-1
```

- 一次拿**全部报告期**（茅台 103 期，`pages=1`，无重复报告期）→ **3 请求/股**，与新浪持平。
- 单位是**元**，与库里一致（茅台四科目逐值相同，见 §5.1）。
- 全量重抓 5788 只 ≈ **1.7 万请求**，datacenter 是三个域名里最宽松的。

## 4. 结构：先迁框架，再换源

财务这一项符合更正后的迁移判据（老方式耦合、维护成本高），而且**它自己写了一套 `MaxFinancialFetchPerRun`（每轮 300 只）+ `maxCount` 参数**，正是 `TaskRunArgs.MaxItems` 要收编的东西——框架注释原话："每个子类各写一遍就会各写错一遍"。

顺序**先迁框架、后换源**：换源的代码直接写在新结构里，省一遍搬运；而且每步验收标准单一（迁框架验"行为一致"，换源验"数据一致"）。

### 4.1 迁框架（provider 仍是新浪）

`FinancialTask : FetchTaskBase<FinancialValue>`，一只票一批（流式，抓一只存一只）。

要一起搬过去的：

| 现在在 orchestrator 里 | 迁移后 |
|---|---|
| pending 判据（`keys_version` 落后 / 报告期落后 / 停牌豁免 / 自选优先） | 搬进 `FetchAsync` 的取数前置段 |
| `MaxFinancialFetchPerRun`（每轮 300 只）+ `maxCount` 参数 | **删掉，改用框架的 `MaxItems`** |
| "必须顺序处理"的信号量 FIFO 约束 | 保留（provider 内部限流器的语义，跟框架无关） |
| `RunFetchFinancialsForCodesAsync`（第二入口，券商保险监管指标在用） | 保留为 Task 的一个参数化入口，或维持独立方法 —— 见下 |

⚠ **第二入口是这次迁移最容易漏的地方**：`RunFetchFinancialsForCodesAsync` 是【券商保险监管指标】按钮调的（只补那一百来只），它和主流程共用 provider 和落库路径。迁移时要么让它也走 Task（传一份 code 名单进去），要么明确留在 orchestrator 里但复用同一个 provider——**不能两边各写一遍落库**。

验收：迁移前后各跑同一批 20 只票，`FinancialReport` 逐行一致；`MaxItems` 生效（本轮只跑 N 只）；中途停止后已落库的票完整、未跑的票状态不变。

### 4.2 换源

- 新增 `EastMoneyFinancialProvider : IFinancialProvider`（`GetAllAsync(code)` 签名不变），按 §2 的映射表取数，按 `ORG_TYPE` 选表。
- 配置开关 `FinancialSource = "sina" | "eastmoney"`，JSONC 模板里写成一行去掉 `//` 就能用。
- `FetchTaskCatalog`：`Sources` 从 `[Sina]` 改成 `[EmDataCenter]`，`QuotaGroup` 从 `Sina` 改成 `Mixed`，文案改"东财"。不改的话调度侧准入按错的源算。
- **旧 provider 不删**，类注释写明退役原因（照 `ExchangeSinaIndustryProvider` 的先例）。

## 4b. 全量比对暴露出来的两类"差异"（2026-09-10）

比对工具：`FinancialParityTests`（默认跳过，`FIN_PARITY=1` 才联网跑）。**它直接 new 产品的
provider**，不另写一套抓取逻辑——行业换源那次的教训是"验证脚本和产品代码各写一套过滤，等于没验证"。

### 4b.1 新浪的"幽灵 0"：不是东财缺数据，是新浪多写了

比对第一版把"库里有、东财没有"一律算成缺失，于是报出银行缺 `ap`/`rd_exp`/`inv_decrease`
上千格。查库才发现**这些值在库里全是 `0.00`，而且 85 家银行一个不落**——银行根本没有应付账款、
存货这些科目。

**是新浪那条路把"这家公司没有的科目"写成了 0，东财老实地不给。** 写 0 的一方才是错的：
"科目不存在"和"科目等于 0"是两回事，后者会让研发强度、存货周转这类比率算出一个假的 0。
所以判据改成：库里是 0 而东财没给 → 记为"幽灵 0"，不计入差异。

### 4b.2 银行的口径差异：东财更完整，新浪漏了一层

平安银行 2020-12-31，三个科目两家系统性不一致，而且库里那三个值在东财整张 `BINCOME`
**一列都对不上**（不是映射错，是口径不同）：

| 科目 | 东财 | 库里(新浪) |
|---|---|---|
| total_cost（营业支出） | 1166.33 亿 | 462.15 亿 |
| interest_net | 1134.70 亿 | 996.50 亿 |
| fee_net | 296.61 亿 | 434.81 亿 |

`total_cost` 这条能判出对错：东财 `BUSINESS_MANAGE_EXPENSE`（业务及管理费）446.90 亿 ＋
该行 2020 年信用减值损失约 700 亿 ≈ 1147 亿，正是东财给的数——**新浪只取到业务及管理费那一层，
漏掉了信用减值损失**。旁证：东财 `OPERATE_INCOME` 1535.42 亿与平安银行 2020 年报披露的营业收入一致。

`interest_net` / `fee_net` 的差异还没判出谁对（东财自洽：利息收入 2010.07 − 利息支出 875.37 ＝
1134.70），需要核对年报原文才能定论。**这类差异只影响银行那 85 家，但换源会改写历史数值，
切默认前要单独拍板。**

## 5. 验收

### 5.1 已经验过的（2026-09-10 沙箱）

600519 / 2026-06-30，东财 vs 库里：营业总收入 92,278,072,083.21、营业成本 9,473,762,565.88、归母净利 44,516,880,421.86、EPS 35.57 —— **四个值完全相同**，单位同为元。研发费用库里是空的、东财有 114,926,219.60。

### 5.2 上线前必须做的

1. **样本逐格比对**：随机 200 只（含 20 家银行、10 家券商、5 家保险、20 只退市股）× 全部报告期 × 60 个 key，与库里逐格比对，容差 0。每条差异都要能说清原因，说不清就不上线。
   - 重点盯**机构专表那 6 个 key**（银行毛/净额陷阱就在这），以及 `UNASSIGN_RPOFIT` 这类拼写怪异的列。
2. **报告期不重复**：同一 `(code, report_date)` 只能有一行（茅台 103 期实测无重复；批量抓要带主键去重自检，照 `DedupeAndWarn` 的做法）。
3. **覆盖不回退**：换源后每只票的报告期数 ≥ 换源前，逐票核对；少了的要逐个查明。

### 5.3 全量切换

`FinancialKeys.Version` 4 → 5。现有增量逻辑会把全部 5788 只判为"科目集落后"重抓，且**版本落后不认停牌豁免**——正好是要的行为，不用写迁移代码。1.7 万请求，跨天分轮跑。

## 6. 旧数据怎么处理

- **全量重抓替换**，不是只换增量。理由：同一张表里前后期来自不同源，同比/环比会出现口径断层，而 `FinancialReport` **没有 `source` 列**，事后分不出哪行是谁给的。
- 落库语义天然干净：`SqliteFinancialRepository` 本来就是"按股票整只 `DELETE` 再插入"，重抓一只就整只替换。
- **切换前 `VACUUM INTO` 导出一份 `FinancialReport` 留档**（1760 万行），验收通过再删。
- **保险那 5 家不受影响**：它们整只走新浪（§2.5.2），重抓写回的仍是含 `claim_expense` 的完整科目集。

## 7. 风险

1. **映射错了不报错**。这是最大的风险，所以 §2 全部用数值匹配证明，而不是看列名像不像。上线前的 200 只逐格比对是第二道闸。
2. **机构类型判错**：`ORG_TYPE` 若与实际不符（比如某家券商标成通用），那几个专有科目会全空。落库前加一条自检：银行/券商/保险的特征科目全空时告警。
3. **追溯调整**：东财同一报告期只有一行（实测无重复），但若将来出现调整前后两版，要定"取哪一版"的规则（建议取 `UPDATE_DATE` 最新的），并在去重自检里打印样例。
4. **沙箱 ≠ 用户环境**：本文的可达性与分页稳定性来自沙箱出口。落地前用 Debug 实例在用户环境复核一次（行业那次就是在实机才抓出非 A 股混入的 bug——**验证脚本和产品代码用两套过滤等于没验证**）。
