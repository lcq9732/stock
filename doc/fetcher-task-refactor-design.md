# 抓取程序重构 — 任务/数据源/存储三层设计

状态：**老任务仍不迁；新任务从 2026-09-08 起按本文的形状写**。

原状态是"已归档、暂不实施"（2026-09-05）：方向认可，但改动面过大——涉及 6,211 行
orchestrator、44 个 dispatch case、40 个 provider，收益不足以抵消回归风险，代码一行未动。

2026-09-08 用户定的折中：**重构现有的不做，但以后新加的任务都写成独立类**。否则等哪天真要
重构，新写的任务反而成了第三种形状，迁移更贵。于是落了本文的**最小子集**：

| 落了 | 没落（仍是设想） |
|---|---|
| `IFetchTask` / `FetchTaskBase<T>` / `TaskRunArgs` / `TaskRunResult`（`StockPlatform.Scheduling/Tasks/`） | `LegacyTaskAdapter`、删 44 个 case（第 9 节阶段一） |
| `IFetchTaskRegistry` + `FetchTaskRegistry`（含到 `FetchResult` 的桥接） | `DataSourceBase` 数据源收口（阶段二） |
| `DispatchPlanActionAsync` 里一条"registry 里有就走新路"的总分支 | `IStagingStore` / `IPersistStore` 存储层 |
| 新任务实现放 `StockPlatform.Tasks`（第一个：【交易日历】） | 声明式参数 UI |

**加一个新任务现在＝写一个类 + 在 App.xaml.cs 注册一行。**

⚠ 第 3 节的接口相对原稿改过三处（准入移出任务、没有 StopAsync、进度改事件广播），
见 3.1 的「2026-09-08 修订」。

文档保留下来的价值：① 第 1 节那五条痛点是实测数据，以后真要重构时不用重新调研；
② 第 9 节的迁移顺序和第 5 节 staging 的判据是踩过坑总结的，别人重做一遍容易踩回去；
③ 其中"执行任务列表 + 停止时逐个收尾"那部分**已经单独实现了**（见 `SourceOccupancy`，
2026-09-04 上线），不在本文档的归档范围内。

要重启这件事，从第 10 节那五个开放问题开始。

---

## 1. 为什么要重构

现状规模（2026-09-04 实测）：

| | 行数 / 数量 |
|---|---|
| `FetchOrchestrator.cs` + `.Steps.cs` | **6,211 行** |
| `MainViewModel.DispatchPlanActionAsync` 的 switch | **44 个 case** |
| `Remote/` 下的 provider 类 | **40 个** |
| orchestrator 的 `Run*Async` 公开入口 | **51 个** |

由此带来的具体麻烦：

1. **加一个任务要改五处**：`FetchActionId` 枚举、`FetchTaskCatalog` 目录、`FetchOrchestrator` 加一个 `Run*Async`、`DispatchPlanActionAsync` 加一个 case、UI 参数格。漏一处就是运行时才发现。
2. **任务逻辑和编排逻辑缠在一起**：`FetchOrchestrator` 既管"怎么抓某一类数据"，又管限流、水位线、失败名单、进度上报。6000 行里没有清晰的模块边界，改一个任务要在整个文件里跳。
3. **"能不能执行"的判断散落各处**：熔断检查在任务里、依赖检查在 `PlanRunner`、数据源占用在 `MainViewModel`、参数校验在 `Dispatch`。同一个问题四个地方回答。
4. **数据源的限流和调度各自为政**：40 个 provider 各自 `new RateLimiter(...)`，同一个域名的两个 provider 互不知情。东财 push2 被限流时，另一个用 push2 的 provider 照样往上撞。
5. **临时保存没有统一约定**：板块列表有 staging（抓一半失败不能删旧数据），但那是单独实现的；别的"整体替换"语义的数据只能整轮重来。

目标不是"把代码变漂亮"，而是让**加一个任务只需写一个类**，且限流/依赖/占用/停止这些横切关注点由基类统一处理。

---

## 2. 总体架构

