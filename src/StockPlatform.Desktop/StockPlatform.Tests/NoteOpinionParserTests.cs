using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 从个股笔记里摘「个人观点」的判据（2026-09-11）。
///
/// 这是 L2 手写层的录入路径：观点写在 <c>notes/{code}.md</c> 里，
/// 【观察项】页那列直接显示。笔记是自由 markdown，所以只认「观点：」这一个标记——
/// 不强制模板，抽不到就留空，不影响任何判断。
/// </summary>
public class NoteOpinionParserTests
{
    [Fact]
    public void 带markdown引用符号的观点行_认得出来()
    {
        const string note = """
            # 宁德时代 300750

            > 观点：估值压到18x，压制来自政策成本+去宁化；回购是情绪转折点

            ## 2026-09-11
            锂价跌破 14 万。
            """;

        Assert.Equal("估值压到18x，压制来自政策成本+去宁化；回购是情绪转折点",
            NoteOpinionParser.Parse(note));
    }

    [Theory]
    [InlineData("观点：直接顶格写")]
    [InlineData("- 观点：列表项")]
    [InlineData("* 观点：星号列表")]
    [InlineData("### 观点：标题形式")]
    [InlineData("  \t> 观点：前面一堆空白")]
    [InlineData("观点:半角冒号")]
    public void 各种前缀和冒号都能认(string line)
        => Assert.NotNull(NoteOpinionParser.Parse(line));

    /// <summary>★ 首行往往是标题或日期，拿它当观点必错——所以必须有标记。</summary>
    [Fact]
    public void 没有标记的笔记_不瞎猜首行()
    {
        const string note = """
            # 宁德时代 300750
            2026-09-11 锂价跌破 14 万，回购一股没买。
            """;

        Assert.Null(NoteOpinionParser.Parse(note));
    }

    [Fact]
    public void 有多条时取第一条()
    {
        const string note = """
            > 观点：第一条
            > 观点：第二条
            """;

        Assert.Equal("第一条", NoteOpinionParser.Parse(note));
    }

    /// <summary>标记后面是空的不算——留着只会在界面上显示一个空观点。</summary>
    [Fact]
    public void 标记后面没内容_当成没写()
        => Assert.Null(NoteOpinionParser.Parse("> 观点：   "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void 空笔记不炸(string? note)
        => Assert.Null(NoteOpinionParser.Parse(note));

    /// <summary>CRLF 行尾（Windows 记事本存的）不能把结尾的 \r 带进观点里。</summary>
    [Fact]
    public void CRLF行尾_不留回车()
    {
        var v = NoteOpinionParser.Parse("# 标题\r\n> 观点：回购是情绪转折点\r\n正文\r\n");
        Assert.Equal("回购是情绪转折点", v);
    }
}
