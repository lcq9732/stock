# 历史行情数据平台 — 设计文档

状态：核心功能已实现（`StockPlatform.Logic/Data/Fetcher/Analyzer`）。本文档记录设计方向，随实现同步更新。

## 1. 背景与目标

曾经有一个 StockAnalyzer（`src/StockAnalyzer.UI`）是实时/近实时的 A 股技术面选股工具，面向"手动添加几只自选股，定时刷新看是否满足7条技术规则"的场景，数据只保留最近90天左右，用于计算指标，不做历史留存。

新的需求是做一个**独立的历史数据平台**：批量获取全市场（或指定范围）近3年的行情与基本面数据，落地保存，供后续开发的分析程序使用。这个平台与当时的 StockAnalyzer 是两条独立的线，通过约定好的 SQLite 数据文件解耦。

**状态变更记录（2026-07-07）**：StockAnalyzer 那套7指标打分逻辑后来被移植成了 [分析程序](analysis-app-design.md) 的"金叉法"（见该文档3.2.2节），确认覆盖完整后，`StockAnalyzer.UI`/`StockAnalyzer.Logic`/`StockAnalyzer.Data` 三个项目已被整体删除（见 analysis-app-design.md 第5节）。本节及下文提到"StockAnalyzer"之处均指这个已删除的历史项目，保留描述是为了说明当时的设计动机，不代表代码仍然存在。

## 2. 总体架构：两个独立程序

```
┌─────────────┐ ①本地抓取 ┌──────────────┐ ②提示上传 ┌──────────────┐ 只读拉取 ┌─────────────┐
│ 数据获取程序  │ ────────▶ │ 本地 SQLite   │ ────────▶ │  网盘         │ ───────▶ │  分析程序     │
│ (Fetcher)   │           │ (master+daily)│  (确认后)  │ (百度网盘等)   │         │ (界面待定)    │
└─────────────┘           └──────────────┘           └──────────────┘         └─────────────┘
```

- **数据获取程序**：独立运行，分两步——先抓取写入本地 SQLite，再提示是否上传到网盘（见6.6）。**抓取动作必须由用户手动点击触发，不会自动静默运行**（不挂定时任务自动跑，见6.7）。
- **分析程序**：独立的程序，从网盘拉取数据合并到本地后只读查询做分析。具体分析规则/界面待后续单独设计。
- 三者之间**只通过 SQLite 文件的表结构 + 网盘上的 manifest.json 约定**耦合，互不依赖对方的代码。

## 3. 数据范围与数据分级

### 3.1 Level-1 与 Level-2 的区别（决定了免费接口能拿到什么）

| | Level-1（免费/低成本，现有东方财富接口属于此级） | Level-2（付费，通常按年订阅） |
|---|---|---|
| 盘口深度 | 五档买卖盘 | 十档买卖盘 |
| 逐笔成交 | 无，或只有"最近若干笔"抽样 | 完整逐笔成交，含买卖方向、毫秒级时间戳 |
| 逐笔委托（含撤单） | 无 | 有，含匿名委托序号 |
| 大单统计 | 无 | 有（或可从逐笔数据自行加工） |
| 基础报价/K线/成交量 | 有 | 有 |

**关键结论：即使是付费 Level-2 数据，也无法得知真实的"人"是谁在买卖**——这是交易所监管层面禁止对外披露的隐私信息。付费数据最多能给到：
1. 每笔成交的买卖方向（主动买/主动卖，基于成交价相对买卖一价推断）
2. 逐笔委托的匿名交易所内部序号（不对应真实身份）
3. 龙虎榜数据（仅限触发异常波动的个股，精确到"某证券营业部"买卖了多少，机构级别，非个人）

### 3.2 本期目标：只做 Level-1，Level-2 表结构也不预留