```
┌──────────────────────────────────────────────────────────┐
│  编排层（PlanRunner / 手动执行）                             │
│  只认 IFetchTask：拿到实例 → Start()，不知道它内部怎么抓        │
└────────────────────────┬─────────────────────────────────┘
                         │
┌────────────────────────▼─────────────────────────────────┐
│  任务层  FetchTaskBase<TItem>                              │
│  · 生命周期骨架（模板方法）                                   │
│  · CanRun 检查 / 参数 / 依赖 / Id / 声明用哪些数据源            │
│  · 每个具体任务一个类，只写"抓什么、怎么存"                      │
└──────┬──────────────────────────────────┬────────────────┘
       │                                  │
┌──────▼──────────────────┐   ┌───────────▼────────────────┐
│  数据源层 DataSourceBase │   │  存储层                      │
│  · Id / 限流 / 调度间隔   │   │  IStagingStore（临时）        │
│  · 熔断状态 / 可用性      │   │  IPersistStore（持久）        │
│  · 能力接口声明           │   │                            │
└─────────────────────────┘   └────────────────────────────┘
```

三层的职责边界，一句话各自概括：

- **任务**回答"要抓哪些数据、抓到了怎么存、什么条件下不该跑"
- **数据源**回答"怎么发请求、多久能发一次、现在通不通"
- **存储**回答"先攒着还是直接落库、什么时候算这一批完成"

---

## 3. 任务基类

### 3.1 非泛型抽象（编排层看到的）

编排层要能把 40 种任务放进一个列表，所以顶层必须是非泛型的：

**2026-09-08 修订**——原稿是下面这样（保留供对照）：任务自己实现 `CheckCanRunAsync`、
自己声明 Sources/Dependencies/Parameters、自己有 `StopAsync`，进度靠 `TaskRunContext` 传进去。

```csharp
// 原稿（2026-09-05），已不是现在的形状
public interface IFetchTask
{
    FetchTaskId Id { get; }
    FetchTaskInfo Info { get; }
    IReadOnlySet<DataSourceId> Sources { get; }
    TaskDependencies Dependencies { get; }
    TaskParameters Parameters { get; }
    Task<TaskRunResult> StartAsync(TaskRunContext ctx, CancellationToken ct);
    Task<bool> StopAsync();
}
```

**现在的形状**（`StockPlatform.Scheduling/Tasks/FetchTaskContracts.cs`）：

```csharp
public interface IFetchTask
{
    /// <summary>直接复用现有的 FetchActionId——另造一套 id 就得维护两套映射。</summary>
    FetchActionId Id { get; }

    /// <summary>真进展：抓完一批、写了多少行。QuietWatchdog 吃的就是这个。</summary>
    event Action<TaskProgress>? OnProgress;

    /// <summary>只证明进程还活着的定时播报。⚠ 不能喂给看门狗，所以单独一路事件。</summary>
    event Action<TaskLiveness>? OnLiveness;

    /// <summary>状态变化：Running / Completed / Failed / Stopped。</summary>
    event Action<TaskStateChanged>? OnStateChanged;

    /// <summary>干活。"能不能跑"已经由调度侧判完了，这里直接开工。</summary>
    Task<TaskRunResult> RunAsync(TaskRunArgs args, CancellationToken ct);
}
```

三处改动的理由：

**① 准入判断移到调度侧。** 原稿写作时（2026-09-05 之前）还没有准入设施；现实早就跑在前面了——
`SourceOccupancy`（2026-09-04）管"谁占着哪个源"、`SourceAdmission`（2026-09-05）管让路与抢占，
占用的 `Release` 也在调度侧的 finally 里。协调是调度的职责，任务只做自己的活。

职责边界：

| 判断 | 归谁 |
|---|---|
| 数据源被谁占着、能不能并发 | 调度（`SourceOccupancy`） |
| 让路 / 抢占 / 抢占超时 | 调度（`SourceAdmission`） |
| 前置任务、时间窗口、参数 | 调度（`PlanRunner` + `FetchTaskCatalog`） |
| 抓什么、怎么存、停了怎么收尾 | 任务 |

