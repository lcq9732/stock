namespace StockPlatform.Pdf;

/// <summary>
/// 一张还原出来的表格。<paramref name="Rows"/> 是二维的单元格文字，行优先。
///
/// ⚠ **单元格里的换行用 "\n" 保留，不合并**。年报里公司名经常被排版拆开
/// （"华鲁恒升（荆" / "州）有限公司"），要不要把它接成"华鲁恒升（荆州）有限公司"、
/// 接的时候空格怎么处理，那是业务侧的判断——工具层如实给出结构，不替它决定。
/// </summary>
public sealed record PdfTable(int Page, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public int RowCount => Rows.Count;
    public int ColCount => Rows.Count == 0 ? 0 : Rows[0].Count;

    /// <summary>越界返回 null，省得调用方每次都判一遍。</summary>
    public string? this[int row, int col]
        => row >= 0 && row < Rows.Count && col >= 0 && col < Rows[row].Count ? Rows[row][col] : null;
}

/// <summary>
/// 找表格线的参数。跟 <see cref="PdfExtractOptions"/> 分开，因为它们量的是不同的东西：
/// 那边量字和字的距离，这边量线段。
/// </summary>
public sealed record PdfTableOptions
{
    /// <summary>只看这几页（1 起）。null = 全部。整份年报三百页，别整份扫。</summary>
    public IReadOnlyList<int>? Pages { get; init; }

    /// <summary>
    /// 够长才算表格线（PDF 单位）。实测华鲁恒升 p160 的表格线长 50~450，
    /// 而字形轮廓里的碎线段都在个位数——20 落在中间，很宽的安全带。
    /// </summary>
    public double MinLineLength { get; init; } = 20;

    /// <summary>
    /// 够细才算线（不是方块）。表格线画出来通常不到 1pt。
    /// 实测比亚迪 p226 有 1421 个填充矩形（字形之类），靠"长且细"这一条筛剩 15 个。
    /// </summary>
    public double MaxLineThickness { get; init; } = 2;

    /// <summary>
    /// 合并重复线的坐标容差。同一条表格线常被画两遍（描边 + 填充，或相邻单元格各画一次），
    /// 不合并的话会多切出一堆零宽的空列。
    /// </summary>
    public double MergeTolerance { get; init; } = 2;

    /// <summary>单元格内按基线聚行的容差，跟 <see cref="PdfExtractOptions.LineTolerance"/> 同理。</summary>
    public double LineTolerance { get; init; } = 5.0;

    /// <summary>单元格内补空格的阈值，跟 <see cref="PdfExtractOptions.SpaceGapRatio"/> 同理。</summary>
    public double SpaceGapRatio { get; init; } = 0.3;

    public static PdfTableOptions Default { get; } = new();
}

/// <summary>
/// 一条"把 PDF 里的表格还原成二维单元格"的路子。
///
/// 跟 <see cref="IPdfLineSource"/> 分开，是因为两者的产出根本不是一回事：那边是一维的行，
/// 这边是二维的格。硬塞进同一个接口，两边都得别扭地假装自己是对方。
/// </summary>
public interface IPdfTableSource
{
    string Name { get; }

    /// <summary>还原表格。一张都没找到时返回空列表（不抛）。</summary>
    IReadOnlyList<PdfTable> ExtractTables(string pdfPath, PdfTableOptions options,
                                          CancellationToken ct = default);
}
