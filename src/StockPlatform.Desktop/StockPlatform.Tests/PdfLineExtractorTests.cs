using StockPlatform.Pdf;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 兜底链组合器（2026-09-11）。
///
/// 这些用例以前**写不出来**：兜底顺序写死在 BankReportParser.ExtractLines 里，要测它就得
/// 准备一份"PdfPig 读不动但 pdftotext 能读"的真 PDF，还得那台机器正好装了 poppler。
/// 把顺序变成构造参数之后，拿假 source 就能把语义钉死——这是对象化换来的实际好处，
/// 不是形式上的好看。
/// </summary>
public class PdfLineExtractorTests
{
    /// <summary>按剧本行事的假 source：要么不可用，要么返回指定结果，要么抛。</summary>
    private sealed class FakeSource(string name, IReadOnlyList<PdfLine>? result,
                                    bool available = true, Exception? throws = null)
        : IPdfLineSource
    {
        public int Calls { get; private set; }
        public string Name => name;
        public bool IsAvailable => available;
        public string UnavailableReason => available ? "" : $"{name} 没装";

        public IReadOnlyList<PdfLine>? TryExtract(string pdfPath, PdfExtractOptions options,
                                                  CancellationToken ct = default)
        {
            Calls++;
            if (throws != null) throw throws;
            return result;
        }
    }

    private static IReadOnlyList<PdfLine> Some(string text) => [new PdfLine(1, text)];

    [Fact]
    public void 第一个出结果的赢_后面的不再调()
    {
        var first = new FakeSource("a", Some("来自 a"));
        var second = new FakeSource("b", Some("来自 b"));

        var lines = new PdfLineExtractor(first, second).Extract("x.pdf", PdfExtractOptions.Default,
                                                                out var used);

        Assert.Equal("来自 a", Assert.Single(lines).Text);
        Assert.Equal("a", used);
        Assert.Equal(1, first.Calls);
        Assert.Equal(0, second.Calls);      // 短路：第一条成了就不该再花第二条的钱
    }

    [Theory]
    [InlineData(null)]      // 返回 null
    [InlineData(true)]      // 返回空列表
    public void 前一条没结果就轮到下一条(bool? emptyInsteadOfNull)
    {
        // null 和空列表对调用方必须等价——这是 IPdfLineSource 明确许诺的语义，
        // 三个实现里两种写法都有（PdfPig 返回空列表、poppler 返回 null）。
        IReadOnlyList<PdfLine>? nothing = emptyInsteadOfNull == true ? [] : null;
        var first = new FakeSource("a", nothing);
        var second = new FakeSource("b", Some("来自 b"));

        var lines = new PdfLineExtractor(first, second).Extract("x.pdf", PdfExtractOptions.Default,
                                                                out var used);

        Assert.Equal("来自 b", Assert.Single(lines).Text);
        Assert.Equal("b", used);
        Assert.Equal(1, first.Calls);
    }

    [Fact]
    public void 不可用的那条直接跳过_不去调它()
    {
        var down = new FakeSource("a", Some("不该出现"), available: false);
        var up = new FakeSource("b", Some("来自 b"));

        var lines = new PdfLineExtractor(down, up).Extract("x.pdf", PdfExtractOptions.Default);

        Assert.Equal("来自 b", Assert.Single(lines).Text);
        Assert.Equal(0, down.Calls);
    }

    [Fact]
    public void 某条抛异常不该让整条链报废()
    {
        // 实测就是这么用的：PdfPig 碰上缺 CMap 的 CJK 字体会抛，pdftotext 一转就出来。
        var boom = new FakeSource("a", null, throws: new InvalidOperationException("字体坏了"));
        var ok = new FakeSource("b", Some("来自 b"));

        var lines = new PdfLineExtractor(boom, ok).Extract("x.pdf", PdfExtractOptions.Default);

        Assert.Equal("来自 b", Assert.Single(lines).Text);
    }

    [Fact]
    public void 取消要能穿透出去_不能被异常兜底吞掉()
    {
        // catch 写宽了很容易把 OperationCanceledException 一起吞掉，那样"停止"就会失灵。
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var s = new FakeSource("a", Some("x"));

        Assert.Throws<OperationCanceledException>(
            () => new PdfLineExtractor(s).Extract("x.pdf", PdfExtractOptions.Default, out _, cts.Token));
    }

