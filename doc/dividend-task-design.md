# 【拉取分红送配】迁到新任务框架（2026-09-18 设计，待确认）

## 0. 起因：中断之后从头再来

凌晨跑【拉取分红送配】，撞上新浪限流；早上手工停止，重新执行——**凌晨已经抓到的
5000 多只会被原样再抓一遍**，而且停止那一轮什么记录都没留下。

复现的三个事实（2026-09-18 查证）：

| 事实 | 位置 |
|---|---|
| 每轮都取全量名单（`stock` + `delisted`，约 5800 只），没有任何"已抓过就跳过"的判断 | `FetchOrchestrator.RunFetchDividendAsync` |
| 失败名单写在 `await Task.WhenAll(tasks)` **之后**；取消直接冒泡，走不到那一步 | 同上 |
| 因此 manifest 里 `FailedDividendCodes: 0`、`Todos: {}`——【重新拉取失败股票】也无从下手 | `publish/data/local/manifest.json` |

老注释里写的是"分红慢变，跟股东数据一样每次全量刷新（不做按期跳过）"。这个决定在
"一轮能顺利跑完"的前提下成立；限流一来，前提就没了。

## 1. 它是老形状的三件套

| 件 | 位置 |
|---|---|
| 目录条目 | `FetchTaskCatalog.cs:1370`（`FetchActionId.FetchDividend`） |
| 执行体 | `FetchOrchestrator.RunFetchDividendAsync` / `RetryDividendAsync` |
| 接线 | `MainViewModel.DispatchPlanActionAsync` 的一条 `case` |

按 2026-09-08 定的规矩（`IFetchTask` 类注释）：老任务不迁，但**要动的时候按新形状重写**。
这次要动的正好是骨架白送的那两件能力——流式落库（停在哪都不丢）和 `Deadline`/`MaxItems`。

## 2. 为什么是迁移，不是在 orchestrator 里打补丁

打补丁要自己写的：批的切分、取消时落盘、分批上限、状态事件、进度节流——
`FetchTaskBase` 的类注释里点名的就是这四件"每个子类各写一遍就会各写错一遍"。
而迁移之后这些一行都不用写，本任务只剩三件自己的事：**名单怎么排、一批怎么抓、状态怎么记**。

## 3. 批的粒度：30 只一批

一只票 = 一个请求（分红和配股是同一页的两张表，一次拿两份，这点不变）。
限流器是 `maxConcurrency: 3, delay: 1s`，所以：

- **一批 = 30 只**，批内 `Task.WhenAll` 并发（并发度仍由 `RateLimiter` 控），批间由骨架落库。
  一批约 10 秒，全量 5800 只≈194 批≈32 分钟——跟现在的吞吐一致，**不牺牲速度**。
- 停止只在批边界生效（符合"取消只在单元之间生效"），最多丢 30 只的在途结果。
- `MaxItems` 的语义自然变成"本轮最多几批"，`SupportsPartialRun: true`，
  于是这一项可以挂「空闲时」慢慢啃。
- 每批报一次进度 → 静默远小于看门狗的 5 分钟默认值，`MaxQuiet` 保持 null。

`TItem` = 一只票的抓取结果：

```csharp
record DividendOutcome(string Code, List<DividendRow> Dividends, List<RightsIssueRow> Rights,
                       bool Ok, string? Error);
```

失败的那只也进批（`Ok=false`）——`SaveBatchAsync` 要靠它记失败状态，扔掉的话就又回到
"取消即失忆"。

## 4. 水位线：新表 `DividendFetchState`

**`Dividend` 表当不了水位线**：没分红的票一行都不写，"没抓过"和"抓过、确实没分红"
长得一模一样。按行数判会让 3000 多只无分红的票每轮全抓——正是要修的病。

```sql
CREATE TABLE IF NOT EXISTS DividendFetchState (
    code          TEXT PRIMARY KEY,
    last_ok_at    TEXT,      -- 上次抓成功的时刻；失败不更新（失败≠抓过）
    dividend_rows INTEGER,   -- 那次拿到几条分红（0 = 确实没有）
    rights_rows   INTEGER,   -- 几条配股
    last_fail_at  TEXT,      -- 上次失败时刻，诊断用
    fail_reason   TEXT
);
```

水位线的粒度是**只**，细于骨架的截断粒度（批），满足
`CustomerSupplierTask` 立的那条一般原则：任务的水位线必须细于骨架的截断粒度。

**陈旧判据**：`last_ok_at` 为空，或早于 `今天 - StaleDays`。
`StaleDays` 建议 **25 天**——分红一年一次为主，方案从预案到实施的 `progress` 变化按月复查足够，
而 25 天保证"同一天里重跑不会重抓"，正是这次要解决的场景。

排序：**先没抓过的、再最旧的**（`last_ok_at` 升序，`code` 次序稳定）。
断点续跑因此天然成立——上一轮抓到哪，下一轮自然从那里接着走，不需要额外的"游标"。

## 5. 模式映射

| 模式 | 这一项的含义 |
|---|---|
| `Incremental`（默认） | 只抓陈旧的（含从没抓过的）。全部都新鲜 → `NothingToDo` |
| `FirstBackfill` | 忽略状态表，全市场重抓一遍（＝今天的行为，留作"我就是要强刷"的出口） |
| `FillBacklog` | 只补 `Todos[FetchDividend].failed` 里的那些 |
| `SpecificDay` | 不支持（分红页是全历史，没有"某一天"的概念） |

