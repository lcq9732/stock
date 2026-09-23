using StockPlatform.Data.Orchestration;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 每项跑完之后 <see cref="FetchTaskRegistry"/> 往 manifest 里记了什么（2026-09-23）。
///
/// ════ 为什么值得有用例 ════
/// 【数据状态】页同一行里显示两样东西："上次抓取：时刻（哪一项）"读
/// <c>LastFetchAt</c>/<c>LastFetchKind</c>，下面的"最近任务运行"清单读 <c>LastRunByTask</c>。
/// 这两处**原来是两个地方写的**——老路都在 <c>FetchOrchestrator.FinishFetchRun</c> 里，
/// 新框架只写了后者。于是 2026-09-22 最后一批任务迁完、那个方法删掉之后，上面那句话
/// 停在最后一次老路运行的陈旧值，下面的清单却天天在变，**同一行里两个数对不上**，
/// 而且不报任何错。
///
/// 这类"少写一处状态"的毛病编译器和别的用例都抓不到，只能在这里钉住。
/// </summary>
public class TaskRunRecordingTests : IDisposable
{
    private readonly string _tmp;
    private readonly FetchPaths _paths;
    private readonly IManifestStore _manifest;

    public TaskRunRecordingTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"runrec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
        _paths = new FetchPaths(_tmp);
        _manifest = new JsonManifestStore(_paths.ManifestPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* 临时目录 */ }
    }

    /// <summary>一个什么都不干的任务，结果由构造参数决定。</summary>
    private sealed class StubTask(FetchActionId id, TaskRunResult result) : IFetchTask
    {
        public FetchActionId Id => id;
        public event Action<TaskProgress>? OnProgress;
        public event Action<TaskLiveness>? OnLiveness;
        public event Action<TaskStateChanged>? OnStateChanged;

        public Task<TaskRunResult> RunAsync(TaskRunArgs args, CancellationToken ct)
        {
            // 订阅者一个都不发也不该让编译器抱怨这三个事件没用上
            OnProgress?.Invoke(new TaskProgress("stub"));
            OnLiveness?.Invoke(new TaskLiveness("stub", TimeSpan.Zero));
            OnStateChanged?.Invoke(new TaskStateChanged(id, result.State));
            return Task.FromResult(result);
        }
    }

    private async Task RunAsync(FetchActionId id, TaskRunResult? result = null)
    {
        var registry = new FetchTaskRegistry(_manifest);
        registry.Register(id, () => new StubTask(id, result ?? TaskRunResult.Ok()));
        await registry.RunAsync(id, new TaskRunArgs(), null, CancellationToken.None);
    }

    [Fact]
    public async Task 跑完一项_两处状态同时更新且指的是同一项()
    {
        var before = DateTime.Now.AddSeconds(-1);
        await RunAsync(FetchActionId.StepLhb);

        var m = _manifest.Load();
        var name = FetchTaskCatalog.Info(FetchActionId.StepLhb).Name;

        // "上次抓取：时刻（哪一项）"
        Assert.NotNull(m.LastFetchAt);
        Assert.True(m.LastFetchAt >= before);
        Assert.Equal(name, m.LastFetchKind);

        // "最近任务运行"清单——必须是**同一个键**，否则界面会把同一项显示成两行
        Assert.True(m.LastRunByTask.ContainsKey(name));
        Assert.Equal(m.LastFetchAt, m.LastRunByTask[name].At);
    }

    [Fact]
    public async Task 空转的轮次也记()
    {
        // 语义跟老路一致："刚检查过、没有新数据"本身就是一条值得显示的最后检查时间。
        await RunAsync(FetchActionId.StepLhb, TaskRunResult.Ok(nothingToDo: true));

        var m = _manifest.Load();
        Assert.NotNull(m.LastFetchAt);
        Assert.Equal(FetchTaskCatalog.Info(FetchActionId.StepLhb).Name, m.LastFetchKind);
    }

    [Fact]
    public async Task 失败的轮次也记_错误条数进的是逐项那份清单()
    {
        await RunAsync(FetchActionId.StepLhb,
                       new TaskRunResult(TaskState.Failed, ["炸了", "又炸了"]));

        var m = _manifest.Load();
        var name = FetchTaskCatalog.Info(FetchActionId.StepLhb).Name;
        // "上次抓取"只答"什么时候、哪一项"，成败要看逐项那份清单
        Assert.NotNull(m.LastFetchAt);
        Assert.Equal(2, m.LastRunByTask[name].ErrorCount);
    }

    [Fact]
    public async Task 连着跑两项_记的是后收尾的那一项()
    {
        await RunAsync(FetchActionId.StepLhb);
        await RunAsync(FetchActionId.StepMargin);

        var m = _manifest.Load();
        Assert.Equal(FetchTaskCatalog.Info(FetchActionId.StepMargin).Name, m.LastFetchKind);
        // 先跑的那项没被冲掉——逐项清单是累积的
        Assert.True(m.LastRunByTask.ContainsKey(FetchTaskCatalog.Info(FetchActionId.StepLhb).Name));
        Assert.Equal(2, m.LastRunByTask.Count);
    }

    [Fact]
    public async Task 没给manifestStore_照样跑得完不抛异常()
    {
        // 测试里常这么用（registry 的构造参数可空）。少记一条状态不该把任务判成失败。
        var registry = new FetchTaskRegistry();
        registry.Register(FetchActionId.StepLhb,
                          () => new StubTask(FetchActionId.StepLhb, TaskRunResult.Ok()));

        var r = await registry.RunAsync(FetchActionId.StepLhb, new TaskRunArgs(), null,
                                        CancellationToken.None);

        Assert.False(r.Failed);
    }
}