本期设计只覆盖 Level-1 数据，**不为 Level-2（完整逐笔成交、十档盘口）预留表结构**，原因：
- 免费公开接口（东方财富等）不提供3年历史的逐笔/十档数据，需要接入付费数据源才能拿到
- 第5节的容量评估表明，全市场多年逐笔数据的量级（百亿~千亿行）已经超出 SQLite 的合理使用范围，很可能需要换用列式/时序数据库（如 DuckDB、ClickHouse）才合适
- 既然存储引擎大概率不同，现在预留的表结构到时候也基本用不上，不如等真正确定要接入 Level-2 数据源、选定了存储方案之后再单独设计，避免预留的设计和实际需求对不上

### 3.3 数据类型

**交易数据（不可变时序事实）**：
- 日线、周线、月线（周/月建议由日线本地聚合计算得出，不单独请求接口，好处：离线也能算，不依赖数据源是否支持）
- 分时（1/5/15/30/60分钟）—— 免费接口历史深度有限（通常几个月到一年，非3年），需要抓取时实测确认
- 逐笔成交（谁在几时以什么价买/卖多少股）—— **本期不做，不预留表结构**，见3.2

**基本面/估值数据（会变化，需保留历史而非覆盖）**：
- 财务指标：营收、净利润、ROE、每股收益(EPS)、每股净资产(BVPS) 等，按报告期（季度/年度）存储
- 估值指标：PE、PB，按交易日存储（因为价格每天变，PE/PB也每天变）
- 数据源细节（东财具体接口字段）需要单独调研确认

### 3.4 多数据源（已实现）：东方财富 + 腾讯财经，单次运行只用一个源，可手动切换

单一数据源在批量抓取全市场股票时容易被对方的反爬/限流机制封锁IP（实测踩过坑）。为此支持接入**多个可选数据源**，但**同一次抓取运行只使用一个源，不自动混用、不自动切换**——原因是不同数据源尽管核心行情数据一致（见3.4末尾的实测校验），但不排除某些衍生字段的计算细节有差异，同一批数据里混用多个源会让"这批数据到底是谁算的"变得含糊，不如每次运行都明确只用一个源，数据口径统一、可追溯。

- `IBarDataFetcher` 接口不变，新增 `TencentBarFetcher` 实现（`web.ifzq.gtimg.cn` 的公开前复权K线接口），跟 `EastMoneyBarFetcher` 并列，两者通过 `NamedBarSource`（名字+实现）注册成一个可选列表
- **抓取程序界面上有一个数据源下拉框**，用户先选一个源再点击"开始抓取"；如果当前选的源连不上/被限流，**手动切换下拉框到另一个源，重新点击"开始抓取"即可**——不需要额外操作，因为增量判断是按"每只股票最后已抓到哪天"来的，切换源之后重新运行会自动只补上一次没抓成功的那些股票，不会重复抓已经有的数据
- 两个源经过实测校验：用贵州茅台 2026-06-25 实际分红事件比对，东方财富和腾讯财经的前复权日线数据逐日逐位完全一致（见项目讨论记录），确认切换数据源不会引入数据不一致的问题
- **腾讯接口有一个已知限制**：单次请求服务端硬性限制最多返回640条K线（无论请求参数怎么写），`TencentBarFetcher` 内部通过"按结束日期向前翻页、拼接结果"的方式自动处理，对调用方透明
- 以后要加新数据源（比如网易财经的新接口、新浪财经等，AKShare 本身是 Python 库不能直接从 C# 调用，但可以参考它聚合的底层数据源自己实现对应的 HTTP 请求），只需要新写一个 `IBarDataFetcher` 实现，加入数据源列表即可，不需要改动抓取编排逻辑，下拉框里自动多一个选项

## 4. 数据库表结构设计

设计原则：**用"维度值"代替"写死字段"**，保证后续加新的数据类型/周期时不需要改表结构、不需要数据库迁移。