目录条目补上 `SupportedModes: Incremental | FirstBackfill | FillBacklog`、
`SupportsPartialRun: true`、`Sources: [DataSourceId.Sina]`。

⚠ `RetryTaskIds.Dividend` 的取值 `"FetchDividend"` **不动**——它已经等于 `FetchActionId` 的
枚举名，manifest 里已有的待办键继续认得出来（`ConfirmedPartialDays` 改名那次踩过的坑）。

## 6. 限流：连续全军覆没就收工，而不是把 5000 只记成失败

`RateLimiter` 自己有熔断和指数退避，但它管的是"这一个请求发不发得出去"。
任务层还要有一条：**连续 3 批（90 只）全部失败 → 停止本轮**，返回
`TaskRunResult.Skipped("新浪在限流，本轮停在第 N 批")`。

为什么是 `Skipped` 不是 `Failed`：`Skipped` 的语义就是"这一轮根本没开工"，
计划项的 `AlreadyRanOn` 不认它，**限流过去之后今天还能再来**；记成完成的话今天就不会再跑了。

失败但没触发熔断的那些，照旧进 `Todos[FetchDividend].failed`，
`SetFailedTodo` 的"这轮碰过、这次没失败的移出名单"语义保持不变。

落盘时机改成**每批都写**（状态表）+ **收尾写 manifest**（`OnCompletedAsync` 和
`OnStoppedAsync` 两条路都写），所以停止之后失败名单也在。

## 7. 两条不能破的铁律

1. **空结果不删**。`ReplaceByCode` 是先 `DELETE` 再插——限流返回空页面时若照写，
   会把库里已有的分红全删光。现有代码靠 `rows.Count > 0` 的判断挡着，迁移后这条判断
   必须原样带过去，并写进注释（"拉不到新数据时，库里上一次的结果仍然有效"）。
2. **分红和配股来自同一次请求**。批内逐只写、两张表各自事务（沿用现状）。
   要不要合成同一个事务是可选项：好处是不会留"有分红没配股"的半拉记录，
   代价是要新增一个跨两张表的写入方法。**默认沿用现状**，除非你要。

## 8. 改动清单

### 新增
- `src/StockPlatform.Tasks/DividendTask.cs`——继承 `FetchTaskBase<DividendOutcome>`
- `SqliteSchema`：`DividendFetchState` 建表（`EnsureSchema` 里，幂等）
- `IDividendRepository`：`GetFetchStates()` / `SaveFetchStates(batch)` 两个方法
- `App.xaml.cs`：`taskRegistry.Register(FetchActionId.FetchDividend, () => new DividendTask(...))` 一行

### 改
- `FetchTaskCatalog.cs:1370`：补 `SupportedModes` / `SupportsPartialRun` / `Sources`，
  Note 里加一段说明"增量＝只抓陈旧的、强刷用整段回补"
- `IFetchTask`：加 `bool HandlesBacklog => false`（见 §9.2）
- `FetchTaskRegistry`：加 `HandlesBacklog(id)`，并实现 `ITaskBacklogRunner`
- `FetchOrchestrator`：加 `ITaskBacklogRunner` 注入点；私有 `RunFillBacklogAsync` 开头转交；
  删掉 `case RetryTaskIds.Dividend`
- `MainViewModel.DispatchPlanActionAsync`：`FillBacklog` 那条总分支加 `HandlesBacklog` 的判断
- `LhbSeatTask` / `BlockTradeTask` / `LhbTask`：那三句"压根到不了这儿"的注释改准（§9.4）

### 删
- `FetchOrchestrator.RunFetchDividendAsync`、`RetryDividendAsync`
- `FetchOrchestrator.RunFillBacklogAsync` 里的 `case RetryTaskIds.Dividend`
- `MainViewModel` 里 `case FetchActionId.FetchDividend`

## 9. `FillBacklog` 统一走 registry（2026-09-18 用户拍板选 B）

### 9.1 不能用"registry 里有就走 registry"这条粗分支

`LhbSeatTask` / `BlockTradeTask` / `LhbTask` 里都有一段 `if (args.Mode == FillBacklog) return [];`，
注释写着"压根到不了这儿，这里返回空是兜底"。粗分支一改，它们就真的会收到 `FillBacklog`，
然后返回空天数 → 报一句"没有欠着的残缺日"→ **待办永远补不上，而且一声不吭**。
这正是这个项目反复栽过的那类静默失效。

所以 B 的正确形状是**按能力声明分派**，不是按"是不是新式任务"分派。

### 9.2 任务自己声明能不能补待办

`IFetchTask` 加一个属性，默认 false：

```csharp
/// <summary>这个任务能不能自己补待办（FetchMode.FillBacklog）。
/// false＝待办编排仍在 orchestrator 那边（席位/大宗/龙虎榜的残缺日就是这样）。</summary>
bool HandlesBacklog => false;
```

`DividendTask` 覆盖成 `true`；其余任务一律不动，行为完全不变。
`FetchTaskRegistry` 加一个 `bool HandlesBacklog(FetchActionId id) => Create(id)?.HandlesBacklog == true;`
（实例很便宜，注册表本来就是"每次现 new 一个"）。