    [Fact]
    public void 全都没结果时返回空_不抛()
    {
        var lines = new PdfLineExtractor(new FakeSource("a", null), new FakeSource("b", []))
            .Extract("x.pdf", PdfExtractOptions.Default, out var used);

        Assert.Empty(lines);
        Assert.Equal("", used);
    }

    [Fact]
    public void 一条可用的都没有时_给得出缺什么()
    {
        var ex = new PdfLineExtractor(new FakeSource("a", null, available: false),
                                      new FakeSource("b", null, available: false));

        Assert.Contains("a 没装", ex.UnavailableReason);
        Assert.Contains("b 没装", ex.UnavailableReason);
    }

    [Fact]
    public void 有一条可用就不算不可用()
    {
        var ex = new PdfLineExtractor(new FakeSource("a", null, available: false),
                                      new FakeSource("b", Some("x")));

        Assert.Equal("", ex.UnavailableReason);
    }

    [Fact]
    public void 页面判据要原样传到_source_手里()
    {
        // 页面筛选下沉到各 source 是这次拆分的关键一步（原来 pdftotext 那条路的筛页写在
        // BankReportParser 里）。组合器不许在中间改判据。
        PdfExtractOptions? seen = null;
        var probe = new ProbeSource(o => seen = o);
        var opts = new PdfExtractOptions { PageFilter = t => t.Contains("指标"), LineTolerance = 7.5 };

        new PdfLineExtractor(probe).Extract("x.pdf", opts);

        Assert.NotNull(seen);
        Assert.Same(opts.PageFilter, seen!.PageFilter);
        Assert.Equal(7.5, seen.LineTolerance);
    }

    // ── ExtractAll：把每条的结果都交出来，取舍留给业务侧（2026-10-23）──────────────
    // 加这个入口是因为"第一个非空的赢"治不了"读残但非空"，而"够不够用"要看解出多少指标，
    // 组合器不懂指标。见 doc/pdf-source-fallback-design.md。

    [Fact]
    public void ExtractAll_按顺序把每条都交出来()
    {
        var first = new FakeSource("a", Some("来自 a"));
        var second = new FakeSource("b", Some("来自 b"));

        var all = new PdfLineExtractor(first, second).ExtractAll("x.pdf", PdfExtractOptions.Default).ToList();

        Assert.Equal(["a", "b"], all.Select(x => x.Source));
        Assert.Equal("来自 a", all[0].Lines[0].Text);
        Assert.Equal("来自 b", all[1].Lines[0].Text);
    }

    [Fact]
    public void ExtractAll_跳过不可用的和没结果的()
    {
        var down = new FakeSource("down", Some("不该出现"), available: false);
        var empty = new FakeSource("empty", []);
        var nil = new FakeSource("nil", null);
        var ok = new FakeSource("ok", Some("有货"));

        var all = new PdfLineExtractor(down, empty, nil, ok).ExtractAll("x.pdf", PdfExtractOptions.Default).ToList();

        Assert.Equal(["ok"], all.Select(x => x.Source));
        Assert.Equal(0, down.Calls);
    }

    [Fact]
    public void ExtractAll_某条抛了不影响后面那条()
    {
        var boom = new FakeSource("boom", null, throws: new InvalidOperationException("字体表坏了"));
        var ok = new FakeSource("ok", Some("有货"));

        var all = new PdfLineExtractor(boom, ok).ExtractAll("x.pdf", PdfExtractOptions.Default).ToList();

        Assert.Equal(["ok"], all.Select(x => x.Source));
    }

    /// <summary>惰性：调用方只要第一条时，后面的 source 不该被白白跑一遍（一份 PDF 好几秒）。</summary>
    [Fact]
    public void ExtractAll_是惰性的()
    {
        var first = new FakeSource("a", Some("来自 a"));
        var second = new FakeSource("b", Some("来自 b"));

        var taken = new PdfLineExtractor(first, second).ExtractAll("x.pdf", PdfExtractOptions.Default).First();

        Assert.Equal("a", taken.Source);
        Assert.Equal(1, first.Calls);
        Assert.Equal(0, second.Calls);
    }

    private sealed class ProbeSource(Action<PdfExtractOptions> spy) : IPdfLineSource
    {
        public string Name => "probe";
        public bool IsAvailable => true;
        public string UnavailableReason => "";
        public IReadOnlyList<PdfLine>? TryExtract(string pdfPath, PdfExtractOptions options,
                                                  CancellationToken ct = default)
        {
            spy(options);
            return [new PdfLine(1, "x")];
        }
    }
}