```sql
-- 行情K线，多粒度统一存储
CREATE TABLE Bar (
    code TEXT NOT NULL,
    granularity TEXT NOT NULL,   -- 'day' / 'week' / 'month' / 'min1' / 'min5' / 'min15' / 'min30' / 'min60' / ...
    period_start TEXT NOT NULL,  -- ISO日期或日期时间，视granularity而定
    open REAL, close REAL, high REAL, low REAL,
    volume REAL, amount REAL, pct_chg REAL, turnover REAL,
    PRIMARY KEY (code, granularity, period_start)
);

-- 基本面/估值指标，Key-Value 式，加新指标不改表结构
CREATE TABLE FundamentalMetric (
    code TEXT NOT NULL,
    metric_key TEXT NOT NULL,    -- 'revenue' / 'net_profit' / 'roe' / 'eps' / 'bvps' / 'pe' / 'pb' / ...（未来可加新值，无需改表）
    as_of_date TEXT NOT NULL,    -- 财务指标=报告期，估值指标=交易日
    value REAL,
    source TEXT,
    fetched_at TEXT,
    PRIMARY KEY (code, metric_key, as_of_date)
);

-- 股票基础信息
CREATE TABLE StockMeta (
    code TEXT PRIMARY KEY,
    name TEXT,
    exchange TEXT,
    list_date TEXT,
    last_updated TEXT
);
```

代码层面的接口预留（延续现有 Logic/Data 分层思路）：
- `IBarDataProvider`：查/写 Bar，参数带 `granularity`，加新粒度不改接口签名
- `IFundamentalMetricProvider`：按 `metric_key` 通用读写，加新指标不改接口

（不定义 `ITickTradeProvider`——Level-2 逐笔数据本期不做，见3.2，等真正要做时再连同存储引擎一起设计。）

## 5. SQLite 容量评估

| 数据类型 | 全市场(~5000只) 3年估算行数 | 结论 |
|---|---|---|
| 日线 | ~375万行 | 轻松支撑，几百MB级别 |
| 周/月线 | 几十万行（若单独存） | 轻松支撑 |
| 基本面指标 | 同量级 | 轻松支撑 |
| 分时（1分钟） | 最多~9亿行（实际因免费源历史深度限制，大概率远低于此） | 能装下但需注意：批量写入必须用事务打包，做好复合索引，文件会到几十GB级别 |
| 逐笔成交（若未来接入Level-2） | 百亿~千亿行级别 | **超出SQLite舒适区**，本期不做（见3.2），真到那一步再单独设计存储方案（DuckDB/ClickHouse等列式引擎） |

## 6. 无服务器多机共享方案

约束：没有服务器，需要多人/多机共享同一份数据。核心原则：**SQLite 不适合在网络共享盘上被多方同时写入**，但"单一生产者写 + 多个消费者只读"是安全的。

### 6.1 文件角色

```
共享目录/
  stockdata_master_20260701.sqlite   ← 当前总数据文件（最新）
  stockdata_master_20260601.sqlite   ← 上一个总数据文件（备份，最多保留这1个）
  daily/
    stockdata_daily_20260702.sqlite  ← 每日增量，只保留最近7天
    stockdata_daily_20260703.sqlite
    ...
  manifest.json                       ← 描述当前状态，供消费者知道读哪些文件
```

`manifest.json` 结构：
```json
{
  "currentMaster": { "file": "stockdata_master_20260701.sqlite", "asOfDate": "2026-07-01" },
  "previousMaster": { "file": "stockdata_master_20260601.sqlite", "asOfDate": "2026-06-01" },
  "dailyFiles": [
    { "file": "stockdata_daily_20260702.sqlite", "date": "2026-07-02" },
    { "file": "stockdata_daily_20260703.sqlite", "date": "2026-07-03" }
  ]
}
```

### 6.2 生产者（数据获取程序）职责

数据获取程序对外提供两种独立的操作，各自都遵循"本地先做完 → 提示上传"的两步流程（见6.6）：

**操作一：抓取**
1. 首次运行：产出 `stockdata_master_{日期}.sqlite`
2. 之后每天：只产出当天的 `stockdata_daily_{日期}.sqlite`（增量，只含当天新增/变化的数据）
3. 每次产出文件后更新本地的 `manifest.json`

