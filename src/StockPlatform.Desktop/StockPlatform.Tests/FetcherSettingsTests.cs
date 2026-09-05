using System.IO;
using System.Text.Json;
using StockPlatform.Data.Orchestration;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 设置文件（<c>data/fetcher-settings.json</c>）的读写（2026-09-05）。
///
/// 盯三件事，每一件都出过或差点出事：
///   ① **模板必须能被程序自己读回来**。它是带 <c>//</c> 注释的 JSONC，手写的——
///      少个逗号、注释符打错，程序就会把整份配置当成坏文件忽略掉，而且是**静默**的：
///      所有设置悄悄回到默认值，日志里一个字都没有。这条测试就是防这个。
///   ② **模板里注释掉的配置行，去掉 <c>//</c> 就得能用**。用户就是照着复制来配置的，
///      要是那些示例行本身键名拼错或少了引号，复制出来的配置一样是静默失效。
///   ③ **删老键不能把整个文件清空**。原来那段代码是 <c>WriteAllText(路径, "{}")</c>，
///      会把用户配的 BarSource、Push2NetworkInterface 一起抹掉且不报错——
///      那多半就是这个文件后来变成 0 字节的原因。
/// </summary>
public class FetcherSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "fetcher-settings-tests-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_dir, "fetcher-settings.json");

    public FetcherSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清不掉不影响测试结论 */ }
    }

    private static readonly JsonDocumentOptions Jsonc = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // ── ① 模板自身 ──

    [Fact]
    public void 模板必须是合法的JSONC()
    {
        FetcherSettings.EnsureTemplate(Path_);
        var text = File.ReadAllText(Path_);

        var ex = Record.Exception(() => JsonDocument.Parse(text, Jsonc));
        Assert.Null(ex);        // 手写模板里少个逗号，整份配置就会被静默忽略
    }

    [Fact]
    public void 模板写出来之后能读到默认通道()
    {
        FetcherSettings.EnsureTemplate(Path_);

        // 默认值要真的能读出来，而不是"文件里写着但读不到"
        Assert.Equal("browser", FetcherSettings.ReadString(Path_, "BoardMemberChannel"));
    }

    [Fact]
    public void 模板里被注释掉的项读出来是空()
    {
        FetcherSettings.EnsureTemplate(Path_);

        // 注释掉＝用代码里的默认值，不能被当成"配了空值"
        Assert.Null(FetcherSettings.ReadString(Path_, "BarSource"));
        Assert.Null(FetcherSettings.ReadString(Path_, "Push2NetworkInterface"));
    }

    // ── ② 注释里的示例行，取消注释就得能用 ──

    [Theory]
    [InlineData("BarSource", "Tencent")]
    [InlineData("BarSource", "Sina")]
    [InlineData("BarSource", "EastMoney")]
    [InlineData("Push2NetworkInterface", "Wi-Fi")]
    [InlineData("BoardMemberChannel", "http")]
    public void 取消注释后的配置行能被读出来(string key, string value)
    {
        // 模拟用户的操作：把模板里那一行前面的 // 去掉
        var line = $"//\"{key}\": \"{value}\",";
        Assert.Contains(line, FetcherSettings.Template);       // 这一行确实在模板里（键名/引号没写错）

        // 同一个键只能留一行——模板里 BoardMemberChannel 本来就有一行是生效的（当前默认值），
        // 两行同名键都在的话 JsonDocument 只认第一个，测的就不是我们想测的东西了。
        // 按行滤掉所有**未注释**的同名键行，再取消目标行的注释：这样不依赖"默认值是哪个"，
        // 以后改默认值这条测试不会跟着断（2026-09-05 就是这么断过一次）。
        var edited = string.Join("\n", FetcherSettings.Template
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith($"\"{key}\""))
            .Select(l => l.Replace(line, $"\"{key}\": \"{value}\",")));
        File.WriteAllText(Path_, edited);

        Assert.Equal(value, FetcherSettings.ReadString(Path_, key));
    }

    // ── ③ EnsureTemplate 不能动已有内容 ──

    [Fact]
    public void 已有配置不被模板覆盖()
    {
        File.WriteAllText(Path_, """{ "BarSource": "Sina" }""");
        FetcherSettings.EnsureTemplate(Path_);

        Assert.Equal("Sina", FetcherSettings.ReadString(Path_, "BarSource"));
        Assert.Null(FetcherSettings.ReadString(Path_, "BoardMemberChannel"));   // 没被塞进模板的默认值
    }

    [Fact]
    public void 空文件会补上模板()
    {
        // 现实里那个文件就是 0 字节的——原来那段清空 bug 留下的
        File.WriteAllText(Path_, "");
        FetcherSettings.EnsureTemplate(Path_);

        Assert.Equal("browser", FetcherSettings.ReadString(Path_, "BoardMemberChannel"));
    }

    [Fact]
    public void 坏文件会被换成模板()
    {
        File.WriteAllText(Path_, "{ 这不是 JSON");
        FetcherSettings.EnsureTemplate(Path_);

        Assert.Equal("browser", FetcherSettings.ReadString(Path_, "BoardMemberChannel"));
    }

    // ── ④ 删键：只删一个，别清空 ──

    [Fact]
    public void 删老键时保留其它设置()
    {
        File.WriteAllText(Path_, """
            {
              "AutoFillFinancialsWhenIdle": true,
              "BarSource": "Sina",
              "Push2NetworkInterface": "Wi-Fi"
            }
            """);

        Assert.True(FetcherSettings.RemoveKey(Path_, "AutoFillFinancialsWhenIdle"));

        Assert.False(FetcherSettings.ReadBool(Path_, "AutoFillFinancialsWhenIdle"));
        Assert.Equal("Sina", FetcherSettings.ReadString(Path_, "BarSource"));            // ← 老代码会抹掉这个
        Assert.Equal("Wi-Fi", FetcherSettings.ReadString(Path_, "Push2NetworkInterface"));
    }

    [Fact]
    public void 键不存在时不动文件_注释才保得住()
    {
        FetcherSettings.EnsureTemplate(Path_);
        var before = File.ReadAllText(Path_);

        Assert.False(FetcherSettings.RemoveKey(Path_, "AutoFillFinancialsWhenIdle"));

        // 一个字节都不该变——重写一遍会把注释洗掉，而这个键正常情况下压根不存在
        Assert.Equal(before, File.ReadAllText(Path_));
    }

    // ── ⑤ 带注释的配置能正常读 ──

    [Fact]
    public void 注释和末尾逗号都不影响读取()
    {
        File.WriteAllText(Path_, """
            // 顶上的说明
            {
              // 中间的说明
              "BarSource": "Sina",   // 行尾的说明
              "BoardMemberChannel": "browser",
            }
            """);

        Assert.Equal("Sina", FetcherSettings.ReadString(Path_, "BarSource"));
        Assert.Equal("browser", FetcherSettings.ReadString(Path_, "BoardMemberChannel"));
    }

    [Fact]
    public void 文件不存在时读取返回空而不是抛异常()
    {
        Assert.Null(FetcherSettings.ReadString(Path.Combine(_dir, "没有这个文件.json"), "BarSource"));
        Assert.False(FetcherSettings.ReadBool(Path.Combine(_dir, "没有这个文件.json"), "X"));
    }

    // ── ⑥ 板块通道的规范化读法（2026-09-05）──
    //
    // 这个值有两个读它的地方：造 fetcher 的 App.CreateBoardFetcher，和【重新读取配置】
    // 在日志里报告"现在生效的是哪条"。两边共用 ReadBoardChannel 就是为了不出现
    // "日志说 http、实际造的是 browser"这种对不上的情况，所以规范化规则得钉死。

    [Theory]
    [InlineData("\"http\"", "http")]
    [InlineData("\"browser\"", "browser")]
    [InlineData("\"HTTP\"", "http")]              // 大小写不该影响
    [InlineData("\"  browser  \"", "browser")]    // 手改配置很容易多敲空格
    public void 板块通道读法会规范化大小写和空格(string rawValue, string expected)
    {
        File.WriteAllText(Path_, $$"""
            {
              "BoardMemberChannel": {{rawValue}}
            }
            """);

        Assert.Equal(expected, FetcherSettings.ReadBoardChannel(Path_));
    }

    [Fact]
    public void 板块通道没配或文件缺失时都是browser()
    {
        // 没配这个键
        File.WriteAllText(Path_, """{ "BarSource": "Sina" }""");
        Assert.Equal("browser", FetcherSettings.ReadBoardChannel(Path_));

        // 文件根本不存在：也得给出默认值，不能抛
        Assert.Equal("browser", FetcherSettings.ReadBoardChannel(Path.Combine(_dir, "没有这个文件.json")));
    }

    [Fact]
    public void 模板里那行板块通道读出来正好是默认值()
    {
        // 模板把 BoardMemberChannel 写成了唯一一个**不注释**的键。它要是跟代码里的默认值
        // 对不上，用户看到的"当前配置"就是错的——而这种错没有任何报错。
        FetcherSettings.EnsureTemplate(Path_);
        Assert.Equal("browser", FetcherSettings.ReadBoardChannel(Path_));
    }
}
