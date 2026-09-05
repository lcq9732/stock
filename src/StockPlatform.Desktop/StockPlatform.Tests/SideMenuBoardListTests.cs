using System.IO;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 板块名单改走行情中心菜单 JSON（2026-09-05）之后的解析和护栏。不联网，喂固定样例。
///
/// 盯两件事：
///   ① **解析要认得 type**——1=地域 2=行业 3=概念。认错的话概念板块会被当行业写进正表，
///      而 <c>CommitStaged</c> 的清僵尸是按 type 限定的：写行业那一下会把"不在这批名单里"的
///      真行业板块全删掉，连它们跨了好几轮才攒齐的成分股一起。
///   ② **半截名单绝不能进正表**。push2 那条路靠"接口自报 total 对不上就弃写"挡住，
///      而这份 JSON 没有 total——换成两道：解析出来少得离谱就抛异常（结构变了），
///      以及 <see cref="EastMoneySideMenuBoardListProvider.CheckAgainstExisting"/>
///      拿库里上次的数量比，掉太多就整轮放弃。
/// </summary>
public class SideMenuBoardListTests
{
    /// <summary>拼一份 bklist 样例：概念、行业、地域各若干个。</summary>
    private static string Menu(int concept, int industry, int region, string? extraItem = null)
    {
        var items = new List<string>();
        int n = 0;
        foreach (var (count, type, prefix) in new[]
                 { (concept, 3, "概念"), (industry, 2, "行业"), (region, 1, "地域") })
            for (int i = 0; i < count; i++, n++)
                items.Add($"{{\"market\":90,\"code\":\"BK{n:D4}\",\"name\":\"{prefix}{i}\","
                        + $"\"pinyin\":\"X\",\"type\":{type},\"flag\":0}}");
        if (extraItem != null) items.Add(extraItem);
        return "{\"url\":\"x\",\"pathname\":\"/gridlist.html\",\"sidemenu\":[],"
             + "\"bklist\":[" + string.Join(",", items) + "]}";
    }

    [Fact]
    public void 概念行业按type分开_地域被跳过()
    {
        var boards = EastMoneySideMenuBoardListProvider.Parse(Menu(320, 300, 31), DateTime.Now);

        Assert.Equal(320, boards.Count(b => b.Type == BoardType.Concept));
        Assert.Equal(300, boards.Count(b => b.Type == BoardType.Industry));
        // 地域板块（type=1）库里从来没存过：抓回来只会让成分股那一步多花 31 个 push2 请求
        Assert.Equal(620, boards.Count);
    }

    [Fact]
    public void 代码和名称都要带出来()
    {
        var boards = EastMoneySideMenuBoardListProvider.Parse(Menu(320, 300, 0), DateTime.Now);
        var first = boards[0];

        Assert.Equal("BK0000", first.BoardCode);
        Assert.Equal("概念0", first.Name);
        Assert.All(boards, b => Assert.False(string.IsNullOrWhiteSpace(b.Name)));
    }

    [Fact]
    public void 认不出的type一律跳过()
    {
        // 东财哪天加了个 type=4，我们不知道那是什么——宁可漏一类，也不要把不认识的东西写进正表
        var json = Menu(320, 300, 0,
            "{\"market\":90,\"code\":\"BK9999\",\"name\":\"某种新板块\",\"type\":4,\"flag\":0}");
        var boards = EastMoneySideMenuBoardListProvider.Parse(json, DateTime.Now);

        Assert.Equal(620, boards.Count);
        Assert.DoesNotContain(boards, b => b.BoardCode == "BK9999");
    }

    [Fact]
    public void 重复代码只留一条()
    {
        var json = Menu(320, 300, 0,
            "{\"market\":90,\"code\":\"BK0000\",\"name\":\"重复的\",\"type\":3,\"flag\":0}");
        var boards = EastMoneySideMenuBoardListProvider.Parse(json, DateTime.Now);

        Assert.Equal(620, boards.Count);
        Assert.Single(boards, b => b.BoardCode == "BK0000");
    }

    [Fact]
    public void 没有bklist就抛异常_不能当成空名单()
    {
        // 静默返回空列表最危险：上游拿到 0 个会以为"板块全下架了"。必须抛。
        var ex = Assert.Throws<InvalidDataException>(() =>
            EastMoneySideMenuBoardListProvider.Parse("""{"sidemenu":[]}""", DateTime.Now));
        Assert.Contains("bklist", ex.Message);
    }

    [Fact]
    public void 解析出来少得离谱就抛异常()
    {
        // 正常约 1000 个。只剩几十个不是"板块变少了"，是文件结构变了。
        var ex = Assert.Throws<InvalidDataException>(() =>
            EastMoneySideMenuBoardListProvider.Parse(Menu(20, 20, 0), DateTime.Now));
        Assert.Contains("结构", ex.Message);
    }

    [Fact]
    public void 不是合法json就抛异常()
    {
        Assert.Throws<InvalidDataException>(() =>
            EastMoneySideMenuBoardListProvider.Parse("<html>验证页</html>", DateTime.Now));
    }

    // ── 护栏：跟库里上次的数量比 ──

    [Fact]
    public void 库里是空的时候不设限()
    {
        // 首次抓取没有"上一次"可比，也没有成分股会被删
        Assert.Null(EastMoneySideMenuBoardListProvider.CheckAgainstExisting(
            BoardType.Concept, fetched: 504, existing: 0));
    }

    [Fact]
    public void 正常增减放行()
    {
        // 板块本来就会增减：新概念上线、旧板块下架。要求相等的话永远提交不了。
        Assert.Null(EastMoneySideMenuBoardListProvider.CheckAgainstExisting(
            BoardType.Concept, fetched: 510, existing: 504));   // 多了 6 个
        Assert.Null(EastMoneySideMenuBoardListProvider.CheckAgainstExisting(
            BoardType.Concept, fetched: 495, existing: 504));   // 少了 9 个，1.8%
    }

    [Fact]
    public void 掉太多就拒绝()
    {
        // 少 10% ≈ 50 个板块。正表是快照语义，这 50 个会被当成已下架、连成分股一起删。
        var why = EastMoneySideMenuBoardListProvider.CheckAgainstExisting(
            BoardType.Industry, fetched: 450, existing: 504);

        Assert.NotNull(why);
        Assert.Contains("行业", why);
        Assert.Contains("450", why);
        Assert.Contains("504", why);
    }

    [Fact]
    public void 一个都没有更要拒绝()
    {
        Assert.NotNull(EastMoneySideMenuBoardListProvider.CheckAgainstExisting(
            BoardType.Concept, fetched: 0, existing: 504));
    }
}
