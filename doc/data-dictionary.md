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
- **数据源原则**：优先 交易所官方 > 中证/巨潮 > 新浪/腾讯 > 东方财富。各表数据源固定，不随"数据源"下拉切换（那个只管 K线/市值/资金流）。
  - ⚠ **2026-09-03 更正**：此前写的"一律避开东方财富（用户环境不可达）"是错的。实测东财**三个域名分别限流、可达性不同**：`datacenter-web`（报表）和 `push2his`（历史资金流）在本机可达且稳定；只有 `push2`（实时行情/板块成分）需要人工过一次反爬验证才放行。
  - 因此新增了一批东财独有的数据（板块成分、业绩预告/快报、龙虎榜营业部席位、分档资金流、大宗交易、机构调研、限售解禁、股东增减持）——这些新浪/腾讯/交易所/巨潮都不提供，**没有回退源**，东财不可用时只能跳过。
  - **但已有官方一手来源的绝不换东财**：融资余额（交易所）、指数权重（中证）、公告（巨潮）都是一手，换成东财是质量降级。
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
|  | `BankRegulatoryMetric` | **银行/券商/保险的监管指标**（不良率、拨备覆盖率、偿付能力…只在财报正文 PDF 里） |
|  | `BankReportFetchState` | 上面那张表的抓取/解析状态（失败要留痕，见该节） |
|  | `EarningsSchedule` | **定期报告预约披露日**（首次预约 + 三次变更 + 实际披露） |
|  | `EarningsForecast` 🆕 | **业绩预告**（比财报早一个月以上，含公司自述的变动原因） |
|  | `EarningsExpress` 🆕 | 业绩快报（介于预告与正式财报之间，非强制披露） |
| 资金 | `NetInflow` | 主力/资金净流入（日频，**只有合计一个字段**） |
|  | `NetInflowDetail` 🆕 | **分档资金流**（超大/大/中/小单的净额+净占比，仅最近约120交易日） |
|  | `MarginDetail` | 融资融券明细（融资余额，仅两融标的） |
| 龙虎榜 | `Lhb` | 每日龙虎榜上榜记录（**无营业部名单**） |
|  | `LhbSeat` 🆕 | **买卖前五营业部明细**（含营业部代码、该席位3日胜率） |
| 筹码/事件 | `BlockTrade` 🆕 | 大宗交易（买卖双方营业部、折溢价率） |
|  | `HolderChange` 🆕 | 股东增减持（期间谁在买卖、多少、什么价） |
|  | `ShareLift` 🆕 | 限售解禁（**含未来解禁计划**，是日程表不是历史表） |
|  | `OrgSurvey` 🆕 | 机构调研（参与调研的机构名单） |
| 板块/指数 | `Board` / `BoardMember` | 板块行情快照 / 成分股 |
|  | `BoardMemberFetchState` 🆕 | 成分股逐板块抓取进度（2500请求跑不完是常态，靠它断点续传） |
|  | `IndexCons` / `IndexWeight` | 指数成分名单 / 成分权重（版本化） |
|  | `EtfIndexMap` | ETF → 跟踪指数（名称匹配，供反查） |
| 元数据 | `StockMeta` | 全部标的的名称/类型 |
|  | `StockIndustry` | 行业归属（证监会门类 + 大类） |
|  | `DelistedStock` | 退市股名单（消除回测幸存者偏差） |
| 公告 | `OrderWinAnnouncement` | 中标/订单类公告 |

> 🆕 = 2026-09-03 新增（东财）。这批数据的共同点：**本地此前完全没有、且没有回退源**。
> 其中 `LhbSeat`/`NetInflowDetail` 跟已有的 `Lhb`/`NetInflow` 是**不同粒度、不是替换**——
> 老表只有汇总（"上榜了""主力净流入多少"），新表才有结构（"是谁在买""哪一档在买"）。

> **首轮跑完后的实测体检（2026-09-04）**——用之前要记住这几条，都是「不报错但会算错」的坑：
>
> | 坑 | 实情 | 用的时候要怎么做 |
> |---|---|---|
> | `BlockTrade` 只有一半是股票 | A股 48%、可转债/债券 41%、基金 2%、其他 9%（B股、国债） | 按代码前缀筛 `00/30/60/68/4/8`，否则折溢价率分布被债券整个带偏（含债券时看着是 -100%~+290%，只看A股才是 -76%~+80%、均值 -6.3%） |
> | `premium_ratio` 是**小数不是百分数** | `-0.0628` = 折价 6.28%。跟 `deal_price/close_price-1` 逐行核对 57.4 万行 **100% 相符** | 展示时自己乘 100 |
> | `OrgSurvey` 只有**滚动一年** | 接口自报 283,922 行、最早 2025-09-05；其余各表都回溯到 2016 | 不是抓漏了，是接口就这么多。想要长历史只能靠每天抓、慢慢养 |
> | `ShareLift` 含**未来**日期 | 2,848 行是还没发生的解禁计划（最远 2035） | 当日程表用；做历史统计要加 `free_date <= 今天` |
> | 各表都有少量非 A 股 | B股/北交所等，占 1%~6% | 同上，按前缀筛 |

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

