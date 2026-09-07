using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 板块那两项占不占数据源，**跟着通道配置走**（2026-09-06）。
///
/// ════ 防的是什么 ════
/// 目录里【概念和行业板块】写死占 EmQuote、【板块成分股】写死占 EmPush2——那是按"走网络"
/// 写的。可 <c>BoardMemberChannel=terminal</c> 时这两项只读东财终端落在本地的那份文件，
/// 一个请求都不发；照旧记账的话，它们会被别的东财任务挡住、或者在计划里一轮轮让路，
/// 界面报"数据源被占用"——占的是它根本不会去打的源（用户 2026-09-06 反馈）。
///
/// 钉三件事：① terminal 下这两项不占源；② 于是跟占着东财的任务不冲突、能同时跑；
/// ③ 换回网络通道后照旧冲突（别把保护一起删了）。
/// </summary>
[Collection(BoardChannelCollection.Name)]
public class BoardChannelSourceTests : IDisposable
{
    private readonly string _saved = FetchTaskCatalog.BoardChannel;

    /// <summary>⚠ 必须复位：这是**静态**状态，漏了会串给同一批跑的别的测试。</summary>
    public void Dispose() => FetchTaskCatalog.BoardChannel = _saved;

    [Fact]
    public void 网络通道下板块两项照旧占着东财()
    {
        foreach (var channel in new[] { "page", "browser", "http" })
        {
            FetchTaskCatalog.BoardChannel = channel;

            Assert.Contains(DataSourceId.EmPush2,
                FetchTaskCatalog.Info(FetchActionId.StepBoardMembers).EffectiveSources);
            Assert.Contains(DataSourceId.EmQuote,
                FetchTaskCatalog.Info(FetchActionId.StepBoardList).EffectiveSources);
        }
    }

    [Fact]
    public void terminal通道下板块两项不占任何数据源()
    {
        FetchTaskCatalog.BoardChannel = "terminal";

        foreach (var id in new[] { FetchActionId.StepBoardMembers, FetchActionId.StepBoardList })
        {
            var info = FetchTaskCatalog.Info(id);
            Assert.Empty(info.EffectiveSources);
            // 界面和日志也要跟着说对——"数据源：东财行情，占用：本地计算"是自相矛盾的一行
            Assert.Contains("本地", info.SourcesText);
            Assert.Contains("本地文件", info.DataSourceText);
        }
    }

    [Fact]
    public void 通道名大小写和空格不影响判定()
    {
        FetchTaskCatalog.BoardChannel = "TERMINAL";
        Assert.Empty(FetchTaskCatalog.Info(FetchActionId.StepBoardMembers).EffectiveSources);
    }

    [Fact]
    public void terminal下板块成分股能跟占着push2的任务同时跑()
    {
        FetchTaskCatalog.BoardChannel = "terminal";
        var occ = new SourceOccupancy();

        // 别人正占着东财 push2（走网络的那几项就是这样）
        var blocker = occ.TryAcquire("别的东财任务", new HashSet<DataSourceId> { DataSourceId.EmPush2 },
                                     manual: true, new CancellationTokenSource(), out _, out _);
        Assert.NotNull(blocker);

        // 读本地文件的板块成分股照样拿得到——这正是这次改动要的结果
        var mine = occ.TryAcquire("板块成分股",
            FetchTaskCatalog.Info(FetchActionId.StepBoardMembers).EffectiveSources,
            manual: true, new CancellationTokenSource(), out var blockedBy, out var blockedSource);
        Assert.NotNull(mine);
        Assert.Null(blockedBy);
        Assert.Null(blockedSource);
    }

    [Fact]
    public void 换回网络通道后照旧被push2挡住()
    {
        FetchTaskCatalog.BoardChannel = "page";
        var occ = new SourceOccupancy();
        occ.TryAcquire("别的东财任务", new HashSet<DataSourceId> { DataSourceId.EmPush2 },
                       manual: true, new CancellationTokenSource(), out _, out _);

        var mine = occ.TryAcquire("板块成分股",
            FetchTaskCatalog.Info(FetchActionId.StepBoardMembers).EffectiveSources,
            manual: true, new CancellationTokenSource(), out var blockedBy, out var blockedSource);

        Assert.Null(mine);
        Assert.Equal("别的东财任务", blockedBy!.Name);
        Assert.Equal(DataSourceId.EmPush2, blockedSource);
    }
}

/// <summary>
/// <see cref="FetchTaskCatalog.BoardChannel"/> 是**静态**的，改它的测试不能跟读
/// <c>EffectiveSources</c> 的测试并行跑（xunit 默认按类并行）。凡是碰这两样的类都进这个
/// collection，同一时刻只跑一个。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BoardChannelCollection
{
    public const string Name = "board-channel";
}
