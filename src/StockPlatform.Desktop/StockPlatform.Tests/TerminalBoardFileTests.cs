using System.Text;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 东财终端本地板块文件这条通道（2026-09-06 新增）。
///
/// 盯两类事：
///   ① **格式的三个坑**——GBK 板块名、行尾在两个版本之间从 CRLF 变成了 LF、成分股列表带尾随逗号。
///      第二个是实测踩过的：解析出来"1031 个板块全部发生变化"，全是残留的 \r 造成的假象。
///      这三条都写成了用例，将来谁重构解析都躲不过去。
///   ② **绝不返回空列表**——上游 ReplaceMembers 是覆盖语义，返回空等于把这个板块的成分股
///      全删掉，而且全程不报错。取不到就必须抛。这是这条通道最危险的地方。
/// </summary>
public class TerminalBoardFileTests : IDisposable
{
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var p in _temp)
            try { if (File.Exists(p)) File.Delete(p); } catch { /* 临时文件 */ }
    }

    // ── 造样本文件 ───────────────────────────────────────────────────────
    // 真实格式：首行全局 CRC，之后每行
    //   crc1;90.BKxxxx;crc2;级别;数字码;板块名(GBK);成分股(市场.代码 逗号分隔，带尾随逗号)

    /// <summary>"银行" 的 GBK 字节——用来确认解析不会被中文卡住（真实文件就是 GBK）。</summary>
    private static readonly byte[] GbkName = { 0xD2, 0xF8, 0xD0, 0xD0 };

    private static void Ascii(MemoryStream ms, string s)
    {
        var b = Encoding.ASCII.GetBytes(s);
        ms.Write(b, 0, b.Length);
    }

    /// <summary>拼一行；<paramref name="members"/> 形如 "0.000001","1.600000"。</summary>
    private static void Line(MemoryStream ms, string boardCode, int level,
                             IEnumerable<string> members, string lineEnding)
    {
        Ascii(ms, $"12345678;90.{boardCode};87654321;{level};475;");
        ms.Write(GbkName, 0, GbkName.Length);              // 板块名：GBK 中文
        Ascii(ms, ";");
        foreach (var m in members) Ascii(ms, m + ",");     // ⚠ 每个后面都跟逗号 → 尾随逗号
        Ascii(ms, lineEnding);
    }

    /// <summary>
    /// 造一份能通过下限检查的文件（默认 250 个板块，高于 MinBoardCount=200），
    /// 头两个板块内容固定，方便断言。
    /// </summary>
    private string MakeFile(string lineEnding = "\n", int boardCount = 250,
                            string? overrideFirstMembers = null)
    {
        var ms = new MemoryStream();
        Ascii(ms, "1563638874" + lineEnding);              // 首行：全局 CRC

        Line(ms, "BK0475", 2,
             overrideFirstMembers == null
                 ? new[] { "0.000001", "1.600000", "1.601398" }
                 : overrideFirstMembers.Split('|', StringSplitOptions.RemoveEmptyEntries),
             lineEnding);
        Line(ms, "BK0500", 4, new[] { "0.300750", "1.600519" }, lineEnding);
        Line(ms, "BK0145", 1, new[] { "0.000001", "1.600000" }, lineEnding);   // 地域，名单里要被跳过
        Line(ms, "BK0146", 1, new[] { "0.000002" }, lineEnding);               // 地域

        for (int i = 4; i < boardCount; i++)
            Line(ms, $"BK{1000 + i}", 3, new[] { $"0.{i:D6}" }, lineEnding);

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                          $"hs_bk_{Guid.NewGuid():N}.dat");
        File.WriteAllBytes(path, ms.ToArray());
        _temp.Add(path);
        return path;
    }

    // ── 解析 ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("\n")]      // 2026-09-06 那版
    [InlineData("\r\n")]    // 2026-09-04 那版 —— 行尾真的变过
    public void ParsesBothLineEndings(string lineEnding)
    {
        var f = new EastMoneyTerminalBoardFile(MakeFile(lineEnding));
        Assert.True(f.Load(out var msg), msg);
        Assert.Equal(250, f.BoardCount);

        var members = f.TryGetMembers("BK0475");
        Assert.NotNull(members);
        // 市场前缀去掉、尾随逗号不产生空项、**结尾没有残留的 \r**
        Assert.Equal(new[] { "000001", "600000", "601398" }, members!);
        Assert.All(members!, c => Assert.Equal(6, c.Length));
        Assert.DoesNotContain(members!, c => c.Contains('\r'));
    }

    [Fact]
    public void ParsesLevels()
    {
        var f = new EastMoneyTerminalBoardFile(MakeFile());
        Assert.True(f.Load(out _));
        Assert.Equal(2, f.LevelOf("BK0475"));   // 行业
        Assert.Equal(4, f.LevelOf("BK0500"));   // 指数/风格
        Assert.Equal(0, f.LevelOf("BK9999"));   // 不在文件里
    }

    [Fact]
    public void DeduplicatesMembers()
    {
        var f = new EastMoneyTerminalBoardFile(
            MakeFile(overrideFirstMembers: "0.000001|1.600000|0.000001"));
        Assert.True(f.Load(out _));
        Assert.Equal(new[] { "000001", "600000" }, f.TryGetMembers("BK0475")!);
    }

    // ── 校验：宁可不更新，也不能把坏数据/陈数据当成好的 ──────────────────

    [Fact]
    public void RejectsMissingFile()
    {
        var f = new EastMoneyTerminalBoardFile(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nope_{Guid.NewGuid():N}.dat"));
        Assert.False(f.Load(out var msg));
        Assert.Contains("找不到", msg);
        Assert.False(f.IsLoaded);
    }

    [Fact]
    public void RejectsStaleFile()
    {
        var path = MakeFile();
        File.SetLastWriteTime(path, DateTime.Now.AddDays(-10));

        var f = new EastMoneyTerminalBoardFile(path);   // 默认上限 3 天
        Assert.False(f.Load(out var msg));
        Assert.Contains("没刷新", msg);
        Assert.Contains("东方财富终端", msg);           // 消息里要告诉人怎么办
        Assert.False(f.IsLoaded);

        // 把上限放宽到 30 天就该能用了——这正是 TerminalBoardMaxAgeDays 的用途
        var f2 = new EastMoneyTerminalBoardFile(path, TimeSpan.FromDays(30));
        Assert.True(f2.Load(out _));
        Assert.Equal(250, f2.BoardCount);
    }

    [Fact]
    public void RejectsTooFewBoards()
    {
        var f = new EastMoneyTerminalBoardFile(MakeFile(boardCount: 50));  // 低于下限 200
        Assert.False(f.Load(out var msg));
        Assert.Contains("50 个板块", msg);
        Assert.False(f.IsLoaded);
    }

    [Fact]
    public void ReloadsAfterFileChanges()
    {
        var path = MakeFile();
        var f = new EastMoneyTerminalBoardFile(path);
        Assert.True(f.Load(out _));
        Assert.Equal(250, f.BoardCount);

        File.WriteAllBytes(path, File.ReadAllBytes(MakeFile(boardCount: 300)));
        Assert.True(f.ReloadIfChanged(out _));
        Assert.Equal(300, f.BoardCount);
    }

    // ── 通道：取不到就抛，绝不返回空列表 ─────────────────────────────────

    private static EastMoneyTerminalBoardFetcher Fetcher(EastMoneyTerminalBoardFile file)
        => new(new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero,
                               batchSize: 50, restDuration: TimeSpan.Zero,
                               jitter: 0, retryDelays: []),
               file, new HttpClient());

    [Fact]
    public async Task ReturnsMembersForKnownBoard()
    {
        var fetcher = Fetcher(new EastMoneyTerminalBoardFile(MakeFile()));
        Assert.True(await fetcher.PrepareAsync());
        Assert.Equal(new[] { "000001", "600000", "601398" },
                     await fetcher.FetchMembersAsync("BK0475"));
    }

    /// <summary>
    /// 板块不在文件里（比如刚上架、客户端还没下发）——必须抛。
    /// 返回空列表的话，上游 ReplaceMembers 会把库里这个板块的成分股全删了。
    /// </summary>
    [Fact]
    public async Task ThrowsForUnknownBoardInsteadOfReturningEmpty()
    {
        var fetcher = Fetcher(new EastMoneyTerminalBoardFile(MakeFile()));
        await fetcher.PrepareAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fetcher.FetchMembersAsync("BK9999"));
        Assert.Contains("BK9999", ex.Message);
    }

    /// <summary>文件不可用时，每个板块都要抛——而不是安静地返回空、把库洗掉。</summary>
    [Fact]
    public async Task ThrowsWhenFileUnavailable()
    {
        var path = MakeFile();
        File.SetLastWriteTime(path, DateTime.Now.AddDays(-10));
        var fetcher = Fetcher(new EastMoneyTerminalBoardFile(path));

        Assert.False(await fetcher.PrepareAsync());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fetcher.FetchMembersAsync("BK0475"));
        Assert.Contains("东方财富终端", ex.Message);
    }

    [Fact]
    public async Task DescribeChannelReflectsState()
    {
        var fetcher = Fetcher(new EastMoneyTerminalBoardFile(MakeFile()));
        Assert.Contains("不可用", fetcher.DescribeChannel());
        await fetcher.PrepareAsync();
        Assert.Contains("250 个板块", fetcher.DescribeChannel());
    }

    // ── 节流：由通道自己说了算（2026-09-06）────────────────────────────────

    /// <summary>
    /// 本地文件通道完全不节流，每轮全量覆盖。约定是"&lt;= 0 表示不节流"，编排层据此把
    /// 新鲜度时间线推到 MaxValue（见 FetchOrchestrator.BoardMemberFreshSince）。
    /// </summary>
    [Fact]
    public void TerminalChannelDoesNotThrottle()
    {
        var fetcher = Fetcher(new EastMoneyTerminalBoardFile(MakeFile()));
        Assert.True(fetcher.MemberFreshFor <= TimeSpan.Zero);
    }

    // ── 板块名单（2026-09-06）──────────────────────────────────────────────

    /// <summary>板块名是文件里唯一的中文字段，GBK 编的，要真解出来才能当名单用。</summary>
    [Fact]
    public void DecodesGbkBoardName()
    {
        var f = new EastMoneyTerminalBoardFile(MakeFile());
        Assert.True(f.Load(out _));
        Assert.Equal("银行", f.NameOf("BK0475"));   // 样本里写的就是 GBK 的"银行"
    }

    /// <summary>
    /// 级别到 BoardType 的映射，**四种级别一个不漏**（2026-09-06 起地域也纳入）：
    /// 1=地域 2=行业 3=概念 4=指数/风格（并进概念）。
    /// </summary>
    [Fact]
    public void BuildsBoardListWithAllTypes()
    {
        var f = new EastMoneyTerminalBoardFile(MakeFile());
        Assert.True(f.Load(out _));

        var list = f.BuildBoardList();
        Assert.Equal(250, f.BoardCount);
        Assert.Equal(250, list.Count);              // 地域不再被丢掉

        var bank = Assert.Single(list, b => b.BoardCode == "BK0475");
        Assert.Equal(BoardType.Industry, bank.Type);        // 级别 2 → 行业
        Assert.Equal("银行", bank.Name);
        Assert.Equal(3, bank.MemberCount);

        Assert.Equal(BoardType.Concept,                     // 级别 4 → 概念
                     Assert.Single(list, b => b.BoardCode == "BK0500").Type);
        Assert.Equal(BoardType.Concept,                     // 级别 3 → 概念
                     Assert.Single(list, b => b.BoardCode == "BK1004").Type);
        Assert.Equal(BoardType.Region,                      // 级别 1 → 地域
                     Assert.Single(list, b => b.BoardCode == "BK0145").Type);
        Assert.Equal(BoardType.Region,
                     Assert.Single(list, b => b.BoardCode == "BK0146").Type);

        // 行情字段留空，由【板块指数合成】回填；CommitBoardListFromMenu 还会把库里旧值带过来
        Assert.All(list, b => Assert.Equal(0, b.ChangePct));
        Assert.All(list, b => Assert.Equal("", b.LeaderCode));
    }

    /// <summary>没见过的级别宁可丢掉，也不要猜一个类型混进概念——那会让概念的快照护栏失真。</summary>
    [Fact]
    public void SkipsUnknownLevels()
    {
        var ms = new MemoryStream();
        Ascii(ms, "1563638874\n");
        Line(ms, "BK0475", 2, new[] { "0.000001" }, "\n");
        Line(ms, "BK9001", 9, new[] { "0.000002" }, "\n");   // 级别 9：没见过
        for (int i = 2; i < 250; i++)
            Line(ms, $"BK{2000 + i}", 3, new[] { $"0.{i:D6}" }, "\n");
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hs_bk_{Guid.NewGuid():N}.dat");
        File.WriteAllBytes(path, ms.ToArray());
        _temp.Add(path);

        var f = new EastMoneyTerminalBoardFile(path);
        Assert.True(f.Load(out _));
        Assert.Equal(250, f.BoardCount);                     // 文件里有 250 个
        Assert.Equal(249, f.BuildBoardList().Count);         // 名单里少了那个级别 9 的
        Assert.DoesNotContain(f.BuildBoardList(), b => b.BoardCode == "BK9001");
    }

    [Fact]
    public void TryGetBoardListReportsCountsPerType()
    {
        var fetcher = Fetcher(new EastMoneyTerminalBoardFile(MakeFile()));
        Assert.True(fetcher.TryGetBoardList(out var list, out var msg));
        Assert.Equal(250, list.Count);
        Assert.Contains("250 个", msg);
        // 分类型逐个数，不能写成"概念 = 总数 - 行业"，否则地域会被并进概念
        Assert.Contains("行业 1", msg);
        Assert.Contains("地域 2", msg);
        Assert.Contains("概念/题材 247", msg);
    }

    /// <summary>
    /// 中文名一律走 <see cref="BoardTypeNames.Label"/>。
    /// 加地域之前散落着好几处 <c>type == Concept ? "概念" : "行业"</c>，
    /// 那种写法会把地域显示成"行业"而且编译器不提醒——这条守住统一入口。
    /// </summary>
    [Fact]
    public void EveryBoardTypeHasItsOwnLabel()
    {
        var labels = Enum.GetValues<BoardType>().Select(t => t.Label()).ToList();
        Assert.Equal(labels.Count, labels.Distinct().Count());   // 没有两类共用一个名字
        Assert.All(labels, l => Assert.False(string.IsNullOrWhiteSpace(l)));
        Assert.Equal("地域", BoardType.Region.Label());
    }

    /// <summary>
    /// 文件不可用时名单要**返回 false 让调用方退回菜单 JSON**，而不是抛——
    /// 名单还有 sidemenu 和 push2 两条路，没道理因为客户端没开就让整项停摆。
    /// （成分股那边相反：拿不到就抛，因为没有别的源可退。）
    /// </summary>
    [Fact]
    public void TryGetBoardListFailsSoftlyWhenFileStale()
    {
        var path = MakeFile();
        File.SetLastWriteTime(path, DateTime.Now.AddDays(-10));
        var fetcher = Fetcher(new EastMoneyTerminalBoardFile(path));

        Assert.False(fetcher.TryGetBoardList(out var list, out var msg));
        Assert.Empty(list);
        Assert.Contains("没刷新", msg);
    }

    /// <summary>
    /// 走网络的通道仍然是 7 天——把节流改成通道属性时最容易出的错，就是顺手把所有通道
    /// 都放开，那三条一轮三小时起、还随时被限流，放开等于每轮都从头再抓一遍。
    /// </summary>
    [Fact]
    public void NetworkChannelsStillThrottleAtSevenDays()
    {
        var limiter = new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero,
                                      batchSize: 50, restDuration: TimeSpan.Zero,
                                      jitter: 0, retryDelays: []);
        Assert.Equal(TimeSpan.FromDays(7),
                     new EastMoneyBoardHttpFetcher(limiter, new HttpClient()).MemberFreshFor);
        Assert.Equal(TimeSpan.FromDays(7),
                     new SinaBoardFetcher(limiter, new HttpClient()).MemberFreshFor);
    }
}