任务连 Sources/Dependencies 都不用自己背——`FetchTaskCatalog` 里已经有 `Sources` 和
`SoftDependsOn`，调度器读目录就够了，所以接口里那三个元数据属性一并去掉。

⚠ 界线：**只有任务自己知道的前提仍归任务**——本地还没有K线所以定不了补齐起点、没配置那个源、
日历已经是最新的、这一天该不该发请求。这些不是"准入"，是任务开工后的第一步结论，返回
`NothingToDo` 或 `Failed`。调度侧无从判断也不该判断。

**② 没有 `StopAsync`。** 停止＝取消 token，任务在自己的 finally 里收尾后正常返回 `Stopped`。
理由见 `SourceOccupancy` 的类注释：40 个手写的 Stop 方法漏一个就永远等不到那个 `true`。

**③ 进度从"传进去"改成"发出来"。** 任务只管广播，**调度类、UI、正在执行任务表、静默看门狗
各自订阅、各取所需**。顺带把裸字符串升级成 `TaskProgress`（带 Done/Total/Phase），UI 画进度条
不用再从文本里抠数字。多订阅者的四个约定（写在 `FetchTaskBase` 里）：

- 事件在工作线程上发射，UI 订阅者自己 marshal；
- **逐个订阅者隔离**：一个订阅者抛异常会中断多播链，后面的收不到、异常还会冒进任务里；
- registry 每次运行 `new` 一个任务实例、跑完丢弃，省掉配对 `+=/-=` 的泄漏；
- 高频任务的进度要节流；占用表只覆盖"最后一条 + 时间戳"，不累积。

**关于"Start 传入线程"** —— 建议改成传 `TaskRunContext` + `CancellationToken`，任务内部走 `async`，由线程池调度：

- 抓取是 **IO 密集**，`async/await` 期间不占线程；裸 `Thread` 每个吃 ~1MB 栈，几十个任务并发就是几十 MB 白费
- 现有 40 个 provider 全是 `async`，包一层裸线程等于把异步退化成同步
- 停止靠 `CancellationToken` 是协作式的、能收尾；`Thread.Abort` 在 .NET Core 直接不支持
- "每个任务独立线程"的**真实意图**是"互不阻塞、能单独停"，这个 `Task` + 独立 CTS 完全满足

`TaskRunContext` 装的是运行期依赖，不进构造函数（这样任务实例可以缓存复用）：

```csharp
public sealed record TaskRunContext(
    IProgress<string> Progress,
    DateTime? Deadline,          // 空闲窗口要在这个点前收尾
    int? MaxItems,               // 分批跑：本轮最多做多少
    bool Manual);                // 手动触发（影响"要不要弹框问"）
```

### 3.2 泛型骨架（任务作者看到的）

```csharp
public abstract class FetchTaskBase<TItem> : IFetchTask
{
    // ── 子类必须实现的三件事 ──

    /// <summary>能不能跑。返回 null 表示可以，返回字符串就是拒绝原因（会显示给用户）。</summary>
    protected abstract Task<string?> CheckCanRunAsync(TaskRunContext ctx, CancellationToken ct);

    /// <summary>
    /// 从数据源抓。**流式产出**，不是一次返回全部——见 3.4 的理由。
    /// 每 yield 一批，骨架就调一次 SaveBatchAsync。
    /// </summary>
    protected abstract IAsyncEnumerable<IReadOnlyList<TItem>> FetchFromNetAsync(
        TaskRunContext ctx, CancellationToken ct);

    /// <summary>存一批。要临时攒的就写 staging，能直接落的就写持久层。</summary>
    protected abstract Task SaveBatchAsync(IReadOnlyList<TItem> batch, CancellationToken ct);

    // ── 可选覆盖 ──

    /// <summary>全部抓完之后。staging 的提交、水位线更新、副产物计算都在这。</summary>
    protected virtual Task OnCompletedAsync(TaskRunStats stats, CancellationToken ct) => Task.CompletedTask;

    /// <summary>被停止时。默认行为：把已攒的 staging 保留（下次接着来），不提交、不丢弃。</summary>
    protected virtual Task OnStoppedAsync(TaskRunStats stats) => Task.CompletedTask;
}
```

