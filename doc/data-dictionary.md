# 数据字典（current.sqlite）

> 本文档描述本地 SQLite 数据库的全部表与字段。**权威 schema 以 `src/StockPlatform.Data/Sqlite/SqliteSchema.cs` 为准**（本文档随它同步维护）。
> `current.sqlite` 是 Fetcher 抓取写入的库，Analyzer 和手机端都只读打开同一个文件（2026-08-21 起不再有 `total.sqlite` 副本，见[数据平台设计](data-platform-design.md)的 2026-08-21 变更记录）。
> 最后更新：2026-09-09（新增[数据起点](#数据起点每张表最早能有哪一天)、[抓取状态](#抓取状态)两节）。
>
> **本地没有的字段去哪找**：东财"条件选股"接口能一次拿到全市场 5423 只 × 167 个字段（1.55 秒、无需认证），
> 其中 53 个是本地既没有、也算不出来的——质押比例、商誉、机构持股六分类、机构一致预期、扣非净利、
> DDX、增仓占比、股吧人气排名。**评估后决定暂不入库**（理由见该文档末尾），但字段名、单位、覆盖率、
> 质量比对结论都记好了：[东财条件选股接口 — 可取字段清单](eastmoney-selection-api.md)。
> 要用某个字段时先查那份文档的"本地状态"列，就知道该查库还是调接口。

## 通用约定

- **所有日期都是 TEXT**：交易日 / 报告期 / 基准日用 `yyyy-MM-dd`；`fetched_at`（抓取墙钟时刻）用 `yyyy-MM-dd HH:mm:ss`。
- **`fetched_at`**：几乎每张表都有，记录"这行是什么时候抓的"，用于收盘确认（≥当天16点才算最终值）、增量水位线判断。
- **代码（code）格式**分两种，混用会撞主键，所以刻意区分：
  - **6 位裸代码**（`600519`）：个股、指数成分股、股东、龙虎榜、融资余额、指数代码（IndexCons/IndexWeight 的 `index_code`）。
  - **带前缀 8 位符号**（`sh000001` 指数 / `sh510300` ETF / `gn_xxx`、`new_xxx` 板块指数）：只出现在 `Bar.code` 和 `EtfIndexMap.etf_code`。选股扫描（`GetAllCodes`）只认 6 位纯数字，带前缀的天然被挡在个股选股外。
- **数据源原则**：优先 交易所官方 > 中证/巨潮 > 新浪/腾讯 > 东方财富。各表数据源固定，不随"数据源"下拉切换（那个只管 K线/市值/资金流）。
  - ⚠ **2026-09-03 更正**：此前写的"一律避开东方财富（用户环境不可达）"是错的。实测东财**三个域名分别限流、可达性不同**：`datacenter-web`（报表）和 `push2his`（历史资金流）在本机可达且稳定；只有 `push2`（实时行情/板块成分）需要人工过一次反爬验证才放行。
  - 因此新增了一批东财独有的数据（板块成分、业绩预告/快报、龙虎榜营业部席位、分档资金流、大宗交易、机构调研、限售解禁、股东增减持）——这些新浪/腾讯/交易所/巨潮都不提供，**没有回退源**，东财不可用时只能跳过。
  - ⚠ **2026-09-06 补**：东财行情侧还有一个镜像域名 `push2delay`（延时行情）**在本机可达，而且提供 `push2` 上那个排行接口 `clist/get`**。分档资金流因此有了"某天全市场"的入口：约 60 个请求拿全当天 5900 只，不用再一只只打 5500 次。收盘清算后取到的就是当日终值（行情时间戳 15:34），跟 `push2his` 逐股拿的**逐条比对过、零差异**（4603 只 × 13 个字段）。
  - 但它**只有当天**：历史缺口仍旧只能靠 `push2his` 一只只补（接口给最近约 120 个交易日）。所以这一项现在是两条通道一起跑，见配置 `MoneyFlowChannel`。
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
|  | `NetInflowDetail` 🆕 | **分档资金流**（超大/大/中/小单的净额+净占比，仅最近约120交易日）。当天走 `push2delay` 全市场快照、历史靠 `push2his` 逐股补 |
|  | `MarginDetail` | 融资融券明细（融资余额，仅两融标的） |
| 龙虎榜 | `Lhb` | 每日龙虎榜上榜记录（**无营业部名单**）。2026-09-09 换东财，上榜原因与 `LhbSeat` 同源可 join |
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
| 抓取状态 | `MissingBarConfirmed` | 逐日白名单："这天数据源确实没有"（区分停牌 vs 漏抓） |
|  | `BarProbeFloor` 🆕 | 逐段水位："这天之前数据源没有该票K线"（省掉往年回补的重复空跑） |
|  | `TradingDay` ✨ | 交易日历（深交所官方 + 2004 前本地归纳）——全库判交易日的唯一依据 |
|  | `DailyFetchNoData` ✨ | 日频表的空日名单："这天这个源确实没有"（龙虎榜/融资余额回补跳过它） |

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
> | `BlockTrade` 主键**不用改** | 2026-09-07 评估过：`(trade_date, code, daily_rank)` 是对的。单次查询内 `DAILY_RANK` 不重复（实测 2016-03-24 002049 的 16 行无重复），先前的撞车告警全部来自翻页重复，跟主键设计无关 | 别去动主键——改它要整表重建，而真正的病根是排序键 |

> **补字段（2026-09-06）**——datacenter 客户端一直用 `columns=ALL`，下面这些字段**本来就跟着
> 返回了，只是当初没解析**，补它们不产生任何新请求。口径全部用实抓样本在
> `MarketEventExtraFieldsTests` 里钉死了。
>
> | 表 | 新增列 | 要注意的 |
> |---|---|---|
> | `BlockTrade` | `buyer_code` `seller_code` `discount_ratio` `free_shares_ratio` `total_shares_ratio` `change_rate_1d/5d/10d/20d` | 营业部**代码**才能跨时间追席位（名称会改）；`change_rate_*` 是**滞后字段**，见下 |
> | `ShareLift` | `pre_free_shares` `non_free_shares` `before20_change` `after20_change` | `pre_free_shares` 是 `free_ratio` 的分母（解禁前已流通），别跟 `lift_shares`（本次解禁）弄混 |
> | `HolderChange` | `change_free_ratio` `close_price` `real_price` `change_rate_quotes` | `change_free_ratio` 占**流通股**，比占总股本的 `change_ratio` 更能说明冲击；`change_rate_quotes` 老数据为空 |
>
> | 新坑 | 实情 | 用的时候要怎么做 |
> |---|---|---|
> | `discount_ratio` 名不副实，且**跟 `premium_ratio` 单位、基准都不同** | 同一行 000338：`premium_ratio` = 0.075 = 成交价/**当日收盘**−1（**小数**）；`discount_ratio` = 5.9859 = 成交价/**前收盘**−1（**百分数**）。两个样本各自吻合到 10 位 | 别照名字用，也别拿一个推另一个。要折溢价用 `premium_ratio` 并自己乘 100 |
> | `BlockTrade.change_rate_*` 是**滞后字段** | 东财事后才算：2026-09-04 那批股票行**全是 null**，2016-01-05 / 2020-06-10 的全部有值 | 只抓「水位线→今天」这四列永远填不上。增量已改为额外回看 30 天（`LaggingFieldLookbackDays`），历史靠回填 |
> | `ShareLift` 的比例是**小数**，涨跌幅是**百分数** | 同一行 000065：`free_ratio` = 0.0846 = 8.46%，而 `before20_change` = -4.81 = -4.81% | 同一张表里两种单位，混用差 100 倍 |
> | `change_rate_quotes` 实测**全期 0 行有值** | 2026-09-07 回填 11.1 万行后统计：0%。东财这一列根本不填，不是抓漏了 | 别用。列留着不占空间，等东财哪天开始给再说 |
> | ⚠ 排序键不足→**静默丢行**（2026-09-07 已修） | `BlockTrade` 原排序 `TRADE_DATE,SECURITY_CODE` 定不了唯一序（一只股票一天几十笔），深分页跨页重复。实测 2016-03：2435 行返回、5 行重复、入库只剩 2430。全期累计**丢 773 行（0.164%）**；`HolderChange` 丢 120 行 | 已给两张表补上定序列（`DAILY_RANK` / `HOLDER_NAME,END_DATE`），实测重复归零。**要重新发布才生效**，且需重跑一次回填把丢的行补回来 |
> | ⚠ `HolderChange.change_ratio` 原先**取错字段**（2026-09-06 已修） | 原先取接口的 `CHANGE_RATE`，那是**公告日股价涨跌幅**，跟增减持无关——实测 200 条里「增持」44% 为负、「减持」58% 为正，还有增持而为 0 的。现改取 `AFTER_CHANGE_RATE`（变动占总股本比例） | **修正前落库的 11.1 万行 `change_ratio` 全是股价涨跌幅**，回填后才是对的。回填前请勿使用该列 |

## 数据起点：每张表最早能有哪一天

> 用的人问的第一个问题往往是"这张表能回溯到什么时候"。下面分三类，**说的都是数据源能给到的最早一天，不是库里现在实际最早的那天**（后者取决于抓过哪些区间，只会更晚）。
> 回补历史时把起点填得比这里更早，不会多抓到任何东西，但会让"本地已补齐就跳过"的判断失效、每只标的都白发一次请求——2026-09-06 那次融资余额从 1990 年起跑就是这么来的。

**一、固定业务起点**（这天之前该业务根本不存在，数据源也没有）

| 表 | 最早 | 依据 |
|---|---|---|
| `Bar`（个股/指数/ETF） | **1990-12-19** | A股开市首日。硬常量 `AShareMarketOpen`（[FetchOrchestrator.cs:163](../src/StockPlatform.Data/Orchestration/FetchOrchestrator.cs#L163)），刻意不取"日历首日"——日历自己缺哪段就瞎哪段 |
| `MarginDetail` | **2010-03-31** | 融资融券首批试点开市当天（6家券商、90只标的），两所在这之前没有任何一天的明细可发（[ExchangeMarginProvider.cs:30](../src/StockPlatform.Data/Remote/ExchangeMarginProvider.cs#L30)） |
| `Lhb` / `LhbSeat` | **2004-06-25** | 东财 `RPT_DAILYBILLBOARD_DETAILSNEW` 自报的第一天（按 TRADE_DATE 升序取首行实测，[EastMoneyLhbProvider.cs](../src/StockPlatform.Data/Remote/EastMoneyLhbProvider.cs)）。本地库里最早的一行也正是这天——新浪那版声称 2002-01-01（"公开信息制度"起点的保守猜测），但它一行都没给出过 2004-06-25 之前的数据。⚠ 跟两融的 2010 无关，两者常被混为一谈 |
| `NetInflow` | **2010-03-01** | 新浪源实测能给出的最早一天（600519 回包 3964 行，最早正是这天）（[SinaNetInflowFetcher.cs:43](../src/StockPlatform.Data/Remote/SinaNetInflowFetcher.cs#L43)）。⚠ 东财源标了同一天，但**那是在新浪上测的**，东财自己能给到哪天没验过 |

**二、滚动窗口**（跟哪一年无关，接口只给最近一段，想要长历史只能每天抓、慢慢养）

| 表 | 窗口 | 说明 |
|---|---|---|
| `NetInflowDetail` | 最近约 **120 个交易日** | `push2his` 的窗口。当天那根走 `push2delay` 全市场快照，历史缺口只能逐股补 |
| `OrgSurvey` | 滚动 **1 年** | 接口自报 283,922 行、最早 2025-09-05（2026-09-04 实测）；不是抓漏了 |

**三、快照型：没有历史**（接口只给"现在"，起点＝第一次抓它的那天）

`Board` / `BoardMember`、`FundamentalMetric`（流通市值）、`IndexCons` / `IndexWeight`、`StockMeta`、`EtfIndexMap`。
回补区间时这几项会被**明确跳过**，不是漏了。

**四、按报告期或公告日的**（跟交易日历无关，起点由抓取范围决定）

| 表 | 库中实测最早 | 说明 |
|---|---|---|
| `Dividend` | 1991-04-03 | 新浪的"历年分红方案"页一次返回该股全部历史，**一次请求就能到最早**；缺的是没抓过的票，不是抓不到早的年份（2026-09-06 起已含退市股） |
| `RightsIssue` | 1992-06-04 | 同上 |
| `FinancialReport`、`ShareholderCount`、`TopShareholder`、`EarningsSchedule` | 按报告期，实际取决于抓过哪些期 | — |
| `BlockTrade`、`ShareLift`、`HolderChange`、`EarningsForecast`、`EarningsExpress` | 2016 起 | 首轮按 2016 起抓（`OrgSurvey` 除外，见上） |

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
**数据源**：东财 datacenter `RPT_DAILYBILLBOARD_DETAILSNEW`（2026-09-09 从新浪换过来，
配置项 `LhbSource` 可切回 `sina`）。同一股同一天可因多个"上榜指标"出现多行。
**更新**：计划任务【龙虎榜】，日更，增量回看 31 个交易日（补滞后字段）。

**为什么换源**：东财的上榜原因是**交易所原文**，跟 `LhbSeat.explanation` 同源，
两张表终于能按 (日期,代码,原因) join。换之前逐条比对过（2026-09-08 单日）：
两源票集 55 vs 55 双向零差异、55 只收盘价与简称全对上、成交额换算比值精确 1.000000。

⚠ **新浪时代的历史行有个已知缺陷**：新浪把交易所原文归并成 28 种粗类，而 `deviation`
仍跟着各自的原规则走，于是同一个 `reason` 下混着语义不同的值（2026-08-04 创业板那批
reason 全是"涨幅偏离值达7%的证券"，对应值却分别是当日涨跌幅 20.0、两日累计 39.8、
多日累计 31.54）。**别拿这段历史当真值校验任何东西**。跑一次【龙虎榜·换源重抓】
可以把这段用东财原文整段覆盖掉。

| 字段 | 类型 | 含义 |
|---|---|---|
| trade_date | TEXT | 交易日 |
| stock_code | TEXT | 6位股票代码 |
| stock_name | TEXT | 简称 |
| close_price | REAL | 收盘价 |
| deviation | REAL | 对应值（随上榜指标而定）。东财不给，本地派生，来路见 `deviation_source` |
| volume | REAL | 成交量（万股）。**东财源下恒为 NULL**，理由见下方"两列缺口" |
| amount | REAL | 成交额（**万元**；东财的 `ACCUM_AMOUNT` 是元，写库前 ÷1e4） |
| reason | TEXT | 上榜指标。东财源下是**交易所原文**（"有价格涨跌幅限制的日收盘价格涨幅偏离值达到7%的前五只证券"），新浪源下是粗类（"涨幅偏离值达7%的证券"） |
| fetched_at | TEXT | 抓取时刻 |
| change_rate | REAL | 当日涨跌幅 %（东财源独有，下同） |
| turnover_rate | REAL | 当日换手率 % |
| free_market_cap | REAL | 流通市值（元） |
| billboard_buy_amt | REAL | 龙虎榜买入额（元） |
| billboard_sell_amt | REAL | 龙虎榜卖出额（元） |
| billboard_net_amt | REAL | 龙虎榜净买额（元） |
| billboard_deal_amt | REAL | 龙虎榜成交额（元） |
| deal_amount_ratio | REAL | 龙虎榜成交额占总成交比 % |
| deal_net_ratio | REAL | 龙虎榜净买额占总成交比 % |
| explain_text | TEXT | 东财"解读"，如"普通席位买入，成功率36.00%"。⚠ 列名不能叫 `explain`，那是 SQLite 关键字 |
| trade_id | TEXT | 榜单流水号，与 `LhbSeat.trade_id` 同源 |
| change_type | TEXT | 东财异动类型编码 |
| trade_market | TEXT | 上市板，如"上交所主板" |
| d1_chg … d30_chg | REAL | 上榜后 1/2/5/10/20/30 日涨跌幅 %。**滞后字段**：抓取当天一律 NULL，N 个交易日后东财才填上——所以增量要回看 31 个交易日重抓覆盖 |
| source | TEXT | `em` / `sina` |
| deviation_source | TEXT | `源` / `派生` / `派生-未核验` / 空 |
| | | **主键** (trade_date, stock_code, reason) |

**两列缺口：东财不给 `deviation` 和 `volume`**

`deviation` 按上榜原因分类补，规则全部拿真值验证过（2026-09-09，东财 2026-07-10~08-31
共 3000 行按原文分类，跟库里新浪那份历史比，|差| < 0.05 算命中）：

| 上榜原因类型 | 取值 | `deviation_source` | 命中率 |
|---|---|---|---|
| 换手率类 | 东财 `TURNOVERRATE` | `源` | 98.3% 主板 / 91.6% 非主板 |
| 涨跌幅达X%类 | 东财 `CHANGE_RATE` | `源` | 85.9% |
| 单日偏离值 | 个股涨跌幅 − 对应指数涨跌幅 | `派生` | 95.3% 主板 |
| 振幅类 | (最高 − 最低) / **最低** | `派生` | 100% 主板 / 66.7% 非主板 |
| 连续N日累计偏离值 | **留空** | 空 | — |
| 比值倍数 / 退市整理 / 无涨跌幅限制 / 融资买入占比 | 留空 | 空 | — |

- **偏离值的基准指数**按板块选（`MarketIndexCatalog.DeviationBenchmarkFor`）：沪主板→上证综指、
  **深主板→深证综指 399106**（不是深证成指 399001，配错的话命中率从 70.6% 掉到 2.3%）、
  创业板→创业板综 399102、科创板→科创50、北交所→北证50。后三者标 `派生-未核验`：
  没有可信真值能验（唯一的对照——新浪那份——对这三个板块本身就是错配的）。
- **振幅分母是最低价**，反直觉但实测如此：/最低 命中 93.6%，/前收 0.0%，/收盘 9.9%。
- **累计偏离值一律留空**：起算日由交易所判定（哪三天算"连续三个交易日"是它认定异动的窗口），
  本地复现不出来。试过 N 日累计涨幅之差（54.9%）、N−1 跨度（38.8%）、逐日偏离求和（1.0%），
  按原文逐类分组也没有哪一类过 74%。留空是"没有"，写个一半对一半错的值是"有毒"。

`volume` **完全不补**。本想从本地日K取（÷100 换成万股），2026-09-08 的 59 只只对上 39 只，
暴露出 `Bar` 表两个独立毛病：① **volume 单位不一致**——那天全市场主板 3203 只、创业板 1404 只、
北交所 342 只都是"手"，而**科创板 688/689 的 613 只是"股"**；② 部分行的量额本身就少了
（603999 记 6914 万，东财和新浪都说 2.13 亿）。这两个是 `Bar` 自己的问题，不该在龙虎榜这儿
绕过去。量能信息用 `amount`、`turnover_rate`、`billboard_*` 那几列。

## 板块 / 指数

### Board — 板块行情快照
**数据源**：东财（2026-09-03 从新浪整体换过来）。名单三条路依次退：终端本地文件 →
行情中心菜单 JSON → push2 分页。**更新**：计划任务【概念和行业板块】，日更。

**不是 `ReplaceAll` 而是走暂存区**（2026-09-04）：抓一页存一页进 `BoardStaging`，
凑齐了才在一个事务里整体搬进正表。半截名单绝不进正表——Board 是快照语义，
"这轮没出现＝已下架"会连成分股一起删掉，而成分股要 2500 个请求、跨好几轮才攒得齐。

| 字段 | 类型 | 含义 |
|---|---|---|
| board_code | TEXT | 板块代码 `BKxxxx` |
| board_type | INTEGER | 0=概念/题材，1=行业，2=地区 |
| name | TEXT | 板块名 |
| member_count | INTEGER | 成分股数。**由成分股抓取写**，板块列表那条路不碰它 |
| change_pct | REAL | 板块涨跌幅（%）。本地用成分股日K等权合成，不从数据源取 |
| amount | REAL | 板块合计成交额（元），同上 |
| leader_code / leader_name | TEXT | 领涨股代码（6位）/名称 |
| as_of | TEXT | 快照时刻 |
| parent_code | TEXT | 父板块。一级行业、概念、地区都是 NULL |
| board_level | INTEGER | **这一行自己**的层级 1/2/3；概念和地区为 NULL |
| | | **主键** board_code |

**`parent_code` / `board_level` 是另一条路写的**（2026-09-07）：来自东财终端本地文件
`IndustryBlockRelation.dat`，不联网。板块列表天天抓、层级一个季度才变一次，
所以那两条 upsert 语句**绝不能带上这两列**——抓列表时根本不知道父级是谁，一碰就抹成 NULL。
（跟 `member_count` 同一个坑，已有测试钉住。）

只覆盖行业板块：496 个行业全部有层级，概念 504 个和地区 31 个一个都没有。
一级 31 个就是申万那套。写库前会拿 `StockIndustryEm` 还原的真实父子链**逐条**校验，
不一致整批拒绝——⚠ 校验的是**父子归属**不是层级数字：解析器第一版层级数字 932 处全对、
却有 51 条边的父是错的（"银行Ⅱ 挂在石油石化下"）。

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

## 行业景气与产业链

这一组服务的是**传统行业（周期股）分析**那一路，跟风口分析分开看：风口看的是叙事能不能
兑现成别人的报表，周期股看的是价格和库存本身，而价格是日周频的、比季报早一个季度。

### IndustryIndicator — 行业景气指标字典
**数据源**：东财 `RPTA_DATA_IF_INDICATOR`（走 datacenter）。
**更新**：计划任务【行业景气指标】，日更组。

116 个指标：猪粮比价、螺纹钢期货价与库存、焦煤、原油、铜铝锌铅、水泥价格指数、
国房景气指数、全国汽车销量…按频率分：日 45 / 月 55 / 周 14 / 旬 1 / 半年 1。

**能力边界**：覆盖 658 只股票（约 12%），**全是周期股**；存储芯片、AI 这类成长题材一个都没有。
历史只到 2024-04（约 2 年），够看当下位置和同比，**不够跑长周期回测**。

| 字段 | 类型 | 含义 |
|---|---|---|
| indicator_id | TEXT | 东财指标库编号 `EMI00139010` |
| name | TEXT | 展示名，如"全国猪粮比价" |
| orig_name | TEXT | 东财原始口径名"全国大中城市:猪粮比价"。带统计范围，**排查靠它** |
| unit | TEXT | 如"元/公斤"；比值类指标为空 |
| frequency | TEXT | 日 / 周 / 旬 / 月 / 半年 |
| granularity | TEXT | 001=个股，002=行业 |
| chart_type | TEXT | 折线图 72 / 柱状图 44 |
| source | TEXT | 商务部、上市公司公告… |
| | | **主键** indicator_id |

⚠ `granularity` 东财**标注不全准**：涤纶、维生素价格被标成 001（个股），实际是行业价格。
**原样存不纠正**——哪天东财自己改对了，我们的"修正"反倒成了错的那一方。
另外前端代码里有个 `003=产业链`，但线上**一条数据都没有**。

⚠ `chart_type` 不是显示用的，它决定拉历史序列走哪个接口（折线图→LINECHART，
柱状图→BARCHART）。**走错不报错，只返回 0 条**。

### IndustryIndicatorValue — 指标序列
**更新**：随上表，每个指标按自己的水位线增量。

| 字段 | 类型 | 含义 |
|---|---|---|
| indicator_id | TEXT | |
| trade_date | TEXT | 值所属日期，不是写入日 |
| value | REAL | |
| yoy_pct | REAL | 同比%。**只有柱状图那 44 个指标给**，其余 NULL |
| | | **主键** (indicator_id, trade_date) |

**故意不带股票代码**：同一指标在不同股票下的值完全一样（实测玻璃期货价 4 只股票同日同为
1431；7 个多股共享的指标 569 个日期 0 冲突）。带上就是把 116 份序列存成 1190 份重复。
东财顺带返回的 `CLOSE_PRICE` 也丢掉——我们自己有日K，多留一份只会多一个对不上的口径。

⚠ `yoy_pct` 为 NULL 表示"这个指标不给同比"，**不是同比为 0**。混了的话 72 个折线图指标
会全被当成"同比持平"。

### StockIndustryIndicator — 股票 ↔ 指标
1190 条映射。反过来用才是重点：**板块 → 它的成分股关联最多的指标**。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 6 位码 |
| indicator_id | TEXT | |
| indicator_order | INTEGER | 东财给的展示序＝相关性排序，可直接当权重 |
| | | **主键** (code, indicator_id) |

查序列**必须带一只代表股**（接口不带 SECUCODE 会返回股票×日期的笛卡尔积，撞分页上限被
静默截断）。取哪只都行——值完全一样——但必须**稳定**：按 code 排序取第一只，
换来换去的话日志对不上、出了问题没法复现。

### StockCustomerSupplier — 前五大客户/供应商
**数据源**：东财 `RPT_F10_BUSINESS_CUSTSUPP`（走 datacenter）。
**更新**：计划任务【客户与供应商】，季度定期组，紧跟【拉取财务报表】——它们是同一份年报里的
东西，一起更新才不会"财务是新的、客户集中度还是去年的"。

**这是能拿到的最硬的产业链数据**：不是别人的分类判断，是年报里的交易金额——
供应商＝上游，客户＝下游。（"上/中/下游标签"那条路已经查死：东财网页、终端本地文件、
终端「数据」/「分析」菜单、F10 全部栏目都没有。）

76.5 万行 / **2002 年至今** / 2025 年覆盖 5284 只（92%）。
对手名 **53% 是真名**，71% 的股票至少有一个真名对手；其余是"第一名""客户1"这类匿名披露
（公司自己决定，大公司往往匿名）。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 6 位码 |
| report_date | TEXT | 报告期（值所属日期）。含年报/中报/一季报 |
| is_supplier | INTEGER | 0=客户（下游），1=供应商（上游） |
| rank | INTEGER | 1-5 前五名；**6=「其余客户/其余供应商」** |
| partner_name | TEXT | 交易对手名，可能是真名也可能是匿名代号 |
| amount | REAL | 交易金额 |
| pct | REAL | 占该类合计的百分比 = amount / total_amount |
| total_amount | REAL | 该类合计 |
| report_name | TEXT | "2025年报" / "2026中报" |
| partner_code | TEXT | 对手方还原出的上市公司代码；**对不上就是 NULL，不猜** |
| match_type | TEXT | `exact` / `normalized`；NULL = 没对上 |
| | | **主键** (code, report_date, is_supplier, rank) |

⚠ `pct` 在东财那边叫 `TOI_RATIO`（Total Operating Income），**这个名字是骗人的**：
客户组的分母近似营收，但供应商组的分母是**采购总额**——宁德时代 2025 年供应商合计 5774 亿，
比它营收还大。所以本地不沿用那个名字。两组口径不同源，**别混着比**。

**`rank=6` 那行不是冗余，是校验和**：前五 + 其余必须 = 100%，落库后一眼能看出有没有漏行。
而且"其余占比"本身就是集中度的反面（宁德客户其余占 61%，说明不依赖大客户）。

**累积语义，全类没有一句 DELETE**：数据按报告期一期一期出，老报告期的行永远有效。
抓取按年切片（全表 1531 页，单年只有约 128 页），**今年和去年每轮都重抓**——年报是分批
披露的，3 月抓到的只有一部分，只认"有没有数据"的话第一次抓到几百行就再也不补了。

**两种用法，价值和难度差很远**：
- **集中度**（100% 可用）：大客户依赖风险、议价能力变化
- **供应链网络**：`partner_code` 把对手名还原成了上市公司代码，
  实测连出 **6,632 条边、4,008 家公司**（2026-09-09）

⚠ **对手方还原只做两档**：精确（全称一字不差）和归一化（去空白/括号/公司后缀后相等）。
**不做"含简称"的模糊匹配**——实测只多 5 个百分点命中率，却会造出"看着像、其实不是"的错边；
产业链数据有错边比没有更糟，因为你会照着它做判断。
同一全称命中 A/B 股两个代码时优先 A 股（京东方 000725/200725 实测踩过）。

命中率看着低（真名里 2.2%），但那个分母是**不同名字数**——14.4 万个对手名里绝大多数是非上市
小公司，本来就连不上。有意义的指标是边数：中石油这种会在几十家公司的年报里重复出现。
想再往上提，得有**参控股公司名单**（把"中国建筑第六工程局"对到中国建筑），
但东财 F10 里没有这份数据，要啃巨潮年报附注或买第三方，判断投入产出不划算。

⚠ **这里的"产业链"是交易关系不是分层标签**：边只说明"A 是 B 的上游"（相对位置），
答不了"某公司在这条链的第几层"（绝对位置）。图太稀疏（平均每家 3 条边），
拓扑排序推出来的层次经不起用。

### CustSuppYearState — 客户/供应商的按年完成度
**更新**：随上表，每抓完一年记一次。

| 字段 | 类型 | 含义 |
|---|---|---|
| year | INTEGER | **主键** |
| reported | INTEGER | 接口自报的总行数（**含非 A 股主体**） |
| saved | INTEGER | 实际落库行数 |
| skipped | INTEGER | 主动丢掉的行数（非 A 股代码等，约占 16.4%） |
| updated_at | TEXT | |

**判据是 `saved + skipped < reported` 就重抓**，三个数缺一不可——这是两次踩坑换来的：

1. **不能只看"这一年有没有数据"**。新式任务骨架会在 Deadline / MaxItems 到点时
   **从批中间收尾**，而且那算正常完成（界面上打勾）。2023 年一度只有 16,000 行
   （邻年都是 5 万），就是这么悄悄缺的——库里、日志里、界面上都看不出来。
2. **也不能只比 `saved` 和 `reported`**。`reported` 含非 A 股主体而落库的只有 A 股，
   2026-09-09 因此误报过：2023 年 51,677/62,791、2025 年 52,214/62,774，
   差额比例 17.7%/16.8% 正好等于非 A 股占比，那几年会每轮重抓且**永远抓不齐**。

一般原则：**任务的水位线粒度必须细于骨架的截断粒度**。这一项的批是 2000 行、水位线是"年"，
粗了两个数量级所以必须记完成度；而【行业景气指标】一批就是一个指标的完整序列、
水位线是每个指标的 `MAX(trade_date)`，粒度对得上，就不需要这张表。

### CompanyProfile — 公司档案
**数据源**：东财 `RPT_HSF9_BASIC_ORGINFO`（走 datacenter），5634 家 A 股、14 页。
**更新**：计划任务【公司档案】，季度定期组，**排在【客户与供应商】前面**——
后者要拿这里的全称做对手方还原，档案排后面的话同一轮里用的永远是上一轮的旧档案。

**直接用途是实体消歧**：年报里写"福建时代星云科技有限公司"这种全称，本地只有简称"宁德时代"，
对不上；有了 `full_name` 才能把对手方还原成股票代码。但它本身也是一份公司基本面档案，
而且这些字段是**同一个请求一起带回来的**，存下来不额外花抓取成本。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 6 位码，**主键**（每股一行） |
| full_name | TEXT | 全称"宁德时代新能源科技股份有限公司" ← **匹配靠它** |
| abbr / name_en | TEXT | 简称 / 英文名 |
| org_form | TEXT | 企业性质 |
| found_date / listing_date / listing_state | TEXT | 成立日 / 上市日 / 上市状态 |
| reg_capital_wan | REAL | 注册资本（**万元**） |
| reg_capital | REAL | 注册资本（**元**） |
| currency | TEXT | 币种 |
| province / city / district | TEXT | 地域分析用 |
| reg_address / address / postcode | TEXT | 注册地 / 办公地 / 邮编 |
| industry_csrc | TEXT | 证监会行业 |
| emp_num | INTEGER | 员工数（空值率约 12%） |
| legal_person / actual_holder / final_holder | TEXT | 法人 / 实控人 / 最终控制人（后者空值率约 41%） |
| holder_name / holder_ratio | TEXT / REAL | 控股股东及持股比 |
| chairman / president / secretary / publish_person | TEXT | 董事长 / 总经理 / 董秘 / 信披负责人 |
| secretary_tel / org_tel / org_fax / org_email / org_web | TEXT | 联系方式 |
| reg_num | TEXT | 统一社会信用代码 |
| law_firm / accountfirm / cpa | TEXT | 律所 / 会计所 / 签字会计师 |
| ah_change | TEXT | 实控人变更历史 |
| main_business | TEXT | 主营业务，一句话（平均 44 字） |
| org_code_em | TEXT | 东财机构号，排查时对得上它那边 |
| report_date | TEXT | 档案数据截止日 |

⚠ **`reg_capital_wan` 和 `reg_capital` 差 1 万倍**，光看名字看不出来：东财 `REG_CAPITAL`
是万元、`REG_CAPITALY` 是元。宁德时代 462677.041 万 = 4626770410 元。
搞反了整份数据的量级就错，而 4.6 亿和 46 亿看起来都"像个正常的注册资本"。

⚠ 全表 7000 行里有港股等非 A 股，按 `SECUCODE` 后缀过滤只留 SH/SZ/BJ（剩 5634，
库里 6040 含退市股）。

### CompanyNarrative — 公司档案的长文本
跟上表同一份接口响应，**拆两张表存**。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | **主键** |
| org_profile | TEXT | 公司简介（平均 664 字） |
| org_evolution | TEXT | 公司沿革/大事年表（平均 386 字） |
| business_scope | TEXT | 经营范围，工商登记原文（平均 252 字） |
| business_review | TEXT | 经营评述（平均 **4186** 字，最长 46410） |

**为什么拆开**：不是洁癖。`CompanyProfile` 会被消歧那一步**全表读**（5000+ 行），
而 `business_review` 一个字段就占整条记录体积的 **69%**——混在一行里每次全表扫描要多读
3 倍数据，而这些长文本是"查某一家时才看"的东西。拆开后档案约 30MB、长文本约 68MB。

两张表的 code 必须**一一对应**，写入在同一个事务里；条数不等就是有半拉记录，
那会让消歧读到全称却查不到简介（任务收尾会检查并报错）。

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

## 抓取状态

这两张表都不存市场数据，存的是**"抓过、确认拿不到"的结论**。没有它们，抓取程序每轮都要把同一批
必然拿不到的东西重新试一遍，而且日志上看起来跟正常干活一模一样（进度条在走、库里没动静）。

### MissingBarConfirmed — 逐日白名单
**写入**：【重新拉取失败】——全库体检报出的缺口先去抓，连着两轮拿不到才写进来，往后体检跳过。
不是体检直接下的判断。**为什么需要**：停牌那几天在数据上跟"漏抓"长得一模一样（交易日历里有、这只票没有），
区分不了就永远收敛不了。**作废**：【全库数据体检】勾「彻底体检」会清空重查。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 6位代码 |
| granularity | TEXT | `day` / `day_hfq` / `day_raw`（三条线各自独立，某只票可能只在其中一条缺） |
| period_start | TEXT | 确认没有的那个交易日 |
| tries | INTEGER | 确认之前抓过几轮 |
| confirmed_at | TEXT | 确认时刻 |
| | | **主键** (code, granularity, period_start) |

### BarProbeFloor — 逐段水位（2026-09-07 新增）
**写入**：两个入口。①【拉取区间数据】——往前补历史时"请求成功、但返回 0 行"就记一条（判定见
`ProbeFloorPlanner.Plan`，两道前提：本地必须已有这只票的K线、且请求终点早于本地最早一根）。
②【回填"无更早数据"水位】——不联网，直接从本地已有历史推（判据：前/后/不复权三路的最早一根
落在同一天，见 `ProbeFloorPlanner.PlanFromLocalHistory`）；三路不一致的、以及只有前复权一路的
（ETF/大盘指数/板块指数）都不填，留给入口 ① 去真探。
**为什么需要**：一只 2020 年上市的票被请求 1990~2016 必然返回空，而这个结论以前不落库——
2026-09-07 实测一轮区间回补 5558 只 × 3 个粒度 ≈ 一万六千个请求、四个半小时、写入为零。
有了水位，下一轮把缺口起点抬到这天，抬过缺口就整只跳过、连请求都不发。
**跟上面那张表的分工**：那张按天记（量小，十年才几天）；这张按段记，一行顶几千个交易日——
上市前那段用逐日表存要写千万行。**作废**：同上，【全库数据体检】的「彻底体检」会清空。

| 字段 | 类型 | 含义 |
|---|---|---|
| code | TEXT | 6位代码 / 带前缀指数符号 |
| granularity | TEXT | `day` / `day_hfq` / `day_raw` |
| no_data_before | TEXT | 已确认：数据源在这一天**之前**没有该标的该粒度的K线。只抬不降（`MAX`） |
| probed_at | TEXT | 探明时刻 |
| | | **主键** (code, granularity) |

### TradingDay — 交易日历（2026-09-08 新增）
**写入**：【交易日历】任务（`StockPlatform.Tasks/TradingCalendarTask.cs`）。
**两个来源按年份分工**：
- `2005-01` 起 = **深交所官网** `www.szse.cn/api/report/exchange/onepersistenthour/monthList?month=yyyy-MM`
  （要带 `Referer: https://www.szse.cn/`；字段 `jyrq` 日期、`jybz` 1=交易日）。一月一个请求；
  能拿到**已公布**的未来月份（实测 2026-09 时已有 2026-12、2027 全空——交易所年底才发下一年）。
- `2004-12` 及以前 = **本地全市场日K归纳**（深交所接口对那段一律返回空，逐月二分确认过）。
  死历史、建一次固定。本地当时没有那么早的K线就先空着，补过历史K线后用「首次整段回补」重跑一次。

**为什么需要**：在它之前，逐日回补（龙虎榜/融资余额）只跳周末，每个节假日每轮都白发一次请求
（龙虎榜从 2002 年补一轮就是几百个）；临时从 `Bar` 表 `DISTINCT` 归纳则是 23GB 上几十秒的索引全扫。
**⚠ 用之前必须问 `TradingCalendar.CoversFrom`**：日历覆盖不到的区间，"不在日历里"的含义是
**日历不知道**，不是"不是交易日"——2026-09-06 把这两者混为一谈，静默漏抓了 2,360 只老股。

| 字段 | 类型 | 含义 |
|---|---|---|
| day | TEXT | 交易日 `yyyy-MM-dd`（非交易日不入库）**主键** |
| source | TEXT | `szse` 深交所官方 / `local` 本地K线归纳 |

### DailyFetchNoData — 空日名单（2026-09-08 新增）
**写入**：龙虎榜/融资余额的抓取路径（`FetchOrchestrator.BackfillDailyAsync` 等）。
**判据三条缺一不可**：① 正常返回的空（抛异常一律不记——一次网络抽风换永久漏一天）；
② 骨架校验通过（返回的确实是那张页，不是空壳反爬页，见 `SinaLhbProvider`）；
③ 日期在 3 天以前（近几天的空可能只是还没发布：两所融资余额 T+1、龙虎榜当晚才出）。
满足就**一次定案**，不像 `MissingBarConfirmed` 要两轮——同一个源抓第二次不会带来新信息。
**跟 `TradingDay` 的分工**：日历挡掉非交易日，这张挡掉"是交易日、但这个源确实没有"的日子。
**作废**：【全库数据体检】勾「彻底体检」按 dataset 清空（会在日志里报"下一轮要多发多少请求"）。

| 字段 | 类型 | 含义 |
|---|---|---|
| dataset | TEXT | `Lhb` / `MarginDetail`（将来别的日频表直接加） |
| day | TEXT | 交易日 `yyyy-MM-dd` |
| confirmed_at | TEXT | 确认时刻 |
| | | **主键** (dataset, day) |

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
