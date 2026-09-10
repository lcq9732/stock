# 【拉取行业分类】换东财 + 迁新框架（设计，2026-09-10）

上游结论见 `doc/datasource-eastmoney-migration.md`。这一项是那份评估里两个"要做"之一，也是先做的那个——最轻，用来把"新 Provider + 配置开关 + 迁 FetchTaskBase + 改 Catalog"这条链路走通，财务再照着做。

迁框架的判据按 2026-09-10 更正后的版本（迁移成本 + 后续维护成本，见 `doc/full-audit-task-migration-design.md` §0），不再要求"正要长大 + 卡在框架能力上"。

---

## 1. 实测依据（2026-09-10，沙箱全量拉取 50 页）

`RPT_F10_ORG_BASICINFO`，`columns=SECUCODE,SECURITY_CODE,CSRC_INDUSTRY_NAME`，`pageSize=500`，50 页拿全，接口自报 `count=24810`，其中 A 股（6 位数字 + .SH/.SZ/.BJ）**6006 只**。

| | 库里现状（两所门类 + 新浪大类） | 东财 |
|---|---|---|
| 有大类的票 | 3886 只 | **6006 只** |
| 门类 distinct | **32 个**（沪深叫法不统一："住宿餐饮" vs "住宿和餐饮业"） | **19 个**，证监会标准全称 |
| 大类 distinct | 84 | 84 |
| 库里有、东财无 | — | **0 只**（东财是超集，无回退） |
| 共有 3886 只里大类不同 | — | **402 只（10.3%）** |

402 条差异抽样分两类：

- **命名版本**（证监会 2012 版 vs 修订版）：`002828` 开采辅助活动 → 开采专业及辅助性活动；`300426` 广播、电视、电影和影视录音制作业 → …录音制作业；`920351` 装卸搬运和运输代理业 → 多式联运和运输代理业
- **真的分到不同行业**：`000900` 批发业 vs 道路运输业；`002023` 铁路船舶… vs 金属制品、机械和设备修理业；`300668` 专业技术服务业 vs 商务服务业

## 2. 范围：门类也换，不只是大类

原计划只换"大类"那半截。实测发现东财一份数据同时给两级、命名自洽（19/84），而库里门类有 32 种叫法——沪深两所各说各话，是既有问题。所以范围扩到：

| 字段 | 迁移前 | 迁移后 |
|---|---|---|
| `class_code`（门类字母 A~S） | 两所官网 | **两所官网**（东财不给字母，这一路必须留） |
| `class_name`（门类名） | 两所官网（32 种叫法） | **东财**（19 种标准名） |
| `major_name`（大类名） | 新浪（覆盖 58%） | **东财**（覆盖 100%） |
| 东财没覆盖到的票 | — | 退回两所门类兜底（`class_code` + 两所门类名） |

## 3. 设计

### 3.1 Provider 层：抽基类，两个实现并存

按 `feedback_datasource_per_class`：共享逻辑进基类，子类只管"这一家怎么给数据"，两套并存靠配置切换。

```
IndustryProviderBase                      // 共享：两所门类抓取、合并、护栏
├─ ExchangeSinaIndustryProvider           // 现有，保留可切回（门类名取两所、大类取新浪）
└─ ExchangeEastMoneyIndustryProvider      // 新增（门类名+大类取东财、字母取两所）
```

基类提供：

- `protected Task<Dictionary<string,(string Code,string Name)>> FetchExchangeClassAsync(ct)`
  —— 深交所 `CATALOGID=1110` + 上交所 `COMMON_SSE_CP_GPJCTPZ_GPLB_GP_L`，原样从现有类搬过来，行为不变。
- `GetAllAsync` 的骨架：两所门类铺底 → 交给子类 `ApplyDetailAsync(map, ct)` 就地补细分。

子类只实现 `ApplyDetailAsync`：新浪补 `MajorName`；东财补 `ClassName` + `MajorName`，并为两所名单外的票（北交所）新建条目。

**护栏放在 `IndustryTask` 而不是基类**（实现时的调整）：那道"比库里少 5% 就整轮放弃"要读库，而 provider 不该碰库。分页对账（`count` 对不上就抛）留在东财子类里，它不需要查库。

### 3.2 东财实现细节

```
GET https://datacenter.eastmoney.com/securities/api/data/v1/get
    ?reportName=RPT_F10_ORG_BASICINFO
    &columns=SECUCODE,SECURITY_CODE,CSRC_INDUSTRY_NAME
    &pageSize=500&pageNumber=N&sortColumns=SECUCODE&sortTypes=1
```

