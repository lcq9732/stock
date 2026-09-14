using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 行业 PE 分位（2026-09-14）。
///
/// ════ 为什么要分行业 ════
/// 全市场中位 39.1 倍这把尺子对谁都不准——一级行业的中位从**银行 6.3** 到**通信 97.9**，差 15 倍。
///
/// ════ 这组测试守的是什么 ════
/// 小样本。中国平安是活教材：二级「保险Ⅱ」只有 5 只样本，PE 6.2 被判成 PE 最高的 10%；
/// 一级「非银金融」77 只，判的是最低的 10%——**结论完全相反，而错的那个来自小样本**。
/// 5 只样本的 P90 就是第 5 名，纯噪音。所以两道门槛必须钉死：
///   · &lt;10 只 → 这一级不可用，回退上一级
///   · 10~30 只 → 中位还算稳，但 P10/P90 只是"第 2 名和第 19 名"，只报中位不报档
/// </summary>
public class IndustryPeStatsTests
{
    /// <summary>造 n 只票，PE 从 start 起每只 +1，归到 (一级, 二级) 两个行业。</summary>
    private static void Seed(Dictionary<string, double> pes,
                             Dictionary<string, (string?, string?)> ind,
                             string prefix, int n, double start, string? l1, string? l2)
    {
        for (int i = 0; i < n; i++)
        {
            var code = $"{prefix}{i:D4}";
            pes[code] = start + i;
            ind[code] = (l1, l2);
        }
    }

    private static (Dictionary<string, double>, Dictionary<string, (string?, string?)>) Empty()
        => (new Dictionary<string, double>(), new Dictionary<string, (string?, string?)>());

    [Fact]
    public void 二级样本够就用二级()
    {
        var (pes, ind) = Empty();
        Seed(pes, ind, "60", 40, 1, "非银金融", "证券Ⅱ");

        var stats = IndustryPeStatsBuilder.Build(pes, ind);

        Assert.Equal("证券Ⅱ", stats["600000"].Name);
        Assert.Equal(2, stats["600000"].Level);
        Assert.Equal(40, stats["600000"].SampleSize);
        Assert.True(stats["600000"].HasBands);
    }

    [Fact]
    public void 二级样本不足就回退一级()
    {
        // 中国平安那个场景：保险Ⅱ 只有 5 只，非银金融 77 只。
        var (pes, ind) = Empty();
        Seed(pes, ind, "60", 5, 5, "非银金融", "保险Ⅱ");       // 二级只有 5 只
        Seed(pes, ind, "61", 72, 10, "非银金融", "证券Ⅱ");     // 一级凑够 77 只

        var stats = IndustryPeStatsBuilder.Build(pes, ind);

        Assert.Equal("非银金融", stats["600000"].Name);   // 保险股退到一级
        Assert.Equal(1, stats["600000"].Level);
        Assert.Equal(77, stats["600000"].SampleSize);
        Assert.Equal("证券Ⅱ", stats["610000"].Name);      // 券商股照样用二级
    }

    [Fact]
    public void 小样本行业只报中位不报分位档()
    {
        // 10~30 只：中位还算稳，P10/P90 只是"第 2 名和第 19 名"，报出来是假装精确。
        var (pes, ind) = Empty();
        Seed(pes, ind, "60", 20, 1, null, "小行业");

        var s = IndustryPeStatsBuilder.Build(pes, ind)["600000"];

        Assert.Equal(20, s.SampleSize);
        Assert.False(s.HasBands);
        Assert.Equal("", s.DescribePosition(1));           // 不给档
        Assert.Contains("样本偏少", s.Describe(1));
        Assert.Contains("中位", s.Describe(1));            // 中位照给
    }

    [Fact]
    public void 两级都不够就查不到()
    {
        // 查不到 = PE 行只显示全市场那半句，跟这个功能上线前的行为一致。
        var (pes, ind) = Empty();
        Seed(pes, ind, "60", 5, 1, "小一级", "小二级");

        Assert.Empty(IndustryPeStatsBuilder.Build(pes, ind));
    }

