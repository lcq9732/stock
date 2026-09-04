# 东财离线取数

主环境访问不了东方财富，但有几项数据现有源（新浪/腾讯/交易所/巨潮）拿不到或质量明显不足。
这套脚本的做法是：在**能访问东财的机器**上取数落成 sqlite 包 → 移动硬盘拷回 → 合并进主库。

## 取哪些、为什么

| 数据 | 现状 | 东财 |
|---|---|---|
| 个股板块归属 | 新浪 224 个老化分类，**没有**存储/算力/液冷/AI芯片 | 1000 个板块，新兴主题全覆盖 |
| 业绩预告 | **完全没有** | 含变动原因文本（可提"涨价/供不应求"等关键词） |
| 业绩快报 | **完全没有** | 比正式财报早 |
| 龙虎榜营业部明细 | **完全没有**（现有 `Lhb` 只有上榜汇总，丢了买卖前五席位） | 营业部代码/名称 + 该席位 3 日胜率 |
| 资金流分级 | 只有 `main_net_inflow` 一个合计字段 | 超大/大/中/小单，各自净额+占比 |
| 大宗交易 / 机构调研 / 限售解禁 / 股东增减持 | **完全没有** | 有 |

**不取**的：财报（实测每期 5500–5700 家无缺口）、融资余额（交易所一手）、指数权重（中证一手）、
公告（巨潮一手）、K线（腾讯可靠）。现有源已经够好或更好的，一律不动。

## 三个脚本

| 脚本 | 跑在哪 | 干什么 |
|---|---|---|
| `em_fetch.py` | 能上东财的机器 | 取数，落成 pack*.sqlite |
| `em_merge.py` | 主环境 | 把包合并进 current.sqlite |
| `em_analyze_log.py` | 任意 | 分析取数日志，反推安全间隔 |

只依赖 Python 3.8+ 标准库，**不用 pip 装任何东西**。把 `em_fetch.py` 单个文件拷到取数机器即可。

---

## 第一晚：全量补历史

把 `em_fetch.py` 拷到取数机器，移动硬盘挂成 `E:`（盘符按实际改）。

```bash
python em_fetch.py --out E:\em --packs 1,2,3,4
```

约 5 小时。跑完硬盘上会有：

```
E:\em\
  pack1_boards_20260903.sqlite      板块归属        ~10 MB   10 分钟
  pack2_earnings_20260903.sqlite    业绩预告+快报   ~150 MB  20 分钟
  pack3_lhb_20260903.sqlite         龙虎榜席位+明细 ~500 MB  3.2 小时
  pack4_misc_20260903.sqlite        大宗/调研/解禁/增减持 ~250 MB  1.5 小时
  em_state.json                     水位线，别删
  logs\fetch-20260903.jsonl         全量请求日志
```

**中途挂了直接重跑同一条命令**，会从断点继续（进度记在包里的 `_progress` 表，精确到"月片"级）。
Ctrl+C 也安全，进度已落盘。

### 第二晚（可选）：资金流

资金流接口只给最近 **120 个交易日**，且只能按股票逐只取，没有增量入口——所以它每次都得重来。
默认 `watch` 模式只取关注板块的成分股：

```bash
python em_fetch.py --out E:\em --packs 5
```

`--flow-scope all` 取全市场（5553 只，约 7.7 小时）。想精确控制范围，在 `E:\em\codes.txt`
里放名单（每行一个 6 位代码），它的优先级最高。

第二晚会自动找目录下**最新的** pack1 来取股票名单，不要求跟 pack1 同一天跑。

---

## 拷回主环境后：合并

先看一眼会发生什么（不写入）：

```bash
python em_merge.py --dir E:\em --dry-run
```

确认没问题再真合并：

```bash
python em_merge.py --dir E:\em
```

- **合并前关掉 Fetcher 和 Analyzer**，否则主库被占用会报 `database is locked`
- 幂等：同一个包合并两次结果一样。已合并过的包会自动跳过，要重合并加 `--force`
- 合并记录在主库的 `EmMergeLog` 表里
- 合并完会把水位线写回 `E:\em\em_state.json`

**可以分批合并**：包 1 拷回来就先合，不用等一整晚跑完。

## 之后：每月增量

第一次是全量补历史，之后走增量，**约 11 分钟**：

```bash
python em_fetch.py --out E:\em --packs 1,2,3,4
```

同一条命令。脚本读 `em_state.json` 里各表的 `merged_to`（注意是**已合并到**，不是已取到——
这样上次取了但没合并成功的会重取，不会丢），只抓那之后的数据。