- **分页**：`sortColumns=SECUCODE` 本身唯一，满足"排序键必须定唯一序"（`project_em_sort_key_must_be_unique`）；用接口自报 `count` 对账，页数对不上就整轮放弃。约 50 页。
- **过滤**：只收 `SECURITY_CODE` 为 6 位数字且 `SECUCODE` 后缀属于 `.SH/.SZ/.BJ` 的行（`count=24810` 含非 A 股/历史行）。市场判定不自己写规则，走 `MarketClassifier`（`feedback_market_prefix_via_classifier`）。
- **拆分**：`CSRC_INDUSTRY_NAME` 形如 `"制造业-酒、饮料和精制茶制造业"`，按**第一个** `-` 拆两段（大类名里含顿号但不含 `-`，用 `Split('-', 2)`）。拆不出两段的记为只有门类。
- **限流**：datacenter-web/securities 是三个域名里最宽松的（1 秒间隔连续几百页无事），沿用现有 `RateLimiter` 参数即可。

### 3.3 Task 层：`IndustryTask : FetchTaskBase<StockIndustry>`

放 `StockPlatform.Tasks/IndustryTask.cs`，`App.xaml.cs` 里注册一行：

```csharp
taskRegistry.Register(FetchActionId.FetchIndustry,
    () => new IndustryTask(industryRepository, industryProvider));
```

- `FetchAsync`：一次全量拉完，**整体作为一批** yield。
- **为什么不分批**：这张表的语义是"全市场当前分类的完整快照"，`SaveBatch` 是清表重写。若按页落账，中途停下来库里就只剩跑过的那部分，其余票的行业凭空消失——跟 `FullAuditTask` 不按 500 只落账是同一个理由（安静的错比全丢更糟）。
- `MaxItems` / `Deadline`：**不适用**，跑完约 1~2 分钟，一批不可分。子类不实现，框架默认行为即可。
- `SaveBatchAsync`：调 `SqliteIndustryRepository.ReplaceAll(rows, source)`（新增方法，见 3.4）。
- `OnCompletedAsync`：报覆盖只数、门类/大类 distinct 数、有大类的比例——这几个数就是每次跑完的自检。

老的 `FetchOrchestrator.RunFetchIndustryAsync` **迁移后删除**，不留双份（留着就会有两条路，配置切换和调度侧准入各走各的）。

### 3.4 Schema 变更

`StockIndustry` 加 `source TEXT` 列（取值 `sina` / `eastmoney`），按 `SqliteSchema` 现有的 ALTER 迁移段写法加。

**加这一列的实际理由不是"分得清来源"，而是配合整表替换**：见 §5 风险 1。

`SqliteIndustryRepository` 新增 `ReplaceAll(IEnumerable<StockIndustry>, string source)`——一个事务里 `DELETE FROM StockIndustry` + 全量插入。现有 `Upsert`（逐条 `INSERT OR REPLACE`）保留给别处用，但这一项不再走它。

### 3.5 配置开关

`fetcher-settings.json`（JSONC，每个可选值写成一行去掉 `//` 就能用，照 `feedback_config_self_documenting`）：

```jsonc
// ── 行业分类数据源 ────────────────────────────────
//"IndustrySource": "eastmoney",  // 东财 F10（门类19+大类84，覆盖 6006 只）【默认】
//"IndustrySource": "sina",       // 两所门类 + 新浪大类（覆盖 3886 只，门类 32 种叫法）
```

### 3.6 Catalog 改动

`FetchTaskCatalog` 里 `FetchActionId.FetchIndustry` 那项：

| 字段 | 改前 | 改后 |
|---|---|---|
| `DataSource` | `"交易所 + 新浪"` | `"东财 + 交易所"` |
| `Sources` | `[Exchange, Sina]` | `[Exchange, EmDataCenter]` |
| `QuotaGroup` | `Mixed` | `Mixed`（不变） |

不改的话调度侧准入按错的源算：新浪配额被白占，东财那边反而放不开。

## 4. 实施与验收（两步，分开验收）

**先迁框架、后换源** —— 这个顺序比反过来省一遍搬运（换源的代码直接写在新结构里），而且每步的验收标准是单一的。

| 步 | 做什么 | 验收标准 |
|---|---|---|
| **4.1** | 只迁框架：`IndustryTask` + 基类抽取 + `ReplaceAll` + Catalog 注册，**provider 仍是新浪** | 跑一次，结果与老路**逐行一致**（5753 只、3886 只有大类、32 门类/84 大类都不变）；单测通过 |
| **4.2** | 换源：新增东财 Provider + 开关，默认切到 `eastmoney` | 见下 |

### 4.2 换源的验收清单

1. **覆盖不回退**：东财结果 ⊇ 库里现有有大类的票（预期"库里有、东财无 = 0"，实测已确认，切换时复核）。顺带确认东财是否覆盖库里全部 5753 只——不覆盖的那些必须落到两所兜底，一只都不能掉。
2. **402 条差异分档**：逐条过一遍，分成「命名版本」「主业变更」「存疑」三档。前两档接受，「存疑」逐个查证监会原始分类，说不清就不上线。
3. **2120 只新增抽查**：随机 30 只，跟公司主业对一遍。
4. **门类收敛**：跑完 `class_name` distinct 应为 19（现在 32）。

