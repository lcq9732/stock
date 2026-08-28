# 数据字典（current.sqlite）

> 本文档描述本地 SQLite 数据库的全部表与字段。**权威 schema 以 `src/StockPlatform.Data/Sqlite/SqliteSchema.cs` 为准**（本文档随它同步维护）。
> `current.sqlite` 是 Fetcher 抓取写入的库，Analyzer 和手机端都只读打开同一个文件（2026-08-21 起不再有 `total.sqlite` 副本，见[数据平台设计](data-platform-design.md)的 2026-08-21 变更记录）。
> 最后更新：2026-07-30。

## 通用约定

- **所有日期都是 TEXT**：交易日 / 报告期 / 基准日用 `yyyy-MM-dd`；`fetched_at`（抓取墙钟时刻）用 `yyyy-MM-dd HH:mm:ss`。
- **`fetched_at`**：几乎每张表都有，记录"这行是什么时候抓的"，用于收盘确认（≥当天16点才算最终值）、增量水位线判断。
- **代码（code）格式**分两种，混用会撞主键，所以刻意区分：
  - **6 位裸代码**（`600519`）：个股、指数成分股、股东、龙虎榜、融资余额、指数代码（IndexCons/IndexWeight 的 `index_code`）。
  - **带前缀 8 位符号**（`sh000001` 指数 / `sh510300` ETF / `gn_xxx`、`new_xxx` 板块指数）：只出现在 `Bar.code` 和 `EtfIndexMap.etf_code`。选股扫描（`GetAllCodes`）只认 6 位纯数字，带前缀的天然被挡在个股选股外。
- **数据源原则**：一律避开东方财富（用户环境不可达），优先 交易所官方 > 新浪 > 中证/巨潮。各表数据源固定，不随"数据源"下拉切换（那个只管 K线/市值/资金流）。
- **更新入口**（见 Fetcher 界面）：
  - *每日* → 「拉取全部」/「拉取当天」（含定时版）自动带；
  - *每日数据的历史* → 「一键补齐每日历史」（一次性）；
  - *季度/定期* → 「一键拉取定期数据」、「拉取板块」；
  - *往年回补* → 「拉取区间数据」。

## 分类总览

| 分类 | 表 | 一句话 |
|---|---|---|
| 行情 | `Bar` | 日/周/月 K线（个股+指数+ETF+板块指数） |
| 基本面 | `FundamentalMetric` | 流通市值（可扩展的通用指标表） |
|  | `ShareholderCount` | 股东户数（按报告期） |
|  | `TopShareholder` | 十大股东 / 十大流通股东 |
|  | `Dividend` | 分红送配（历年方案，每10股口径） |
|  | `FinancialReport` | **财务三表关键科目（52个，长表）** |
|  | `FinancialFetchState` | 财务抓取状态（报告期+科目集版本，供增量判断） |
| 资金 | `NetInflow` | 主力/资金净流入（日频） |
|  | `MarginDetail` | 融资融券明细（融资余额，仅两融标的） |
| 龙虎榜 | `Lhb` | 每日龙虎榜上榜记录 |
| 板块/指数 | `Board` / `BoardMember` | 板块行情快照 / 成分股 |
|  | `IndexCons` / `IndexWeight` | 指数成分名单 / 成分权重（版本化） |
|  | `EtfIndexMap` | ETF → 跟踪指数（名称匹配，供反查） |
| 元数据 | `StockMeta` | 全部标的的名称/类型 |
|  | `DelistedStock` | 退市股名单（消除回测幸存者偏差） |
| 公告 | `OrderWinAnnouncement` | 中标/订单类公告 |

---

## 行情

### Bar — K线
个股、大盘指数、ETF、板块指数的日/周/月 K线。**数据源**：K线走所选数据源（默认 Tencent，可切 Sina/EastMoney）；周/月线=本地按日线聚合（`BarAggregator`）；板块指数=本地等权合成（`BoardIndexSynthesizer`，非官方数值）。**更新**：拉取全部（每股水位线：首次回看 N 年、之后增量）/拉取当天（指定日）/拉取区间数据（回补往年）；"今天"用覆盖写入（盘中抓的会被收盘后再抓覆盖修正）。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 个股6位 / 指数`sh000001` / ETF`sh510300` / 板块指数`gn_xxx`·`new_xxx` |
| granularity | TEXT | `day` / `week` / `month` |
| period_start | TEXT | 周期起始日（周=周一，月=月初） |
| open / close / high / low | REAL | 开/收/高/低（前复权） |
| volume | REAL | 成交量 |
| amount | REAL | 成交额（元；2026-07-10 前腾讯老接口为 0，已回填） |
| turnover | REAL | 换手率（%） |
| fetched_at | TEXT | 抓取时刻 |
| | | **主键** (code, granularity, period_start) |

