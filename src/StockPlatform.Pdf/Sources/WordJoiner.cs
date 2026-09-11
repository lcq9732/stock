using System.Text;
using UglyToad.PdfPig.Content;

namespace StockPlatform.Pdf.Sources;

/// <summary>
/// 把 PdfPig 给的**带坐标的词**拼成人能读的行。
///
/// 抽出来是因为两个地方都要用它：<see cref="PdfPigLineSource"/> 拼整页的行，
/// <see cref="RuledTableExtractor"/> 拼单元格里的行。这两段逻辑里埋着几个量出来的阈值
/// （行容差、字间距按字宽算），复制两份迟早改漏一份。
///
/// 无状态纯函数，所以是静态的。
/// </summary>
internal static class WordJoiner
{
    /// <summary>
    /// 按基线聚成行：**降序扫过去，跟当前行基线差在容差内就并进来**，超出就另起一行。
    ///
    /// 原来用的是 GroupBy(Round(Bottom / 容差)) 那种"分桶"写法，有个致命的边界问题——
    /// 财报表格里多行单元格的**值和标签基线只差 2**（正常行距是 16），但分桶会按绝对位置切，
    /// 两个只差 2 的基线照样可能落进相邻两个桶。实测山西证券：
    ///     Bottom≈412  "193.08% 195.89% 下降2.81个百分点"   ← 值
    ///     Bottom≈410  "净稳定资金率"                        ← 标签
    /// 被切成两行后，标签那行没有数字、值那行没有标签，这个指标就永远取不到。
    /// 改成相邻聚类后两者合并、行内再按 X 排序（标签在左、值在右），自然拼成
    /// "净稳定资金率 193.08% 195.89% …"。跨行截断的标签（"自营权益类证券及证券衍生"
    /// ＋ "品/净资本"）也一并被这个改动救回来了。
    /// </summary>
    public static List<List<Word>> ClusterByBaseline(IEnumerable<Word> words, double tolerance)
    {
        var groups = new List<List<Word>>();
        foreach (var w in words.OrderByDescending(w => w.BoundingBox.Bottom))
        {
            if (groups.Count == 0
                || Math.Abs(groups[^1][0].BoundingBox.Bottom - w.BoundingBox.Bottom) > tolerance)
                groups.Add([]);
            groups[^1].Add(w);
        }
        return groups;
    }

    /// <summary>
    /// 把同一行的词按 X 排序拼成一个字符串。
    ///
    /// ⚠ 财报 PDF 里**中文是一个字一个 word** 存的（"不 良 贷 款 率" 是 5 个 word）。
    /// 无脑用空格拼会得到"不 良 贷 款 率"，标签就永远匹配不上；完全不加空格又会把
    /// 相邻两列的数字粘成"0.940.95"。所以按**字间距**判断：中文字之间几乎贴着
    /// （间距接近 0），表格列之间有明显空白，超过阈值才补一个空格。
    /// 阈值按字宽算，理由见 <see cref="PdfExtractOptions.SpaceGapRatio"/>。
    /// </summary>
    public static string JoinLine(IEnumerable<Word> lineWords, double spaceGapRatio)
    {
        var sb = new StringBuilder();
        double prevRight = double.NaN, prevWidth = 0;
        foreach (var w in lineWords.OrderBy(w => w.BoundingBox.Left))
        {
            double width = w.BoundingBox.Width;
            if (!double.IsNaN(prevRight))
            {
                double gap = w.BoundingBox.Left - prevRight;
                double threshold = Math.Min(prevWidth, width) * spaceGapRatio;
                if (gap > threshold) sb.Append(' ');
            }
            sb.Append(w.Text);
            prevRight = w.BoundingBox.Right;
            prevWidth = width;
        }
        return sb.ToString().Trim();
    }
}