### 4.3 单测（`StockPlatform.Tests`）

- 解析：`CSRC_INDUSTRY_NAME` 拆分——正常两段、只有一段、大类含顿号、含多个 `-`。
- 过滤：非 A 股行（港股/历史代码）被剔除。
- 分页对账：`count` 与实收行数不符 → 抛出，不落库。
- 护栏：结果比库里少 5% 以上 → 整轮放弃。
- 合并优先级：东财缺的票退回两所门类；`class_code` 始终来自两所。
- Task 层：`SaveBatch` 走 `ReplaceAll`（清表语义），中途取消不落半截。

## 5. 风险

1. **命名版本变化 → 必须整表替换，不能逐条 upsert 留残**。"开采辅助活动"和"开采专业及辅助性活动"若在库里并存，行业中性化会把同一个行业当成两个分组。消费端（`MarketData` 的中性化分组、`FactorTabViewModel`）只把行业名当分组键、没有硬编码具体名字，所以改名本身无害——**前提是全表一次换完**。这是 §3.4 清表重写和 `source` 列的实际理由。
2. **回滚**：`IndustrySource` 改回 `sina` 重跑（1~2 分钟）。`ExchangeSinaIndustryProvider` 不删，类注释里写明退役原因和怎么切回（照 `SinaBoardFetcher` 的先例）。
3. **沙箱 ≠ 用户环境**：本文可达性与 50 页稳定性来自沙箱出口。落地前用 Debug 实例（独立数据目录）在用户环境复核一次连续 50 页。


---

## 9. 实机验收结果（2026-09-10，Debug 实例、独立数据目录）

两条路各在空库上跑一遍全市场、逐票比对。

| | 新浪（基线） | 东财 |
|---|---|---|
| 票数 | 5692 | **6025** |
| 有大类 | 3483 | **6005** |
| 门类 distinct | **32** | **20**（19 个东财标准名 + 上交所简称"其它" 17 只） |
| 大类 distinct | 82 | 84 |
| 新浪有大类、东财没有 | — | **1 只**（201872，B股/老三板，两所名单里也没有） |
| 原有门类字母丢失 | — | **0 只** |
| 无门类字母（两所名单外） | — | 661 只：920 北交所 348、B股(200/900) 114、深市新票 183 |

### 9.1 抓到的真 bug：非 A 股混入（已修）

第一次实机跑出 **21038 只**（真实约 6000）。原因是沙箱验证时按 `SECUCODE` 后缀过滤，
产品代码却只用了 `MarketClassifier`——港股等只要形如 6 位数字就被收下。
**它一个错都不报**，只在日志里表现为"15674 只是两所名单外的"。

两处修复：`IsAShare()` 同时判后缀和代码形状（单测覆盖）；
provider 加上限护栏——两所名单外的票超过 15% 就整轮放弃（北交所实际约 5%）。

教训记一笔：**验证脚本和产品代码用了两套过滤，等于没验证**。

### 9.2 意外发现：新浪那条路的大类是**整组错位**的

3482 只共有票里 **557 只（16%）** 大类不同，而且是成批错、错得离谱：

| 新浪 | 东财 | 抽样 | 只数 |
|---|---|---|---|
| 金属制品、机械和设备修理业 | 化学原料和化学制品制造业 | 湖北宜化 / 新金路 / 红太阳 / 安道麦 / 渝三峡 | 114 |
| 黑色金属冶炼和压延加工业 | 造纸和纸制品业 | ST晨鸣 / 美利云 / 凯恩股份 / 太阳纸业 | 23 |
| 石油加工、炼焦和核燃料加工业 | 家具制造业 | 索菲亚 / 喜临门 / 永艺股份 / 曲美家居 | 7 |
| 铁路、船舶、航空航天和其他运输设备制造业 | 文教、工美、体育和娱乐用品制造业 | 奥飞娱乐 / 珠江钢琴 / 海伦钢琴 | 7 |

公司名一看就知道谁对。病根应在 `FetchSinaMajorAsync`：节点清单是正则从 `newFLJK.php`
抠出来的 `(node, name)` 配对，某个节点字段一错位，整个节点的成分股就被贴上别人的行业名，
**而且不报任何错**。这个错从 2026-08-04 建表起就在库里。

**因此新浪那条路的定位从"退路"降级为"留档"**：真要切回去，得先修错位。
已写进 `ExchangeSinaIndustryProvider` 类注释和 `IndustrySource` 的配置说明。

> 这条比覆盖率提升重要得多——原本以为换源是"多拿 2000 只"，实际上还修掉了
> 已经用了一个多月的错误行业标签。FactorLab 的行业中性化、板块中位数这些都吃这张表。
