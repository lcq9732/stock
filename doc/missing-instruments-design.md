# 库里缺 K 线的两融标的（2026-09-16，方案待确认）

## 0. 问题

`MarginDetail` 里有 **478 个代码在 `Bar`(granularity='day') 里一根日 K 都没有**（裸码、
`sh`+码、`sz`+码三种形式都查过）。发现于融券余额回填对账——没有收盘价就补不出融券余额，
占回填缺口的 2.46%（见 `doc/margin-fields-design.md` §4.2）。

| 类型 | 数量 |
|---|---|
| ETF（51x/52x/55x/56x/58x/588/589 等） | 465 |
| 历史退市 / 更名股票 | 12 |
| 仍在交易的股票 | 1（**689009 九号公司**） |

三件事成因**完全不同**，下面分开写。

---

## 1. 已查实的事实

### 1.1 ⭐ 新浪 `hs_a` 节点里没有 689009

这是本次唯一已经**证实到根因**的一条。个股列表走 `SinaStockListProvider`，
`node=hs_a`（沪深北 A 股）。按 `symbol` 升序翻页，沪市段的末尾是这样的：

| page | 首 symbol | 末 symbol |
|---|---|---|
| 24 | sh688296 | sh688418 |
| 25 | sh688419 | sh688583 |
| 26 | sh688584 | sh688717 |
| **27** | **sh688718** | **sz000060** |

第 27 页从 `sh688718` 一路排到 `sz000060`——**沪市段结束后直接跳进深市，中间没有 sh689009**。
不是分页丢的，是 `hs_a` 这个节点本身就不含科创板 CDR。

而同一个接口的 `node=kcb`（科创板）**有**它：按 symbol 降序第一页的第一条就是 `sh689009`，
其后才是 sh688981、sh688837……

⇒ **689009 从来没进过 `StockMeta`，所以从来没被排进抓取清单，一根 K 线都没有。**
`MarketClassifier` 对 689 的分类是对的（→ 科创板 → `sh` 前缀），抓取侧没有任何过滤挡它，
纯粹是名单源没给。

（探测走的是云端出口，没有从本机发请求、没有占用户的 IP 配额。）

### 1.2 ETF 名单源本身是全的，缺口在链路上

`SinaEtfListProvider` 走同一个接口的 `node=etf_hq_fund`。实测这个节点：

- 有效页到 **page 17**（末条 `sz159998`，正是降序第一条），page 19 空 ⇒ 全量约 **1600~1700 只**
- **含**深市 ETF（`sz159xxx`）、**含** 科创板 ETF：光 page 10 一页就有 33 个 `sh589xxx`
- 我们库里已有 **1659 只 ETF**（`doc/etf-backtest-granularity-design.md` §0）——跟接口全量对得上

⇒ 名单接口给得全，**缺的 465 只不是"源里没有"**。

### 1.3 ETF 名单拉取有一处**静默截断**

`SinaEtfListProvider.FetchPageAsync` 里：

```csharp
if (string.IsNullOrWhiteSpace(json) || json == "null") return new List<StockListEntry>();
```

新浪限流时**返回字符串 `null` 而不是报错**。这里把它当成空页返回，调用方

```csharp
if (pageEntries.Count == 0) break; // ran past the last page
```

直接当成"翻过最后一页"**break**——于是那一页之后的 ETF 全部丢掉，**一条错误都不报**。
按 symbol 升序，`sh588/sh589` 和**全部 700 多只深市 `sz159xxx`** 都排在后半程，正好是
最容易被截掉的那一段。`SinaStockListProvider` 有一模一样的写法。

两者的后果不一样：

| | 列表截断后 |
|---|---|
| 个股 | 只丢**新票**——日常抓取清单走本地 `SqliteStockMetaUpsert.GetAll()`，已知票有兜底 |
| **ETF** | **直接丢 K 线**——`FetchEtfBarsAsync` 每轮都重新联网取名单，**没有本地兜底**，名单里没有就这轮不抓 |

而且 `FetchEtfBarsAsync` 拿到名单后只在 `count == 0` 时提示一句，**拉回 300 只还是 1600 只
一样往下跑**，没有任何"半截名单"护栏——`TotalSharesTask` 早就有这道护栏（少 5% 就整轮放弃），
ETF 这条路径上没有。

---

## 2. 还没查实的（要等库空闲）

Fetcher 正在写库，以下都要等它跑完再查，**现在不动库**：

1. **465 只 ETF 里，有多少是已清盘/摘牌的历史 ETF**。`MarginDetail` 是跨年历史数据，
   历史两融 ETF 标的里必然有一批今天已经不存在了——这部分**不该补也补不到**，
   要跟"在市却漏抓"分开计数。判据：跟 §1.2 拉到的当前全量名单取差集。
2. **那 12 只"退市/更名"里有几只其实还在交易**。用户给的清单里 `000043`（现招商积余）、
   `600200` 江苏吴中、`600636` 国新文化、`600696` 岩石股份**今天都还在交易**。
   如果它们在库里真的一根 K 线都没有，那就是跟 689009 同性质的当前缺口，
   **不能整批按"历史标的"放过**。这一条我认为是本次最需要先确认的。