### 9.3 两个入口都要改，漏一个就漏一半

| 入口 | 现在 | 改成 |
|---|---|---|
| 计划项设成【只补待办】 | `MainViewModel.DispatchPlanActionAsync` 一律 `orchestrator.RunFillBacklogAsync` | 先问 `HandlesBacklog`，是就落到下面那条 registry 总分支 |
| 【重新拉取失败股票】 | `orchestrator.RunRetryFailedInternalAsync` 内部循环调私有 `RunFillBacklogAsync(taskId,…)` | 私有重载开头先转交 |

第二个入口是 B 的**真正代价**：`RunRetryFailedInternalAsync` 在 orchestrator 内部按
`DispatchOrder` 挨个跑 taskId，根本不经过界面那一层。分红的补法搬进任务之后，
这条路必须能回调 registry，否则【重新拉取失败股票】会**静默跳过分红**。

层次不允许直接调：registry 在 Scheduling，它引用 Data；Data 不能反过来引用 Scheduling。
所以用一个注入的钩子（接口定义在 `Data.Orchestration`，实现在 Scheduling，`App.xaml.cs` 注入）：

```csharp
public interface ITaskBacklogRunner
{
    bool Handles(string taskId);
    Task<FetchResult> RunAsync(string taskId, IProgress<string>? progress, CancellationToken ct);
}
```

`RunFillBacklogAsync` 的私有重载开头：

```csharp
if (_backlogRunner?.Handles(taskId) == true)
{
    var r = await _backlogRunner.RunAsync(taskId, progress, ct);   // 走 registry，Mode=FillBacklog
    // ⚠ 立刻返回空 attempted：失败名单由任务自己写自己的那一条。
    //   不返回的话，收尾的 FinishFetchRun 会拿**转交之前** Load 的 manifest 去保存，
    //   把任务刚写进去的名单覆盖掉。
    if (r.Errors.Count > 0) foreach (var e in r.Errors) errors.Add(e);
    done.Add($"{TaskLabel(taskId)}（任务自补）");
    return [];
}
```

### 9.4 三处注释要同步改

`LhbSeatTask` / `BlockTradeTask` / `LhbTask` 里那句"压根到不了这儿"已经不准确了——
改成"只有 `HandlesBacklog = true` 的任务才会收到 `FillBacklog`；本任务是 false，
待办仍由 orchestrator 编排"。注释写错比没有更糟，下一个人会照着它判断。

### 9.5 范围

这一版**只让分红一个任务接管待办**。其余任务的 `HandlesBacklog` 全是 false，
分派路径、manifest 格式、【重新拉取失败股票】的顺序表都不变。
以后谁要接管，改一个属性 + 在自己任务里实现 `FillBacklog` 分支即可，不用再碰分派逻辑。

> **2026-09-18 当天就有了下一批**：龙虎榜/席位/大宗三项的残缺日也照这个形状搬进了各自任务，
> `DailyRefetcherFor` 只剩两融。机制一行没改——这正是能力位那个设计想要的结果。
> 见 doc/fill-backlog-to-tasks-design.md。

## 10. 不做的事

- **不换数据源**。东财有全市场分红明细接口，用它可以只重抓"最近有新公告"的票，
  请求量能再砍一个数量级——但换源掉数据的结论已有（见 `doc/datasource-eastmoney-migration.md`），
  而且那是"另一个索引源"的新话题，跟这次的断点续跑无关。留个口子记在这。
- **不改并发度**。3 并发/1 秒是现状，本次不动，免得把"限流"这个变量搅进来。
- **不做"永久跳过无分红的票"**。`dividend_rows = 0` 只是那一次的事实，
  新股第一次分红就会变——状态表只管陈旧度，不管"以后还抓不抓"。

## 11. 验证

1. `dotnet test`（`StockPlatform.Tests`）——改了 `FetchPlan`/目录要跑。
2. 新增单测：`PlanStocks` 的排序与陈旧过滤（纯本地、不发请求）；
   连续失败触发 `Skipped` 的判据。
3. Debug 实机（数据目录天然隔离）：
   - 跑一轮 `MaxItems: 3`（90 只）→ 停止 → 看 `DividendFetchState` 有 90 行；
   - 再跑一轮 → 日志应显示跳过那 90 只，从第 91 只接着抓；
   - `FirstBackfill` 跑一小段 → 应无视状态表从头来。
4. 数据等价性：迁移前后各抓同一批 20 只，比对 `Dividend` / `RightsIssue` 两张表逐行相同。
5. **两条待办入口都要实机走一遍**（§9.3 漏一个就漏一半）：
   - 计划项设成【只补待办】→ 日志应显示任务自己在补，不是 orchestrator；
   - 点【重新拉取失败股票】→ 分红那几只要真被抓、名单要清零。
   顺带回归一次席位/大宗/龙虎榜的残缺日待办——它们的 `HandlesBacklog` 是 false，
   补法必须跟改动前一模一样。

---

## 12. 实现记录（2026-09-18 落地）

按上面的方案实现完，编译通过、`dotnet test` 全绿（1708 passed / 1 skipped）。三处跟方案的偏差：