    [Fact]
    public void 没有行业归属的票查不到()
    {
        // 退市股、个别新股（实测 41 只）在 StockIndustryEm 里没有记录。
        var (pes, ind) = Empty();
        Seed(pes, ind, "60", 40, 1, "有行业", "有二级");
        pes["999999"] = 20;                                 // 有 PE、没行业

        var stats = IndustryPeStatsBuilder.Build(pes, ind);

        Assert.False(stats.ContainsKey("999999"));
        Assert.True(stats.ContainsKey("600000"));
    }

    [Fact]
    public void 亏损股不进样本()
    {
        // PE 为负混进来会把分位算歪；而且那些票的 PE 行本来就不显示。
        var (pes, ind) = Empty();
        Seed(pes, ind, "60", 40, 1, null, "行业");
        for (int i = 0; i < 20; i++) { pes[$"70{i:D4}"] = -50; ind[$"70{i:D4}"] = (null, "行业"); }

        var s = IndustryPeStatsBuilder.Build(pes, ind)["600000"];

        Assert.Equal(40, s.SampleSize);                     // 那 20 只亏损的没算进来
        Assert.True(s.Median > 0);
    }

    [Fact]
    public void 分位单调不降()
    {
        var (pes, ind) = Empty();
        Seed(pes, ind, "60", 100, 1, null, "行业");

        var s = IndustryPeStatsBuilder.Build(pes, ind)["600000"];

        Assert.True(s.P10 <= s.P25 && s.P25 <= s.Median && s.Median <= s.P75 && s.P75 <= s.P90);
    }

    [Fact]
    public void 措辞只说高低_不说便宜贵()
    {
        // 跟全市场那边同一条纪律：PE 行刻意钉死 Verdict.Neutral 不上色，因为 FactorLab 十分组实测
        // PE 最高那组年化 +10.6% 是十组里最高的、曲线 U 型不单调——"贵"并不预示跌。
        // 为了不暗示结论而不上色，却在文字里写"便宜"，等于白守。
        var (pes, ind) = Empty();
        Seed(pes, ind, "60", 100, 1, null, "行业");
        var s = IndustryPeStatsBuilder.Build(pes, ind)["600000"];

        var words = new[] { s.DescribePosition(1), s.DescribePosition(1000) };
        Assert.All(words, w => Assert.DoesNotContain("便宜", w));
        Assert.All(words, w => Assert.DoesNotContain("贵", w));
        Assert.Contains("最低", words[0]);
        Assert.Contains("最高", words[1]);
    }

    [Fact]
    public void 六档互不相同()
    {
        var (pes, ind) = Empty();
        Seed(pes, ind, "60", 100, 1, null, "行业");
        var s = IndustryPeStatsBuilder.Build(pes, ind)["600000"];

        var buckets = new[]
        {
            s.DescribePosition(s.P10 - 1), s.DescribePosition(s.P25 - 1),
            s.DescribePosition(s.Median - 1), s.DescribePosition(s.Median + 1),
            s.DescribePosition(s.P75 + 1), s.DescribePosition(s.P90 + 1),
        };
        Assert.Equal(6, buckets.Distinct().Count());
        Assert.All(buckets, b => Assert.False(string.IsNullOrWhiteSpace(b)));
    }

    [Fact]
    public void 参考文本带行业名和样本数()
    {
        // 界面上必须写出是哪个行业、多少只——看到"行业中位"却不知道说的是电力设备还是电池，
        // 那句话就没用。
        var (pes, ind) = Empty();
        Seed(pes, ind, "60", 42, 1, null, "银行Ⅱ");
        var s = IndustryPeStatsBuilder.Build(pes, ind)["600000"];

        var text = s.Describe(2);
        Assert.Contains("银行Ⅱ", text);
        Assert.Contains("42 只", text);
        Assert.Contains("中位", text);
    }
}