### 3.3 生命周期（骨架里写死的顺序）

**2026-09-08 修订**：准入和占用登记那两步移出了任务（见 3.1 ①），所以骨架里只剩流式的
抓—存循环和两条收尾路径。

```
调度侧（已实现，不在任务里）
  ├─ SourceAdmission：让路 / 抢占 / 拿 lease
  └─ SourceOccupancy：登记占用；finally 里 Release
        │
        ▼
RunAsync(args, ct)
  │
  ├─ 1. 发 Running 状态事件
  │
  ├─ 2. foreach batch in FetchAsync(args, ct)      ← 子类实现，流式
  │        └─ SaveBatchAsync(batch)                 ← 抓一批存一批
  │           · MaxItems / Deadline 到了就收尾（骨架统一处理）
  │           · ct 被取消 → 跳到 4
  │
  ├─ 3. OnCompletedAsync                            ← 水位线、对账、副产物
  │      → Completed（条数为 0 时 NothingToDo）
  │
  └─ 4. OnStoppedAsync → Stopped，取消照旧往上抛
         异常 → Failed
```

三种结局（原稿的 `Refused` 去掉了——拒绝发生在调度侧，任务压根没被调用，
那边用 `AdmissionKind.GaveWay` / `PreemptTimedOut` 表达）：

| 结局 | 含义 | 计划引擎的处理 |
|---|---|---|
| `Completed` | 这一轮做完了 | 记 Ok；`NothingToDo` 另标 |
| `Failed` | 开工了但出错 | 记失败，当天不再自动重试 |
| `Stopped` | 被用户停止 | 记 Cancelled |

"没开工"和"开工了但失败"仍然必须分开——这是踩过的坑：数据源熔断时一行都没抓，界面上却是
绿勾"完成"。区别只是这个判断现在归调度侧，由 `FetchResult.SkippedReason` 承载。

### 3.4 为什么 FetchFromNet 必须是流式

原设计是 `FetchFromNet()` 拿到数据、再 `SaveData(数据)`。对这个项目的实际数据量不可行：

- 【个股日K】5,500 只 × 2,400 根 = **1,300 万行**，一次性堆内存是几个 GB
- 【分档资金流】跑 3 小时，中途被停止的话，先抓后存意味着**三小时全白费**
- 现在的实现全都是"抓一只存一只"，正是靠这个才能"停在哪都不丢、下次从没抓的接着来"

流式的另一个好处：`MaxItems`（分批跑）和 `Deadline`（空闲窗口收尾）在骨架里统一处理，子类不用各写一遍。

---

## 4. 数据源层

### 4.1 基类：只管身份、限流、可用性

```csharp
public abstract class DataSourceBase
{
    public abstract DataSourceId Id { get; }

    /// <summary>可读名，界面和日志用。</summary>
    public abstract string Name { get; }

    /// <summary>两次请求之间至少隔多久。子类按各自的反爬强度定。</summary>
    public abstract TimeSpan MinInterval { get; }

    /// <summary>最大并发。新浪财报接口是 1，腾讯K线可以 3。</summary>
    public virtual int MaxConcurrency => 1;

    /// <summary>限流器——**每个源一个，全程序共享**，这是重构要解决的第 4 个痛点。</summary>
    public RateLimiter Limiter { get; }

    /// <summary>熔断到什么时候。非空表示现在别发请求。</summary>
    public DateTime? PausedUntil { get; protected set; }

    /// <summary>探活。给"启动时报告哪些源不通"用。</summary>
    public virtual Task<bool> ProbeAsync(CancellationToken ct) => Task.FromResult(true);
}
```

**关键收益**：限流器从"每个 provider 自己 new"变成"每个源一个、全局共享"。这样【板块列表】和【板块成分股】都走 `em.push2`，共用同一个限流器和同一份熔断状态——一个被限流，另一个立刻知道，不会接着往上撞。

### 4.2 "支持哪些数据类型"：建议用能力接口，不用枚举 + object