### EarningsSchedule — 定期报告预约披露日

**数据源**：巨潮资讯网 `/new/information/getPrbookInfo`（页面入口 `data/yypl`），深沪京全覆盖。
**更新**：计划任务【拉取财报预约日】，每工作日；每次整期全量覆盖，不做增量。
**成本**：一期全市场 5500 条，`pagesize=8000` 一个请求 0.3 秒拿完。

**⚠ 分页参数是全小写的 `pagenum` / `pagesize`**，写成驼峰服务端不报错也不生效、永远只回第一页 10 条——
踩过一次：日志显示"抓了 5550 条"，其实是同样 10 只重复 555 次，入库按主键去重只剩 10 条。

**⚠ 只给最近两期**（如 2026 半年报 + 2026 一季）。填别的报告期一律返回 0 条，所以**拿不到更早的历史**，
可用报告期要先问 `getSelectData`。由此带来一段**空窗**：两期都披露完、下一期预约表还没发布时，
全市场都没有"下次财报日"——界面那一列会退回显示"最近一次已披露"并压成灰色，两者含义完全不同。

**为什么每天都要抓**：预约日**会改**，实测深沪京全市场 6.8% 改过（沪市样本 12%），而且
**提前的比延后的还多**（55% vs 44%，最多提前 44 天）。提前那半边更要紧——按原日期盯的话，
财报已经出了还不知道。抓一次当定论是不行的。

**用途**：主动仓/自选股/底仓三处的"财报日"列（临近 7 天标红）、晨检的跨财报持仓提醒。
底仓关心它是因为**分红方案跟年报一起公布**——派息缩水或连续分红中断，持有理由本身就变了。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 6位股票代码 |
| report_period | TEXT | 报告期，如 2026-06-30 |
| appoint_date | TEXT | 首次预约披露日 |
| change1 | TEXT | 一次变更后的日期（空=没改过） |
| change2 | TEXT | 二次变更 |
| change3 | TEXT | 三次变更（最多改三次） |
| actual_date | TEXT | 实际披露日（**空=还没披露**，这一条才是"下次财报"） |
| fetched_at | TEXT | 抓取时刻 |
| | | **主键** (code, report_period) |

> 取"当前有效日期"的顺序：`actual_date` → `change3` → `change2` → `change1` → `appoint_date`。

### BankRegulatoryMetric — 银行/券商/保险的监管指标

**数据源**：巨潮公告里的**财报 PDF 正文**（下载 + 解析，见 `BankRegulatoryFetcher`），
文本层抽不出来时退回 OCR（tesseract，chi_sim+eng）。**更新**：计划任务【金融监管指标】，每月。
只有 42 家银行 + 券商 + 保险有。

**为什么不并进 `FinancialReport`**：① 数据源完全不同（PDF vs 新浪三表接口），抓取状态和重试逻辑
要独立；② 那张表存金额（元），这里全是比率（%），混存极易出单位 bug，而且比率的"同比"是
**百分点差**不是百分比变化；③ 塞进 5000+ 只股票的表里是噪音。

**⚠ `basis`（口径）是主键的一部分**：核心一级资本充足率同时披露"高级法"和"权重法"两个数
（招行 2026H1 分别是 14.07% 和 11.84%），只有六大行+招行等少数获批高级法。
**横向比较必须用权重法**，否则行业分位会被算歪。其它指标 basis 为空串（不能用 NULL，它在主键里）。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 6位股票代码 |
| report_date | TEXT | 报告期（只抓年报和中报） |
| metric_key | TEXT | 指标键，如 npl_ratio / provision_coverage / core_tier1_car |
| basis | TEXT | 口径：`''` / `weighted`(权重法) / `advanced`(高级法) |
| value | REAL | 指标值（%） |
| standard_value | TEXT | 监管标准值，报表自带（"≥25" / "≤10"） |
| source_page | INTEGER | 取自 PDF 第几页，便于人工回查 |
| fetched_at | TEXT | 抓取时刻 |
| | | **主键** (code, report_date, metric_key, basis) |

