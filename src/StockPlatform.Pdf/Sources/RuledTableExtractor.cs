using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace StockPlatform.Pdf.Sources;

/// <summary>
/// 从**画了线框**的表格里还原二维单元格：按页面线段构网格，再把词落进格子。
///
/// ════ 为什么需要它 ════
/// 单靠"把页面聚成行"处理不了**窄表**——单元格里的内容换了行，一条记录就散成十几行，
/// 行与行之间的归属彻底丢失。实测华鲁恒升 2025 年报 p160 的子公司清单表：
///     纯文本行拿到的是：  "华鲁恒升（荆"  /  "州）有限公司"  /  "湖北省荆州市"  …
///     按线框还原出来的是：3 行 × 8 列，第 1 列就是完整的"华鲁恒升（荆 州）有限公司"
/// 宽表（一行一条记录，比如比亚迪 p226）不需要它，坐标聚类就够——那种页面**本来也没有线框**，
/// 本类会如实返回空。
///
/// ════ 线是怎么认出来的 ════
/// 走 <c>page.ExperimentalAccess.Paths</c>，每个子路径取包围盒，按"够长 + 够细"判横竖。
/// 实测这个判据的安全带很宽：
///     华鲁恒升 p160 —— 48 条路径 → 横线 4 条、竖线 9 条（正好围出 3 行 × 8 列）
///     比亚迪   p226 —— 1421 条路径（大多是字形填充）→ 筛剩横 2 竖 3，构不成网格
/// **不能只看描边线**：不少 PDF 的表格线是用填充的细长矩形画的，所以按包围盒判形状，
/// 不问它是 stroke 还是 fill。
///
/// ⚠ **必须剔掉不参与网格的孤线**。华鲁恒升那页有一条 Y=786 的页眉线，几何上是一条合格的
///   横线，但它离表格（Y 在 266~352）十万八千里。不剔掉的话它会凭空多切出一行巨大的空行。
///   判据是**实际相交**：横线要跟至少 2 条竖线交叉，竖线也要跟至少 2 条横线交叉，反复筛到稳定。
///
/// ════ 现在只支持"一页一张表" ════
/// 剔除孤线之后，剩下的线当成同一个网格。一页有两张表时会被并成一张（中间多出空行）。
/// 真遇到了再按 Y 方向的断裂分簇——**现在不做，因为还没有一个真实样本要求它**，
/// 凭空加的分簇阈值只是又一个没人验证过的猜测。
/// </summary>
public sealed class RuledTableExtractor : IPdfTableSource
{
    public string Name => "ruled";

    /// <inheritdoc/>
    public IReadOnlyList<PdfTable> ExtractTables(string pdfPath, PdfTableOptions options,
                                                 CancellationToken ct = default)
    {
        var tables = new List<PdfTable>();

        PdfDocument doc;
        try { doc = PdfDocument.Open(pdfPath); }
        catch { return tables; }
        using var _ = doc;

        var only = options.Pages is { Count: > 0 } ? new HashSet<int>(options.Pages) : null;

        for (int pageNo = 1; pageNo <= doc.NumberOfPages; pageNo++)
        {
            ct.ThrowIfCancellationRequested();
            if (only != null && !only.Contains(pageNo)) continue;

            // 逐页单独兜异常，理由同 PdfPigLineSource：坏的往往只是个别页的字体
            try
            {
                var t = FromPage(doc.GetPage(pageNo), options);
                if (t != null) tables.Add(t);
            }
            catch { }
        }
        return tables;
    }

    private static PdfTable? FromPage(Page page, PdfTableOptions o)
    {
        var (hLines, vLines) = CollectLines(page, o);
        if (hLines.Count < 2 || vLines.Count < 2) return null;

        (hLines, vLines) = KeepIntersecting(hLines, vLines);
        if (hLines.Count < 2 || vLines.Count < 2) return null;

        // 行边界按 Y **降序**——PDF 的 Y 轴朝上，第一行在最上面，也就是 Y 最大处
        var rowEdges = Merge(hLines.Select(l => l.Pos), o.MergeTolerance)
                       .OrderByDescending(y => y).ToList();
        var colEdges = Merge(vLines.Select(l => l.Pos), o.MergeTolerance)
                       .OrderBy(x => x).ToList();
        if (rowEdges.Count < 2 || colEdges.Count < 2) return null;

        int rows = rowEdges.Count - 1, cols = colEdges.Count - 1;

        // 把词按中心点丢进格子。用中心点而不是包围盒，是因为紧贴边线的字两边都沾，
        // 中心点只会落在一个格里，不会被数两遍。
        var buckets = new List<Word>[rows, cols];
        List<Word> words;
        try { words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList(); }
        catch { return null; }

        foreach (var w in words)
        {
            double cx = (w.BoundingBox.Left + w.BoundingBox.Right) / 2;
            double cy = (w.BoundingBox.Bottom + w.BoundingBox.Top) / 2;

            int r = IndexOfDesc(rowEdges, cy);
            int c = IndexOfAsc(colEdges, cx);
            if (r < 0 || r >= rows || c < 0 || c >= cols) continue;   // 落在表格外

            (buckets[r, c] ??= []).Add(w);
        }

        var grid = new List<IReadOnlyList<string>>(rows);
        for (int r = 0; r < rows; r++)
        {
            var row = new string[cols];
            for (int c = 0; c < cols; c++)
                row[c] = CellText(buckets[r, c], o);
            grid.Add(row);
        }
        return new PdfTable(page.Number, grid);
    }