**操作二：合并**（不设自动触发条件，纯粹由**用户点击"合并"按钮**触发，想合并的时候自己点）
1. 把当前 master 文件 + 目前已下载到本地的**所有** daily 文件，用第6.4节的合并逻辑整合到一起
2. 按命名规则生成**新的** master 文件（文件名用合并当天的日期，如 `stockdata_master_20260801.sqlite`），旧 master 自动降级为"上一个"，更老的按第6.2节末尾的清理策略删除
3. 更新本地 `manifest.json`：`currentMaster` 指向新文件，`previousMaster` 指向刚才降级的旧文件，已被合并进去的 daily 文件从 `dailyFiles` 列表移除
4. 合并完成后，跟抓取操作一样，走"提示是否上传到网盘"的流程（见6.6），把新的 master 文件和更新后的 manifest 推到网盘，同时可以把网盘上已经被合并掉的旧 daily 文件一并清理

清理策略：
   - master 只留最新 + 前一个，超过2个删最老的
   - daily 只留最近7天，超过的删除

### 6.3 消费者（每台机器的分析程序）职责

每台机器本地维护一份**已经物理合并好的独立数据库** + 一个同步状态文件：

```json
// local_sync_state.json（消费者本地文件）
{
  "basedOnMaster": "stockdata_master_20260701.sqlite",
  "dataAsOfDate": "2026-07-05",
  "appliedDailyFiles": ["stockdata_daily_20260702.sqlite", "stockdata_daily_20260703.sqlite"]
}
```

**首次在某台机器运行**（无本地状态文件）：
1. 读共享目录 manifest，取**当前最新**的 master 文件，复制到本地作为起点
2. 根据该 master 的 `asOfDate`，找出共享目录里比这个日期新的所有 daily 文件
3. 依次合并进本地数据库，更新本地状态

**之后每次运行**：
1. 读本地状态的 `dataAsOfDate`
2. 只拉共享目录里"日期比本地更新"的 daily 文件，合并进本地库，更新状态
3. 不重新拉 master，除非发生下面的断档情况

**断档情况**（本地太久没运行，本地 `dataAsOfDate` 和共享目录现存最早的 daily 文件之间有缺口，说明中间的 daily 已被清理）：
- 退化为"首次运行"流程：重新拉最新 master + 该 master 之后的 daily，本地状态重置

### 6.4 合并操作实现方式

无论是生产者合并 daily 到 master，还是消费者合并 daily 到本地库，动作一致：

```sql
ATTACH DATABASE 'stockdata_daily_20260702.sqlite' AS delta;
BEGIN;
INSERT OR IGNORE INTO main.Bar SELECT * FROM delta.Bar WHERE granularity NOT IN ('week','month');
INSERT OR REPLACE INTO main.Bar SELECT * FROM delta.Bar WHERE granularity IN ('week','month');
INSERT OR IGNORE INTO main.FundamentalMetric SELECT * FROM delta.FundamentalMetric;
COMMIT;
DETACH DATABASE delta;
```

- `day`（以及未来任何直接抓取而非派生计算的粒度，如分钟线）是不可变的原始事实，一旦写入就不会再变，用 `INSERT OR IGNORE` 防止主键冲突导致合并失败（正常情况下不会有重复，防御性处理）
- `week`/`month` 是派生数据：抓取程序每次抓取都会基于全部日线历史重新计算"当前尚未收盘"的那一周/那一月并覆盖本地（见第6.2节 `SqliteBarUpsert`），因此同一个 `(code, granularity, period_start)` 主键在更新的 daily 增量文件里可能带着比目标库里已有行更新的值。如果对 `week`/`month` 也用 `INSERT OR IGNORE`，目标库里的当前周/当前月会永远停留在**第一次**合并进来时的值，不会再随后续 daily 文件更新——因此必须用 `INSERT OR REPLACE`，让后合并的（更新的）daily 文件覆盖旧值。这依赖调用方按日期顺序依次合并 daily 文件（消费者侧 `SyncOrchestrator` 已经这样做），保证"后合并的值更新"
- 整个合并过程包在一个事务里，保证要么全部成功要么全部不生效，避免合并到一半崩溃导致本地数据库损坏

### 6.5 共享介质：网盘（已确定），当前用百度网盘 + 手工上传（暂无开发者凭证）

