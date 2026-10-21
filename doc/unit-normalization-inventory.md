# 入库单位统一：盘点清单（2026-10-20）

**目标（用户定）**：入库一律存基本单位——股、元、份；比例一律存**百分数**（8.46 表示 8.46%）。
在解析（provider）时换算，存量一次性迁移，使用方不再各自换算。分批做，`Bar.volume` 最后单独做。

**起因**：分析程序事件栏把 301489 的限售解禁显示成"2969.61股，25.19万元，占流通 0.43%"，
实际是 2969.61 万股、25.19 亿元、42.68%——库里的值是对的（逐列跟东财原始响应比对一致），
是使用方忘了这张表存的是万股、万元、小数。

盘点方法：子代理扫建表语句 / 模型注释 / provider 解析 / 所有读取代码；再拿库里实际取值做乘除互证
（成交额÷成交量≈成交价、变动股数÷总股本≈比例、解禁股数×股价≈解禁市值）。

## 1. 要改的列

| 表.列 | 现在存的 | 改成 | 谁写入（API 字段） | 谁在读 | 列名带单位 |
|---|---|---|---|---|---|
| **ShareLift.lift_shares** | 万股 | 股 | EastMoneyMarketEventProvider:155（CURRENT_FREE_SHARES） | SqliteStockEventSource:136 **当成股显示（bug）** | |
| **ShareLift.lift_market_cap** | 万元 | 元 | :156（LIFT_MARKET_CAP） | 同上 **当成元显示（bug）** | |
| **ShareLift.free_ratio / total_ratio** | 小数 | 百分数 | :157/:158（FREE_RATIO / TOTALSHARES_RATIO） | 同上 **当成百分数显示（bug）**；total_ratio 无人读 | |
| ShareLift.pre_free_shares / non_free_shares | 万股 | 股 | :162/:163（FREE_SHARES / NON_FREE_SHARES） | 无人读 | |
| **HolderChange.change_shares / after_shares** | 万股 | 股 | :243/:249（CHANGE_NUM_SYMBOL / AFTER_HOLDER_NUM） | SqliteStockEventSource:196 自己拼"万股"（补偿，要一起改）；after_shares 无人读 | |
| **Lhb.amount** | 万元（东财给元，解析时**特意 ÷1e4** 跟老新浪数据对齐） | 元 | EastMoneyLhbProvider:139（ACCUM_AMOUNT）；老行来自新浪"成交额(万元)" | SqliteStockDossierReader:749 用 Money() **当成元显示（bug）** | |
| Lhb.volume | 万股（老新浪行；现在恒为空） | 股 | SinaLhbProvider:114 | SqliteStockDossierReader:749 | |
| **LhbSeat.buy_ratio / sell_ratio** | 小数 | 百分数 | EastMoneyLhbSeatProvider:136-137（TOTAL_BUYRIO / TOTAL_SELLRIO） | 无人读 | |
| **BlockTrade.premium_ratio** | 小数（建表注释错写成 %） | 百分数 | EastMoneyMarketEventProvider:210（PREMIUM_RATIO） | SqliteCapitalDiagnosisReader:357 → CapitalDiagnosisAnalyzer:818 **自己 ×100（补偿，要一起改）** | |
| **EtfShare.shares_wan** | 万份（沪市原样；深市从份 **÷1e4**） | 份 | SseEtfShareProvider:136；SzseEtfShareProvider:140 | EtfTurnoverRecalcTask / FullAuditTask → **EtfTurnoverRule:53（换手率＝手÷万份，单位一变公式要跟着改）** | **是**，改名 shares |
| **CompanyProfile.reg_capital_wan** | 万元 | — | EastMoneyCompanyProfileProvider:84（REG_CAPITAL） | 无人读 | **是**；同表 reg_capital 已是元，这一列是重复——建议删列（值等于 reg_capital÷1e4） |
| **IpoIssue.issue_num / total_issue_num** | 万股 | 股 | EastMoneyIpoIssueProvider（通用） | 无人读 | |
| **IpoIssue.total_raise_funds / predict_raise_funds** | 亿元 | 元 | 同上 | 无人读 | |
| **IpoIssue.netsumfina / dec_sumfina** | ⚠ **沪深亿元、北交所元**（002051：4.23；920229：148,708,374） | 元 | 同上 | 无人读 | |
| **IpoIssue.online_va_shares** | ⚠ **沪深万股、北交所股** | 股 | 同上（另有 online_vashares_nonunit 已是股） | 无人读 | |
| IpoIssue.offline_vats / total_apply_num | 万股（对照 *_nonunit / enquiry_share_num 已是股） | 股 | 同上 | 无人读 | |
| IpoIssue.top_apply_marketcap / apply_amt_upper | 万元 | 元 | 同上 | 无人读 | |
| ~~IpoIssue 待查证~~ | 已查清、都不用改：orig_transfer_num 股（占发行量 0.01~1）、offline_vap_ratio 百分数（＝配售量÷有效申购量×100，3677 行吻合）、predict_onfund_upper 元、per_shares_income 每签元、netflowin 元 | — | — | 无人读 | |
| **Bar.volume**（最后单独做） | 手（个股，科创板解析时 股→手）；ETF 注释互相矛盾（手 / 份）；指数、板块是成分加总 | 股 | Tencent/Sina/EastMoney BarFetcher；BoardIndexSynthesizer；BarAggregator | 图表、短线/金叉/RisingLows 引擎（比值，单位抵消）；**值体检 V6（50~250 判"手"）、BarVolumeUnit、EtfTurnoverRule 硬依赖手**；MarketEnvironmentCalculator:67 当成股算成交额（比值里抵消） | |

## 2. 不用改（已核对是基本单位）