**① `BatchSize` 做成了可配的构造参数**（默认仍是 30）。原方案里它是常量，
但那样一轮里只有一批（测试用 7 只票），"中断之后接着跑"这件事**根本测不出来**——
而它正是这次迁移的全部意义。现在测试用 `batchSize: 2` 真的跑出多批，
产品代码走默认 30，行为不变。

**② 抽了两个静态方法出来** `DividendTask.SelectDue` 和 `DividendTask.IsDeadBatch`。
理由跟 `FetchOrchestrator.DispatchOrder` 当初抽出来一样：真跑一轮要发几千个请求，测不了；
而这两条判据错一个字，后果分别是"每轮重抓全市场"和"限流时把 5800 只全记成失败"。

**③ `ITaskBacklogRunner.Handles` 做了 `Enum.TryParse` 的兜底**：待办里的 taskId 是字符串，
解析不出动作时返回 false（走老路），而不是抛。

### 落地后的验证

| 项 | 结果 |
|---|---|
| `dotnet build StockAnalyzer.sln` | 0 error |
| `dotnet test`（全量） | 1708 passed、0 failed、1 skipped、2m44s |
| `DividendTaskTests`（7 条） | 陈旧过滤、排序、失败不抹 `last_ok_at`、无分红也算抓过、熔断判据 |
| `DividendTaskResumeTests`（10 条） | 离线跑**产品代码本身**：真 SQLite + 真 manifest + 假数据源 |

离线整链覆盖的场景：
- 跑完一轮 → 第二轮 **0 个请求**（老实现会重抓 5800 只）；
- 一批 2 只、抓到第 5 只时取消 → 前两批 4 只留在状态表里，下一轮**只抓剩下的 3 只**；
- `MaxItems` 到量收尾（正常完成）→ 水位线照样落好，下一轮不重抓那 4 只；
- 全部按限流失败 → 跑到第三批就收工，结果是 **Skipped（带原因）而不是 Completed**，
  7 只只发了 6 个请求；
- 失败的进待办 → 【只补待办】只抓那两只 → 补上就从名单里划掉；
- 整批失败时**库里已有的分红没被删**（先删后插那条铁律）；
- 注册表按 `HandlesBacklog` 分派：分红 true、龙虎榜 false，
  `RetryTaskIds.Dividend` 这个字符串解析得成 `FetchActionId`。

### 实机验证（2026-09-18 09:14~09:18，隔离的 Debug 实例）

把 Debug 构建整份拷到临时目录跑（数据目录跟着 exe 走，所以那份拷贝天然跟正式实例、
也跟 bin 下那个共用的 Debug 数据目录隔开），库里只放 3 只票，全程 4 个真实请求：

| 点了什么 | 日志 | 结论 |
|---|---|---|
| 【重新拉取失败】（manifest 里预置了分红失败 2 只） | `本轮要补：分红 2 只` → `开始拉取分红送配…本轮 2 只` → `本轮重试完成：分红送配（任务自补）` → `失败名单已全部清零` | **转交口通了**——这条是 §9.3 说的那个不经过界面的入口 |
| 【拉取分红送配】增量 | `本轮 1 只` | 刚抓过的两只被跳过，只抓第三只 |
| 再点一次 | `分红送配都是 25 天以内抓的，本轮没有要抓的` | **0 个请求** |
| 【重新拉取失败】（改成预置席位残缺日 1 天） | `本轮要补：席位残缺日 1 天` → `龙虎榜席位(东财)残缺日补齐完成：补上 1/1 天、写入 730 行` | 老路没被动——席位的 `HandlesBacklog` 是 false，仍由 orchestrator 编排 |

库里核对：`DividendFetchState` 两行都有 `last_ok_at`，`Dividend` 000001 38 条 / 600000 26 条，
`RightsIssue` 000001 3 条，manifest 的 `Todos` 清空。

### 顺带发现（**不是这次引入的**，没改）

补完之后日志里跟着一句 `已排定自动重试：09-18 21:00（分红 2 只）`，
跟上一行的"失败名单已全部清零"自相矛盾。原因是 `MainViewModel.RefreshFailedCodeCount`
是异步刷新的（`Task.Run` + `Dispatcher.Invoke`），而 `ScheduleAutoRetry` 读的是还没刷新的
`FailedRetry` 快照。这条竞态对所有任务都一样（跟走不走 registry 无关），
后果只是多排一次空的自动重试——到点跑一轮发现名单是空的就停了。

### 还没做的

**正式实例没跑过**——新 exe 要用户自己发布，这次只在 Debug 里验。
另外「整段回补」强刷全市场那条只有单测覆盖（离线），没在实机点过，
真要强刷时留意一下日志里说的是 `（整段回补：无视水位线全抓）`。

---

## 13. 水位线播种（2026-09-18 当天追加）

### 为什么要

状态表是随这次迁移新建的，**空表意味着全市场 5902 只都算"没抓过"**，
于是新版第一轮还是会全量重抓一遍——而库里绝大多数票 11 天前刚抓过。
断点续跑要到第二轮才开始省事。

正式库实测（只读查询，没动数据）：

```
名单(stock+delisted)                     5902 只
Dividend 表里有行、时刻有效 → 可播种      5827 只（其中 5825 在名单里，且都在 25 天内）
名单里一行都没有 →「无分红 或 没抓过」      77 只
每只票最后写入日：09-07 3674 / 09-18 2136 / 09-17 15 / 0001-01-01 323（都不在名单里）
```