共享介质确定为**网盘**，不依赖公司内部共享盘/NAS。

**现状**：暂时没有申请百度网盘开放平台的开发者凭证（AppKey/SecretKey/access_token），所以**当前不做自动化的API上传/下载**，改成"程序只负责把文件命名准备好，上传这个动作由用户手工打开百度网盘网页完成"。这不影响后续升级成自动化——接口设计上仍然按可替换的抽象来定义，只是当前的"实现"是一个手工提示，而不是真正调用网盘API：

```csharp
public interface ICloudStorageClient
{
    Task UploadAsync(string localFilePath, string remoteFolder, CancellationToken ct);
    Task DownloadAsync(string remoteFilePath, string localFilePath, CancellationToken ct);
    Task<List<CloudFileInfo>> ListFilesAsync(string remoteFolder, CancellationToken ct);
    Task DeleteAsync(string remoteFilePath, CancellationToken ct);
}
```

- **当前实现（无凭证过渡方案）**：`ManualUploadPrompter : ICloudStorageClient`——`UploadAsync` 不真正联网，只是把文件放进本地一个"待上传"文件夹，并弹窗/提示用户"请手动把以下文件上传到百度网盘的 XXX 目录：{文件名列表}"，方便的话可以顺便帮用户打开这个本地文件夹（`explorer.exe` 定位到文件）和/或用默认浏览器打开百度网盘网页，减少手工操作步骤；`DownloadAsync` 同理，检查一个本地"已下载"文件夹里有没有用户手工从网盘下载好、放进来的对应文件，有就直接用，没有就提示用户"请从网盘手动下载 XXX 文件放到这个文件夹"
- **未来有凭证后的实现**：`BaiduNetdiskClient : ICloudStorageClient`——按6.5节原计划走OAuth+openapi 自动上传下载，替换掉 `ManualUploadPrompter` 即可，调用方（抓取程序/分析程序的业务逻辑）完全不用改
- 具体用哪个实现由配置决定（类似原 StockAnalyzer.Logic 里 AppConfig 的思路，该项目已删除，见第1节变更记录），换成自动化方案时只是切一下配置

**只要文件命名规则符合第6.1节约定（`stockdata_master_{日期}.sqlite` / `stockdata_daily_{日期}.sqlite` / `manifest.json`），用户手工上传到网盘哪个目录、什么时候上传，都不影响下游消费者读取——消费者只关心文件名和 `manifest.json` 里的内容，不关心它是怎么传上去的。**

**将来申请到开发者凭证后**（前置条件：百度网盘开放平台注册账号、创建应用拿到 AppKey/SecretKey、完成一次 OAuth 用户授权拿到 access_token/refresh_token），随时可以把 `ManualUploadPrompter` 换成真正的 `BaiduNetdiskClient`，不影响已经写好的其他逻辑。

#### 6.5.1【进行中，未完成】分享链接自动下载的可行性调研

用户提供了一个真实的分享链接用于测试：`https://pan.baidu.com/s/1EDo57ELpCEGy-dZtIc87Lg?pwd=y3m7`（提取码 y3m7，文件夹名 `stock-share`）。用户明确要求：这个方案**独立实现**（做成单独一个 `ICloudStorageClient` 实现类，不要和 `ManualUploadPrompter` 混在一起），"当前能用就行，以后再改进"——即接受这是逆向出来的非官方接口，可能因百度改版而失效，坏了再单独修这一个文件即可，不影响其他部分。

**已经用 curl 对着真实链接实测验证成功的部分**（可直接照抄成 C# 实现）：

1. **提交提取码验证**：
   ```
   POST https://pan.baidu.com/share/verify?surl={surl}&t={毫秒时间戳}&channel=chunlei&web=1&clienttype=0
   Body（form-urlencoded）: pwd={提取码}
   Referer: https://pan.baidu.com/s/1{完整分享ID}
   ```
   其中 `surl` = 分享链接 `/s/1EDo57ELpCEGy-dZtIc87Lg` 里 `/s/1` 之后的部分，即 `EDo57ELpCEGy-dZtIc87Lg`（注意去掉那个前导的 `1`）。
   成功响应：`{"errno":0,"err_msg":"","request_id":...,"randsk":"..."}`，且响应会通过 `Set-Cookie` 自动种下一个 `BDCLND` cookie（务必用带 CookieContainer 的 HttpClient，让这个 cookie 自动保留，后续请求都要带着它）。