原设计是 `Fetch(枚举类型)`。问题在于返回值：K线返回 `List<Bar>`、分红返回 `List<DividendRow>`、板块成分返回 `List<string>`——一个方法签名装不下，只能返回 `object` 再强转，编译器帮不上忙。

建议改成**能力接口**：

```csharp
public interface IBarCapability      { Task<List<Bar>> GetBarsAsync(string code, ...); }
public interface IDividendCapability { Task<DividendAndRights> GetAsync(string code, ...); }
public interface IBoardCapability    { Task<List<Board>> GetBoardsAsync(...); }
// ...
```

```csharp
// 腾讯：只会抓K线
public sealed class TencentSource : DataSourceBase, IBarCapability { }

// 新浪：K线、分红、股东、财务都会
public sealed class SinaSource : DataSourceBase, IBarCapability, IDividendCapability, ... { }
```

任务这样取源，类型安全、且天然支持回退：

```csharp
var source = registry.Resolve<IBarCapability>(preferred: DataSourceId.Tencent,
                                              fallback: DataSourceId.Sina);
```

"这个源支持哪些数据类型"仍然可以给 UI 看——由注册表反射它实现了哪些能力接口自动列出，不需要手工维护一份枚举（手工维护的那份一定会跟代码不同步）。

如果坚持要枚举，建议**枚举只用于展示和运行时查询**，实际调用仍走能力接口，两者由注册表关联。

---

## 5. 存储层：临时 + 持久

```csharp
/// <summary>临时区：先攒着，最后一次性提交。</summary>
public interface IStagingStore<T>
{
    Task StageAsync(IReadOnlyList<T> items, CancellationToken ct);
    /// <summary>提交：把临时区搬进正式表。返回 (写入数, 清理掉的旧数据数)。</summary>
    Task<(int Committed, int Pruned)> CommitAsync(CancellationToken ct);
    Task ClearAsync(CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
}

/// <summary>持久区：直接落库。</summary>
public interface IPersistStore<T>
{
    Task<int> SaveAsync(IReadOnlyList<T> items, CancellationToken ct);
}
```

### 什么时候该用 staging

判据只有一条：**这批数据是"整体替换"语义吗？**

| 数据 | 语义 | 用 staging？ |
|---|---|---|
| 板块列表 | 这轮没返回的板块要删掉 | **要**。抓一半失败就删，会把好数据清空（已实现过，`BoardStaging` 表） |
| 指数成分名单 | 同上 | 要 |
| 个股日K | 逐只累加，互不影响 | 不要。抓一只存一只，停在哪都不丢 |
| 板块成分股 | 每个板块独立替换 | 不要。按板块为单位落库即可 |

写进文档是因为这个判断容易做错：给逐只累加的数据套 staging，会白白丢掉"停在哪都不丢"这个好性质。

---

## 6. 参数系统（UI 统一处理）

要让 UI 自动生成输入格，参数需要带**元数据**，不能只是一个 `Dictionary<string, string>`：

```csharp
public sealed class TaskParameters
{
    private readonly List<TaskParameter> _items = [];
    public IReadOnlyList<TaskParameter> Items => _items;

    /// <summary>子类在构造里声明自己的参数。</summary>
    protected internal TaskParameter Declare(TaskParameter p) { _items.Add(p); return p; }

    public string? GetString(string key);
    public int? GetInt(string key);
    public DateOnly? GetDate(string key);
}

public sealed record TaskParameter(
    string Key,
    string Label,              // UI 上的标签
    TaskParamKind Kind,        // Text / Int / Date / YearRange / Enum / Bool
    string? Default = null,
    string? Tooltip = null,    // 现在那些长说明
    string[]? Choices = null,  // Kind=Enum 时的选项
    Func<string, string?>? Validate = null);   // 返回错误信息，null 表示通过
```

具体任务这样声明：

```csharp
public sealed class FetchDayTask : FetchTaskBase<DailyBundle>
{
    private readonly TaskParameter _date;

    public FetchDayTask()
    {
        _date = Parameters.Declare(new("date", "日期", TaskParamKind.Date,
            Tooltip: "要补哪一天的数据",
            Validate: s => DateOnly.TryParseExact(s, "yyyy-MM-dd", out _) ? null : "要 yyyy-MM-dd 格式"));
    }
}
```