两个例外：
- **板块归属**是快照，没有时间维度，每次全量重取（188 页，10 分钟）
- **限售解禁**含未来解禁计划，也是全量重取（63 页）

---

## 调限流参数

第一晚跑完，分析日志：

```bash
python em_analyze_log.py E:\em\logs\fetch-20260903.jsonl
```

它会给出各域名成功率、响应时间漂移、最长连续成功段，以及最关键的**限流模型判定**：

- 某域名被限时另一个域名仍正常 → **模型 A（各域名独立额度）**，之后可以考虑跨域名并发
- 两个域名几乎同时哑掉 → **模型 B（IP 总额度）**，必须串行，并发只会更快撞墙

拿到结论再用 `--gap` 调整间隔。默认值是保守起点：

| 域名 | 默认间隔 | 实测耐受度 |
|---|---|---|
| `datacenter-web` | 2.0s | 最宽松 |
| `push2his` | 5.0s | 敏感 |
| `push2` | — | 最严（已绕开不用） |

> 板块成分股本来只能走限流最凶的 push2，探测时被限一个多小时没恢复。
> 后来在 datacenter 找到 `RPT_F10_CORETHEME_BOARDTYPE`（个股→板块的全市场映射表，
> 9.4 万行、188 页拿全），完全绕开了 push2。

## 合并后主库新增的表

全部是**新表**，一张现有表都没动：

| 表 | 内容 | 主键 |
|---|---|---|
| `EmStockBoard` | 个股板块归属 | (code, board_code) |
| `EmBoard` | 板块清单（从上表派生） | board_code |
| `EmEarningsForecast` | 业绩预告 | (code, report_date, notice_date, predict_finance_code) |
| `EmEarningsExpress` | 业绩快报 | (code, report_date) |
| `EmLhbSeat` | 龙虎榜席位（买卖用 `side` 区分 B/S） | (trade_date, code, side, operatedept_code, trade_id) |
| `EmLhbDetail` | 龙虎榜每日明细 | (trade_date, code, trade_id) |
| `EmNetInflowDetail` | 资金流分级 | (code, trade_date) |
| `EmBlockTrade` | 大宗交易 | (trade_date, code, daily_rank) |
| `EmOrgSurvey` | 机构调研 | (code, receive_start_date, receive_object, receive_way) |
| `EmShareLift` | 限售解禁（含未来计划） | (code, free_date, free_shares_type) |
| `EmHolderChange` | 股东增减持 | (code, notice_date, holder_name, end_date) |
| `EmMergeLog` | 合并记录，防重复合并 | (pack_file, src_table) |

现有的 `Board` / `BoardMember`（新浪那套）**保留不动**——留着做交叉验证，
两边成分股差异大的板块正好是值得人工看一眼的。

### 两个口径说明

- **日期一律截成 `YYYY-MM-DD`**。东财返回的是 `2026-09-02 00:00:00`，而主库现有表
  （`MarginDetail.trade_date` / `Lhb.trade_date` 等）用的是 10 位日期，不截断 join 不上。
- **包里存的是东财原始字段**（列名大写、全部 TEXT），字段映射和类型转换都在合并阶段做。
  所以映射写错了，改 `em_merge.py` 重跑即可，**不用重新取一夜数据**。

## 设计上几个刻意的选择

1. **大表按月切片**：龙虎榜席位全量 131 万行，按 500/页是 2620 页，东财深分页（pageNumber 上千）
   会拒绝或极慢。切成月片后每片只有 40 页左右，翻页永远是浅的。
2. **断点续传到片级**：重跑某片前先删掉该片已有的行再重取，比记录页级偏移简单且不会产生重复。
3. **状态文件放移动硬盘**：取数机器读不到主库，所以水位线靠 `em_state.json` 跟着硬盘走——
   取数端读 `merged_to`，合并端写 `merged_to`，两台机器从不直连。
4. **`pageSize=500`**：实测上限，传 1000 也只返回 500。

## 已知限制

- 资金流分级只有最近 120 个交易日，要攒历史得定期取、滚动累积，短期内做不了长周期回测
- `RPT_LIFTUNLOCK_STA`（解禁明细）和 `RPT_LICO_INTERNAL_TRADE`（高管增减持）报表不存在，
  前者用 `RPT_LIFT_STAGE` 替代，后者暂缺
- `BOARD_LEVEL` 字段含义待确认——如果它表达的是产业链层级，主题分层标签就不用手工做了
