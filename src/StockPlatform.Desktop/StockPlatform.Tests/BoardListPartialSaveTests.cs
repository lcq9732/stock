using Microsoft.Data.Sqlite;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 板块列表的**页级断点续传 + 暂存区提交**（2026-09-04）。
///
/// 这批测试盯的是两个真实付出过代价的毛病：
///
/// ① **部分成果被整批丢掉**：原来"概念+行业都抓完才写一次库"，概念 504 个明明完整拿到了，
///    行业一被限流抛异常，写库那行根本执行不到，504 个跟着丢。试多少次丢多少次。
///
/// ② **每轮从第 1 页重来，永远到不了第 6 页**：push2 限流下一轮抓到第 5 页就被拒，
///    而整类作废意味着下轮又从第 1 页开始——白烧 5 页配额，再在同一个地方被拒。
///
/// 现在的做法：抓一页存一页（进暂存区，正表一动不动）+ 记断点，凑齐了才整体搬进正表。
/// 下面每个测试对应一种"抓一半"的现实场景。
/// </summary>
public class BoardListPartialSaveTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"bl_{Guid.NewGuid():N}.sqlite");

    public BoardListPartialSaveTests(ITestOutputHelper o) => _out = o;

    private SqliteBoardRepository NewRepo()
    {
        var r = new SqliteBoardRepository(_db);
        r.EnsureSchema();
        return r;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch { }
    }

    /// <summary>造第 page 页的板块（每页 100 个，最后一页 4 个），模拟东财 504 个概念板块。</summary>
    private static (List<Board> Items, int Total, bool IsLast) Page(BoardType type, int page, int total = 504)
    {
        int size = 100;
        int from = (page - 1) * size;
        int n = Math.Max(0, Math.Min(size, total - from));
        var prefix = type == BoardType.Concept ? "BK1" : "BK9";
        var items = Enumerable.Range(from + 1, n).Select(i => new Board
        {
            BoardCode = prefix + i.ToString("0000"),
            Name = (type == BoardType.Concept ? "概念" : "行业") + i,
            Type = type,
            AsOf = DateTime.Now,
        }).ToList();
        return (items, total, n < size);
    }

    /// <summary>一个可编程的假数据源：指定哪些 (类型,页) 会失败。</summary>
    private static Func<BoardType, int, CancellationToken, Task<(List<Board>, int, bool)>> Source(
        Func<BoardType, int, bool> fails, int conceptTotal = 504, int industryTotal = 86,
        List<string>? calls = null)
        => (t, page, _) =>
        {
            calls?.Add($"{t}:{page}");
            if (fails(t, page)) throw new InvalidOperationException("被限流");
            return Task.FromResult(Page(t, page, t == BoardType.Concept ? conceptTotal : industryTotal));
        };

    [Fact]
    public async Task 抓到第五页被拒_正表不动_已抓的存在暂存区()
    {
        var repo = NewRepo();
        // 先放一份"上次的完整快照"，用来验证它不会被半截数据破坏
        repo.UpsertBoards([new Board { BoardCode = "OLD001", Name = "旧概念", Type = BoardType.Concept, AsOf = DateTime.Now.AddDays(-1) }]);

        var (committed, unfinished) = await BoardListFetchLoop.RunAsync(
            Source((t, p) => t == BoardType.Concept && p >= 6),
            repo, m => _out.WriteLine(m), _ => { });

        Assert.Equal(500, repo.CountStaged(BoardType.Concept));      // 前 5 页在暂存区
        Assert.Contains("概念/题材", unfinished);
        // 正表里概念这一类还是上次那一个，没被半截数据动过
        Assert.Equal(["OLD001"], repo.QueryBoards(BoardType.Concept).Select(b => b.BoardCode));

        var st = repo.GetListState(BoardType.Concept);
        Assert.NotNull(st);
        Assert.Equal(6, st!.Value.NextPage);                          // 下次从第 6 页接着抓
    }

    [Fact]
    public async Task 下一轮从断点接着抓_不重抓已经拿到的页()
    {
        var repo = NewRepo();
        // 第一轮：第 6 页挂了
        await BoardListFetchLoop.RunAsync(
            Source((t, p) => t == BoardType.Concept && p >= 6),
            repo, _ => { }, _ => { });

        // 第二轮：这次谁都不挂，记录它到底请求了哪些页
        var calls = new List<string>();
        var (committed, unfinished) = await BoardListFetchLoop.RunAsync(
            Source((_, _) => false, calls: calls),
            repo, m => _out.WriteLine(m), _ => { });

        _out.WriteLine("第二轮请求的页：" + string.Join(", ", calls));
        // ★ 核心：概念这一类**直接从第 6 页开始**，前 5 页一个请求都不再发
        Assert.DoesNotContain("Concept:1", calls);
        Assert.DoesNotContain("Concept:5", calls);
        Assert.Contains("Concept:6", calls);

        Assert.Equal(504 + 86, committed);
        Assert.Empty(unfinished);
        Assert.Equal(504, repo.QueryBoards(BoardType.Concept).Count);
        Assert.Equal(0, repo.CountStaged(BoardType.Concept));         // 提交后暂存区清空
        Assert.Null(repo.GetListState(BoardType.Concept));            // 断点也清掉
    }

    [Fact]
    public async Task 概念抓不完不影响行业照样抓完提交()
    {
        var repo = NewRepo();
        var (committed, unfinished) = await BoardListFetchLoop.RunAsync(
            Source((t, p) => t == BoardType.Concept && p >= 3),
            repo, m => _out.WriteLine(m), _ => { });

        Assert.Equal(86, committed);                                   // 行业那 86 个提交了
        Assert.Equal(["概念/题材"], unfinished);
        Assert.Equal(86, repo.QueryBoards(BoardType.Industry).Count);
        Assert.Empty(repo.QueryBoards(BoardType.Concept));             // 概念还没凑齐，正表里没有
        Assert.Equal(200, repo.CountStaged(BoardType.Concept));        // 但前 2 页存着
    }

    [Fact]
    public async Task 提交概念不会删掉行业的成分股()
    {
        // 成分股是逐板块抓的、约 2500 个请求、跨好几轮才攒得齐。误删要重抓好几天。
        var repo = NewRepo();
        await BoardListFetchLoop.RunAsync(Source((t, _) => t == BoardType.Concept),
            repo, _ => { }, _ => { });
        repo.ReplaceMembers("BK90001", ["600000", "601398"]);

        await BoardListFetchLoop.RunAsync(Source((_, _) => false), repo, _ => { }, _ => { });

        Assert.Equal(2, repo.QueryMembers("BK90001").Count);
    }

    [Fact]
    public async Task 凑齐提交时把下架的板块连同成分股清掉()
    {
        var repo = NewRepo();
        // 正表里先有一个"这一轮不会再出现"的板块，带着成分股
        repo.UpsertBoards([new Board { BoardCode = "BK1_GONE", Name = "已下架", Type = BoardType.Concept, AsOf = DateTime.Now.AddDays(-1) }]);
        repo.ReplaceMembers("BK1_GONE", ["000001"]);

        await BoardListFetchLoop.RunAsync(Source((_, _) => false), repo, m => _out.WriteLine(m), _ => { });

        Assert.DoesNotContain(repo.QueryBoards(BoardType.Concept), b => b.BoardCode == "BK1_GONE");
        Assert.Empty(repo.QueryMembers("BK1_GONE"));
    }

    [Fact]
    public async Task 第一页就被拒_什么都不动_下次还从第一页来()
    {
        var repo = NewRepo();
        var (committed, unfinished) = await BoardListFetchLoop.RunAsync(
            Source((_, p) => p == 1), repo, m => _out.WriteLine(m), _ => { });

        Assert.Equal(0, committed);
        Assert.Equal(2, unfinished.Count);
        Assert.Equal(0, repo.CountStaged(BoardType.Concept));
        Assert.Equal(1, repo.GetListState(BoardType.Concept)!.Value.NextPage);
    }

    [Fact]
    public async Task 用户中途停止_暂存区和断点都保住()
    {
        var repo = NewRepo();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoardListFetchLoop.RunAsync(
                (t, page, _) =>
                {
                    if (page >= 4) throw new OperationCanceledException();
                    return Task.FromResult(Page(t, page));
                },
                repo, m => _out.WriteLine(m), _ => { }, cts.Token));

        Assert.Equal(300, repo.CountStaged(BoardType.Concept));       // 停之前那 3 页保住了
        Assert.Equal(4, repo.GetListState(BoardType.Concept)!.Value.NextPage);
    }

    [Fact]
    public async Task 隔夜的断点要重开一轮_不能把昨天的半截跟今天的混在一起()
    {
        var repo = NewRepo();
        // 伪造一个 13 小时前的断点（超过 12 小时的过期线）
        repo.SaveListState(BoardType.Concept, DateTime.Now.AddHours(-13), nextPage: 6, total: 504, fetchedCount: 500);
        repo.StageBoards([new Board { BoardCode = "BK1_STALE", Name = "昨天的", Type = BoardType.Concept, AsOf = DateTime.Now.AddHours(-13) }]);

        var calls = new List<string>();
        await BoardListFetchLoop.RunAsync(Source((_, _) => false, calls: calls),
            repo, m => _out.WriteLine(m), _ => { });

        Assert.Contains("Concept:1", calls);                          // 从头开始
        // 昨天那条没混进今天的名单里
        Assert.DoesNotContain(repo.QueryBoards(BoardType.Concept), b => b.BoardCode == "BK1_STALE");
        Assert.Equal(504, repo.QueryBoards(BoardType.Concept).Count);
    }

    [Fact]
    public async Task 反复跑到抓完为止_成功过的页一次都不会重抓()
    {
        // 模拟真实处境：限流严重，每轮每一类只抓得动 3 页。看它能不能靠断点一点点攒齐，
        // 以及——**配额有没有浪费在已经拿到手的页上**（这才是断点续传的全部意义）。
        //
        // 只盯概念这一类：行业只有 1 页，第一轮就凑齐提交了，之后每轮都会重抓一份当天快照——
        // 那是对的（板块列表本来就是每日快照），不能算浪费，混进来会把断言搞糊涂。
        var repo = NewRepo();
        var okPages = new List<string>();      // 成功拿到的页
        var allPages = new List<string>();     // 请求过的页（含失败）
        int rounds = 0;

        for (int round = 1; round <= 8; round++)
        {
            rounds = round;
            var perType = new Dictionary<BoardType, int>();
            var calls = new List<string>();

            var (_, unfinished) = await BoardListFetchLoop.RunAsync(
                (t, page, _) =>
                {
                    var key = $"{t}:{page}";
                    calls.Add(key);
                    if (t == BoardType.Concept) allPages.Add(key);
                    perType[t] = perType.GetValueOrDefault(t) + 1;
                    if (perType[t] > 3) throw new InvalidOperationException("被限流");
                    if (t == BoardType.Concept) okPages.Add(key);
                    return Task.FromResult(Page(t, page, t == BoardType.Concept ? 504 : 86));
                },
                repo, _ => { }, _ => { });

            _out.WriteLine($"第 {round} 轮：{string.Join(", ", calls)}");
            if (!unfinished.Contains("概念/题材")) break;      // 概念凑齐了就收工
        }

        Assert.Equal(504, repo.QueryBoards(BoardType.Concept).Count);

        // ★ 核心断言：**成功拿到过的页，一次都没有被重新请求**。
        //   请求列表里允许出现重复，但重复的只能是上一轮失败的那一页——它本来就没拿到数据，
        //   必须重试。真正要防的是"每轮从第 1 页重来"，那才是白烧配额。
        Assert.Equal(okPages.Count, okPages.Distinct().Count());

        var retried = allPages.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        _out.WriteLine($"概念共 {allPages.Count} 个请求、{rounds} 轮抓完 6 页；"
                     + $"重试过的页：{(retried.Count == 0 ? "无" : string.Join(", ", retried))}");
        // 重试过的页必须是"曾经失败过"的——okPages 无重复已经保证了这一点，
        // 这里只再确认重试的量很小，不是变相的"每轮重来"。
        Assert.True(retried.Count <= 2, $"重试了 {retried.Count} 页，太多了");

        // 6 页的活，最多也就多花几个重试请求——不该出现"抓了十几二十个请求还没完"
        Assert.True(allPages.Count <= 10, $"请求数 {allPages.Count} 偏多，断点可能没生效");
    }
}
