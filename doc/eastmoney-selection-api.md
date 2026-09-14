# 东财条件选股接口 — 可取字段清单

> 2026-09-06 探明并验证，2026-09-09 整理。**当时的评估结论是暂不接入**（理由见文末）。
> 这份文档的用途是：以后真需要某个字段时，知道能从哪里拿、字段名叫什么、有什么坑。

## 接口

```
GET https://datacenter.eastmoney.com/stock/selection/api/data/get
    ?type=RPTA_APP_STOCKSELECT
    &sty=<字段名,逗号分隔>        # 不支持 ALL，必须显式列举
    &filter=<条件>                # 可空；多条件并列即 AND
    &p=1&ps=10000
    &source=SELECT_SECURITIES&client=PC
```

| 项 | 实测 |
|---|---|
| 单次容量 | **全市场 5423 只 × 167 字段，1.55 秒，23MB**（`ps=10000` 一次拿完，`nextpage=false`） |
| 认证 | **不需要**。无 Cookie、无 Referer、连 User-Agent 都不用，裸请求 HTTP 200 |
| `sty=ALL` | **不支持**，返回空 |
| filter | 数值区间可用：`(PE9>0)(PE9<10)(HOLDNUM_GROWTHRATE_3Q<-5)` 实测正确返回。文本型 `(INDUSTRY="银行")` 返回空，语法待查 |
| 数据口径 | **只有截面，没有历史**。返回里带 `MAX_TRADE_DATE`，即值所属交易日 |
| 域名可达性 | `datacenter` 正常（本机只有 `push2` 不通） |

地址是从东财终端安装目录的配置里挖出来的（`config/Template/free/request_http_url.view.txt`），
但**不依赖东财客户端** —— 装不装都能调。

## 数据质量（已比对，不是抽查）

拿接口结果和东财终端 UI 导出的全市场表做过 5400+ 只的逐字段比对：

| 字段 | 结果 |
|---|---|
| 最新价、涨跌幅 | 5416 只**零差异** |
| 振幅、换手率 | 最大差 0.01 |
| 加权净资产收益率 | 最大差 0.005，无一只超 0.01 |
| 资产负债率 | 最大差 0.005 |
| 每股收益 | **581 只不一致 —— 是口径差异不是错**。接口用财报披露的**加权平均股本**，UI 用期末总股本；不一致的全部是报告期内股本变动过的次新股/增发股（中科仪 8.215 vs 9.72）。**接口口径更规范** |
| 每股净资产 | 625 只不一致，同一原因 |

## 字段清单

- **覆盖率** = 全市场 5423 只里有值的比例。低不一定是缺数据 —— 质押 39.8%、社保持股 11.2% 属于真实稀疏（没质押/没社保持股的公司本来就空）。
- **本地状态**：`已有` = 库里有等价数据；`可算` = 有原始数据能自己算；**`缺口`** = 本地真没有。

### 股票范围（7 个）