⇒ 播种之后第一轮只抓 **77 只**，不是 5902 只。

### 怎么做

`IDividendRepository.SeedFetchStatesFromDividends()`：**只在状态表整张是空的时候**跑一次，
用 `Dividend` 表每只票的 `max(fetched_at)` 当 `last_ok_at`。任务在 `Plan()` 开头调它，
播了就报一句"水位线首次播种：认领 N 只"。

### 两处不精确（方向都是"宁可多抓"）

① **`fetched_at` 是"最后一次写进分红行的时刻"，不是"最后一次抓取的时刻"**。
   某只票今天抓了但返回空（真没分红），老代码不写行，时刻停在上一次 → 播种偏早 → 多抓一次。
② **一行都没有的票播不了种**——"无分红"和"没抓过"在 `Dividend` 表里长得一模一样，
   这正是要单独建状态表的理由。它们照旧去抓。

反过来**绝不会**把没抓过的票播成"抓过"，那才是会造成静默漏抓的方向。
另外 `fetched_at` 早于 1990 的丢掉：库里 323 只老 code 记的是 `DateTime.MinValue`，
那不是"抓过"，播成水位线会让它们永远不抓。

### 验证

单测 4 条（播种生效后不重抓、表非空不覆盖真实状态、MinValue 不播种、无行的票只能抓），
外加隔离 Debug 实例实机跑（4 只票的假库、1 个真实请求）：

```
[09:59:56] 水位线首次播种：按库里 Dividend 的抓取时刻认领 3 只
[09:59:56] 开始拉取分红送配…本轮 1 只          ← 只剩 Dividend 表里一行都没有的那只
[09:59:58] 分红送配完成：本轮 1 只、有分红 1 只、共写 38 条、配股 2 条、失败 0 只
[10:00:25] 分红送配都是 25 天以内抓的，本轮没有要抓的   ← 再点一次：0 请求、也不再播种
```

全量 `dotnet test`：1742 passed / 0 failed / 1 skipped。

---

## 14. 下一步：用"新公告索引"决定谁要抓（还没做）

播种解决的是"已经抓过的别重抓"；它解决不了**每 25 天一轮的全市场刷新**——
那仍然是 5902 个请求、限流下要跑几小时，而其中绝大多数票这期间**根本没出新方案**。

新浪那一页没有时间参数（URL 只有 `stockid`，一次返回整页全历史、无分页），
所以"只拉某天之后的"在这个源上省不了任何东西：成本 100% 在"抓哪些票"。

真正能省的是先问一句"最近哪些票出了新分红方案"：东财 `RPT_SHAREBONUS_DET`
全量 56973 行、带公告日（实测记录在 doc/datasource-eastmoney-migration.md §3.3），
按公告日倒序取最近 N 天，一两个请求就拿到名单，只对这些票去新浪抓整页。
每月刷新 5902 → 几十个请求。

**只当索引、值仍取新浪**，所以不掉数据——那张表当值源是不合格的：
退市股全空（000033/600145/300216 抽查都是 0 行），配股比例只在 `EVENT_EXPLAIN` 文本里。

### 沙箱实测（2026-09-18，走沙箱出口，本机没发请求）

| 验的东西 | 结果 |
|---|---|
| 接口可达、总量 | `pages=18992 @pageSize=3` ⇒ 约 **56976 行**，跟 09-10 记的 56973 对得上 |
| 能不能按公告日筛 | 能。`filter=(NOTICE_DATE>='2026-09-01')` ⇒ **337 行 / 337 个代码，一页装得下** |
| 近 7 周 | `NOTICE_DATE>='2026-08-01'` ⇒ 963 行 / 942 个代码、2 页 |
| 跟新浪比对 | 库里今天抓的、公告日 ≥ 08-01 的 282 只中，**4 只不在东财索引里** |

那 4 只查清了，是两类缺口，都不是偶然：

**① 东财这张表只收「实施分配」，不收预案**（3 只）
```
000838 库里 2026-08-10 预案(10转5.96)   东财最新记录 2021-05-08
000958 库里 2026-08-28 预案(10派0.7)    东财最新记录 2025-10-30
300044 库里 2026-09-02 预案(10转11)     东财最新记录 2019-07-03
```

**② 实施公告也会滞后**（1 只）
```
000538 库里 2026-09-17 实施(10派10.38, 除权 09-24)   东财最新记录 2026-04-24
```

### 于是设计上必须带三件事

1. **索引窗口要宽**，不能只查"昨天"。按 ① ② 的滞后量，回看 **30~45 天**起步。
2. **预案阶段抓不到**是这条路的固有代价——新出的预案要等它变成"实施"才会进东财表、
   才会被索引命中。对股息率因子无影响（只算已实施的），但库里 `progress='预案'` 的行
   会比新浪旧一阵子。
3. **兜底必须留**：索引法只管日常增量，**每 90 天仍全量刷一遍**（捞预案、捞东财缺的）；
   退市股不在东财那张表里（000033/600145/300216 抽查全空），单独按老规矩轮着刷。

### 还没定的

- 排序键唯一性没测到底。索引只要 code 集合，重复无所谓、遗漏才要命，
  所以实现上**按天切片**（一天几十行，永远单页）比深分页稳，也绕开
  "排序键排不到主键末列就跨页重复+遗漏"那个坑。
