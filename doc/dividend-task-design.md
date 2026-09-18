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

### 还没做的

**实机跑一遍没做**——本机这会儿正在跑【拉取分红送配】（老版本 exe），
按"本机抓数据时不发网络请求/不启程序"的约定，没有起 Debug 实例。
等那一轮结束后要补两件事（§11 的第 3、5 条）：
计划项设成【只补待办】走一遍、点一次【重新拉取失败股票】，
确认日志里是任务自己在补，并回归席位/大宗/龙虎榜的残缺日待办（它们的 `HandlesBacklog` 是 false）。

