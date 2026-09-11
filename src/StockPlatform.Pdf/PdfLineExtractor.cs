namespace StockPlatform.Pdf;

/// <summary>
/// 兜底链的**组合器**：按给定顺序挨个试 <see cref="IPdfLineSource"/>，第一个拿到行的赢。
///
/// ════ 为什么单独一个类 ════
/// 它是**协调者**，不是又一条提取路子——所以自己一份 PDF 也不碰，一个字节也不解析。
/// 拆分前这段协调逻辑写死在 BankReportParser.ExtractLines 里（"PdfPig 空了就调 pdftotext"），
/// 混在业务解析类里污染了它的职责。抽出来之后：
///   · 各 source  ── 我负责把 PDF 变成行
///   · 组合器      ── 我负责排兜底顺序
///   · 业务解析类  ── 我负责给页面判据、从行里取数，不知道底下有几级兜底
///
/// 顺序变成了**构造参数**，于是调顺序、加一级、测试时只塞一个 source，都不用改代码。
///
/// ════ 这里**不放** OCR ════
/// OCR 不是"前面失败了就试下一个"，而是"文本层拿到了、但数字被转成了矢量图形"时的二次补充；
/// 触发条件和该 OCR 哪几页都是业务判断（见 BankReportParser 的 LooksLikeDigitsStripped /
/// CandidatePages）。把那种判断塞进组合器，就等于又把业务知识漏进了协调层。
/// 所以 TesseractLineSource 是一个独立的 source，由业务侧按需直接调。
/// </summary>
public sealed class PdfLineExtractor
{
    private readonly IReadOnlyList<IPdfLineSource> _sources;

    /// <param name="sources">按尝试顺序给。空数组会让每次提取都返回空列表。</param>
    public PdfLineExtractor(params IPdfLineSource[] sources)
        => _sources = sources ?? [];

    /// <summary>这条链上的 source 名字，按尝试顺序。给日志和诊断用。</summary>
    public IEnumerable<string> SourceNames => _sources.Select(s => s.Name);

    /// <summary>
    /// 挨个试，返回第一个非空结果；全都没结果时返回空列表（不抛）。
    /// <paramref name="usedSource"/> 回传实际出结果的那条路的名字，全空时是空串。
    /// </summary>
    public IReadOnlyList<PdfLine> Extract(string pdfPath, PdfExtractOptions options,
                                          out string usedSource, CancellationToken ct = default)
    {
        foreach (var s in _sources)
        {
            ct.ThrowIfCancellationRequested();
            if (!s.IsAvailable) continue;

            // 某条路自己出错不该让整条链报废——下一条可能正好能读。
            // （实测就是这么用的：PdfPig 遇到缺 CMap 的 CJK 字体会抛，pdftotext 一转就出来。）
            IReadOnlyList<PdfLine>? lines;
            try { lines = s.TryExtract(pdfPath, options, ct); }
            catch (OperationCanceledException) { throw; }
            catch { continue; }

            if (lines is { Count: > 0 })
            {
                usedSource = s.Name;
                return lines;
            }
        }
        usedSource = "";
        return [];
    }

    /// <summary>不关心用了哪条路时的简写。</summary>
    public IReadOnlyList<PdfLine> Extract(string pdfPath, PdfExtractOptions options,
                                          CancellationToken ct = default)
        => Extract(pdfPath, options, out _, ct);

    /// <summary>
    /// 整条链都不可用时的说明（比如一台没装 poppler 的机器上，PdfPig 又读不动这份 PDF）。
    /// 有任何一条可用就返回空串。
    /// </summary>
    public string UnavailableReason
        => _sources.Any(s => s.IsAvailable)
            ? ""
            : string.Join("；", _sources.Select(s => $"{s.Name}: {s.UnavailableReason}"));
}