| 指标 | API 字段名 | 单位 | 覆盖率 | 本地状态 | 样例(浦发) |
|---|---|---|---|---|---|
| 市场(多选) | `MARKET` |  | 100% | 已有 `StockMeta.exchange` | 上交所主板 |
| 行业 | `INDUSTRY` |  | 100% | 已有 `StockIndustryEm` | 银行 |
| 地区 | `AREA` |  | 100% | **缺口** | 上海 |
| 概念 | `CONCEPT` |  | 100% | 已有 `StockThemeEm` | ['互联网金融', '长江三角', '蚂蚁金服概 |
| 风格 | `STYLE` |  | 100% | **缺口** | ['标准普尔', '融资融券', '金融地产风格 |
| 指数成份 | `INDEX` |  | — | 已有 `IndexCons` |  |
| 上市时间 | `LISTING_DATE` | 年 | 100% | 已有 `StockMeta.list_date` | 1999-11-10 |

### 基本面选股（54 个）

| 指标 | API 字段名 | 单位 | 覆盖率 | 本地状态 | 样例(浦发) |
|---|---|---|---|---|---|
| 市盈率TTM | `PE9` |  | 100% | 可算 | 6.13054704 |
| 市净率MRQ | `PBNEWMRQ` |  | 100% | 可算 | 0.41662451 |
| 市盈率TTM(扣非) | `PETTMDEDUCTED` |  | 100% | **缺口** | 6.14253692 |
| 市销率TTM | `PS9` |  | 100% | 可算 | 1.77260701 |
| 市现率TTM | `PCFJYXJL9` |  | 100% | 可算 | 0.41386361 |
| 预测市盈率(今年) | `PREDICT_PE_SYEAR` |  | 53% | **缺口** | 5.890383219161 |
| 预测市盈率(明年) | `PREDICT_PE_NYEAR` |  | 53% | **缺口** | 5.530426995661 |
| 总市值 | `TOTAL_MARKET_CAP` | 亿 | 100% | 可算 | 314074055169 |
| 流通市值 | `FREE_CAP` | 亿 | 100% | 已有 `FundamentalMetric.circulating_market_cap` | 314074055169 |
| 动态市盈率 | `DTSYL` |  | 100% | 可算 | 5.07373033 |
| 预测PEG | `YCPEG` |  | 53% | **缺口** | 0.892028876931 |
| 企业价值倍数 | `ENTERPRISE_VALUE_MULTIPLE` |  | 98% | 可算 |  |
| 每股收益 | `BASIC_EPS` | 元 | 100% | 已有 `FinancialReport.eps_basic` | 0.89 |
| 每股净资产 | `BVPS` | 元 | 100% | 可算 | 22.634289916672 |
| 每股经营现金流 | `PER_NETCASH_OPERATE` | 元 | 100% | 可算 | 12.14 |
| 每股自由现金流 | `PER_FCFE` | 元 | 98% | 可算 |  |
| 每股资本公积 | `PER_CAPITAL_RESERVE` | 元 | 100% | **缺口** | 4.035849396505 |
| 每股未分配利润 | `PER_UNASSIGN_PROFIT` | 元 | 100% | 可算 | 7.670509818051 |
| 每股盈余公积 | `PER_SURPLUS_RESERVE` | 元 | 98% | **缺口** | 6.041343902 |
| 每股留存收益 | `PER_RETAINED_EARNING` | 元 | 100% | 可算 | 13.71185372005 |
| 归属净利润 | `PARENT_NETPROFIT` | 亿 | 100% | 已有 `FinancialReport.np_parent` | 30951000000 |
| 扣非净利润 | `DEDUCT_NETPROFIT` | 亿 | 100% | **缺口** | 30971000000 |
| 营业总收入 | `TOTAL_OPERATE_INCOME` | 亿 | 100% | 已有 `FinancialReport.revenue` | 93777000000 |
| 净资产收益率ROE | `ROE_WEIGHT` | % | 99% | 可算 | 3.96 |
| 总资产净利率ROA | `JROA` | % | 100% | 可算 | 0.3060852778 |
| 投入资本回报率ROIC | `ROIC` | % | 98% | 可算 |  |
| 最新股息率 | `ZXGXL` | % | 99% | 可算 | 4.4538706257 |
| 毛利率 | `SALE_GPR` | % | 98% | 可算 |  |
| 净利率 | `SALE_NPR` | % | 100% | 可算 | 33.4346374911 |
| 净利润增长率 | `NETPROFIT_YOY_RATIO` | % | 100% | 可算 | 4.082456199348 |
| 扣非净利润增长率 | `DEDUCT_NETPROFIT_GROWTHRATE` | % | 100% | **缺口** | 3.291755602988 |
| 营收增长率 | `TOI_YOY_RATIO` | % | 100% | 可算 | 3.5534844687 |
| 净利润3年复合增长率 | `NETPROFIT_GROWTHRATE_3Y` | % | 100% | 可算 | -0.7574506965 |
| 营收3年复合增长率 | `INCOME_GROWTHRATE_3Y` | % | 100% | 可算 | -2.6605219481 |
| 预测净利润同比增长 | `PREDICT_NETPROFIT_RATIO` | % | 53% | **缺口** | 6.6033548593 |
| 预测营收同比增长 | `PREDICT_INCOME_RATIO` | % | 53% | **缺口** | 3.8755555337 |
| 每股收益同比增长率 | `BASICEPS_YOY_RATIO` | % | 100% | 可算 | -10.101010101 |
| 利润总额同比增长率 | `TOTAL_PROFIT_GROWTHRATE` | % | 100% | 可算 | 9.982497435 |
| 营业利润同比增长率 | `OPERATE_PROFIT_GROWTHRATE` | % | 100% | 可算 | 8.922920771389 |
| 资产负债率 | `DEBT_ASSET_RATIO` | % | 100% | 可算 | 91.9206504728 |
| 产权比率 | `EQUITY_RATIO` |  | 99% | 可算 | 11.377234041288 |
| 权益乘数 | `EQUITY_MULTIPLIER` |  | 99% | 可算 | 12.377234041347 |
| 流动比率 | `CURRENT_RATIO` |  | 98% | 可算 |  |
| 速动比率 | `SPEED_RATIO` |  | 98% | 可算 |  |
| 总股本 | `TOTAL_SHARES` | **股** | 100% | **已接入** `FundamentalMetric.total_shares`（2026-09-14） | 33305838300 |
| 流通股本 | `FREE_SHARES` | **股** | 100% | 可算 | 33305838300 |
| 最新股东户数 | `HOLDER_NEWEST` | 万 | 100% | 已有 `ShareholderCount.holder_num` | 163327 |
| 股东户数增长率 | `HOLDER_RATIO` | % | 100% | 可算 | 8.0984 |
| 户均持股金额 | `HOLD_AMOUNT` | 万 | 100% | 可算 | 1676554.27181256 |
| 户均持股数量 | `AVG_HOLD_NUM` | 万 | 100% | 已有 `ShareholderCount.avg_shares` | 203921.20286297 |
| 户均持股数季度增长率 | `HOLDNUM_GROWTHRATE_3Q` | % | 99% | 可算 | -7.4917190667 |
| 户均持股数半年增长率 | `HOLDNUM_GROWTHRATE_HY` | % | 99% | 可算 | -25.103014 |
| 十大股东持股比例合计 | `HOLD_RATIO_COUNT` | % | 100% | 已有 `TopShareholder` | 74.87 |
| 十大流通股东比例合计 | `FREE_HOLD_RATIO` | % | 99% | 已有 `TopShareholder` | 74.870181 |

### 技术面选股（30 个）

| 指标 | API 字段名 | 单位 | 覆盖率 | 本地状态 | 样例(浦发) |
|---|---|---|---|---|---|
| MACD金叉 | `MACD_GOLDEN_FORK` |  | 100% | 可算 (Bar) | 0 |
| KDJ金叉 | `KDJ_GOLDEN_FORK` |  | 100% | 可算 (Bar) | 0 |
| 放量突破 | `BREAK_THROUGH` |  | 100% | 可算 (Bar) | 0 |
| 低位资金净流入 | `LOW_FUNDS_INFLOW` |  | 100% | 可算 (Bar) | 0 |
| 高位资金净流出 | `HIGH_FUNDS_OUTFLOW` |  | 100% | 可算 (Bar) | 0 |
| 向上突破均线 | `BREAKUP_MA` |  | 0% | 可算 (Bar) |  |
| 均线多头排列 | `LONG_AVG_ARRAY` |  | 100% | 可算 (Bar) | 0 |
| 均线空头排列 | `SHORT_AVG_ARRAY` |  | 100% | 可算 (Bar) | 0 |
| 连涨放量 | `UPPER_LARGE_VOLUME` |  | 100% | 可算 (Bar) | 0 |
| 下跌无量 | `DOWN_NARROW_VOLUME` |  | 100% | 可算 (Bar) | 0 |
| 一根大阳线 | `ONE_DAYANG_LINE` |  | 100% | 可算 (Bar) | 0 |
| 两根大阳线 | `TWO_DAYANG_LINES` |  | 100% | 可算 (Bar) | 0 |
| 旭日东升 | `RISE_SUN` |  | 100% | 可算 (Bar) | 0 |
| 强势多方炮 | `POWER_FULGUN` |  | 100% | 可算 (Bar) | 0 |
| 拨云见日 | `RESTORE_JUSTICE` |  | 100% | 可算 (Bar) | 0 |
| 七仙女下凡(七连阴) | `DOWN_7DAYS` |  | 100% | 可算 (Bar) | 0 |
| 八仙过海(八连阳) | `UPPER_8DAYS` |  | 100% | 可算 (Bar) | 0 |
| 九阳神功(九连阳) | `UPPER_9DAYS` |  | 100% | 可算 (Bar) | 0 |
| 四串阳 | `UPPER_4DAYS` |  | 100% | 可算 (Bar) | 0 |
| 天量法则 | `HEAVEN_RULE` |  | 100% | 可算 (Bar) | 0 |
| 放量上攻 | `UPSIDE_VOLUME` |  | 100% | 可算 (Bar) | 0 |
| 穿头破脚 | `BEARISH_ENGULFING` |  | 100% | 可算 (Bar) | 0 |
| 倒转锤头 | `REVERSING_HAMMER` |  | 100% | 可算 (Bar) | 0 |
| 射击之星 | `SHOOTING_STAR` |  | 100% | 可算 (Bar) | 0 |
| 黄昏之星 | `EVENING_STAR` |  | 100% | 可算 (Bar) | 0 |
| 曙光初现 | `FIRST_DAWN` |  | 100% | 可算 (Bar) | 0 |
| 身怀六甲 | `PREGNANT` |  | 100% | 可算 (Bar) | 0 |
| 乌云盖顶 | `BLACK_CLOUD_TOPS` |  | 100% | 可算 (Bar) | 0 |
| 早晨之星 | `MORNING_STAR` |  | 100% | 可算 (Bar) | 0 |
| 窄幅整理 | `NARROW_FINISH` |  | 100% | 可算 (Bar) | 1 |

### 消息面选股（30 个）

| 指标 | API 字段名 | 单位 | 覆盖率 | 本地状态 | 样例(浦发) |
|---|---|---|---|---|---|
| 限售解禁 | `LIMITED_LIFTDATE` |  | 100% | 已有 `ShareLift` | 2020-09-04 |
| 定向增发 | `DIRECTIONAL_SEO_DATE` |  | 56% | **缺口** | 2017-09-06 |
| 资产重组 | `RECAPITALIZE_DATE` |  | 38% | **缺口** |  |
| 股权质押 | `EQUITY_PLEDGE_DATE` |  | 100% | **缺口** | 2013-07-30 |
| 质押比例 | `PLEDGE_RATIO` | % | 40% | **缺口** | 0.02 |
| 商誉规模 | `GOODWILL_SCALE` | 亿 | 50% | **缺口** | 5351000000 |
| 商誉占净资产比例 | `GOODWILL_ASSETS_RATRO` | % | 50% | **缺口** | 0.636504755051 |
| 业绩预告 | `PREDICT_TYPE` |  | 0% | 已有 `EarningsForecast.predict_type` |  |
| 每股股利(税前) | `PAR_DIVIDEND_PRETAX` | 元 | 15% | 已有 `Dividend.dividend_yuan` |  |
| 每股红股 | `PAR_DIVIDEND` | 股 | 0% | 已有 `Dividend.bonus_shares` |  |
| 每股转增股本 | `PAR_IT_EQUITY` | 股 | 0% | 已有 `Dividend.transfer_shares` |  |
| 近3月股东增减比例 | `HOLDER_CHANGE_3M` | % | 8% | 已有 `HolderChange` |  |
| 近3月高管增减比例 | `EXECUTIVE_CHANGE_3M` | % | 13% | **缺口** |  |
| 近3月机构调研 | `ORG_SURVEY_3M` | 次 | 39% | 已有 `OrgSurvey` |  |
| 机构评级 | `ORG_RATING` |  | 52% | **缺口** | 增持+ |
| 机构持股家数合计 | `ALLCORP_NUM` | 家 | 99% | **缺口** | 523 |
| 基金持股家数 | `ALLCORP_FUND_NUM` | 家 | 96% | **缺口** | 513 |
| 券商持股家数 | `ALLCORP_QS_NUM` | 家 | 14% | **缺口** |  |
| QFII持股家数 | `ALLCORP_QFII_NUM` | 家 | 31% | **缺口** |  |
| 保险公司持股家数 | `ALLCORP_BX_NUM` | 家 | 9% | **缺口** | 4 |
| 社保持股家数 | `ALLCORP_SB_NUM` | 家 | 11% | **缺口** |  |
| 信托公司持股家数 | `ALLCORP_XT_NUM` | 家 | 3% | **缺口** |  |
| 机构持股比例合计 | `ALLCORP_RATIO` | % | 99% | **缺口** | 76.23788682 |
| 基金持股比例 | `ALLCORP_FUND_RATIO` | % | 96% | **缺口** | 1.3677 |
| 券商持股比例 | `ALLCORP_QS_RATIO` | % | 14% | **缺口** |  |
| QFII持股比例 | `ALLCORP_QFII_RATIO` | % | 31% | **缺口** |  |
| 保险公司持股比例 | `ALLCORP_BX_RATIO` | % | 9% | **缺口** | 19.9312851 |
| 社保持股比例 | `ALLCORP_SB_RATIO` | % | 11% | **缺口** |  |
| 信托公司持股比例 | `ALLCORP_XT_RATIO` | % | 3% | **缺口** |  |
| 限制控股股东减持 | `HOLDER_JC` |  | 100% | **缺口** | 是 |

### 人气指标（10 个）

| 指标 | API 字段名 | 单位 | 覆盖率 | 本地状态 | 样例(浦发) |
|---|---|---|---|---|---|
| 股吧人气排名 | `POPULARITY_RANK` | 名 | 100% | **缺口** | 726 |
| 人气排名变化 | `RANK_CHANGE` |  | 100% | **缺口** | 2 |
| 人气排名连涨 | `UPP_DAYS` |  | 100% | **缺口** | 1 |
| 人气排名连跌 | `DOWN_DAYS` |  | 100% | **缺口** | 0 |
| 人气排名创新高 | `NEW_HIGH` |  | 100% | **缺口** | 38 |
| 人气排名创新低 | `NEW_DOWN` |  | 100% | **缺口** | 1 |
| 新晋粉丝占比 | `NEWFANS_RATIO` | % | 100% | **缺口** | 48.97 |
| 铁杆粉丝占比 | `BIGFANS_RATIO` | % | 100% | **缺口** | 51.03 |
| 7日关注排名 | `CONCERN_RANK_7DAYS` | 名 | 100% | **缺口** | 1592 |
| 今日浏览排名 | `BROWSE_RANK` | 名 | 94% | **缺口** | 728 |

### 行情数据（36 个）

| 指标 | API 字段名 | 单位 | 覆盖率 | 本地状态 | 样例(浦发) |
|---|---|---|---|---|---|
| 股价 | `NEW_PRICE` | 元 | 100% | 已有 `Bar.close` | 9.43 |
| 涨跌幅 | `CHANGE_RATE` | % | 100% | 已有 `Bar` | 1.73 |
| 振幅 | `AMPLITUDE` | % | 100% | 已有 `Bar` | 2.05 |
| 破发股票 | `IS_ISSUE_BREAK` |  | 100% | **缺口** | 1 |
| 破净股票 | `IS_BPS_BREAK` |  | 100% | 可算 | 1 |
| 今日创历史新高 | `NOW_NEWHIGH` |  | 100% | 可算 | 0 |
| 今日创历史新低 | `NOW_NEWLOW` |  | 100% | 可算 | 0 |
| 近期创历史新高 | `HIGH_RECENT` |  | 0% | 可算 |  |
| 近期创历史新低 | `LOW_RECENT` |  | — | 可算 |  |
| 近期跑赢大盘 | `WIN_MARKET` |  | 0% | 可算 |  |
| 换手率 | `TURNOVERRATE` | % | 100% | 已有 `Bar` | 0.23 |
| 量比 | `VOLUME_RATIO` |  | 100% | 可算 | 0.91 |
| 成交量 | `VOLUME` | 万 | 100% | 已有 `Bar.volume` | 757660 |
| 成交额 | `DEAL_AMOUNT` | 亿 | 100% | 已有 `Bar.amount` | 712270967.0 |
| 当日净流入额 | `NET_INFLOW` | 亿 | 100% | 已有 `NetInflow` | -18030246.0 |
| 3日主力净流入 | `NETINFLOW_3DAYS` | 亿 | 100% | 已有 `NetInflow` | -76687461.0 |
| 5日主力净流入 | `NETINFLOW_5DAYS` | 亿 | 100% | 已有 `NetInflow` | 40458361.0 |
| 当日增仓占比 | `NOWINTERST_RATIO` | % | 100% | **缺口** | -2.53 |
| 3日增仓占比 | `NOWINTERST_RATIO_3D` | % | 100% | **缺口** | -3.52 |
| 5日增仓占比 | `NOWINTERST_RATIO_5D` | % | 100% | **缺口** | 1.0 |
| 当日DDX | `DDX` | % | 100% | **缺口** | -0.006 |
| 3日DDX | `DDX_3D` | % | 100% | **缺口** | -0.025 |
| 5日DDX | `DDX_5D` | % | 100% | **缺口** | 0.013 |
| 10日内DDX飘红天数 | `DDX_RED_10D` |  | 100% | **缺口** | 3 |
| 3日涨跌幅 | `CHANGERATE_3DAYS` | % | 100% | 可算 | 0.86 |
| 5日涨跌幅 | `CHANGERATE_5DAYS` | % | 100% | 可算 | 4.78 |
| 10日涨跌幅 | `CHANGERATE_10DAYS` | % | 100% | 可算 | 4.2 |
| 今年以来涨跌幅 | `CHANGERATE_TY` | % | 100% | 可算 | -21.55 |
| 连涨天数 | `UPNDAY` |  | 100% | 可算 | 1 |
| 连跌天数 | `DOWNNDAY` |  | 100% | 可算 | 0 |
| 上市以来年化收益率 | `LISTING_YIELD_YEAR` | % | 97% | 可算 | 10.434451458483 |
| 上市以来年化波动率 | `LISTING_VOLATILITY_YEAR` | % | 97% | 可算 | 6.645006569977 |
| 今日创阶段新高 | `STAGE_NEWHIGH` |  | — | 可算 |  |
| 今日创阶段新低 | `STAGE_NEWLOW` |  | — | 可算 |  |
| 大幅高开 | `LARGE_HIGH_OPENING` |  | 100% | 可算 | 0 |
| 大幅低开 | `LARGE_LOW_OPENING` |  | 100% | 可算 | 0 |

## 真缺口汇总（53 个）

本地既没有、也算不出来的：

- **股票范围**：地区 `AREA`、风格 `STYLE`
- **基本面选股**：市盈率TTM(扣非) `PETTMDEDUCTED`、预测市盈率(今年) `PREDICT_PE_SYEAR`、预测市盈率(明年) `PREDICT_PE_NYEAR`、预测PEG `YCPEG`、每股资本公积 `PER_CAPITAL_RESERVE`、每股盈余公积 `PER_SURPLUS_RESERVE`、扣非净利润 `DEDUCT_NETPROFIT`、扣非净利润增长率 `DEDUCT_NETPROFIT_GROWTHRATE`、预测净利润同比增长 `PREDICT_NETPROFIT_RATIO`、预测营收同比增长 `PREDICT_INCOME_RATIO`
- **消息面选股**：定向增发 `DIRECTIONAL_SEO_DATE`、资产重组 `RECAPITALIZE_DATE`、股权质押 `EQUITY_PLEDGE_DATE`、质押比例 `PLEDGE_RATIO`、商誉规模 `GOODWILL_SCALE`、商誉占净资产比例 `GOODWILL_ASSETS_RATRO`、近3月高管增减比例 `EXECUTIVE_CHANGE_3M`、机构评级 `ORG_RATING`、机构持股家数合计 `ALLCORP_NUM`、基金持股家数 `ALLCORP_FUND_NUM`、券商持股家数 `ALLCORP_QS_NUM`、QFII持股家数 `ALLCORP_QFII_NUM`、保险公司持股家数 `ALLCORP_BX_NUM`、社保持股家数 `ALLCORP_SB_NUM`、信托公司持股家数 `ALLCORP_XT_NUM`、机构持股比例合计 `ALLCORP_RATIO`、基金持股比例 `ALLCORP_FUND_RATIO`、券商持股比例 `ALLCORP_QS_RATIO`、QFII持股比例 `ALLCORP_QFII_RATIO`、保险公司持股比例 `ALLCORP_BX_RATIO`、社保持股比例 `ALLCORP_SB_RATIO`、信托公司持股比例 `ALLCORP_XT_RATIO`、限制控股股东减持 `HOLDER_JC`
- **人气指标**：股吧人气排名 `POPULARITY_RANK`、人气排名变化 `RANK_CHANGE`、人气排名连涨 `UPP_DAYS`、人气排名连跌 `DOWN_DAYS`、人气排名创新高 `NEW_HIGH`、人气排名创新低 `NEW_DOWN`、新晋粉丝占比 `NEWFANS_RATIO`、铁杆粉丝占比 `BIGFANS_RATIO`、7日关注排名 `CONCERN_RANK_7DAYS`、今日浏览排名 `BROWSE_RANK`
- **行情数据**：破发股票 `IS_ISSUE_BREAK`、当日增仓占比 `NOWINTERST_RATIO`、3日增仓占比 `NOWINTERST_RATIO_3D`、5日增仓占比 `NOWINTERST_RATIO_5D`、当日DDX `DDX`、3日DDX `DDX_3D`、5日DDX `DDX_5D`、10日内DDX飘红天数 `DDX_RED_10D`

### 其中值得留意的几组

| 组 | 为什么留意 |
|---|---|
| 质押比例、商誉规模及占净资产比 | **不是选股因子，是排雷项** —— 不需要预测力，只需要帮着避开尾部风险。跟"超额全来自控亏"这条已验证的优势逻辑一致 |
| 机构持股六分类（基金/券商/QFII/保险/社保/信托 的家数与比例） | `TopShareholder` 只有十大股东，`share_type` 是股份性质不是机构类型，**推不出来** |
| 机构一致预期（预测PE今明年、预测净利/营收同比、PEG） | `EarningsForecast` 是**公司自己发的业绩预告**，跟卖方一致预期是两回事 |
| 扣非净利润（连带扣非PE、扣非增长率） | `FinancialReport` 60 个 key 里没有 deduct 系列，**连算都算不出来** |
| 股吧人气排名那一组 | ⚠ 跟本系统里说的"热度"**不是一回事**。本地的热度指板块热度 / 成交额热度 |

## 已知的坑

| 坑 | 说明 |
|---|---|
| 4 个字段全空 | `BREAKUP_MA`(向上突破均线)、`HIGH_RECENT`(近期创新高)、`WIN_MARKET`(近期跑赢大盘)、`PAR_DIVIDEND`(每股红股) 覆盖率 0%。它们在选股器里要填参数才出值 |
| 只有截面 | 想要时间序列只能每天抓、慢慢养。这是它跟已接入的 datacenter 报表接口最大的区别 |
| 单位混杂 | 有的是小数、有的是百分数，`unit` 列给了单位类型但不完全可靠，入库前逐个核实。**已抓到一例**：`TOTAL_SHARES` 这份文档原先记「亿」，实测是**股**（科士达 582,225,094）——上面那张表已改 |
| 标签型字段是数组 | `CONCEPT`、`STYLE` 返回数组而非标量 |

## 后来接入了什么

### 2026-09-14：`TOTAL_SHARES`（只这一个字段）

**起因是这份文档自己记错了一行。** 原先那行写的是「总股本 → 已有 `FinancialReport.share_capital`」，
于是评估时它被归进"已有"、没进缺口名单。实际上两者不是一回事：

```
share_capital（元，财报科目「实收资本(或股本)」） = TOTAL_SHARES（股） × 每股面值（元/股）
```

A 股绝大多数票面值 1.00 元，两个数**恰好相等**——这个巧合被 PE/PB 当成了定义。
2026-09-14 拿接口跟库里逐只比对，5561 只里 **373 只对不上**，而且两个方向都有：

| 方向 | 只数 | 例子 |
|---|---|---|
| 报表偏小（面值 < 1 元） | 269 | 紫金矿业 26.59 亿 vs 265.91 亿股（面值 0.1）；中芯国际差 35 倍；诺诚健华（美元面值）差 7.5 万倍 |
| 报表偏大（H 股会计口径，股本科目含溢价） | 104 | 中国移动 4703.59 亿元 vs 216.91 亿股 |

重算后 3923 只里 **277 只（7.1%）的 PE 变了**：中国移动 348.8 → 16.1、中国海油 18.4 → 11.6、
分众传媒 0.5 → 20.1、诺诚健华 0.0 → 50.7。全市场分位基本没动（中位 38.7 → 39.1）——
**错的是个股，不是分布**。

⚠ 偏大那一类**没有本地判据能发现**。用「流通市值 ÷ 收盘价推出流通股数 > 报表股本」
只抓得出偏小的那些（流通股不可能多于总股本），对偏大的完全失明。所以这个数只能取，
不能靠本地校验兜住。

这次取字段正好符合下面那条原则：**先有具体场景，再取那个场景需要的字段**。
场景是"PE/PB 算错了 7% 的票"，需要的字段就一个，所以只取了一个。

落地：`EastMoneyTotalSharesProvider` + `TotalSharesTask`（日更，一个请求 1~2 秒）。
另外实测确认了两件文档里没有的事——`ps=10000` 一页装得下全市场 5562 只（`nextpage=false`、782KB），
**北交所 920 开头 343 只在内**。

## 当时为什么没接入

2026-09-09 评估，结论 **暂不做**。三条已有证据：

1. `回调法选股` 的回测里，**重基本面筛选是负贡献**，最后只留了净利和现金流 —— 再堆基本面字段（ROIC、PEG、预测PE）先验概率很低。
2. `量化因子检验结果`：**股东户数/融资动量截面因子基本无效**，这类"看起来有道理"的截面因子已经实测过。
3. `自主交易归因`：2026 上半年超额全来自**下跌股控亏，不是选股**。

加上囤数据的真实代价不在抓取（1.5 秒）而在后面：库已 22.6GB；50 字段 × 5400 只 = 27 万行/天；字段一多，以后做分析要先搞清"该用哪个 EPS"。

**该做的顺序是反过来的**：先有具体假设或场景，再取那个假设需要的字段。`回调法` 那次教训正说明，先囤字段再想用法，会把负贡献的东西塞进策略里。

查单只股票不需要入库 —— 直接带 `filter` 请求一次即可。