- 索引那一步算不算独立任务、还是并进 `DividendTask` 的 `Plan()`——
  倾向后者（它就是"谁要抓"的一部分），但那样这一项就同时占新浪和东财两个源，
  调度侧的 `Sources` 要跟着改。


---

# 索引法设计方案（2026-09-18，待确认）

## 15. 要解决的问题

播种解决了"已经抓过的别重抓"，但**每轮全市场刷新仍是 5902 个请求**。
实测凌晨那轮在限流下跑 4 小时 48 分才到 2350 只——这一项的成本压不下来，
根子是"不问就不知道谁出了新方案"。

新浪那一页没有时间参数（URL 只有 `stockid`，整页全历史、无分页），
所以省不了"抓哪一段"，只能省"抓哪些票"。

## 16. 沙箱实测结论（2026-09-18，本机没发请求）

东财 `RPT_SHAREBONUS_DET`，`filter=(NOTICE_DATE>='…')`：

| 项 | 结果 |
|---|---|
| 近 18 天（09-01 起） | 337 行 / 337 代码，**一页装得下** |
| 近 7 周（08-01 起） | 963 行 / 942 代码，2 页 |
| 近 90 天（06-20 起） | 1897 行 / 1692 代码，4 页 |
| `ASSIGN_PROGRESS` 取值 | 实施分配 1444 / 董事会预案通过 275 / 股东大会通过 177 / 预披露 1 |
| **漏检率** | 库里近 90 天有公告的 504 只中，**5 只不在索引里**（≈1%） |

两个关键事实：

**① 预案也在索引里**（推翻了先前只看 4 个样本得出的"东财只收实施"）。
董事会预案通过、股东大会通过、预披露都收，所以进度推进也能被索引捕捉——
同一个方案实施时 `NOTICE_DATE` 会更新（样例 000400：`PLAN_NOTICE_DATE=08-20`、
`NOTICE_DATE=09-18`、进度"实施分配"）。

**② 那 1% 是东财表本身缺记录，不是窗口不够**。000538（09-17 实施、10派10.38）、
000838、000958、300044 在 90 天索引里全都没有，扩大窗口救不了。
⇒ **索引法不能单独用，必须配周期性全量兜底**；兜底周期＝漏检数据最长滞后多久。

## 17. 设计

### 17.1 判据：把"索引命中"并进现有的 `SelectDue`

不新增模式、不新增表。增量这一轮要抓的 =

```
索引命中的票        （有新公告/进度变了 → 无视水位线，必抓）
  ∪
超过 StaleDays 没抓过的票   （兜底，StaleDays 从 25 天改成 90 天）
```

排序：**索引命中的排最前**，其余仍是"先没抓过的、再最旧的"。
这样被 `MaxItems`/`Deadline` 截断时，先保住有新数据的那些。

### 17.2 兜底摊开，别再攒成一次 5902

`StaleDays: 25 → 90`，同时给这一项配**每轮上限**（建议 400 只）。
于是日常一轮 ≈ 索引命中几十只 + 到期兜底几十只，
**每天一两百个请求、几分钟跑完**，而不是每 25 天一次几小时。

配合「空闲时」触发最合适——这一项 `SupportsPartialRun` 已经是 true。

### 17.3 退市股单独一档

退市股不在东财索引里（`RPT_SHAREBONUS_DET` 退市股全空，09-10 已实测），
而它们的分红是**静态历史、不会再变**。给一个单独的周期：`DelistedStaleDays = 365`。
不设成"抓过就永不再抓"，是留一条自愈的路（数据源补录过历史）。

### 17.4 索引拿不到就退化，绝不空转

```
索引请求失败/超时/返回空  →  报一句「索引不可用，本轮退回按水位线抓」
                          →  这一轮按 StaleDays 正常跑
```

**绝不能**因为索引没拿到就"本轮没有要抓的"——那是静默漏抓。

### 17.5 按周切片，不深分页

一片 7 天 ≈ 150 行，稳稳单页；回看 45 天 ⇒ **7 个请求**。
不用 `pageSize=500` 翻页是因为排序键 `NOTICE_DATE+SECURITY_CODE` 未必唯一，
而这个项目在"排序键排不到主键末列 ⇒ 跨页重复+遗漏"上栽过
（见 project_em_sort_key_must_be_unique / 龙虎榜席位那次）。
索引只要 code 集合，重复无所谓、遗漏才要命，所以选永远单页的切法。

回看窗口 45 天：覆盖"预案→实施"的正常间隔（样例 08-20 → 09-18 是 29 天），
留一倍余量。索引**无状态**，不记"上次索引到哪天"——省下一个会写错的状态，
代价只是每轮多几个请求。

### 17.6 新增一个 provider 类，Plan 变成 async

按"每个数据源/通道一个类"的规矩：

```csharp
public interface IDividendNoticeIndex          // Logic 层
{
    /// 最近 lookbackDays 天有分红公告（含预案/进度更新）的股票代码。
    Task<IReadOnlySet<string>> GetRecentAsync(int lookbackDays, CancellationToken ct);
}

public sealed class EastMoneyDividendNoticeIndex : IDividendNoticeIndex   // Data.Remote
```