**收益**：UI 那 44 个 case 的参数格（现在是在 XAML 里按 `FetchActionParams` 标志位手工拼的）变成一个 `ItemsControl` + `DataTemplateSelector`。参数校验从 `DispatchPlanActionAsync` 里的散落 `if` 变成声明式的 `Validate`，而且**在点执行之前**就能报错，不用等任务跑起来才抛异常。

---

## 7. 依赖与身份

```csharp
public sealed record TaskDependencies(
    /// <summary>硬依赖：它这一轮失败了，本项跑了也白跑 → 自动跳过。</summary>
    FetchTaskId? Hard = null,
    /// <summary>软依赖：没它也能跑，只是结果会旧一点 → 日志提醒，不跳过。</summary>
    IReadOnlyList<FetchTaskId>? Soft = null);
```

依赖检查的**触发时机**按已定的规则分开（2026-09-04 确认）：

- **自动执行**：硬依赖失败 → 跳过并记原因；软依赖旧 → 日志提醒。不弹任何框（夜里没人点，会把整份计划卡死）
- **手动执行**：依赖今天没跑成功 → **弹框问是否照样跑**。"今天有没有跑成功"用 `AlreadyRanOn`，跟"今天还要不要再跑"同一个判据

`FetchTaskId` 迁移期直接复用现有的 `FetchActionId` 枚举值，避免两套 Id 互转。等 44 个任务全迁完再考虑换成字符串 Id（那样插件式加任务不用改枚举）。

---

## 8. 运行时：注册表 + 执行列表 + 停止

```csharp
// 现在的形状（Scheduling/Tasks/FetchTaskRegistry.cs）：存**工厂**不是实例——
// 每次运行现 new 一个、跑完丢弃，事件订阅就不会累积。
public interface IFetchTaskRegistry
{
    bool Has(FetchActionId id);
    IFetchTask? Create(FetchActionId id);
    IReadOnlyCollection<FetchActionId> Registered { get; }
}
```

`FetchTaskRegistry.RunAsync` 顺带做两件桥接，于是新任务和老世界能并存：

- `OnProgress` / `OnLiveness` → 老的 `IProgress<string>`，日志窗、计划引擎、静默看门狗零改动；
- `TaskRunResult` → `FetchResult`（`Errors` / `NothingToDo` / `Progress`）。

它还留了个 `subscribe` 口子给别的订阅者（占用表挂实时进度、UI 挂进度条）——事件是多播的，
挂多少个都互不影响。

**执行列表直接复用已实现的 `SourceOccupancy`**（2026-09-04 已上线）——它已经在做用户要的第 5 点：

- 表项带 `{任务名, 源[], 开始时间, 手动?, CTS}`
- 停止 = 遍历表逐个 Cancel
- 任务在 `finally` 里释放登记，所以**表空了就等于全部收尾完毕**
- 3 分钟超时兜底，等不到就如实报告哪一项没退出

这一块不需要重新设计，新架构里把 `RunningTask` 的 `Name` 换成 `IFetchTask` 引用就行，顺带能拿到 `StopAsync()`。

---

## 9. 迁移路径（最关键的一节）

6,211 行 orchestrator + 44 个 case 不可能一次改完。分四阶段，**每个阶段结束都是可发布的状态**：

### 阶段一：搭骨架 + 全量接上（不动任何任务逻辑）

1. 定义 `IFetchTask` / `FetchTaskBase<T>` / `DataSourceBase` / 存储接口
2. 写一个 **`LegacyTaskAdapter`**：把现有 `Run*Async` 包成 `IFetchTask`

```csharp
public sealed class LegacyTaskAdapter(
    FetchTaskId id, FetchTaskInfo info,
    Func<TaskRunContext, CancellationToken, Task<FetchResult>> run) : IFetchTask
{
    // CheckCanRun：沿用现有的熔断/依赖检查
    // StartAsync：直接调 run，把 FetchResult 翻译成 TaskRunResult
}
```

