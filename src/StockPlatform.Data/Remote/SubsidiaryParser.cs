using System.Text.RegularExpressions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Pdf;
using StockPlatform.Pdf.Sources;
using StockPlatform.Pdf.Tools;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 从年报 PDF 里解析**子公司名单**（2026-09-11）——「合并财务报表范围 / 在子公司中的权益」那张表。
///
/// ════ 为什么必须双策略 ════
/// 这张表有两种版式，一条路走不通：
///   · **窄表**（华鲁恒升）——列多、单元格内换行，一条记录散成十几行。纯文本行拿到的是
///     "华鲁恒升（荆" / "州）有限公司" / "湖北省荆州市" …，记录边界彻底没了。
///     只能按线框还原成二维单元格（RuledTableExtractor），第 1 列就是完整的名字。
///   · **宽表**（比亚迪、券商）——一行一条记录，而且那种页面**本来就没有线框**，
///     坐标聚类成行就够。
/// 实测 32 家非金融样本：23 家走线框、4 家走坐标、2 家两者都用，可用率 91%。
/// v1/v2 只有纯文本行一条路时是 ~50%，差的那一半全是窄表。
///
/// ════ 一条一定要记住的教训 ════
/// v1 的判据是"公司名后面紧跟数字"，那是**券商年报**的列序（名字→持股比例）。
/// 制造业是"名字→经营地(中文)→注册资本→…"，名字后面跟的是中文，于是整个制造业全军覆没。
/// 比亚迪从 3 家变成 13 家就是改掉这条之后的事。所以坐标那条路的判据放宽成
/// "**行首是公司名 + 这一行里有数字**"，不要求紧邻。
///
/// ════ 宁缺毋滥 ════
/// 清洗一律从严：断片（"州）有限公司"）、正文句子（"本公司持有…"）、表头（"子公司名称"）
/// 全部扔掉。产业链数据有错边比没有更糟——没有的时候你知道自己不知道，
/// 有错边的时候你会照着它做判断。
/// </summary>
public sealed class SubsidiaryParser
{
    /// <summary>
    /// 解析规则的版本。**改了规则就 +1**，落进 SubsidiaryParseState.parser_version，
    /// 任务据此重新解析已经处理过的 PDF——跟财务报表"科目集版本 v4→v5"同一套路。
    /// </summary>
    public const int ParserVersion = 1;

    /// <summary>
    /// 章节锚点。三个写法都见过，按出现频率排。
    /// 找到第一个命中的就停——同一份年报里这几个词可能同时出现，但指的是同一张表。
    /// </summary>
    private static readonly string[] Anchors =
    [
        "在子公司中的权益", "企业集团的构成", "合并财务报表范围",
    ];

    /// <summary>锚点页往后再看几页。这张表经常跨页。</summary>
    private const int PagesAfterAnchor = 4;

    private readonly PdfLineExtractor _lines;
    private readonly IPdfTableSource _tables;
    private readonly IPdfTextReader _textReader;

    public SubsidiaryParser(PdfLineExtractor lines, IPdfTableSource tables, IPdfTextReader textReader)
    {
        _lines = lines;
        _tables = tables;
        _textReader = textReader;
    }

    /// <summary>
    /// 默认装配。跟 <see cref="BankReportParser.Default"/> 同样的理由做成共享单例：
    /// 外部程序的探测结果缓存在 toolset 上，每 new 一套就要重跑一遍探测进程。
    /// </summary>
    public static SubsidiaryParser Default { get; } = CreateDefault();

    private static SubsidiaryParser CreateDefault()
    {
        var poppler = new PopplerToolset();
        return new SubsidiaryParser(
            new PdfLineExtractor(new PdfPigLineSource(), new PopplerLineSource(poppler)),
            new RuledTableExtractor(),
            new PdfPigTextReader());
    }

    /// <summary>
    /// 解析一份年报。解析不出来返回空列表（**不抛**）——版式不支持是常态，
    /// 全市场几千份里总有认不出的，那是要计数的事实，不是异常。
    /// </summary>
    public List<CompanySubsidiary> Parse(string pdfPath, string code, DateTime reportDate,
                                         string? selfFullName = null, CancellationToken ct = default)
    {
        var pages = LocateSection(pdfPath);
        if (pages.Count == 0) return [];

        var found = new Dictionary<string, (int Page, double? Pct)>(StringComparer.Ordinal);

        // ── 策略①：线框表 ────────────────────────────────────────────────────
        // ⚠ 只调**一次** ExtractTables：它内部要重新打开并解析整份 PDF，调两次就白花一倍时间。
        var tables = _tables.ExtractTables(pdfPath, new PdfTableOptions { Pages = pages }, ct);
        foreach (var table in tables)
            foreach (var (name, pct) in FromTable(table))
                found.TryAdd(name, (table.Page, pct));

        // ── 策略②：没认出表格的页，退回坐标聚类 ──────────────────────────────
        // 只补**表格没覆盖到**的页，不重复处理——两条路对同一页给出的名字会互相打架，
        // 而线框那条路更可信（它有列边界，不靠正则猜）。
        var covered = tables.Select(t => t.Page).ToHashSet();
        var rest = pages.Where(p => !covered.Contains(p)).ToList();
        if (rest.Count > 0)
        {
            foreach (var line in _lines.Extract(pdfPath, new PdfExtractOptions { Pages = rest }, ct))
            {
                var name = FromLine(line.Text);
                if (name != null) found.TryAdd(name, (line.Page, null));
            }
        }

        var me = PartnerNameMatcher.Compact(selfFullName ?? "");
        return found.Where(kv => kv.Key != me)
                    .Select(kv => new CompanySubsidiary
                    {
                        Code = code,
                        ReportDate = reportDate,
                        Name = kv.Key,
                        HoldPct = kv.Value.Pct,
                        SourcePage = kv.Value.Page,
                    })
                    .ToList();
    }