2. **访问分享页面拿到根目录文件列表**（验证成功、cookie 生效之后）：
   ```
   GET https://pan.baidu.com/s/1EDo57ELpCEGy-dZtIc87Lg?pwd={提取码}
   ```
   返回的 HTML 里直接内嵌了一段 JSON（不需要另外调用API），可以用正则或字符串查找定位 `"file_list":[...]` 这段，每个条目包含 `fs_id`（文件唯一ID）、`isdir`（0=文件，1=文件夹）、`server_filename`（文件名）、`size`、`path` 等字段。也能拿到 `shareid` 和 `share_uk`（页面里 `yunData={...shareid:"...", share_uk:"..."}` 这段）。
   实测这个链接的根目录返回了一个文件夹 `stock-share`（`fs_id=806084441431740, isdir=1`）。

**后续调研（子文件夹列表已打通）**：

之前"子文件夹列不出来"的根因找到了——**不是签名问题，是漏了 `app_id` 参数**。真实网盘目录结构调整为直接把 master/daily/manifest 放在分享文件夹根目录下（`stock-share/` 直接含 `stockdata_master_*.sqlite`、`manifest.json`、`daily/` 子目录）之后，用下面的请求可以稳定拿到子目录内容（`errno:0`）：

```
GET https://pan.baidu.com/share/list?uk={share_uk}&shareid={shareid}&order=time&desc=1&showempty=0&web=1&page=1&num=100&dir={URL编码后的目录路径，如 /stock-share/daily}&t={毫秒时间戳}&channel=chunlei&clienttype=0&app_id=250528
```
- `shareid`、`share_uk`（请求里的参数名是 `uk`）从分享页面 HTML 内嵌的 `yunData={...shareid:"...", share_uk:"..."}` 里取
- `app_id=250528` 是关键——同样能在根目录 `file_list` JSON 的条目里看到这个字段（`"app_id":"250528"`），照抄即可，不用查文档
- 返回的 `list` 数组里每个条目就是 `fs_id`/`isdir`/`server_filename`/`size`/`md5`/`path`，跟根目录 `file_list` 的字段基本一致，分页用 `page`/`num`（本项目文件数量级用不到分页）

**结论：文件下载这条路走不通（不是参数没凑对，是百度网盘刻意要求登录）**——用拿到的真实 `fs_id` 反复测试 `https://pan.baidu.com/share/download`（POST，带 `fid_list`/`uk`/`shareid`/`primaryid`/`app_id` 等各种参数组合）和 `/api/sharedownload`，全部固定返回 `{"errno":112}`。做了对照实验排除"参数没配对"的可能：
- 换成明显无效的 `fid_list`（不存在的 fs_id）—— 同样是 `errno:112`，说明服务端根本没走到"校验这个文件"这一步
- 完全不带任何 cookie（连 verify 拿到的 `BDCLND` 都不带）发起请求 —— 结果还是 `errno:112`，跟带 cookie 时完全一样
- 分享页面 HTML 全文搜索不到任何 `sign`/`timestamp` 的现成值，且这两次对照测试的结果不随参数变化，说明缺的不是"算出一个签名"，而是这个接口本身要求一个**登录态**（真实账号的 `BDUSS`，即百度登录 cookie）才放行——这与开源资料里"分享链接客户端下载普遍要求 `BDUSS`"的说法一致（例如 AList 的百度分享驱动文档也明确写着下载需要 `BDUSS` 这个登录 cookie）。这是百度网盘刻意做的防盗链/反爬限制，不是随便调参能绕过的。