`DividendTask.Plan()` 从纯本地变成 `async` 并接 `ct`：先查库（名单+水位线），
再问索引，合并成目标名单。索引结果**不落库**——它是一次性的判据，
落库就多一张要维护、要对账、会过期的表。

### 17.7 开关（JSONC，照抄就能用）

`fetcher-settings.json` 加一项，出问题能一键退回今天的行为：

```jsonc
// 分红送配的「谁要抓」判据。
//   "EastMoney" = 先问东财最近有谁出了新公告，只抓这些 + 到期兜底的（省 90% 请求）
//   "None"      = 不问，纯按水位线到期轮换（2026-09-18 的行为）
"DividendNoticeIndex": "EastMoney",
```

### 17.8 调度侧

`Sources: [DataSourceId.Sina]` → `[DataSourceId.Sina, DataSourceId.EmDataCenter]`。
不改的话并发判断会以为它只占新浪，可能跟别的东财任务撞在一起。

## 18. 代价与残留风险

| 风险 | 缓解 |
|---|---|
| 东财漏 1%（表本身缺记录） | 90 天全量兜底；最坏情况某只票的新方案晚 90 天入库 |
| 索引源不可达 | 退化成纯水位线（§17.4），不静默 |
| 多一个源的依赖 | 开关一键退回 `"None"` |
| 预案期的方案变动频繁 | 每次变动都会更新 `NOTICE_DATE`，索引能捕捉；不捕捉的那 1% 靠兜底 |

**收益**：每 25 天一次 5902 请求（限流下几小时）
→ 每天 7 个索引请求 + 一两百只，几分钟。

## 19. 验收

1. 单测：索引命中 ∪ 到期 的合并与排序；索引为空/抛异常时退化成纯水位线；
   退市股走 365 天那一档。
2. 离线整链：`FakeNoticeIndex` + 现有 `FakeProvider`，验证"索引命中的票即使昨天抓过也抓"。
3. 沙箱等价性（已做一半）：再跑一次"近 45 天索引 ∩ 库里新公告"的比对，把漏检清单钉死。
4. 实机：Debug 实例跑一轮，日志应显示「索引命中 N 只、到期兜底 M 只」，
   并手动把索引源断开一次，确认退化路径。

## 20. 实现记录（2026-09-18 落地）

编译通过、`dotnet test` 全绿（1787 passed / 1 skipped）。

### 改动

| 件 | 位置 |
|---|---|
| 索引契约 | `IDividendNoticeIndex`（Logic.Abstractions）——只回答"谁要抓"，不提供任何值 |
| 东财实现 | `EastMoneyDividendNoticeIndex`（Data.Remote）——`RPT_SHAREBONUS_DET`，按周切片、永远单页 |
| 判据 | `DividendTask.SelectDue` 加 `hits` / `delisted` / `delistedCutoff` 三个参数 |
| 常量 | `StaleDays` 25→**90**、新增 `DelistedStaleDays=365` / `MaxCodesPerRun=400` / `IndexLookbackDays=45` |
| 计划 | `Plan()` → `PlanAsync()`（要发请求了），纯查库那半拆成 `LoadLocal()` 整块推线程池 |
| 开关 | `FetcherSettings.ReadDividendNoticeIndex`，模板里两行照抄就能用 |
| 装配 | `App.xaml.cs` 按开关造索引；**索引自带一份限流器**，不跟财务那条 datacenter 线共用 |
| 目录 | `Sources: [Sina, EmDataCenter]` + Note 写清索引/兜底/怎么关 |

只在增量模式问索引：**只补待办**（目标从清单来）和**整段回补**（本来就全抓）都不问，省一轮请求。

### 实机验证（隔离 Debug 实例，4 只票的假库）

开关默认（eastmoney）：

```
[10:53:52] 水位线首次播种：…认领 4 只
[10:53:52] 先问一句最近 45 天谁出了分红公告（东财 datacenter）...
[10:54:01] 公告索引：934 只在最近 45 天有分红公告或进度更新。      ← 7 个请求、9 秒
[10:54:01] 开始拉取分红送配…本轮 2 只                              ← 4 只里命中 2 只
[10:54:02] 分红送配完成：本轮 2 只、有分红 2 只、共写 72 条、配股 4 条、失败 0 只
```

四只票全都是"2 天前抓过"，纯水位线本该一只都不抓；**索引命中的那 2 只照样抓了**——
这正是索引法要的行为（命中＝出了新方案或进度变了）。

开关设成 `"none"` 重启后：

```
[10:55:32] 分红送配都是 90 天以内抓的，本轮没有要抓的（想强刷全市场用【整段回补】）。
```

没有"先问一句"，退回纯水位线。

### 顺带修掉一个测试污染

`DividendTaskResumeTests.Dispose` 原来抄了 `BlockTradeTaskTests` 的
`SqliteConnection.ClearAllPools()`。那是**进程级**的，会把别的测试类正在用的连接池一起清掉：
单独跑这一类 31 条全绿，跟全量一起跑必 `Test Run Aborted`（testhost 崩，每次停在不同条数——
298、234，典型的并发误伤）。去掉之后全量稳定 1787 全绿。
代价只是临时目录偶尔删不掉，留在 `%TEMP%` 里。

⚠ `BlockTradeTaskTests` 里那一句还在（不是这次的改动范围），它是同一颗雷。

### 还没做的