### BankReportFetchState — 上面那张表的抓取/解析状态

**为什么要单独留痕**：某家银行改了版式、或者遇到没有文本层的扫描件时，如果静默跳过，
界面上的"无数据"就分不清是【没抓】【抓失败】还是【这一项本来就取不到】——这是完全不同的三件事。
`pdf_path` 让已下载的 PDF 留在本地，解析规则改进后能**不重新下载就重跑**（幂等自愈）。

| 字段 | 类型 | 含义 |
|---|---|---|
| code / report_date | TEXT | 主键 |
| status | TEXT | ok / no_pdf / no_text / no_match / error |
| metric_count | INTEGER | 成功解析出几个指标 |
| message | TEXT | 失败原因 |
| pdf_url | TEXT | 来源 URL，缓存被清理后可重新下载 |
| pdf_path | TEXT | 本地缓存路径（相对 `data/reports`） |
| fetched_at | TEXT | 抓取时刻 |

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

### StockIndustry — 行业归属

**数据源**：两所官网（证监会**门类** A~S，覆盖沪深全部）+ 新浪（证监会**大类**，约覆盖 58%）。
**更新**：计划任务【拉取行业分类】，每月。

**两级并存的原因**：门类太粗（"制造业"一个门类装了两千多家，做行业中性化等于没做），
大类才有区分度（"汽车制造业"）；但大类覆盖不全，所以**大类优先、门类兜底**。
FactorLab 里行业中性化的覆盖率就是这么来的（实测 105 个行业、覆盖 5343 只）。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 6位股票代码（**主键**） |
| class_code | TEXT | 证监会门类代码 A~S |
| class_name | TEXT | 门类名称，如"制造业"（太粗，仅作兜底） |
| major_name | TEXT | 证监会大类名称，如"汽车制造业"（优先用这个） |
| fetched_at | TEXT | 抓取时刻 |

### StockIndustryEm — 东财三级行业归属

**数据源**：东财 `RPT_F10_CORETHEME_BOARDTYPE`（走 datacenter，**不碰 push2**）。
**更新**：计划任务【拉取个股行业与题材】，季度定期组，排在【拉取行业分类】之后。

**为什么在 StockIndustry 之外再来一张**：证监会那套粒度不够用——实测 **1867 只（32.5%）
大类为空**、只能退回门类，而"制造业"一个门类装了 3596 只（占 62%）。拿它做行业中性化
等于没中性化。东财是**三级**（一级 31 / 二级 128 / 三级 337），最大的三级行业也才 627 只。

**两份并存、不替换**：东财覆盖 5644/5753 只，剩下约 109 只仍要靠 `StockIndustry` 兜底，
所以那张老表**不能删**。取用顺序：东财最细一级 → 证监会大类 → 证监会门类。

**是快照表**（接口没有时间维度），每次全量重取约 9.4 万行、188 页。写入是"先抓到第一批
才清表"——接口挂了的话库里旧数据原样保留，不会被清空。

⚠ 抓取时排序键必须是 `SECURITY_CODE,BOARD_CODE` 两列：一只股票有十几行，单列排序时
同键行跨页顺序不定，实测前 3 页 1500 行里重了 15 行（＝也丢了 15 行）。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 6位股票代码（**主键**之一） |
| board_code | TEXT | 板块代码 BKxxxx（**主键**之一，取自 NEW_BOARD_CODE） |
| board_name | TEXT | 行业名，如"白酒" |
| board_level | INT | 1/2/3 级。**中性化用 level 最大的那条** |
| fetched_at | TEXT | 抓取时刻 |

### StockThemeEm — 东财题材归属（带入选理由）

**数据源**：同上一张表，一次抓取同时产出（靠 `BOARD_TYPE` 区分，非"行业"的都归这里）。

**独有价值**：`reason` 是**入选理由原文**（取自互动易回复、公告），配合 `is_precise`
精确匹配标记，是判断"实质业务 vs 蹭概念"目前唯一能自动化的判据。实测题材行约 52%
带理由——没有理由的多是指数类板块（HS300_、深成500、融资融券这类），本来就没有理由可言。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 6位股票代码（**主键**之一） |
| board_code | TEXT | 板块代码 BKxxxx（**主键**之一） |
| board_name | TEXT | 题材名，如"液冷服务器" |
| is_precise | INT | 1=精确匹配（有实质业务佐证），0=宽泛归类 |
| board_rank | INT | 东财给的该题材在这只股票上的排序 |
| reason | TEXT | 入选理由原文，可能为空 |
| fetched_at | TEXT | 抓取时刻 |

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