**结论落地**：匿名（不登录）方式**只能列目录、拿元数据（文件名/大小/md5/fs_id），拿不到真实文件字节**。要自动下载真实文件内容，唯一现实的路径是让用户从自己已登录的浏览器里手动导出 `BDUSS`（+通常还需要 `STOKEN`）这个登录态 cookie，粘贴到本地配置里，程序带着这个 cookie 去请求已登录用户视角下的下载接口（此时应该不再受 errno 112 限制）。这跟"申请官方开发者凭证（AppKey/SecretKey+OAuth）"是两条不同的路：前者是"借用你自己账号的网页登录态"，免申请、但 cookie 会过期需要定期更新、且相当于把账号登录凭证放在本地文件里，需要用户接受这个安全取舍；后者更规范但要走官方申请流程（见 6.5 节）。

**目前状态**：`ListFilesAsync` 已经有明确可用的实现方案（verify + 根目录/子目录 `share/list`，全部匿名可用），但 `DownloadAsync` 卡在"匿名做不到，需要登录态 cookie"这个根本限制上，需要用户决定要不要接受"手动提供 BDUSS cookie"这个方案再继续实现。

### 6.6 通用两步流程：本地先做完 → 提示手工上传

数据获取程序的**抓取**和**合并**这两种操作，都遵循同一个两步模式：

1. **第一步：本地操作**——抓取操作在本地生成/更新 daily（或首次的master）文件；合并操作在本地把 daily 整合进 master、按命名规则生成新 master 文件（见6.2）。这一步完全不涉及网络存储，失败了不影响已有的本地数据
2. **第二步：提示上传**——本地操作成功后，程序提示"数据已更新，请手动上传以下文件到网盘：{文件列表}"（当前是6.5节的手工过渡方案；以后接了真实API，这一步就变成用户确认后自动上传，交互上是同一个"确认/提示"位置，只是背后动作从"手工"换成"自动"）

这样设计的好处：无论上传是手工还是以后自动化，本地数据都已经是完整可用、命名正确的，不会因为上传环节（不管是人操作失误还是API失败）连累抓取/合并的结果本身。

### 6.7 抓取必须手动触发 + 限速策略

**手动触发**：数据获取程序不会自己定时/静默发起抓取，必须用户主动点击"开始抓取"（或类似的确认动作）才会真正调用东方财富接口。这一点跟第2节"分析程序下载数据也要点击才触发"是同一个原则——凡是会对外发起大量网络请求的动作，都要有一个明确的用户触发点，不做后台自动静默调用。

**限速策略**：为了避免请求过于频繁被数据源限流/封禁IP，抓取时对每个请求之间加统一的延时（具体延时值和并发数留到实现阶段调参，先按保守值起步，比如控制并发数在个位数、每次请求间隔几百毫秒量级，观察是否被限流再调整）。

这里有一个关键判断：**限速策略不需要区分"首次全量"和"之后每日增量"两种场景，统一用同一套保守限速就够了**：
- **首次全量**（全市场 × 3年历史）：数据量最大，本来就是"一次性慢工程"，跑几个小时甚至更久都可以接受，限速造成的额外耗时不是问题，反而是必须的（防止被封）
- **之后每日增量**：每次只抓"每只股票的最新一天"，数据量本身就很小，就算按同样保守的限速策略跑，实际耗时也很短（几分钟量级），不需要为了"跑快点"单独放开限速、增加被封风险

所以代码里不需要做"首次用慢速、之后用快速"这种模式切换，统一一套限速参数即可，简化实现，也更安全。

### 6.8 限速策略的三层防护（已实现）

在6.7节"并发+固定延时"的基础上，`RateLimiter`（见 [RateLimiter.cs](../src/StockPlatform.Data/Remote/RateLimiter.cs)）额外加了两层：

1. **失败重试**：最多重试2次，间隔固定 2秒、10秒
2. **全局熔断**：请求遇到 403/429/空响应/连接失败（统一归类为 `RateLimitedException`），会被判定为"疑似触发反爬限流"；**这次调用自己的2次重试用完、最终还是失败之后**，才触发一个全局暂停（5~15分钟随机，避免多个并发请求同时解除暂停造成瞬时冲击），暂停期间**其他后续请求**会先等待暂停结束再尝试
3. **请求超时**：统一设为12秒（10~15秒区间内），避免单个请求卡死占用并发名额