## 基本面

### FundamentalMetric — 通用基本面指标
"维度值代替写死字段"的设计——加新指标不用改表。**目前只有** `circulating_market_cap`（流通市值，元）。**数据源**：新浪股票列表顺带的 `nmc` 字段（`SinaListMarketCapFetcher`）。**更新**：拉取全部/当天全市场扫描，同一交易日重复抓覆盖。`as_of_date`=**该快照所属的交易日**（不是抓取日；2026-08-04 起，盘前/周末/节假日抓到的值会归到上一个交易日，见 data-platform-design.md 9.2节）。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 6位股票代码 |
| metric_key | TEXT | 指标键，目前=`circulating_market_cap` |
| as_of_date | TEXT | 指标日期 |
| value | REAL | 指标值（流通市值=元） |
| source | TEXT | 来源标记 |
| fetched_at | TEXT | 抓取时刻 |
| | | **主键** (code, metric_key, as_of_date) |

### ShareholderCount — 股东户数
**数据源**：新浪股本股东页（`vCI_StockHolder`）。**更新**：一键拉取定期数据；新浪一次返回该股**历年全部报告期**，按 code 删旧写新（历史完整、天然按报告期版本化）。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 6位股票代码 |
| report_date | TEXT | 报告期（截至日期） |
| holder_num | INTEGER | 股东户数 |
| avg_shares | REAL | 户均持股数 |
| fetched_at | TEXT | 抓取时刻 |
| | | **主键** (code, report_date) |

### TopShareholder — 十大股东 / 十大流通股东
**数据源**：新浪股本股东页（十大股东 `vCI_StockHolder`、十大流通股东 `vCI_CirculateStockHolder`）。**更新**：一键拉取定期数据，按 code 删旧写新（含历年报告期）。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 6位股票代码 |
| report_date | TEXT | 报告期 |
| kind | TEXT | `total`=十大股东（占总股本）/ `float`=十大流通股东（占流通股） |
| rank | INTEGER | 名次 1..10 |
| holder_name | TEXT | 股东名称 |
| shares | REAL | 持股数量 |
| ratio | REAL | 占比（%） |
| share_type | TEXT | 股本性质（流通A股/国有法人股…） |
| fetched_at | TEXT | 抓取时刻 |
| | | **主键** (code, report_date, kind, rank) |

### Dividend — 分红送配
**数据源**：新浪分红派息页 `vISSUE_ShareBonus`（表 `sharebonus_1`）。**更新**：一键拉取定期数据（也有独立"拉取分红"按钮），逐只、按 code 删旧写新（含历年全部方案）。**为什么要单独存**：库里的 K线是复权价——前复权把分红效果揉进了价格、反而看不出"哪天除权、每股派多少"，减法式前复权对高分红老股还会算出负价（见 `project_qfq_hfq`）；股息率因子、核对除权除息日都得靠这张表。**口径**：送股/转增/派息均为**每10股**，每股股息 = `dividend_yuan` ÷ 10。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 6位股票代码 |
| announce_date | TEXT | 公告日期（方案标识） |
| bonus_shares | REAL | 送股（每10股送X股） |
| transfer_shares | REAL | 转增（每10股转增X股） |
| dividend_yuan | REAL | 派息税前（每10股派X元）→ 每股股息=X/10 |
| progress | TEXT | 进度：实施/预案/董事会通过/不分配…（算股息率通常只取"实施"） |
| record_date | TEXT | 股权登记日（未实施时为空） |
| ex_date | TEXT | 除权除息日（未实施时为空） |
| fetched_at | TEXT | 抓取时刻 |
| | | **主键** (code, announce_date) |

### FinancialReport — 财务报表关键科目

