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
/// 实测 32 家非金融样本**可用率 81%**（26/32），提出 773 家子公司。
/// 只有纯文本行一条路时是 ~50%，差的那一半全是窄表。
///
/// ⚠ 别拿 91% 那个数当基线——那是 python 原型（pdfplumber）测的，本类走 PdfPig，
///   两条实现各有各的版式脾气。文档里记的必须是**产品代码**跑出来的数。
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
    public const int ParserVersion = 6;

    /// <summary>
    /// 章节锚点，**按专指程度排，不是按出现频率**。找到第一个命中的就用它，不再往下试。
    ///
    /// ⚠ 顺序是踩出来的：「企业集团的构成」是那张名单表**自己的标题**，专指；
    ///   而「在子公司中的权益」是一整节的名字，节里还有关联方之类别的表，所以会多处命中。
    ///   原来把后者排在前面，长电科技(600584)就中招了——它命中 p181 和 p193，
    ///   而 p193 那一带是关联方章节，于是"华润(集团)有限公司"被当成了它的子公司
    ///   （华润是它的**股东**，方向正好反了）。换成专指的锚点优先，p193 就不会被选中。
    /// </summary>
    private static readonly string[] Anchors =
    [
        "企业集团的构成", "在子公司中的权益", "合并财务报表范围",
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
            // 取前 2 处：名单经常跨页，第二处往往是续表（中国建筑那种几百家子公司的，
            // 名单本身就要好几页，而且中间会被别的附注打断）。
            //
            // ⚠ 只取 1 处试过，代价太大：子公司 814→461、能连出的新边 402→225，
            //   中国建筑（贡献最多的那家）整个消失。
            //
            // ⚠⚠ 但第二处**经常落进关联交易章节**，那里列的是关联方——同集团的兄弟公司、
            //   控股股东——不是子公司。实测三家都中招：
            //     600271 航天信息：没有"企业集团的构成"这个标题，退到"在子公司中的权益"，
            //                      命中 p186 和 p199，而 p198 就是关联交易 → 航天科工系的
            //                      兄弟公司（武汉磁电、海鹰机电、梅岭电源…）全成了它的子公司
            //     601678 滨化股份：p181 **同时**是"企业集团的构成"和"关联交易" →
            //                      北京首都旅游集团、中海沥青都被算了进来
            //     600584 长电科技：华润(集团)——它是**股东**，方向正好反了
            //   所以要拦的是**关联方那种页**，不是"第二处"这个位置。
            var hits = pages
                .Where(p => p.Text.Contains(anchor, StringComparison.Ordinal))
                // ① 锚点页自己就含"关联交易"（滨化 p181 那种）
                .Where(p => !IsRelatedParty(p.Text))
                // ② 锚点**落在关联交易章节内部**（航天信息 p199 那种）——标题在上一页 p198，
                //    这一页自己不含那四个字，只判当页会整段漏过去。看前一页才拦得住。
                .Where(p => p.Page < 2 || !IsRelatedParty(pages[p.Page - 2].Text))
                .Select(p => p.Page).Take(2).ToList();
            if (hits.Count == 0) continue;

            var set = new HashSet<int>();
            foreach (var h in hits)
                for (int p = h; p < h + PagesAfterAnchor && p <= pages.Count; p++)
                {
                    // ③ 往后扫时遇到关联交易章节就停——名单到此为止
                    if (IsRelatedParty(pages[p - 1].Text)) break;
                    set.Add(p);
                }
            if (set.Count == 0) continue;
            return set.OrderBy(p => p).ToList();
        }
        return [];
    }

    /// <summary>
    /// 这一页是不是**关联方/关联交易**章节。是的话它列的是同集团兄弟公司和股东，
    /// 不是子公司，一个字都不能要。
    /// </summary>
    private static bool IsRelatedParty(string pageText)
        => pageText.Contains("关联交易", StringComparison.Ordinal)
        || pageText.Contains("关联方及关联交易", StringComparison.Ordinal);

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

    /// <summary>
    /// 去掉机构后缀后**只剩这些词**的，是被截断的残片，不是公司名。
    /// 判的是"剩余部分正好等于某个行业通用词"，不是"包含"——
    /// "华东医药"含"医药"但它是专名，"医药有限公司"剥完正好剩"医药"，那才是断片。
    /// </summary>
    private static readonly HashSet<string> GenericStems = new(StringComparer.Ordinal)
    {
        "科技", "信息", "医药", "实业", "贸易", "投资", "发展", "工业", "能源",
        "材料", "新材料", "化工", "电子", "机械", "建设", "置业", "物流", "咨询",
        "服务", "技术", "网络", "软件", "数据", "环保", "生物", "电气", "设备",
        "制造", "销售", "商贸", "供应链", "文化", "传媒", "教育", "医疗", "健康",
        "金融", "资本", "资产", "管理", "运营", "工程", "设计", "研究", "检测",
    };

    /// <summary>出现这些词的一律不是公司名，是正文句子或表头。</summary>
    private static readonly string[] BadWords =
    [
        "本公司", "通过", "方式取得", "导致", "若干", "注：", "上述", "以上", "其中",
        "序号", "合计", "本期", "上期", "占该公司", "权益", "披露", "名称", "子公司",
    ];

    /// <summary>
    /// 清洗。过不了就返回 null——**宁可漏，不可错**。
    ///
    /// ⚠ **公开是为了能直接测**（跟 EastMoneyCompanyProfileProvider.Parse 同样的理由）。
    ///   依赖样本的测试证明不了这里的判据：断片规则先写成"去后缀后不足 4 字就扔"，
    ///   32 份样本测试全绿，可它会杀掉"华纺股份有限公司"（剩"华纺"）——样本里恰好
    ///   没有这类公司而已。纯函数就该喂字符串直接测。
    /// 长度下限 6：比这短的基本是断片（"州）有限公司"是 6 个字，但它过不了 BadHead）。
    /// 长度上限 40：比这长的基本是被当成一行的整句话。
    /// </summary>
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // 单元格内的换行是排版造成的（"华鲁恒升（荆\n州）有限公司"），拼回去
        var n = PartnerNameMatcher.Compact(raw);
        if (n.Length is < 6 or > 40) return null;
        if (BadHead.IsMatch(n)) return null;
        if (BadWords.Any(w => n.Contains(w, StringComparison.Ordinal))) return null;
        if (!OrgSuffix.IsMatch(n)) return null;       // 必须以机构后缀结尾

        // ⚠ **去掉机构后缀之后得剩下专名**，否则是被排版截断的残片。
        //
        // 真机跑出来的坑："信息有限公司"(母 600271)、"医药有限公司"(母 600998)、
        // "科技有限公司"(母 688009) 都落了库 —— 它们总长刚好 6 字，压着下限过去了，
        // 可去掉"有限公司"只剩"信息""医药""科技"，全是行业通用词，没有任何专名。
        //
        // ⚠⚠ 判据**不能用长度**。先写成"去后缀后 < 4 字就扔"，当场误杀正规公司：
        //     华纺股份有限公司 → 剩"华纺"(2 字)   ← 600448，真公司
        //     比亚迪股份有限公司 → 剩"比亚迪"(3 字)
        //   "XX股份有限公司"这种标准全称，剥掉后缀本来就只剩两三个字的品牌名。
        //   32 份样本里恰好没撞上，测试还过了——全市场跑必然出事。
        //
        // 真正的区别是**剩下的是专名还是行业通用词**："华纺"是专名，"科技"不是。
        // 所以改成黑名单——短，但覆盖了实际见到的全部断片。
        var stem = OrgSuffix.Replace(n, "");
        if (stem.Length < 2 || GenericStems.Contains(stem)) return null;

        // ⚠ **括号不配对 = 断片**。"州）有限公司" 是最典型的那个
        //   （"华鲁恒升（荆州）有限公司"被排版截成了两半），可它能过掉上面所有判据：
        //   开头"州"不是标点、总长 6 字、以"有限公司"结尾、去后缀剩"州）"也不是通用词。
        //   右括号比左括号多，就说明前半截连着左括号一起被截掉了。
        if (CountAny(n, '）', ')') > CountAny(n, '（', '(')) return null;

        return n;
    }

    private static int CountAny(string s, char a, char b)
        => s.Count(ch => ch == a || ch == b);

    private static string StripNonNumeric(string s)
        => new([.. s.Where(ch => char.IsAsciiDigit(ch) || ch == '.')]);
}