**踩过一个真实的坑，记录一下**：最初实现时，熔断触发的时机写错了——在"每一次"失败（包括还会重试的那几次）都触发熔断，导致这次调用自己的下一次重试会立刻等待自己刚设的5~15分钟暂停，把本该12秒完成的重试拖到了20多分钟。用模拟测试（不针对真实接口，避免真的被封）验证出这个问题后修正为：**熔断只在"这次调用的重试全部用完、即将放弃"的那一刻触发**，只影响之后的其他请求，不会拖慢这次调用自己的重试节奏。修正后重新测试，模拟"失败两次+第3次成功"确实耗时12秒（2s+10s），符合预期。

## 7. 数据源与成本评估（第三方 Level-2 数据，暂缓实现）

如果后续需要 Level-2（完整逐笔成交、十档盘口）数据，评估过的候选：

| 数据源 | 定位 | 接入方式 | 备注 |
|---|---|---|---|
| Tushare Pro | 个人/量化圈常用 | 纯HTTP API，语言无关 | **优先推荐**，接入成本最低，按积分分级付费，几百到几千元/年 |
| 掘金量化/聚宽/米筐 | 量化研究平台 | 主要Python SDK | 跟现有C#技术栈对接需要中转 |
| 券商增值服务 | 部分券商给VIP客户开通 | 各家不同 | 需确认使用协议是否允许导出存为己用数据库 |
| Wind/同花顺iFinD/东财Choice | 机构级 | Python/C++/MATLAB/Excel为主，.NET支持弱 | 年费几万到几十万，超出当前项目规模 |

## 8. 待确认 / 待整合事项

1. ~~合并周期~~：已确定为纯手动触发，用户点击"合并"按钮时合并当前所有已下载的 daily 文件（见6.2）
2. ~~共享介质~~：已确定为网盘，当前用百度网盘 + 手工上传过渡方案（见6.5、6.6）
3. ~~百度网盘开发者凭证~~：暂时没有，当前用"程序命名好文件 + 提示用户手工上传"的过渡方案，不阻塞开发；以后申请到凭证再换成自动化实现（见6.5）
4. ~~分时数据历史深度~~：不设最低要求，接口能回溯多久就存多久（"能取多少就取多少"），实际取到的深度见第9节
5. **数据可获取性，原则是"能取则取，不能取则不取"**：PE/PB历史数据源、财务指标具体字段（营收/净利润/ROE/毛利率等）、以及第3.3节列的其他字段，实现阶段挨个验证东方财富接口是否提供，能拿到的就实现，拿不到的就跳过，不为了凑齐字段清单而额外接入更多数据源。**最终结果记录在第9节，作为文档的一部分补充**
6. **逐笔成交（Level-2）**：本期不做、不预留表结构。等确定采购 Tushare Pro 或其他付费源、选定存储引擎后，再单独设计这部分，不影响本期 Level-1 方案
7. **限速具体参数**：并发数、请求间隔的具体数值（见6.7）留到实现阶段实测调整，先用保守值起步

## 9. 实施后补充：实际可获取的数据范围

> 本节留空，等实现阶段实测完东方财富各接口后回填。格式建议：

- **日线**：✅ / ❌，可回溯天数
- **周线/月线**：本地聚合日线得出，理论上跟日线回溯深度一致
- **分时（1/5/15/30/60分钟）**：各周期分别注明 ✅/❌ 及实际可回溯的时间范围
- **PE/PB**：✅ / ❌，数据来源（直接接口 or 自行计算），可回溯天数
- **财务指标**（营收/净利润/ROE/EPS/BVPS/毛利率等）：逐项列出 ✅/❌ 及对应的东方财富接口字段

> [Requirement.txt](Requirement.txt) 是用户手工记录重要事项的独立笔记，与本设计文档保持独立，不需要合并或对齐。