    /// <summary>
    /// 单元格里的文字。格内可能有多行（那正是窄表的特征），按基线聚行后**用 \n 连接**——
    /// 要不要接成一个词是业务侧的事，这里不替它决定。
    /// </summary>
    private static string CellText(List<Word>? cell, PdfTableOptions o)
    {
        if (cell == null || cell.Count == 0) return "";
        var lines = WordJoiner.ClusterByBaseline(cell, o.LineTolerance)
                              .Select(g => WordJoiner.JoinLine(g, o.SpaceGapRatio))
                              .Where(t => t.Length > 0);
        return string.Join("\n", lines);
    }

    /// <summary>一条轴对齐的线：<paramref name="Pos"/> 是它所在的坐标，另两个是它的跨度。</summary>
    private readonly record struct Seg(double Pos, double Start, double End);

    private static (List<Seg> H, List<Seg> V) CollectLines(Page page, PdfTableOptions o)
    {
        var h = new List<Seg>();
        var v = new List<Seg>();

        IReadOnlyList<UglyToad.PdfPig.Graphics.PdfPath> paths;
        try { paths = page.ExperimentalAccess.Paths; }
        catch { return (h, v); }

        foreach (var path in paths)
            foreach (var sp in path)
            {
                // 按包围盒判形状，不问 stroke 还是 fill：表格线常是填充的细长矩形。
                PdfRectangle? bb;
                try { bb = sp.GetBoundingRectangle(); }
                catch { continue; }
                if (!bb.HasValue) continue;
                var r = bb.Value;

                if (r.Width >= o.MinLineLength && r.Height <= o.MaxLineThickness)
                    h.Add(new Seg((r.Bottom + r.Top) / 2, r.Left, r.Right));
                else if (r.Height >= o.MinLineLength && r.Width <= o.MaxLineThickness)
                    v.Add(new Seg((r.Left + r.Right) / 2, r.Bottom, r.Top));
            }
        return (h, v);
    }

    /// <summary>
    /// 只留**真的参与构成网格**的线：横线至少跨过 2 条竖线，竖线至少跨过 2 条横线。
    /// 反复筛到不再变化——剔掉一批之后，原本"够格"的线可能就不够了。
    ///
    /// 这一步是必须的，不是优化：华鲁恒升那页的页眉线（Y=786）几何上完全合格，
    /// 留着它就会在表格上方凭空多出一行。
    /// </summary>
    private static (List<Seg> H, List<Seg> V) KeepIntersecting(List<Seg> h, List<Seg> v)
    {
        const double Slack = 2;     // 线画得差一点点也算相交（端点常差 0.5 以内）

        for (int round = 0; round < 5; round++)
        {
            int before = h.Count + v.Count;
            var hh = h.Where(a => v.Count(b => Crosses(a, b)) >= 2).ToList();
            var vv = v.Where(b => hh.Count(a => Crosses(a, b)) >= 2).ToList();
            h = hh; v = vv;
            if (h.Count + v.Count == before || h.Count < 2 || v.Count < 2) break;
        }
        return (h, v);

        // 横线 a 在 Y=a.Pos、横跨 [a.Start, a.End]；竖线 b 在 X=b.Pos、纵跨 [b.Start, b.End]
        static bool Crosses(Seg a, Seg b)
            => b.Pos >= a.Start - Slack && b.Pos <= a.End + Slack
            && a.Pos >= b.Start - Slack && a.Pos <= b.End + Slack;
    }

    /// <summary>把坐标相近的线并成一条（取平均），避免同一条线被画两遍切出零宽的空列。</summary>
    private static List<double> Merge(IEnumerable<double> positions, double tolerance)
    {
        var sorted = positions.OrderBy(p => p).ToList();
        var result = new List<double>();
        int i = 0;
        while (i < sorted.Count)
        {
            int j = i;
            double sum = 0;
            // 跟**本组第一条**比，不是跟前一条比：否则一串间距都在容差内的线会被串成一大组
            while (j < sorted.Count && sorted[j] - sorted[i] <= tolerance) sum += sorted[j++];
            result.Add(sum / (j - i));
            i = j;
        }
        return result;
    }

    /// <summary>在降序边界里找 value 落在第几段（边界 0 和 1 之间是第 0 段）。</summary>
    private static int IndexOfDesc(List<double> edges, double value)
    {
        for (int i = 0; i < edges.Count - 1; i++)
            if (value <= edges[i] && value >= edges[i + 1]) return i;
        return -1;
    }

    /// <summary>在升序边界里找 value 落在第几段。</summary>
    private static int IndexOfAsc(List<double> edges, double value)
    {
        for (int i = 0; i < edges.Count - 1; i++)
            if (value >= edges[i] && value <= edges[i + 1]) return i;
        return -1;
    }
}
