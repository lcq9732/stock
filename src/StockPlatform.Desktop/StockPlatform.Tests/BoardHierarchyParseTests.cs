using StockPlatform.Data.Local;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 东财终端层级文件的解析（2026-09-07）。
///
/// 这个格式是**逆向出来的**，没有文档 —— 所以必须有测试钉住，否则哪天改坏了没人知道。
/// 用的字节全部来自真实文件 <c>C:\eastmoney\dfcf\data\IndustryBlockRelation.dat</c>，
/// 不读磁盘、不联网。
///
/// 格式：0x45 起，每 9 字节一条 = 6 字节代码 + 0x00 + 0x00 + 层级(1/2/3)；全 0 是空槽。
///
/// 记录是成对的 (父,子)，但**不能按位置死配**：有效记录 1273 条是奇数，中间夹着 338 条
/// 落单的，从第 145 条起整体错开半格。所以配对要能重新同步（层级对不上就只前进一格）。
///
/// ⚠ 这里走过一次弯路，最后一个用例专门钉它：先用的是"层级栈"，那版同样解出 467 条边、
///   层级数字**全对**，可 51 条边的父是错的（"银行Ⅱ 挂在石油石化下"）。错位之后每个子板块
///   还是会挂到某个层级正确的父上，光对层级数字发现不了。
/// </summary>
public class BoardHierarchyParseTests
{
    private readonly ITestOutputHelper _out;
    public BoardHierarchyParseTests(ITestOutputHelper o) => _out = o;

    private const int HeaderSize = 0x45;

    /// <summary>拼一条 9 字节记录。</summary>
    private static byte[] Rec(string code, int level)
    {
        var b = new byte[9];
        System.Text.Encoding.ASCII.GetBytes(code).CopyTo(b, 0);
        b[6] = 0; b[7] = 0; b[8] = (byte)level;
        return b;
    }

    /// <summary>空槽：9 个 0。文件里有 339 个，是占位/分段用的。</summary>
    private static byte[] Blank() => new byte[9];

    private static byte[] Build(params byte[][] records)
    {
        var buf = new List<byte>(new byte[HeaderSize]);   // 头部内容解析时不看，填 0 即可
        foreach (var r in records) buf.AddRange(r);
        return buf.ToArray();
    }

    [Fact]
    public void 真实文件开头十四条_解析出七条父子边()
    {
        // 这 14 条是从真实文件 0x45 起原样抄的：航空机场→航空运输/机场、铁路公路→高速公路/铁路运输/公交、
        // 物流→仓储物流/端到端供应链服务
        var raw = Build(
            Rec("BK0420", 2), Rec("BK1479", 3),
            Rec("BK0420", 2), Rec("BK1480", 3),
            Rec("BK0421", 2), Rec("BK1483", 3),
            Rec("BK0421", 2), Rec("BK1485", 3),
            Rec("BK0421", 2), Rec("BK1484", 3),
            Rec("BK0422", 2), Rec("BK1486", 3),
            Rec("BK0422", 2), Rec("BK1491", 3));

        var edges = EastMoneyTerminalHierarchyProvider.Parse(raw);
        foreach (var e in edges) _out.WriteLine($"{e.BoardCode} → {e.ParentCode} (L{e.Level}←L{e.ParentLevel})");

        Assert.Equal(7, edges.Count);
        Assert.Equal(("BK1479", "BK0420", 3, 2),
            (edges[0].BoardCode, edges[0].ParentCode, edges[0].Level, edges[0].ParentLevel));
        // 铁路公路下面挂了三个三级
        Assert.Equal(3, edges.Count(e => e.ParentCode == "BK0421"));
    }

    [Fact]
    public void 空槽不能中断扫描()
    {
        // ★ 早期版本遇到空槽就 break，1612 个槽只读出了前面一小截。
        //   文件里有 339 个空槽，中断一次就丢掉后面全部数据。
        var raw = Build(
            Rec("BK0427", 1), Rec("BK0428", 2),
            Blank(), Blank(),                       // 中间夹两个空槽
            Rec("BK0428", 2), Rec("BK1377", 3),
            Blank(),
            Rec("BK0428", 2), Rec("BK1380", 3));

        var edges = EastMoneyTerminalHierarchyProvider.Parse(raw);
        Assert.Equal(3, edges.Count);
        Assert.Contains(edges, e => e.BoardCode == "BK1377" && e.ParentCode == "BK0428");
        Assert.Contains(edges, e => e.BoardCode == "BK1380" && e.ParentCode == "BK0428");
    }