3. **588/589 到底缺哪 77 只**——是截断丢的（那次跑到哪一页停的），还是抓取阶段失败进了
   `FailedCodes` 没重试成功。两者的修法不同。

---

## 3. 方案

### 3.1 689009：给个股名单补上 CDR（改 provider）

`SinaStockListProvider` 在 `hs_a` 之后**再翻一遍 `node=kcb`**，按 code 去重合并。

- 代价：科创板约 590 只 ⇒ 6 页 × 300ms ≈ 2 秒，可忽略
- 为什么不只特判 689009 一只：将来再发 CDR 一样会漏，特判是把这次的坑原样留给下次
- 为什么不换东财：`push2` 在用户环境不通（`push2delay` 通，但那是另一件事，
  见 `project_datasource_eastmoney_migration`），这次不牵进来

进来之后是全自动的：`MarketClassifier` → `sh689009` → 腾讯 K 线；`BarVolumeUnit` 已经认识
689（科创板按股、要除 100）；财务/行业/资金流都按 `StockMeta` 枚举，跟着就有了。
首轮水位线为空，会按 `lookbackYears` 回溯补历史（九号公司 2020-10 上市，要确认回溯窗口够）。

### 3.2 ETF：先堵静默截断，再加护栏，最后补抓

**① 堵截断**（`SinaEtfListProvider` + `SinaStockListProvider`，两个都改）
把"返回 `null`/空响应"跟"真的翻到末页"分开：

- 响应体是字面量 `null` 或空 ⇒ 抛 `RateLimitedException`（走现有重试），**不再当末页**
- 只有**解析出 0 条的合法 JSON 数组**才算末页
- 重试耗尽仍失败 ⇒ **整轮放弃、报错**，不返回半截名单

**② 半截名单护栏**（`FetchEtfBarsAsync`）
照搬 `TotalSharesTask` 的做法：本轮拿到的 ETF 数比库里 `StockMeta(type='etf')` 少 5% 以上
就**整轮放弃 ETF 段并报错**，不拿半截名单去跑。

**③ 本地兜底**
名单取不到/被护栏拦下时，用库里 `type='etf'` 的存量名单跑增量，而不是整段跳过——
ETF 跟个股不该是两套待遇。

**④ 补抓历史缺口**：新任务 `EtfBarBackfillTask`
放 `StockPlatform.Tasks`，继承 `FetchTaskBase`，`FetchTaskCatalog` 注册一行。

- 口径：`MarginDetail` 里的 ETF 代码 ∪ 当前全量名单，减去 `Bar` 里已有的
- 代码形态：**一律带前缀存**（`sh510300`）——前缀取名单接口给的 `symbol`，
  **不自己按 5 开头算沪市**（`MarketClassifier` 对 5xxxxx 返回 `Unknown`，
  自写前缀规则的代价见 `feedback_market_prefix_via_classifier`）
- 已清盘 ETF：拿不到就记进"确认无数据"名单，不每轮重试（沿用 `BarProbeFloor` 那套思路）
- 流式：一只一批，中断即断点续

### 3.3 那 12 只：先判在市状态，再决定补不补

**不建议直接按"历史标的、不影响当前分析"结案**——§2.2 说了，里面很可能混着还在交易的票。
做法：

1. 库空闲后逐只查 `StockMeta` 的 type 和 `DelistedStock` 的终止日
2. **仍在交易的**并进 §3.1 一起修（查为什么它们也不在名单里，可能跟 689009 同因，也可能不同）
3. **真退市的**走已有路径补：腾讯能拉退市股完整历史（`project_delisted_data_sources`），
   `CatchUpDelistedTailsAsync` 只补"本地已有历史"的尾巴，**一根都没有的它补不到**，
   要靠"拉取区间数据"那条路径或 §3.2④ 的同款补抓任务
4. 结论写回本文档，免得下次又当成新发现

---

## 4. 实现清单（确认后再动手）

| 层 | 文件 | 做什么 |
|---|---|---|
| 名单 | `SinaStockListProvider.cs` | 合并 `node=kcb`；`null` 响应改抛限流异常 |
| 名单 | `SinaEtfListProvider.cs` | `null` 响应改抛限流异常；重试耗尽整轮放弃 |
| 编排 | `FetchOrchestrator.FetchEtfBarsAsync` | 半截名单护栏 + 本地 `type='etf'` 兜底 |
| 任务 | `EtfBarBackfillTask.cs` + Catalog 注册 + App 装配 | 【补抓缺 K 线的 ETF】 |
| 测试 | 新增 | ① `null` 响应不被当末页 ② 半截名单触发放弃 ③ ETF 写入必须带前缀（钉死裸码会漏进个股选股全集） |

## 5. 验证

1. 单元测试（上表三条）
2. **实机**（`feedback_verify_by_running_app`）：Debug 起 Fetcher，跑一次拉取，
   日志里确认 ETF 名单条数 ≈1600 而不是几百；跑【补抓缺 K 线的 ETF】
3. 复跑 `doc/margin-fields-design.md` 的复现 SQL，看 478 降到多少、剩下的是不是全是已清盘/已退市