**数据源**：新浪财经的报表下载接口 `money.finance.sina.com.cn/corp/go.php/vDOWN_{ProfitStatement|BalanceSheet|CashFlow}/displaytype/4/stockid/{code}/ctrl/all.phtml`（GBK 编码 TSV）。**一个请求返回该股上市以来所有报告期的整张报表**（603501 实测 46 个报告期），所以"只抓最新一期"既没必要也没收益——抓一次就是全历史。**更新**：一键拉取定期数据 / 独立的"拉取财务报表"按钮 / "空闲时自动补财务"开关，按 code 删旧写新。退市股同样有数据（乐视网退市后仍在老三板披露）。

⚠ **这个接口的配额比其它接口严得多**，用常规的 3并发/1秒 跑到 100 多个请求就会被返回 HTTP 456（封约 40 分钟）。已单独降速到约 10 请求/分钟、每轮上限 300 只、靠 `FinancialFetchState` 断点续传，全市场要分几天补齐。详见[数据平台设计 6.9](data-platform-design.md#69-财务报表接口的特殊限速与空闲自动补已实现-2026-08-27)。

**增量判断看两个条件**（任一落后就重抓）：`report_date` 是否够新、`keys_version` 是否落后于 `FinancialKeys.Version`。后者是 2026-08-27 加的——只看报告期的话，扩充科目后老数据的 `report_date` 仍是"最新"，新科目永远补不上。状态记在 `FinancialFetchState` 表：

| 列 | 说明 |
|---|---|
| code | 6位股票代码（主键） |
| keys_version | 抓这份数据时的 `FinancialKeys.Version`；没有记录的按 0 算，一律重抓 |
| report_date | 抓到的最新报告期 |
| fetched_at | 抓取时刻 |

**结构是长表**——加科目不用改表、不用迁移：

| 列 | 类型 | 说明 |
|---|---|---|
| code | TEXT | 6位股票代码 |
| report_date | TEXT | 报告期（季度末 0331/0630/0930/1231）。**不是公告日**，数据源没有公告日；消费端按法定披露截止日估计可用时点（见 FactorLab） |
| metric_key | TEXT | 规范化科目键，见下表 |
| value | REAL | 单位**元**。利润表/现金流量表为**年内累计**口径，TTM/单季由消费端换算。例外见下方"单位例外" |
| fetched_at | TEXT | 抓取时刻 |
| | | **主键** (code, report_date, metric_key) |

**科目 2026-08-27 从 8 个扩到 52 个**。原来读完整张表只留 8 行、其余全扔，导致每次做深入分析都得上网查——而网页抓取踩过的坑包括公司名张冠李戴、资产负债表两列标签互换、网页"合同负债"与报表"预收款项"科目混淆、净利率分子口径搞错。扩充只是多留几行，不多发一个请求。

**单位例外**（做单位换算时要跳过这两个）：
- `eps_basic` 是**元/股**
- `share_capital` 数值上是**股数**（A股面值1元）

**三个反直觉的点**（行名是实测下载三张表逐行打印确认的，不是猜的）：
- 新准则的"合同负债"在 TSV 里仍归到**预收款项**这一行（只有网页版显示"合同负债"）
- 所得税那行带"减："前缀，而解析器的 `StripOrdinalPrefix` 只剥"一、二、"，所以备选名里全名和两种冒号都列上了
- 现金流量表**附注**里的"公允价值变动损失"跟利润表"公允价值变动收益"**符号相反**，是两个不同的 key；附注里的"净利润""财务费用""少数股东权益"跟别处同名，而解析器对同名行取第一次出现的，所以这几个一律不从现金流量表取

**为什么附注那 8 个科目值得单独抓**：它们把"净利润 → 经营现金流"的每一步差异都列了出来，实测能对平（603501 2026H1：净利 12.03 + 折旧摊销 6.28 + 减值 1.89 + … − 存货 9.25 − 经营性应收 13.72 + 经营性应付 0.53 + … = 4.08 = 表内直接法数字）。用资产负债表两个时点相减**还原不出来**——实测存货差 5.89 vs 9.25、应收差 7.12 vs 13.72，因为附注口径含合并范围变动、且"经营性应收项目"覆盖应收账款+票据+预付+其他应收。

**利润表**（18 个）

| metric_key | 科目 | 说明 |
|---|---|---|
| `revenue` | Revenue | 营业(总)收入（银行=一、营业收入）。 |
| `total_cost` | TotalCost | 营业总成本（含四费和税金附加，跟 OperCost 不是一回事）。 |
| `oper_cost` | OperCost | 营业成本（银行/券商没有，毛利率因子对它们为空）。 |
| `tax_surcharge` | TaxSurcharge | 营业税金及附加。 |
| `sell_exp` | SellExpense | 销售费用。 |
| `admin_exp` | AdminExpense | 管理费用。 |
| `fin_exp` | FinanceExpense | 财务费用（含汇兑损益，汇率大幅波动时是利润的重要扰动项）。 |
| `rd_exp` | RdExpense | 研发费用。 |
| `impair_loss` | ImpairmentLoss | 资产减值损失。 |
| `fv_gain` | FvChangeGain | 公允价值变动**收益**（利润表口径，赚钱为正）。 |
| `invest_income` | InvestIncome | 投资收益。 |
| `oper_profit` | OperProfit | 营业利润。 |
| `total_profit` | TotalProfit | 利润总额。 |
| `income_tax` | IncomeTax | 所得税费用。 |
| `net_profit` | NetProfit | 净利润（利润表"五、净利润"，含少数股东损益；现金流量表附注里同名行不取，见类注释）。 |
| `np_parent` | NetProfitParent | 归属于母公司所有者的净利润（银行叫"归属于母公司的净利润"）。 |
| `minority_pl` | MinorityPl | 少数股东损益。 |
| `eps_basic` | EpsBasic | 基本每股收益（元/股）。 |

**资产负债表**（20 个）

| metric_key | 科目 | 说明 |
|---|---|---|
| `cash` | Cash | 货币资金。 |
| `note_recv` | NoteReceivable | 应收票据。 |
| `ar` | AccountsReceivable | 应收账款。 |
| `prepay` | Prepayment | 预付款项。 |
| `inventory` | Inventory | 存货。 |
| `cur_assets` | CurrentAssets | 流动资产合计。 |
| `assets` | TotalAssets | — |
| `st_loan` | ShortLoan | 短期借款。 |
| `note_pay` | NotePayable | 应付票据。 |
| `ap` | AccountsPayable | 应付账款。 |
| `advance_recv` | AdvanceReceipts | 预收款项。 |
| `cur_liab` | CurrentLiabilities | 流动负债合计。 |
| `lt_loan` | LongLoan | 长期借款。 |
| `bond_pay` | BondPayable | 应付债券（可转债在转股前挂这里，转股后归零——净资产、股本、资产负债率会同时跳变）。 |
| `liab` | TotalLiabilities | — |
| `share_capital` | ShareCapital | 实收资本(或股本)——**股数**不是金额（A股面值1元，所以数值上等于股本股数）。 |
| `equity_parent` | EquityParent | 归属于母公司股东权益（一般/银行叫法不同，取不到时退回所有者权益合计）。 |
| `minority_equity` | MinorityEquity | 少数股东权益。 |
| `equity_total` | EquityTotal | 所有者权益(或股东权益)合计。 |
| `undist_profit` | UndistributedProfit | 未分配利润（分红能力的上限之一）。 |

**现金流量表（正表）**（6 个）

| metric_key | 科目 | 说明 |
|---|---|---|
| `sales_cash` | SalesCash | 销售商品、提供劳务收到的现金。 |
| `ocf` | Ocf | 经营活动产生的现金流量净额。 |
| `icf` | Icf | 投资活动产生的现金流量净额。 |
| `fcf` | Fcf | 筹资活动产生的现金流量净额。 |
| `capex` | Capex | 购建固定资产、无形资产和其他长期资产所支付的现金（资本开支）。 |
| `cash_end` | CashEnd | 期末现金及现金等价物余额。 |

**现金流量表（附注 / 间接法补充资料）**（8 个）

| metric_key | 科目 | 说明 |
|---|---|---|
| `impair_provision` | ImpairmentProvision | 资产减值准备（附注加回项）。 |
| `depreciation` | Depreciation | 固定资产折旧、油气资产折耗、生产性物资折旧。 |
| `amort_intangible` | AmortIntangible | 无形资产摊销。 |
| `amort_lt_prepaid` | AmortLongPrepaid | 长期待摊费用摊销。 |
| `fv_loss` | FvChangeLoss | 公允价值变动**损失**（附注口径，亏钱为正——跟利润表的 FvChangeGain 符号相反）。 |
| `inv_decrease` | InventoryDecrease | 存货的减少（**负数=存货增加=占用现金**）。 |
| `recv_decrease` | ReceivableDecrease | 经营性应收项目的减少（**负数=应收增加=占用现金**）。 |
| `pay_increase` | PayableIncrease | 经营性应付项目的增加（**正数=占用上游资金=释放现金**）。 |

---

## 资金

### NetInflow — 主力/资金净流入（日频）
**数据源**：新浪资金流向接口（`SinaNetInflowFetcher`，`netamount`=全单量级净流入之和，非仅"主力"）。**更新**：拉取全部/当天，逐只、水位线，新股首次回溯约 60 天。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 6位股票代码 |
| period_start | TEXT | 交易日 |
| main_net_inflow | REAL | 当日净流入（元） |
| fetched_at | TEXT | 抓取时刻 |
| | | **主键** (code, period_start) |

### MarginDetail — 融资融券明细
**数据源**：交易所官方（上交所 `queryMargin` JSON + 深交所 `ShowReport` xlsx）。**只覆盖两融标的（约 4400 只）**，非标的股票天生没有。**更新**：拉取全部/当天带当天；历史用「一键补齐每日历史」（按交易日累积、跳过已有日）。

| 字段 | 类型 | 含义 |
|---|---|---|
| trade_date | TEXT | 交易日 |
| code | TEXT | 6位标的代码 |
| name | TEXT | 简称 |
| margin_balance | REAL | **融资余额**（元） |
| margin_buy | REAL | 融资买入额（元） |
| short_balance | REAL | 融券余额（元） |
| short_volume | REAL | 融券余量（股/份） |
| fetched_at | TEXT | 抓取时刻 |
| | | **主键** (trade_date, code) |

## 龙虎榜

### Lhb — 每日龙虎榜
**数据源**：新浪龙虎榜（`vInvestConsult`）。同一股同一天可因多个"上榜指标"出现多行。**更新**：拉取全部/当天带当天；历史用「一键补齐每日历史」（按日累积）。

| 字段 | 类型 | 含义 |
|---|---|---|
| trade_date | TEXT | 交易日 |
| stock_code | TEXT | 6位股票代码 |
| stock_name | TEXT | 简称 |
| close_price | REAL | 收盘价 |
| deviation | REAL | 对应值（涨跌幅/偏离值，随上榜指标而定） |
| volume | REAL | 成交量 |
| amount | REAL | 成交额 |
| reason | TEXT | 上榜指标（如"日涨幅偏离值达7%的证券"） |
| fetched_at | TEXT | 抓取时刻 |
| | | **主键** (trade_date, stock_code, reason) |

## 板块 / 指数

### Board — 板块行情快照
**数据源**：新浪（概念/题材 + 行业）。当下快照。**更新**：拉取板块（`ReplaceAll` 整体覆盖，只留最近一次）。

| 字段 | 类型 | 含义 |
|---|---|---|
| board_code | TEXT | 板块代码（`gn_xxx` 概念 / `new_xxx` 行业） |
| board_type | INTEGER | 0=概念/题材，1=行业 |
| name | TEXT | 板块名 |
| member_count | INTEGER | 成分股数 |
| change_pct | REAL | 板块涨跌幅（%） |
| amount | REAL | 板块合计成交额（元） |
| leader_code / leader_name | TEXT | 领涨股代码（6位）/名称 |
| as_of | TEXT | 快照时刻 |
| | | **主键** board_code |

### BoardMember — 板块成分股
**更新**：随拉取板块整体覆盖。

| 字段 | 类型 | 含义 |
|---|---|---|
| board_code | TEXT | 板块代码 |
| stock_code | TEXT | 6位成分股代码 |
| | | **主键** (board_code, stock_code) |

### IndexCons — 指数成分名单
**数据源**：新浪"最新成份股目录"（`vII_NewestComponent`，覆盖 732 指数内置清单）。**更新**：一键拉取定期数据，按 index_code 删旧写新（只留最新成分）。

| 字段 | 类型 | 含义 |
|---|---|---|
| index_code | TEXT | 6位指数代码（如 000300） |
| stock_code | TEXT | 6位成分股代码 |
| in_date | TEXT | 纳入日期（仅供单向时点过滤，重建不了历史完整成分） |
| fetched_at | TEXT | 抓取时刻 |
| | | **主键** (index_code, stock_code)；另有索引 `ix_indexcons_stock(stock_code)` 供股票→指数反查 |

### IndexWeight — 指数成分权重（版本化）
**数据源**：中证指数官网 `closeweight.xls`（**仅中证系指数**）。**版本化**：按 `as_of_date`（调样基准日）累积、不删旧，供回测按时点取当期权重（中证只给最新一期，从现在起累积）。

| 字段 | 类型 | 含义 |
|---|---|---|
| index_code | TEXT | 6位指数代码 |
| stock_code | TEXT | 6位成分股代码 |
| weight | REAL | 占指数权重（%） |
| as_of_date | TEXT | 权重基准日（版本键） |
| fetched_at | TEXT | 抓取时刻 |
| | | **主键** (index_code, stock_code, as_of_date) |

### EtfIndexMap — ETF → 跟踪指数（名称匹配）
**数据源**：本地生成——用 ETF 名称匹配指数清单（不是接口）。供"股票→指数→ETF"反查。**更新**：随一键拉取定期数据整体覆盖。

| 字段 | 类型 | 含义 |
|---|---|---|
| etf_code | TEXT | 带前缀8位（`sh510300`） |
| index_code | TEXT | 匹配到的6位指数代码（可空=未匹配） |
| match_type | TEXT | `exact` / `contains` / `unmatched` |
| | | **主键** etf_code；另有索引 `ix_etfindexmap_index(index_code)` 供 JOIN |

## 元数据

### StockMeta — 全部标的清单
所有标的的名称+类型。**个股相关用途只认 type='stock'**（选股扫描、拉取当天清单）；指数/ETF/板块只为"查询"页能搜到。**更新**：各抓取流程 upsert。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 标的代码（个股6位；指数/ETF带前缀8位；板块`gn_xxx`/`new_xxx`） |
| name | TEXT | 名称 |
| type | TEXT | `stock`/`index`/`etf`/`board`/`delisted`（NULL 视为 stock） |
| exchange | TEXT | 交易所 |
| list_date | TEXT | 上市日 |
| last_updated | TEXT | 最后更新时刻 |
| | | **主键** code |

### DelistedStock — 退市股名单
**数据源**：沪深两所官网"终止上市"名单（`ExchangeDelistedListProvider`）。退市股的历史 K线也会补进 `Bar` 表（补到终止日），让回测池含"当年在市、后来退市"的输家，**消除幸存者偏差**。**更新**：拉取区间数据。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 6位A股代码 |
| name | TEXT | 终止上市时的简称（如"乐视退"） |
| exchange | TEXT | `sse`=上交所 / `szse`=深交所 |
| list_date | TEXT | 上市日 |
| delist_date | TEXT | 终止上市日（上交所转板/合并的行可能为 NULL） |
| fetched_at | TEXT | 抓取时刻 |
| tail_fetched_at | TEXT | 已尝试补"最后几天K线"的时间（停牌后退市的股票靠它避免每天徒劳重抓） |
| | | **主键** code |

## 公告

### OrderWinAnnouncement — 中标/订单公告
**数据源**：巨潮资讯网全文检索 + 东财公告正文提取金额。**更新**：拉取全部（回看约14天）/拉取当天（当天），按界面"公告关键词"搜索。主键含 title 去重。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 6位股票代码 |
| name | TEXT | 股票简称 |
| title | TEXT | 公告标题 |
| publish_date | TEXT | 发布日期 |
| keyword | TEXT | 命中的关键词 |
| art_code | TEXT | 巨潮公告编号 |
| pdf_url | TEXT | 公告PDF链接 |
| content | TEXT | 正文/摘要 |
| total_amount_yuan | REAL | 从正文提取的金额（元） |
| source | TEXT | 来源 |
| fetched_at | TEXT | 抓取时刻 |
| | | **主键** (code, title, publish_date) |

---

## 跨表关系（反查链）

- **股票 → ETF**：`IndexCons`（股票→所属指数）→ `EtfIndexMap`（指数→跟踪它的ETF）。
- **股票 → 板块**：`BoardMember`（股票→板块）→ `Board`（板块行情）。
- **标的名称/类型**：任意 code → `StockMeta`（个股/指数/ETF/板块）或 `DelistedStock`（退市股）。
- **彬哥法选股条件**用到：`ShareholderCount`（股东户数环比）、`MarginDetail`（融资余额增长）、`FundamentalMetric`（流通市值）、`Bar`（K线）。