- 元 / 股：FinancialReport、NetInflow、NetInflowDetail、EarningsForecast.amount_*、EarningsExpress、TopShareholder.shares、
  ShareholderCount.avg_shares、MarginDetail、Board.amount、BlockTrade.deal_volume/deal_amount、LhbSeat.buy/sell/net、
  Lhb.billboard_*、PlanAnnouncement.cum_*、OrderWinAnnouncement、FundamentalMetric、StockCustomerSupplier、CompanyProfile.reg_capital、IpoIssue.issue_price/total_shares/*_nonunit。
- 已在解析时换成基本单位的（现有惯例）：Bar.amount（腾讯万元 ×1e4）、流通市值（新浪万元 ×1e4）、回购/中标公告文本里的万/亿、两融故意选元/股口径的 xlsx。
- 比例已是百分数：Bar.turnover、Board.change_pct、IndexWeight.weight、TopShareholder.ratio、Lhb 各比例、LhbSeat.rise_prob_3day/change_rate、
  NetInflowDetail 各占比、BlockTrade 其余比例、ShareLift.before20/after20_change、**HolderChange.change_ratio（2026-10-20 拿 8 只票按
  变动股数÷总股本验算，逐只吻合）**/after_ratio/change_free_ratio、EarningsForecast/Express 各同比、BankRegulatoryMetric、
  IndustryIndicatorValue、PlanAnnouncement、StockCustomerSupplier、CompanySubsidiary、IpoIssue 各涨幅/中签率。
- 另一种口径、不在这次范围：Dividend / RightsIssue 的"每 10 股"口径（列名已写明）。

## 3. 顺带查出的现有显示 bug（换单位时一并消失）

1. 分析程序事件栏：限售解禁（SqliteStockEventSource:136-138）——股数、市值、比例三处都错。
2. 个股档案龙虎榜表：成交额（SqliteStockDossierReader:749）——21.79 亿显示成"21.79万"。
3. 建表注释写错：BlockTrade.premium_ratio、ShareLift.free_ratio/total_ratio 写的是 %，实际是小数。

## 4. 分批

- **第一批** ✅ 2026-10-20 已做：ShareLift、IpoIssue（含沪深/北交所单位不一致的几列）、HolderChange（两个显示问题之一的补偿要一起改）。
  - 解析时换算走 `Logic/Services/UnitScale`（按 decimal 乘，没有浮点尾巴）；IpoIssue 系数表 `IpoIssueColumns.UnitFactors`，按同一行 IS_BEIJING 选沪深/北交所系数。
  - 存量迁移 `SqliteDataMigrations`（记账表 `DataMigration`，id `unit_batch1`）。拿生产库三张表的副本实跑：31,833 / 112,056 / 5,976 行，
    逐格比"迁移前×系数" 0 差（ShareLift 279,898 格、HolderChange 618,942 格、IpoIssue 359,054 格），第二次启动不再改；耗时 2.2 秒。
  - IpoIssue 全量响应离线核对 851,640 格 0 差，另用同行 *_nonunit 列独立互证 10,763 对 0 差。
  - ⚠ 迁移后不能再运行旧版 Fetcher（会按老单位写回），Fetcher 和 Analyzer 要一起换新版。
- **第二批** ✅ 2026-10-20 已做：Lhb.amount、LhbSeat 比例、BlockTrade.premium_ratio（+诊断那处 ×100 删掉）、EtfShare（改名 shares + EtfTurnoverRule 公式）、CompanyProfile.reg_capital_wan（用户定删列）。
  - 盘点时发现 Lhb 已经**没有新浪老行**（26.96 万行全是东财，09-10 换源整段替换过），volume 全表为空；备用的新浪源解析也改成存股/元。
  - 一张表一条迁移、一个事务、做完 checkpoint（`unit_batch2_*`）。生产库五张表副本实跑：Lhb 269,592 / LhbSeat 1,793,655 / BlockTrade 578,206 /
    EtfShare 1,532,246 行，耗时约 20 秒、WAL 峰值 468MB；逐格比"迁移前×系数"6,526 万格 0 差，第二次启动不再改。
  - ETF 换手率公式写成"手 ÷ (份 ÷ 1e4)"：份 ÷ 1e4 跟原来存的万份**逐位相同**（153 万行全验），所以【ETF换手率校正】算出来的值跟改之前逐位一致，不会凭空重写。
  - Lhb.amount 有 426 行带分（源给 215642184.58 元这种），换算后干净、没有浮点尾巴。
- **最后单独做**：Bar.volume ✅ 2026-10-20 已做（代码 + 演练；生产库的迁移在用户启动新版 Fetcher 时自动跑）。
  - 解析出口 `BarVolumeUnit.ToShares`（腾讯主板/ETF/指数 ×100、科创板原样；新浪原样；东财 ×100——东财 K 线原来不走这里）。
  - 存量：一次性任务【成交量改存股】，按 rowid 每 50 万一批、一批一个事务（改值+游标），能停能续；整数行 SQL 乘、
    带小数的走 decimal（SQLite 的 ROUND 会把精确整数 41779124800 变成 41779124799.999992，不能用）；不碰 fetched_at。
  - 没做完之前：注册表准入闸（`FetchTaskRegistry.RunGate` ← `BarVolumeMigrationGate`）不放行别的任务；新版 Fetcher 启动自动开跑；Analyzer 状态栏提醒。
  - 依赖"手"的三处：值体检 V6 区间 [50,250] → [0.5,2.5]；ETF 换手率写成 `(量÷100)÷(份÷1e4)` 保持逐位一致；
    【统一成交量单位】退役（它会把全库除回手）。

每批：provider 解析时换算 → 存量迁移（带版本标记，只换一次）→ 改掉使用方的补偿 → 抽样跟源接口比对 → 测试 → 更新数据字典。