    /// <summary>
    /// 找章节在第几页。**用整份的原始页文本找**，不做坐标聚类——
    /// 定位只需要"这页提到没提到"，那是最便宜的一步，几百页也不慢。
    /// </summary>
    private List<int> LocateSection(string pdfPath)
    {
        List<PdfLine> pages;
        try { pages = _textReader.ReadPages(pdfPath).ToList(); }
        catch { return []; }
        if (pages.Count == 0) return [];

        foreach (var anchor in Anchors)
        {
            // 同一个锚点可能出现在目录页和正文里，都要看——目录页提不出东西，白跑几页而已；
            // 只取前 2 处，别被"财务报表附注"里反复出现的引用带偏。
            var hits = pages.Where(p => p.Text.Contains(anchor, StringComparison.Ordinal))
                            .Select(p => p.Page).Take(2).ToList();
            if (hits.Count == 0) continue;

            var set = new HashSet<int>();
            foreach (var h in hits)
                for (int p = h; p < h + PagesAfterAnchor && p <= pages.Count; p++)
                    set.Add(p);
            return set.OrderBy(p => p).ToList();
        }
        return [];
    }

    /// <summary>
    /// 从还原出来的二维表里取名字。
    ///
    /// 名字列：表头前两行里写着"名称"的那一列；找不到就用第 0 列——实测子公司表
    /// 第一列几乎总是名字（华鲁恒升那张 8 列表的表头是"子公司/名称"拆成两行）。
    /// </summary>
    private static IEnumerable<(string Name, double? Pct)> FromTable(PdfTable table)
    {
        int nameCol = 0, pctCol = -1;
        for (int r = 0; r < Math.Min(2, table.RowCount); r++)
            for (int c = 0; c < table.Rows[r].Count; c++)
            {
                var h = table.Rows[r][c];
                if (string.IsNullOrEmpty(h)) continue;
                if (h.Contains("名称", StringComparison.Ordinal)) nameCol = c;
                else if (pctCol < 0 && h.Contains("持股", StringComparison.Ordinal)) pctCol = c;
            }

        foreach (var row in table.Rows)
        {
            if (nameCol >= row.Count) continue;
            var name = Clean(row[nameCol]);
            if (name == null) continue;

            double? pct = null;
            if (pctCol >= 0 && pctCol < row.Count
                && double.TryParse(StripNonNumeric(row[pctCol]), out var v) && v is > 0 and <= 100)
                pct = v;

            yield return (name, pct);
        }
    }

    /// <summary>
    /// 行首是公司名、而且这一行里有数字（注册资本/持股比例之类）。
    ///
    /// ⚠ 数字**不要求紧跟在名字后面**。见类注释里 v1 的教训：紧邻那条判据是券商的列序，
    ///   制造业的名字后面跟的是中文经营地，会把整个制造业判掉。
    /// </summary>
    private static string? FromLine(string line)
    {
        if (!line.Any(char.IsAsciiDigit)) return null;
        var m = LeadingName.Match(line);
        return m.Success ? Clean(m.Groups[1].Value) : null;
    }

    private static readonly Regex LeadingName = new(
        @"^([\u4e00-\u9fffA-Za-z0-9（）()·\-\s]{4,40}?(?:公司|中心|企业|银行|厂|院|所|集团))\s",
        RegexOptions.Compiled);

    private static readonly Regex OrgSuffix = new(
        @"(股份)?有限(责任)?公司$|集团有限公司$|有限公司$|集团$|公司$|中心$|厂$|研究院$",
        RegexOptions.Compiled);

    /// <summary>开头就不对的：断片（"州）有限公司"）、编号、标点。</summary>
    private static readonly Regex BadHead = new(@"^[）)，,、。；;：:0-9]", RegexOptions.Compiled);

    /// <summary>出现这些词的一律不是公司名，是正文句子或表头。</summary>
    private static readonly string[] BadWords =
    [
        "本公司", "通过", "方式取得", "导致", "若干", "注：", "上述", "以上", "其中",
        "序号", "合计", "本期", "上期", "占该公司", "权益", "披露", "名称", "子公司",
    ];

    /// <summary>
    /// 清洗。过不了就返回 null——**宁可漏，不可错**。
    /// 长度下限 6：比这短的基本是断片（"州）有限公司"是 6 个字，但它过不了 BadHead）。
    /// 长度上限 40：比这长的基本是被当成一行的整句话。
    /// </summary>
    private static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // 单元格内的换行是排版造成的（"华鲁恒升（荆\n州）有限公司"），拼回去
        var n = PartnerNameMatcher.Compact(raw);
        if (n.Length is < 6 or > 40) return null;
        if (BadHead.IsMatch(n)) return null;
        if (BadWords.Any(w => n.Contains(w, StringComparison.Ordinal))) return null;
        if (!OrgSuffix.IsMatch(n)) return null;       // 必须以机构后缀结尾
        return n;
    }

    private static string StripNonNumeric(string s)
        => new([.. s.Where(ch => char.IsAsciiDigit(ch) || ch == '.')]);
}