正式实例没跑过——新 exe 要用户自己发布。发布后第一轮的预期是：
播种认领 5827 只 → 索引问一次（约 7 个请求）→ 抓"索引命中 ∩ 名单" + 到期兜底的那批，
上限 400 只。日志第一行会写"本轮 N 只"。

## 21. 判据补强：命中≠在索引里（2026-09-18 当天，发布后立刻改）

### 毛病

索引原来只返回**代码集合**，判据是"在索引里就抓"。但回看窗口是固定的 45 天，
于是同一只票在这 45 天里**天天都会被重抓**——稳态每天 900 多个新浪请求，
而真正"自上次抓取之后又出了新公告"的只有几十只。正式实例第一轮实测印证了这个量级：
索引 934 只、到期 1010 只。

### 改法

索引改成返回 **代码 → 这段窗口里它最新那条公告的日期**，判据从"在索引里"收敛成：

```
要抓 ⇔ last_ok_at 为空  或  last_ok_at < 公告日的次日零点
```

### ⚠ 为什么是"次日零点"而不是"公告日"

公告只有日期没有时刻（`2026-09-18 00:00:00`）。写成 `notice > lastOk` 的话，
**当天出的公告永远抓不到**：今天抓过之后，明天再比仍是"公告日(今天) == 上次抓取日(今天)"，
不大于，于是被永久跳过——而且一声不吭。

取次日零点，代价是每只票在它出公告那天之后会被多抓一次（第二天再确认一遍），
换来的是绝不因为时刻缺失而漏。这笔交换在这个项目里做过很多次：宁可多抓，不可静默漏。

### 验证

`DividendTaskTests` 加了两条正对着这个陷阱：
`上次抓取晚于那条公告就不再抓`、`当天出的公告_当天抓过之后第二天还会再抓一次`。
Dividend 相关 33 条全绿。

⚠ **这一版还没发布**——正式实例上跑的是 11:04 那个 exe（"命中就抓"）。
下次发布带上即可，两版的差别只在稳态请求量，数据结果一样。

## 22. 限流规律与「不撞墙」的节奏（2026-09-18，发布当天）

### 凌晨那轮留下的证据

2026-09-18 凌晨跑了 4 小时 48 分、抓到 2350/5902，日志里 **18 次熔断**，整齐得出奇：

| | |
|---|---|
| 熔断间隔 | 15.5 ~ 25.2 分钟，绝大多数 **17~18 分钟** |
| 每次暂停 | 12 ~ 17 分钟（退避带抖动） |
| 每个窗口抓到 | **几乎恒定 100 只**（偶有 150；03:48 那次 600 只） |
| 窗口有效时长 | 2 ~ 3 分钟 |
| 窗口内速率 | 35 ~ 68 只/分钟 |
| 熔断序号 | **18 次全是"连续第 1 次"** |

⇒ 新浪的模型是：**大约每 18 分钟放行 100~150 个请求**。我们 2~3 分钟就把额度打光，
然后被拒、熔断、干等 15 分钟，再来一轮。有效吞吐 **5.6 只/分钟**，全市场要 17 小时。

### 两个从数据直接得出的结论

**① 被拒不会延长封禁。** 18 个循环里每次恢复后都能顺利抓满 100 只，
产量没有任何递减——所以"熔断后发探针试探恢复"这类做法在这个接口上风险很低。
（这条回答了"探针会不会把限流拖长"的担心。）

**② 撞墙是纯浪费。** 每次熔断前要连拒 15 个才判定，每个还带 2 次重试＝45 个无效请求，
18 次≈**810 个请求白发**，而它们很可能还在消耗配额。

### 做法：踩在配额线以下匀速走

`SinaDividendProvider` 的限流器从默认的 `(50 个 / 歇 30 秒)` 改成 **`(90 个 / 歇 13 分钟)`**，
即约 6.9 只/分钟——比撞墙-罚站的 5.6 只/分钟还略高，而且零熔断、零无效请求。

两个参数可配（`fetcher-settings.json`，模板里带说明）：

```jsonc
//"DividendBatchSize": 90,      // 一批发多少个请求（默认 90）
//"DividendRestMinutes": 13,    // 一批发完歇多少分钟（默认 13；填 0 就是不歇）
```

⚠ **为什么必须可配**：18 分钟/100 个是从**一天**的数据推出来的，会随时段浮动
（那天 03:48 那一轮放行了 600 只）。跑两天看日志里还撞不撞墙再定。
撞了就把 BatchSize 调小或 RestMinutes 调大；一直不撞可以反过来试探着放宽。

离谱的值（0、负数、非数字、大得没边）一律回退默认——这是人手改的文件，
填错不该让抓取挂掉，但更不能照用（一批 0 个会让任务永远等下去，而且一声不吭）。
`FetcherSettingsTests` 里有 6 条盯着这些。

### 诚实的预期

这个改动**不会让它快很多**：吞吐 5.6 → 6.9 只/分钟（+23%），1010 只仍要两个多小时。
它换来的是"可预期"——不再有 810 个无效请求、不再受熔断惩罚的不确定性、日志读得懂。
真正的提速只有两条路：**降低需求**（公告索引，已做，稳态几十只）
或**换出口**（多机/不同公网 IP，成本另说，且要先确认几台机真的不同出口）。

全量 `dotnet test`：1817 passed / 1 skipped。