    [Fact]
    public void 落单记录之后能重新同步()
    {
        // ★ 这是 1273 条记录为什么是奇数的原因：中间有 338 条配不成对的。
        //   按位置死配的话，一条落单就把**后面全部**错开半格 —— 实测从第 145 条起就废了。
        var raw = Build(
            Rec("BK0420", 2), Rec("BK1479", 3),
            Rec("BK0458", 3),                        // 落单：它后面那条层级对不上
            Rec("BK0421", 2), Rec("BK1483", 3));

        var edges = EastMoneyTerminalHierarchyProvider.Parse(raw);
        Assert.Equal(2, edges.Count);
        Assert.Contains(edges, e => e.BoardCode == "BK1479" && e.ParentCode == "BK0420");
        Assert.Contains(edges, e => e.BoardCode == "BK1483" && e.ParentCode == "BK0421");  // 同步回来了
    }

    [Fact]
    public void 一级板块没有父_不产生边()
    {
        var raw = Build(Rec("BK0427", 1), Rec("BK0433", 1));
        Assert.Empty(EastMoneyTerminalHierarchyProvider.Parse(raw));
    }

    [Fact]
    public void 子板块先以父的身份出现过_也不能挂错父()
    {
        // ★★ 层级栈那版栽在这儿，值 51 条错边。
        //
        // 真实文件里，"银行Ⅱ"会先作为**父**出现（银行Ⅱ → 它的三级子），过一段才作为**子**
        // 出现在 (银行, 银行Ⅱ) 这一对里。层级栈在它第一次露面时就拿"当时最近的一级"给它定了父，
        // 而那个一级是上一段的石油石化 —— 于是得出"银行Ⅱ 挂在石油石化下"。
        // 层级数字（2 级挂 1 级）完全正确，所以只对层级的校验一路放行。
        var raw = Build(
            Rec("BK0464", 1), Rec("BK0473", 2),      // 石油石化 → 证券Ⅱ
            Rec("BK0475", 2), Rec("BK1234", 3),      // 银行Ⅱ 先以父的身份露面
            Rec("BK1283", 1), Rec("BK0475", 2));     // 银行 → 银行Ⅱ ← 真正的父在这儿

        var edges = EastMoneyTerminalHierarchyProvider.Parse(raw);
        var bank = Assert.Single(edges, e => e.BoardCode == "BK0475");
        Assert.Equal("BK1283", bank.ParentCode);     // 银行，不是石油石化
        Assert.Equal(3, edges.Count);
    }

    [Fact]
    public void 同一个子板块重复出现只记一次()
    {
        // 文件里父板块会重复出现（每条边都重复一次父），子板块理论上不重复。
        // 真出现了，以先到的为准 —— 否则结果依赖文件顺序，不可复现。
        // ⚠ 这里的"先到"指的是**作为子**先到，跟上一个用例不冲突。
        var raw = Build(
            Rec("BK0420", 2), Rec("BK1479", 3),
            Rec("BK0421", 2), Rec("BK1479", 3));   // 同一个子挂到了第二个父

        var edges = EastMoneyTerminalHierarchyProvider.Parse(raw);
        Assert.Single(edges);
        Assert.Equal("BK0420", edges[0].ParentCode);   // 先到的那个
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(255)]
    public void 层级越界的记录直接丢掉(int level)
    {
        var raw = Build(Rec("BK0420", 2), Rec("BK9999", level));
        Assert.Empty(EastMoneyTerminalHierarchyProvider.Parse(raw));
    }

    [Fact]
    public void 文件太短或全空时返回空列表不抛异常()
    {
        Assert.Empty(EastMoneyTerminalHierarchyProvider.Parse([]));
        Assert.Empty(EastMoneyTerminalHierarchyProvider.Parse(new byte[HeaderSize]));
        Assert.Empty(EastMoneyTerminalHierarchyProvider.Parse(new byte[HeaderSize + 5]));
    }

    [Fact]
    public void 不是BK开头的记录跳过而不是中断()
    {
        // 万一哪天格式里混进别的东西，也不该让后面的数据全丢
        var junk = new byte[9];
        junk[0] = (byte)'X'; junk[1] = (byte)'Y'; junk[8] = 3;

        var raw = Build(
            Rec("BK0420", 2), junk, Rec("BK1479", 3));

        var edges = EastMoneyTerminalHierarchyProvider.Parse(raw);
        Assert.Single(edges);
        Assert.Equal("BK1479", edges[0].BoardCode);
    }
}