3. 注册表里 44 项**全部**用 adapter 注册（一行一个）
4. `PlanRunner` 和手动执行改成只认 `IFetchTask`，删掉 `DispatchPlanActionAsync` 那个 switch

阶段一结束：架构就位，行为完全不变，`MainViewModel` 少掉几百行 switch。**风险极低**，因为任务逻辑一行没动。

### 阶段二：数据源收口

1. 建 8 个 `DataSourceBase` 子类（对应已有的 `DataSourceId`）
2. 限流器改成从数据源取，40 个 provider 不再各自 `new RateLimiter`
3. 熔断状态挂到数据源上

阶段二结束：同源的 provider 共享限流和熔断。**这一步单独就有价值**——东财 push2 被限流时，两个板块任务不会再接着往上撞。

### 阶段三：逐个迁移任务（每次一个，可停可验）

按"收益 ÷ 风险"排序，建议顺序：

| 批次 | 任务 | 为什么先做 |
|---|---|---|
| 1 | 板块列表、板块成分股 | 正在痛（几天追不上时效性），且有 staging 需求，能验证存储层设计 |
| 2 | 分档资金流 | 分批 + 轮换排队，能验证 `MaxItems`/流式 |
| 3 | 分红送配、股东、财务报表 | 同源同结构，一次迁三个 |
| 4 | K线四种（前/后/不复权/指数） | 量最大、逻辑最多，放最后 |
| 5 | 其余零散项 | — |

每迁一个：删掉对应的 `Run*Async`，orchestrator 相应瘦身。迁到一半随时可以停——adapter 和真任务在注册表里可以并存。

### 阶段四：清理

`FetchOrchestrator` 剩下的应该只有跨任务的共享工具（水位线计算、失败名单、体检）。届时再决定它是拆成几个服务还是保留。

---

## 10. 开放问题（2026-09-08 定了四个）

| # | 问题 | 结论 |
|---|---|---|
| 1 | 线程模型 | **Task + CancellationToken**，不用裸 Thread |
| 2 | 数据源能力接口 | 未定，留给阶段二（数据源收口时再说） |
| 3 | 流式抓取 | **采用** `IAsyncEnumerable`，边抓边存 |
| 4 | 迁移节奏 | **老任务不迁，只新任务用新形状** |
| 5 | `FetchResult` 去向 | 新任务返回 `TaskRunResult`，registry 翻译成 `FetchResult`；老代码零感知 |

原文（保留）：


1. **线程模型**：接受 `Task` + `CancellationToken` 替代裸 `Thread` 吗？（第 3.1 节给了三条理由）
2. **数据源能力**：用能力接口（类型安全、UI 靠反射自动列），还是坚持 `Fetch(枚举)` 返回 `object`？
3. **流式抓取**：接受 `IAsyncEnumerable` 边抓边存吗？（原设计的"先抓完再存"对 1,300 万行的K线不可行）
4. **迁移节奏**：阶段一（adapter 全量接上、零风险）先做完发布，再逐个迁？还是直接挑两三个任务按新架构重写、跑通了再铺开？
5. **`FetchResult` 的去向**：现在它承载 `Errors`/`NothingToDo`/`SkippedReason`/`Progress`。新架构用 `TaskRunResult` 的四种结局替代，`Progress` 那类"存量进度"要不要保留在结果里？

---

## 11. 风险

| 风险 | 应对 |
|---|---|
| 重构期间新旧两套并存，行为不一致 | adapter 保证行为完全不变；每阶段可发布、可回退 |
| 40 个 provider 的限流器收口时，某个源的实际间隔被改错 → 触发反爬封禁 | 阶段二先只收口 `em.push2`（当前最痛的），跑一周确认再铺开 |
| 任务迁移时漏掉隐藏逻辑（水位线、失败名单、副产物） | 每个任务迁移前先把它的 `Run*Async` 完整读一遍，把隐藏副作用列进迁移清单；已有 412 个单元测试兜底 |
| 参数系统改造牵动 XAML | 阶段一先不动 UI，参数仍走老路；等任务迁完再换成声明式 |
