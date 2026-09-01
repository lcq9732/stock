using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StockPlatform.Logic.Models;
using UglyToad.PdfPig;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 从银行财报 PDF 里解析监管指标（2026-08-29 新增）——不良率、拨备覆盖率、核心一级资本充足率、
/// 客户集中度、迁徙率等。这些数三张报表接口一个都没有（实测新浪财务指标页 265 个字段、同花顺
/// 财务主表 20 个科目，都是通用工商企业模板），只能从财报正文取。
///
/// ════ 为什么可行 ════
/// 招行 2026 中报 309 页，但要的东西全在第 6~9 页的「会计数据和财务指标摘要」一章，而且所有
/// 上市银行按监管要求披露同一套指标、表格结构基本一致。所以是"定位两三页 + 按标签取数"，
/// 不是"解析 309 页"。
///
/// ════ 怎么把表格还原成行 ════
/// PdfPig 给的是带坐标的词，不是行。表格文字直接 <c>page.Text</c> 连起来会串行，所以按**基线 Y
/// 坐标聚类**成行、每行内按 X 排序拼接——这样"不良贷款率 0.94 0.94 – 0.95"才是完整一行，
/// 才能确定 0.94 是它的值而不是隔壁行的。
///
/// ════ 三个容易踩的坑 ════
/// ① **高级法 vs 权重法**：招行同时披露两套资本充足率（14.07% / 11.84%），只有六大行+招行等
///    少数获批高级法。横向比较必须用权重法，混着存会把行业分位算歪。表内值按表头判断口径，
///    注释里的"权重法下…分别为 X%、Y% 和 Z%"用单独的正则捞。
/// ② **标准值列**：流动性比例那张表自带"标准值 ≥25"，这是体检表"参考值"最权威的来源，
///    要跟着值一起存下来。
/// ③ **同名多处**：同一指标可能有"本集团/本公司"两套。取**第一次命中**——各行财报都是集团
///    口径在前，跟对外披露的口径一致。
/// </summary>
public static class BankReportParser
{
    /// <summary>
    /// 同一行的基线 Y 容差（PDF 单位）。取 5.0 是量出来的：财报表格里多行单元格的值与标签
    /// 基线只差约 2，而正常行距在 16 上下——5 落在两者中间，既能把被排版拆开的值和标签并回
    /// 一行，又不会把相邻两行粘在一起。
    /// </summary>
    private const double LineTolerance = 5.0;

    /// <summary>标签 → metric_key。按长的在前排——"关注类贷款迁徙率"必须先于"关注类"匹配上。</summary>
    private static readonly (string Label, string Key)[] BankLabels =
    [
        ("不良贷款率", BankRegulatoryKeys.NplRatio),
        ("贷款拨备率", BankRegulatoryKeys.LoanProvisionRatio),
        ("拨备覆盖率", BankRegulatoryKeys.ProvisionCoverage),
        ("信用成本", BankRegulatoryKeys.CreditCost),
        ("核心一级资本充足率", BankRegulatoryKeys.CoreTier1Car),
        ("一级资本充足率", BankRegulatoryKeys.Tier1Car),
        ("资本充足率", BankRegulatoryKeys.TotalCar),
        ("净利息收益率", BankRegulatoryKeys.Nim),
        ("净息差", BankRegulatoryKeys.Nim),
        ("净利差", BankRegulatoryKeys.NetInterestSpread),
        ("成本收入比", BankRegulatoryKeys.CostIncomeRatio),
        ("单一最大客户贷款和垫款比例", BankRegulatoryKeys.Top1LoanRatio),
        ("单一最大客户贷款比例", BankRegulatoryKeys.Top1LoanRatio),
        ("前十大客户贷款和垫款比例", BankRegulatoryKeys.Top10LoanRatio),
        ("前十大客户贷款比例", BankRegulatoryKeys.Top10LoanRatio),
        ("流动性覆盖率", BankRegulatoryKeys.Lcr),
        ("流动性比例", BankRegulatoryKeys.LiquidityRatio),
        ("正常类贷款迁徙率", BankRegulatoryKeys.NormalMigration),
        ("关注类贷款迁徙率", BankRegulatoryKeys.SpecialMentionMigration),
        ("次级类贷款迁徙率", BankRegulatoryKeys.SubstandardMigration),
        ("可疑类贷款迁徙率", BankRegulatoryKeys.DoubtfulMigration),
    ];

    /// <summary>
    /// 券商的风险控制指标（《证券公司风险控制指标管理办法》，年报/中报统一披露）。
    /// ⚠ 标签里的斜杠在 PDF 里两侧带空格（"净资本 / 净资产（%）"），靠标签匹配时允许字符间
    /// 有空白来兼容，见 LabelRegex。长标签必须排在短标签前面——"净资本/净资产"要先于"净资本"。
    /// </summary>
    private static readonly (string Label, string Key)[] BrokerLabels =
    [
        ("自营权益类证券及证券衍生品/净资本", BrokerRegulatoryKeys.EquityPropToNetCapital),
        ("自营权益类证券及其衍生品/净资本", BrokerRegulatoryKeys.EquityPropToNetCapital),
        ("自营非权益类证券及其衍生品/净资本", BrokerRegulatoryKeys.NonEquityPropToNetCapital),
        ("自营非权益类证券及证券衍生品/净资本", BrokerRegulatoryKeys.NonEquityPropToNetCapital),
        // 山西证券等把这一项写作"自营固定收益类证券/净资本"——同一个监管指标的另一种叫法
        ("自营固定收益类证券/净资本", BrokerRegulatoryKeys.NonEquityPropToNetCapital),
        ("自营固定收益类证券及其衍生品/净资本", BrokerRegulatoryKeys.NonEquityPropToNetCapital),
        ("净资本/各项风险准备之和", BrokerRegulatoryKeys.RiskCoverage),
        ("净资本/净资产", BrokerRegulatoryKeys.NetCapitalToNetAssets),
        ("净资本/负债", BrokerRegulatoryKeys.NetCapitalToLiabilities),
        ("风险覆盖率", BrokerRegulatoryKeys.RiskCoverage),
        ("资本杠杆率", BrokerRegulatoryKeys.CapitalLeverage),
        ("净稳定资金率", BrokerRegulatoryKeys.NetStableFunding),
        ("流动性覆盖率", BrokerRegulatoryKeys.LiquidityCoverage),
    ];

    /// <summary>
    /// 保险的偿付能力与承保指标。集团型公司会同时披露集团口径和各子公司口径，取第一次命中
    /// （集团在前），见 InsurerRegulatoryKeys 的类注释。
    /// </summary>
    private static readonly (string Label, string Key)[] InsurerLabels =
    [
        ("车险综合成本率", InsurerRegulatoryKeys.AutoCombinedRatio),
        ("核心偿付能力充足率", InsurerRegulatoryKeys.CoreSolvency),
        ("综合偿付能力充足率", InsurerRegulatoryKeys.ComprehensiveSolvency),
        ("综合成本率", InsurerRegulatoryKeys.CombinedRatio),
    ];

    /// <summary>按机构类型选标签集。非金融/未细分的金融机构没有统一披露的监管指标，返回空。</summary>
    private static (string Label, string Key)[] LabelsFor(FinancialInstitutionKind kind) => kind switch
    {
        FinancialInstitutionKind.Bank => BankLabels,
        FinancialInstitutionKind.Broker => BrokerLabels,
        FinancialInstitutionKind.Insurer => InsurerLabels,
        _ => [],
    };

    /// <summary>
    /// 标签的预编译正则：**字符之间允许空白**。两个必须这么做的理由——
    /// ① 券商的"净资本 / 净资产（%）"斜杠两侧有空格，直接 IndexOf 匹配不到；
    /// ② 少数 PDF 的字间距会让拼接逻辑多插空格，允许空白顺带提高鲁棒性。
    /// \s* 只吃空白、不会跨越其它文字，所以不会误匹配到远处的词。
    /// </summary>
    private static readonly Dictionary<string, Regex> LabelRegex = new();

    private static Regex RegexFor(string label)
    {
        if (LabelRegex.TryGetValue(label, out var re)) return re;
        // 标签里的 / 编译成"可有可无"。PDF 里斜杠字符的 X 坐标常常有偏差，按坐标排序拼行时
        // 会跑到别的位置——实测华林证券把"净资本/负债"排成了"净资本负债/"、
        // "生品/净资本"排成了"生品净资本/"，一个字符错位就整项匹配不上。
        // 去掉斜杠后各标签依然互不相同（净资本负债 / 净资本净资产 / 净资产负债），不会误判。
        var pattern = string.Join(@"\s*",
            label.Select(ch => ch == '/' ? "[/]?" : Regex.Escape(ch.ToString())));
        re = new Regex(pattern, RegexOptions.Compiled);
        lock (LabelRegex) LabelRegex[label] = re;
        return re;
    }

    /// <summary>
    /// 繁体→简体（只覆盖指标标签会用到的字）。
    ///
    /// 为什么需要：不少银行披露的是**H 股版年报，正文是繁体**——交通银行、青岛银行的表格里写的是
    /// "不良貸款率"（貸）而不是"不良贷款率"，简体标签一个都匹配不上，整份报告解析为 0 个指标。
    /// PdfPig 报的 <c>Adobe-CNS1-7</c> 里 CNS1 正是繁体字符集（GB1 才是简体），两件事同源。
    ///
    /// 不引繁简转换库、只列标签用得到的字：这些字繁简一一对应、没有"后/後""发/發髮"那类一对多
    /// 歧义，映射是安全的；简体文本里不含这些繁体字，转换对它无影响。
    /// </summary>
    private static readonly Dictionary<char, char> TradToSimp = new()
    {
        ['貸'] = '贷', ['撥'] = '拨', ['備'] = '备', ['蓋'] = '盖', ['資'] = '资',
        ['級'] = '级', ['淨'] = '净', ['單'] = '单', ['戶'] = '户', ['墊'] = '垫',
        ['動'] = '动', ['類'] = '类', ['遷'] = '迁', ['關'] = '关', ['風'] = '风',
        ['險'] = '险', ['槓'] = '杠', ['桿'] = '杆', ['穩'] = '稳', ['產'] = '产',
        ['債'] = '债', ['營'] = '营', ['權'] = '权', ['證'] = '证', ['償'] = '偿',
        ['綜'] = '综', ['車'] = '车', ['項'] = '项', ['準'] = '准', ['額'] = '额',
        ['負'] = '负', ['應'] = '应', ['業'] = '业', ['務'] = '务', ['總'] = '总',
        ['個'] = '个', ['點'] = '点', ['較'] = '较', ['減'] = '减', ['擔'] = '担',
        ['計'] = '计', ['實'] = '实', ['數'] = '数', ['報'] = '报', ['產'] = '产',
        ['營'] = '营', ['蓋'] = '盖', ['覆'] = '覆', ['詳'] = '详', ['釋'] = '释',
    };

    /// <summary>把一行里的繁体字换成简体，供标签匹配用（取数仍用原文，数字不受影响）。</summary>
    private static string Normalize(string line)
    {
        if (!line.Any(TradToSimp.ContainsKey)) return line;   // 绝大多数行是简体，直接返回
        var sb = new StringBuilder(line.Length);
        foreach (var ch in line) sb.Append(TradToSimp.TryGetValue(ch, out var s) ? s : ch);
        return sb.ToString();
    }

    /// <summary>
    /// 每个指标的合理区间（%）。取值宽松——目的是挡掉页码、年份、金额这类明显不是指标的数，
    /// 不是替代人工判断。边界参照 42 家上市银行的实际分布再往外放一档。
    /// 迁徙率可以超过 100%（次级类转下迁的比例常年 100%+），单独给大区间。
    /// </summary>
    private static readonly Dictionary<string, (double Min, double Max)> Ranges = new()
    {
        [BankRegulatoryKeys.NplRatio] = (0.1, 6),      // 监管红线 5%，A股实际 0.5~2%
        [BankRegulatoryKeys.LoanProvisionRatio] = (0.5, 15),
        [BankRegulatoryKeys.ProvisionCoverage] = (30, 2000),
        [BankRegulatoryKeys.CreditCost] = (0, 10),
        [BankRegulatoryKeys.CoreTier1Car] = (4, 30),
        [BankRegulatoryKeys.Tier1Car] = (4, 32),
        [BankRegulatoryKeys.TotalCar] = (6, 35),
        [BankRegulatoryKeys.Nim] = (0.1, 8),
        [BankRegulatoryKeys.NetInterestSpread] = (0.1, 8),
        [BankRegulatoryKeys.CostIncomeRatio] = (5, 80),
        [BankRegulatoryKeys.Top1LoanRatio] = (0, 30),
        [BankRegulatoryKeys.Top10LoanRatio] = (0, 80),
        [BankRegulatoryKeys.LiquidityRatio] = (10, 400),
        [BankRegulatoryKeys.Lcr] = (50, 2000),
        [BankRegulatoryKeys.NormalMigration] = (0, 500),
        [BankRegulatoryKeys.SpecialMentionMigration] = (0, 500),
        [BankRegulatoryKeys.SubstandardMigration] = (0, 500),
        [BankRegulatoryKeys.DoubtfulMigration] = (0, 500),

        // 券商：前六项是"越高越安全"的下限型指标，后两项是自营规模上限（监管 ≤100% / ≤500%）。
        [BrokerRegulatoryKeys.RiskCoverage] = (50, 3000),
        [BrokerRegulatoryKeys.CapitalLeverage] = (4, 80),
        [BrokerRegulatoryKeys.LiquidityCoverage] = (50, 2000),
        [BrokerRegulatoryKeys.NetStableFunding] = (50, 1000),
        // 上限不能卡在 100：**净资本可以超过净资产**——附属净资本（次级债）会把它推上去。
        // 实测财达证券 101.11%，被原来的 (10,100) 直接拒掉了。
        [BrokerRegulatoryKeys.NetCapitalToNetAssets] = (10, 200),
        [BrokerRegulatoryKeys.NetCapitalToLiabilities] = (4, 200),
        [BrokerRegulatoryKeys.EquityPropToNetCapital] = (0, 200),
        [BrokerRegulatoryKeys.NonEquityPropToNetCapital] = (0, 800),

        // 保险：偿付能力充足率监管下限 50%/100%，实际值多在 150%~400%。
        [InsurerRegulatoryKeys.CoreSolvency] = (30, 1000),
        [InsurerRegulatoryKeys.ComprehensiveSolvency] = (50, 1000),
        [InsurerRegulatoryKeys.CombinedRatio] = (60, 160),
        [InsurerRegulatoryKeys.AutoCombinedRatio] = (60, 160),
    };

    /// <summary>
    /// 一页至少要命中几个指标名才算"指标表页"。
    /// 银行有 21 个标签，要求 2 个很容易满足；但**保险只有 4 个标签**，同一页同时出现两个的
    /// 概率低得多——中国人保就是这么被整份过滤掉的（pdftotext 明明提出了 7 万多汉字）。
    /// 所以标签集小的时候把门槛降到 1，靠后面的"标签必须在行首 + 标签到值的距离"两道约束防误命中。
    /// </summary>
    private static int MinLabelHits((string Label, string Key)[] labels) => labels.Length <= 6 ? 1 : 2;

    /// <summary>只在这些字样出现过的页里找——避免在正文叙述段落里误命中一个同名的数。</summary>
    private static readonly string[] SectionHints =
    [
        "财务指标", "补充财务比率", "资产质量指标", "资本充足率指标", "迁徙率指标",
        "主要会计数据", "补充财务指标",
    ];

    /// <summary>"权重法下核心一级资本充足率、一级资本充足率和资本充足率分别为11.84%、13.96%和15.06%"</summary>
    private static readonly Regex WeightedNote = new(
        @"权重法下[^。]*?分别为\s*([\d.]+)\s*%[、,，]\s*([\d.]+)\s*%[和及]\s*([\d.]+)\s*%",
        RegexOptions.Compiled);

    /// <summary>一行文字 + 它所在页码。</summary>
    private readonly record struct PdfLine(int Page, string Text);

    /// <summary>
    /// 某个 metric_key 在财报里可能的所有写法。给「待手工回填清单」判断
    /// **这家公司到底披没披露这一项**用：PDF 全文里连提都没提，就说明它没有这项指标，
    /// 不该让人去翻一个根本不存在的数（典型例子：纯寿险公司没有"综合成本率"，
    /// 那是财险指标；不少银行也不披露"单一最大客户贷款比例"）。
    /// </summary>
    public static string[] LabelsOf(string metricKey) =>
        BankLabels.Concat(BrokerLabels).Concat(InsurerLabels)
                  .Where(l => l.Key == metricKey)
                  .Select(l => l.Label)
                  .ToArray();

    /// <summary>
    /// 粗判这份 PDF 是不是**年报/中报正文**。
    ///
    /// 用来兜住"标题过滤没拦住、下错了文件"的情况：问询函回复、募集资金专项报告这些同样
    /// 含"年度报告"四个字，里面却没有任何监管指标表，翻遍也找不到数——实测 353 份里有 9 份
    /// 是这么下错的，白白进了手工回填清单让人去翻。判据是正文前几页有没有年报的结构特征。
    ///
    /// **读不出文本的一律返回 true**——那是字体问题（比如中国人保），跟"下错文件"是两回事，
    /// 误删了还得重新下 6MB。
    /// </summary>
    public static bool LooksLikeReport(string pdfPath)
    {
        string head;
        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            var sb = new StringBuilder();
            for (int i = 1; i <= Math.Min(3, doc.NumberOfPages); i++)
            {
                try { sb.Append(doc.GetPage(i).Text); } catch { }
            }
            head = Normalize(sb.ToString());
        }
        catch { return true; }

        if (head.Length < 50) return true;   // 读不出来，交给别的环节判断

        string[] structure =
        [
            "目录", "重要提示", "第一节", "第一章", "公司基本情况",
            "会计数据和财务指标", "释义", "董事会报告", "管理层讨论",
        ];
        return structure.Any(k => head.Contains(k, StringComparison.Ordinal));
    }

    /// <summary>
    /// 这份 PDF 里**确实披露了数值**的指标 key。读不出文本、或数字被转曲（无从判断）时返回 null。
    ///
    /// ════ 为什么不能只看"指标名出没出现" ════
    /// 「待手工回填清单」原来的判据是"全文里提到过这个指标名就算披露了"，结果让人白跑腿：
    /// 瑞丰银行 2024 年报（实际是**财务报表分册**，274 页）里，"核心一级资本充足率"只在
    /// 附注的一句话里出现过——
    ///     "截至 2024 年 12 月 31 日，本集团及本行核心一级资本充足率、一级资本充足率及资本
    ///      充足率均满足《商业银行资本管理办法》…请参见本行网站披露的《资本管理第三支柱
    ///      信息披露报告》"
    /// ——**通篇没有这个数**（它在另一份第三支柱报告里）。清单照样把它列出来让人去找，
    /// 翻遍全文只能找到这句话。
    ///
    /// ════ 判据 ════
    /// 标签**之后**有数字才算数：同一行标签后面有数字，或紧接的下一行（同页）有数字。
    /// 跟 ScanLines 的取值逻辑一致——那边取不到值的位置，这边也不该说"披露了"。
    /// 上面那句话里标签后面跟的是"、一级资本充足率及资本充足率均满足《…》"，下一行也没有数，
    /// 于是判定为未披露；而封面图表那种"标签一行、数值在下一行"的排版仍然算披露。
    /// </summary>
    public static HashSet<string>? DisclosedKeys(string pdfPath)
    {
        var all = BankLabels.Concat(BrokerLabels).Concat(InsurerLabels).ToArray();
        List<PdfLine> lines;
        try { lines = ExtractLines(pdfPath, all); }
        catch { return null; }
        if (lines.Count == 0) return null;
        // 数字被转曲的 PDF 文本层里根本没有数，这个判据对它没有意义——返回 null 让调用方
        // 保守处理（宁可多列一项让人核，也别把该补的数悄悄漏掉）。
        if (LooksLikeDigitsStripped(lines)) return null;

        var found = new HashSet<string>();
        for (int i = 0; i < lines.Count; i++)
        {
            var text = Normalize(lines[i].Text);
            // 下一行只在同一页才算——跨页拼接没有意义
            var next = i + 1 < lines.Count && lines[i + 1].Page == lines[i].Page
                ? lines[i + 1].Text : "";
            foreach (var (label, key) in all)
            {
                if (found.Contains(key)) continue;
                foreach (Match m in RegexFor(label).Matches(text))
                {
                    var rest = text[(m.Index + m.Length)..];
                    if (rest.Any(char.IsAsciiDigit) || next.Any(char.IsAsciiDigit))
                    {
                        found.Add(key);
                        break;
                    }
                }
            }
        }
        return found;
    }

    /// <summary>
    /// 解析一份财报 PDF。<paramref name="reportDate"/> 是这份报告的报告期（决定指标归属哪一期）。
    /// 抛异常由调用方记进 BankReportFetchState，不在这里吞掉。
    /// </summary>
    /// <param name="allowOcr">
    /// 允不允许在"数字被转曲"时走 OCR 兜底。默认允许；重解析本地缓存时，若这一期的指标
    /// 人已经全部核对过了，调用方会传 false——OCR 一份要一分钟，跑出来的结果反正也覆盖不了
    /// 人拍板的值，没必要再花这个时间。
    /// </param>
    public static List<BankRegulatoryMetric> Parse(string pdfPath, string code, DateTime reportDate,
        FinancialInstitutionKind kind = FinancialInstitutionKind.Bank,
        Action<string>? progress = null, CancellationToken ct = default, bool allowOcr = true)
    {
        var labels = LabelsFor(kind);
        if (labels.Length == 0) return [];      // 非金融/未细分：没有统一披露的监管指标可解析

        var lines = ExtractLines(pdfPath, labels);
        if (lines.Count == 0)
            throw new InvalidOperationException("PDF 没有可提取的文本层（可能是扫描件，需要 OCR）");

        var result = ScanAll(lines, labels, code, reportDate, kind, MetricSources.Pdf);

        // ── 数字被转曲的 PDF：走 OCR 兜底 ──────────────────────────────────────
        // 中文文本层是好的、数字全没了（见 LooksLikeDigitsStripped 的说明）。这时中文正好
        // 拿来定位"哪几页有指标名"，只把那几页渲染成图 OCR，不用啃整份三百页。
        // OCR 的结果**只补 result 里没有的 key**，且单独标 source='ocr'——它有认错的可能
        // （实测把 "94.5%，" 认成 "94.59%,"），要进回填清单让人核对一次。
        if (allowOcr && LooksLikeDigitsStripped(lines))
        {
            if (!PdfOcrExtractor.IsAvailable())
                progress?.Invoke($"    ⚠ {Path.GetFileName(pdfPath)} 的数字被转成了矢量图形，"
                               + $"文本层里没有数，而 OCR 不可用：{PdfOcrExtractor.UnavailableReason()}");
            else
            {
                var pages = CandidatePages(lines, labels);
                progress?.Invoke($"    {Path.GetFileName(pdfPath)} 的数字在文本层里是缺的（被转成了矢量图形），"
                               + $"改用 OCR 识别 {pages.Count} 页...");
                var ocrLines = PdfOcrExtractor.TryOcrPages(pdfPath, pages, progress, ct)
                    ?.Select(x => new PdfLine(x.Page, x.Text)).ToList();
                if (ocrLines is { Count: > 0 })
                {
                    var ocr = ScanAll(ocrLines, labels, code, reportDate, kind, MetricSources.Ocr);
                    // 文本层已经拿到的不动——那是确定的；OCR 只填空缺
                    var have = result.Select(m => (m.MetricKey, m.Basis)).ToHashSet();
                    int added = 0;
                    foreach (var m in ocr)
                        if (have.Add((m.MetricKey, m.Basis))) { result.Add(m); added++; }
                    progress?.Invoke($"    OCR 识别出 {added} 个指标（已标记为待核对，会列进回填清单）。");
                }
                else progress?.Invoke("    ⚠ OCR 没能识别出任何内容。");
            }
        }

        // ── 整份质量门槛：认出来的指标太少，说明这份根本没定位到指标表 ──
        // 银行和券商的监管指标表是成套披露的，正常能解析出 8~21 项；只认出一两项时，
        // 那一两项几乎肯定是在正文叙述里误命中的。实测瑞丰银行两份年报各只解析出
        // "不良率" 一项，值是 12.82% 和 4.32%——银行不良率不可能这么高（真实约 0.97%）。
        // 与其留个错值，不如整份判失败进手工清单：**宁可空着，也不能给错的**。
        // 保险不设这道门槛：寿险公司本来就只有偿付能力两项，是正常的。
        int minMetrics = kind switch
        {
            FinancialInstitutionKind.Bank => 3,
            FinancialInstitutionKind.Broker => 3,
            _ => 1,
        };
        if (result.Count < minMetrics) result.Clear();

        return result;
    }

    /// <summary>
    /// 这份 PDF 的**数字是不是被转成矢量图形了**（2026-08-30 新增）。
    ///
    /// 中国人保 2025 年报/2026 中报排版时把所有阿拉伯数字和 % 号转了曲，文本层里只剩中文：
    ///     2025-06-30.pdf：汉字 72950、数字 21998   ← 正常，比值 30%
    ///     2026-06-30.pdf：汉字 71875、数字    19   ← 转曲，比值 0.03%
    /// 正常财报这个比值在 25%~30%，取 2% 当阈值，中间隔着一个数量级，不会误判。
    /// 汉字太少（&lt;500）的不判——那多半是提取本身就没成，属于另一类问题，交给原有的流程。
    /// 全库 349 份实测只有这两份命中，OCR 不会被无谓触发。
    /// </summary>
    private static bool LooksLikeDigitsStripped(List<PdfLine> lines)
    {
        int cjk = 0, digits = 0;
        foreach (var l in lines)
            foreach (var ch in l.Text)
            {
                if (ch >= 0x4E00 && ch <= 0x9FFF) cjk++;
                else if (char.IsAsciiDigit(ch)) digits++;
            }
        return cjk >= 500 && digits < cjk * 0.02;
    }

    /// <summary>
    /// 挑出值得 OCR 的页：**按这一页出现了几个指标名排序**，多的在前。
    ///
    /// 一页 OCR 要 4~5 秒，不能整份跑。而指标表页一页就有好几个标签、正文叙述页顶多一两个，
    /// 所以命中数排序天然把表格页排在前面——那也正是 OCR 认得最准的页（表格里数字独占一列，
    /// 不像正文里 "94.5%，" 那样跟标点粘着）。
    /// 同名次时页码小的在前：财报的摘要指标表都在前面。
    /// </summary>
    private static List<int> CandidatePages(List<PdfLine> lines, (string Label, string Key)[] labels,
        int maxPages = 12)
    {
        return lines
            .GroupBy(l => l.Page)
            .Select(g =>
            {
                var text = Normalize(string.Join('\n', g.Select(x => x.Text)));
                return (Page: g.Key, Hits: labels.Count(l => text.Contains(l.Label, StringComparison.Ordinal)));
            })
            .Where(x => x.Hits > 0)
            .OrderByDescending(x => x.Hits).ThenBy(x => x.Page)
            .Take(maxPages)
            .Select(x => x.Page)
            .OrderBy(p => p)          // 真正 OCR 时按页码顺序走，日志看着不跳
            .ToList();
    }

    /// <summary>
    /// 把一批文本行扫成指标。<paramref name="source"/> 标明这批行是从哪来的
    /// （<see cref="MetricSources.Pdf"/> 文本层 / <see cref="MetricSources.Ocr"/> OCR），
    /// 它会一路带到数据库的 source 列，决定要不要让人核对、以及重解析时能不能覆盖。
    /// </summary>
    private static List<BankRegulatoryMetric> ScanAll(
        List<PdfLine> lines, (string Label, string Key)[] labels,
        string code, DateTime reportDate, FinancialInstitutionKind kind, string source)
    {
        var result = new List<BankRegulatoryMetric>();
        var seen = new HashSet<(string Key, string Basis)>();

        // 资本充足率那张表的口径：表头写"(高级法)"就是高级法，否则按权重法算。
        // 口径状态随页面推进而更新——表头在前、数据行在后。
        string capitalBasis = BankRegulatoryKeys.BasisWeighted;

        // 两遍扫描。第一遍严格（标签必须在行首 6 字内）只认表格行；第二遍放开位置限制，
        // 到正文段落里补第一遍没拿到的指标。
        // 为什么需要第二遍：各行排版差异很大，实测三种表格里取不到值的情况——
        //   宁波银行：表格没有独立的不良率行，只有正文"…不良贷款率0.76%，拨备覆盖率373.35%…"
        //   杭州银行：表格格子里写的是文字"不良贷款率 与上年末持平"，压根没有数字
        //   兰州银行：标签独占一行"不良贷款率（%）"，数值被排版换到了别处
        // 靠 seen 去重 + 第一遍优先，正文里的值只在表格确实没有时才会被采用。
        ScanLines(6);
        ScanLines(int.MaxValue);
        ScanWrappedLabels();
        ScanColumnPairs();

        if (kind == FinancialInstitutionKind.Bank) DropInconsistentCapital(result);
        if (kind == FinancialInstitutionKind.Insurer) DropCrossPageSolvency(result);

        return result;

        void ScanLines(int maxLabelIndex)
        {
        for (int li = 0; li < lines.Count; li++)
        {
            var line = lines[li];
            // 繁体版年报（H 股版）要先转简体，否则"不良貸款率"匹配不上"不良贷款率"。
            // 数字和标点不受影响，取数仍然准确。
            var text = Normalize(line.Text);

            if (text.Contains("资本充足率指标"))
                capitalBasis = text.Contains("高级法") ? BankRegulatoryKeys.BasisAdvanced
                             : text.Contains("权重法") ? BankRegulatoryKeys.BasisWeighted
                             : capitalBasis;

            // 注释里的权重法三兄弟——招行这类高级法银行，权重法数值只在注里出现。
            var wm = WeightedNote.Match(text);
            if (wm.Success)
            {
                AddIfNew(BankRegulatoryKeys.CoreTier1Car, BankRegulatoryKeys.BasisWeighted, wm.Groups[1].Value, null, line.Page);
                AddIfNew(BankRegulatoryKeys.Tier1Car, BankRegulatoryKeys.BasisWeighted, wm.Groups[2].Value, null, line.Page);
                AddIfNew(BankRegulatoryKeys.TotalCar, BankRegulatoryKeys.BasisWeighted, wm.Groups[3].Value, null, line.Page);
                continue;
            }

            foreach (var (label, key) in labels)
            {
                // 用允许空白的正则而不是 IndexOf——券商的"净资本 / 净资产（%）"斜杠两侧有空格。
                var m = RegexFor(label).Match(text);
                // 标签必须在行首附近——表格行都是"标签 值 值 值"，出现在句子中间的多半是叙述文字。
                if (!m.Success || m.Index > maxLabelIndex) continue;
                // 标签前面挂着"三年平均""上年同期"这类限定词的，值不是当期值，整行跳过。
                if (IsQualifiedByPrefix(text, m.Index)) continue;
                int maxGap = MaxLabelToValueGap;

                var rest = text[(m.Index + m.Length)..];
                var (value, std) = FirstNumberAfterStandard(rest, maxGap);

                // **标签落在行尾、值被排版换到了下一行**——正文叙述里很常见：
                //     …同比增长9.05%。成本收入比        ← 这一行到标签就没了
                //     28.08%，同比下降2.39个百分点      ← 值在下一行开头
                // 这时往下一行的开头找一个数字。只在本行标签之后确实没有数字时才这么做，
                // 而且要求下一行以数字起头（中间不能夹别的字），免得把无关的数字捞进来。
                if (value == null && rest.Trim().Length == 0 && li + 1 < lines.Count
                    && lines[li + 1].Page == line.Page)
                {
                    var next = lines[li + 1].Text.TrimStart();
                    if (next.Length > 0 && char.IsDigit(next[0]))
                        (value, std) = FirstNumberAfterStandard(" " + next);
                }
                if (value == null) break;

                bool isCapital = key is BankRegulatoryKeys.CoreTier1Car
                                     or BankRegulatoryKeys.Tier1Car
                                     or BankRegulatoryKeys.TotalCar;
                AddIfNew(key, isCapital ? capitalBasis : "", value, std, line.Page);
                break;   // 一行只认一个指标，命中即止（Labels 已按长度排序）
            }
        }
        }

        // ── 第三遍：跨行拼接标签 ────────────────────────────────────────────────
        // 财报表格里的长标签常被排版拆到 2~3 行，**而且值夹在中间**：
        //     自营权益类证券及证券衍生          自营权益类证券
        //     19.25% 14.70% 增长4.55个百分点    及其衍生品/净 1.89% 11.92%…
        //     品/净资本                         资本
        // 行距 6~16，跟正常行距重叠，靠调大聚类容差会把相邻行误粘在一起，只能单独处理。
        //
        // 做法：取每行"第一个数字之前"的那截当**标签片段**（上例中分别是"自营权益类证券及证券
        // 衍生"、""、"品/净资本"），连着拼起来再匹配；值取这几行里第一个合法数字。
        // 只对前两遍没认出来的指标做，所以不会抢走单行匹配的结果。
        void ScanWrappedLabels()
        {
            const int MaxSpan = 3;
            for (int i = 0; i < lines.Count; i++)
            {
                for (int span = 2; span <= MaxSpan && i + span <= lines.Count; span++)
                {
                    var window = lines.GetRange(i, span);
                    // 跨行只在同一页内拼，跨页拼出来的标签没有意义
                    if (window.Any(w => w.Page != window[0].Page)) break;

                    // ⚠ **窗口第一行必须对标签有贡献**，否则值会取到标签前面那一行的数字。
                    // 真实踩到的：瑞丰银行年报封面（p4）几张图排在一起——
                    //     4.32%                             ← 存款总额增速，图上的标注
                    //     2024年末 2025年6月末 …
                    //     不良贷款率 拨备覆盖率 资本充足率      ← 标签行
                    //     0.98% 340.28% 14.11%              ← 真正的值
                    // 窗口从第一行起算时，标签片段全由第三行贡献，值却取了窗口里"第一个数字"
                    // 4.32——**存款增速被存成了不良贷款率**（2024 年报同样的位置是 12.82%，
                    // 靠合理区间才侥幸挡住）。要求 window[0] 自己就带标签片段，值就只会从
                    // 标签这一行往后取了。
                    if (LabelPart(Normalize(window[0].Text)).Length == 0) continue;

                    var combined = string.Concat(window.Select(w => LabelPart(Normalize(w.Text))));
                    if (combined.Length < 6) continue;

                    foreach (var (label, key) in labels)
                    {
                        if (seen.Contains((key, "")) || seen.Contains((key, capitalBasis))) continue;
                        if (!RegexFor(label).IsMatch(combined)) continue;

                        // 值：窗口里第一个落在合理区间的数字
                        foreach (var w in window)
                        {
                            var (value, std) = FirstNumberAfterStandard(" " + w.Text, int.MaxValue);
                            if (value == null) continue;
                            bool isCapital = key is BankRegulatoryKeys.CoreTier1Car
                                                 or BankRegulatoryKeys.Tier1Car
                                                 or BankRegulatoryKeys.TotalCar;
                            AddIfNew(key, isCapital ? capitalBasis : "", value, std, w.Page);
                            break;
                        }
                        break;
                    }
                }
            }
        }

        // ── 第四遍：整行标签 ＋ 整行数值，按列顺序配对 ──────────────────────────
        // 年报封面那张"关键指标"图是这么排的（瑞丰银行 2024 年报 p4）：
        //     不良贷款率        拨备覆盖率        资本充足率
        //     0.97%            320.87%          14.87%
        // 标签和数值各占一行、三列并排。前三遍全都取不到：标签行没有数字，值行没有标签。
        // 而这份 PDF 下到的是**财务报表分册**，正文里一个指标表都没有——封面这三个数是
        // 全篇仅有的，不认它就只能整份判失败、丢给人手工填三个数。
        //
        // 约束下得很死，避免把无关的数字配上去：
        //   ① 标签行**一个数字都不能有**（真正的表格行是"标签 值"同行，轮不到这一遍）
        //   ② 值行**只能是数字和 % . , - 空格**，混进任何汉字都不算
        //   ③ **标签个数必须跟数值个数完全相等**，且至少两个——一对一才能按顺序配
        // 放在最后一遍，只补前面没拿到的（AddIfNew 里的 seen 会挡住），所以正文表格永远优先。
        void ScanColumnPairs()
        {
            for (int i = 0; i + 1 < lines.Count; i++)
            {
                if (lines[i + 1].Page != lines[i].Page) continue;

                var text = Normalize(lines[i].Text);
                if (text.Any(char.IsAsciiDigit)) continue;              // ① 标签行不能有数字

                var valueLine = lines[i + 1].Text.Trim();
                if (valueLine.Length == 0) continue;
                // ② 纯数值行
                if (!valueLine.All(c => char.IsAsciiDigit(c)
                                     || c is '%' or '.' or ',' or ' ' or '\t' or '-' or '－' or '％'))
                    continue;
                var nums = Regex.Matches(valueLine, @"-?\d+(?:\.\d+)?").Select(m => m.Value).ToList();
                if (nums.Count < 2) continue;

                // 这一行上出现了哪些标签，按出现位置排序。**区间重叠的丢掉**——labels 已按标签
                // 长度排序，所以"核心一级资本充足率"先占位，里面的"资本充足率"不会再算一个。
                var hits = new List<(int Index, string Key)>();
                var used = new List<(int Start, int End)>();
                foreach (var (label, key) in labels)
                    foreach (Match m in RegexFor(label).Matches(text))
                    {
                        int s = m.Index, e = m.Index + m.Length;
                        if (used.Any(u => s < u.End && e > u.Start)) continue;
                        used.Add((s, e));
                        hits.Add((s, key));
                    }
                if (hits.Count != nums.Count) continue;                 // ③ 一一对应才配

                hits.Sort((a, b) => a.Index.CompareTo(b.Index));
                for (int j = 0; j < hits.Count; j++)
                {
                    bool isCapital = hits[j].Key is BankRegulatoryKeys.CoreTier1Car
                                                 or BankRegulatoryKeys.Tier1Car
                                                 or BankRegulatoryKeys.TotalCar;
                    AddIfNew(hits[j].Key, isCapital ? capitalBasis : "", nums[j], null, lines[i].Page);
                }
            }
        }

        /// 取一行里"第一个数字之前"的部分——多行单元格中，这就是标签被拆开的那一截。
        // ⚠ 必须连 ≥ ≤ 一起剔掉。监管标准值那一列有时被排版挤到单独一行，此时该行的
        // "第一个数字之前"就只剩一个 ≤，拼进标签里就成了
        //     自营非权益类证券及其 ＋ ≤ ＋ 衍生品/净资本
        // 标签因此匹配不上、整项丢失（实测国元证券 2025 年报正是这样丢的；同一页的
        // "自营权益类"因为标准值跟数值同行、没有这个残留，反而正常解析出来了）。
        static string LabelPart(string text)
        {
            var m = Regex.Match(text, @"\d");
            var head = m.Success ? text[..m.Index] : text;
            return Regex.Replace(head, @"[\s≥≤]", "");
        }

        void AddIfNew(string key, string basis, string raw, string? std, int page)
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return;

            // 按指标各自的合理区间过滤。原来只有一条 (-100, 1000) 的宽泛边界，挡不住真实踩到的坑：
            // 民生银行的**目录页**有一行"七、资本充足率分析 42"——42 是页码，却被当成了资本充足率
            // 存进库（真实值在 p8，是 13.36）。
            // ⚠ 被拒的值**不占位**（seen 在这之后才写），所以后面真正的表格页仍然能命中，
            //    这正是"先拒绝再继续"能自愈的原因。
            if (Ranges.TryGetValue(key, out var range) && (v < range.Min || v > range.Max)) return;
            if (!seen.Add((key, basis))) return;   // 只取第一次命中（集团口径在前）
            result.Add(new BankRegulatoryMetric
            {
                Code = code, ReportDate = reportDate, MetricKey = key,
                Basis = basis, Value = v, StandardValue = std, SourcePage = page,
                Source = source,
            });
        }
    }

    /// <summary>
    /// 资本充足率三兄弟必须满足 <c>核心一级 ≤ 一级 ≤ 资本充足率</c>（分子逐层放宽、分母相同，
    /// 这是定义决定的恒等关系）。违反就说明这一组取错了列，**整组丢掉**而不是留下错值。
    ///
    /// 真实踩到的例子：张家港行的监管指标表里，别的行都写"≥10.5"，唯独一级资本充足率那行漏了
    /// ≥ 号写成"8.5"，于是标准值被当成了当期值，存进去 tier1=8.5 而 core_tier1=10.63 ——
    /// 一级资本充足率比核心一级还低，逻辑上不可能。这种情况宁可没有数据，也不能给出错的。
    /// </summary>
    private static void DropInconsistentCapital(List<BankRegulatoryMetric> rows)
    {
        foreach (var basis in new[] { BankRegulatoryKeys.BasisWeighted, BankRegulatoryKeys.BasisAdvanced })
        {
            double? V(string key) => rows.FirstOrDefault(r => r.MetricKey == key && r.Basis == basis)?.Value;
            double? core = V(BankRegulatoryKeys.CoreTier1Car);
            double? t1 = V(BankRegulatoryKeys.Tier1Car);
            double? tc = V(BankRegulatoryKeys.TotalCar);

            bool bad = (core.HasValue && t1.HasValue && core > t1 + 0.001)
                    || (t1.HasValue && tc.HasValue && t1 > tc + 0.001)
                    || (core.HasValue && tc.HasValue && core > tc + 0.001);
            if (!bad) continue;

            rows.RemoveAll(r => r.Basis == basis && r.MetricKey is BankRegulatoryKeys.CoreTier1Car
                                                              or BankRegulatoryKeys.Tier1Car
                                                              or BankRegulatoryKeys.TotalCar);
        }
    }

    /// <summary>
    /// 核心与综合偿付能力充足率必须来自**同一张表**，否则口径对不上。
    ///
    /// 真实踩到的例子：中国平安年报里，集团口径的综合偿付能力充足率在 p15（193.3%），而 p15 没有
    /// 集团核心偿付能力那一行，于是核心偿付能力一路取到 p51 的子公司表，拿到的是**平安寿险**的
    /// 123.3%。两个数一个是集团、一个是寿险子公司，放在一份体检表里对比毫无意义。
    ///
    /// 判据是页距：单一主体的保险公司（新华、太保寿险）这两个指标必然在同一张表、同一页；
    /// 隔了好几页就说明是从不同口径的表里凑出来的。这时丢掉核心偿付能力——综合偿付能力那个
    /// 因为带"集团"前缀会先命中，更可能是集团口径，予以保留。
    /// </summary>
    private static void DropCrossPageSolvency(List<BankRegulatoryMetric> rows)
    {
        const int MaxPageGap = 3;
        var core = rows.FirstOrDefault(r => r.MetricKey == InsurerRegulatoryKeys.CoreSolvency);
        var comp = rows.FirstOrDefault(r => r.MetricKey == InsurerRegulatoryKeys.ComprehensiveSolvency);
        if (core == null || comp == null) return;
        if (Math.Abs(core.SourcePage - comp.SourcePage) <= MaxPageGap) return;
        rows.Remove(core);
    }

    /// <summary>
    /// 取标签之后的第一个数值；如果中间夹着"≥25"这样的监管标准值，先把它摘出来当
    /// <c>standardValue</c>，再往后取真正的当期值。
    /// </summary>
    /// <param name="maxGap">标签末尾到值之间允许的最大字符距离。跨行拼接的场景要传
    /// <see cref="int.MaxValue"/>——那时传进来的是**整行**，标签片段本身就占掉了距离额度，
    /// 用常规阈值会把真值挤出去（实测国海证券的"及其衍生品/净 1.89%"就是这么丢的）。</param>
    private static (string? Value, string? Standard) FirstNumberAfterStandard(string rest, int maxGap = MaxLabelToValueGap)
    {
        // 注释角标会夹在标签和数值之间，不清掉就会被当成指标值。花样比想象的多：
        //   招行：拨备覆盖率(1)          → 纯数字括号
        //   平安：拨备覆盖率 ≥130（注3）  → **带"注"字**，原来的 [（(]\d+[)）] 匹配不到，
        //         于是"注3"里的 3 被当成拨备覆盖率入库（实测 000001 存进了 3.0 和 2.0）
        //   其它：⑴ ① 之类的符号角标、(%)、（年化）
        // 所以改成"清掉一切短括号内容"（≤5 字），长括号如"（人民币百万元）"保留、不影响取数。
        // 注意这只作用于标签之后的 rest，判断高级法/权重法用的是整行原文，不受影响。
        rest = Regex.Replace(rest, @"[（(][^）)]{0,5}[)）]|[⑴-⑽①-⑩]", " ");

        string? std = null;

        // pdftotext -layout 用大量空格做列对齐，压成单空格后"标签到值"的距离才有可比性
        // （否则 MaxLabelToValueGap 会把正常的表格值判成"隔太远"）。
        rest = Regex.Replace(rest, @"[ \t]+", " ");

        // **去掉数字里的千位分隔符**。比率超过 1000% 时财报会写成 "1,005.73"，
        // 而数字正则遇到逗号就断了，只能取到 "1"，接着又被合理区间拒掉——
        // 表现为"这项死活解析不出来"（实测红塔证券的流动性覆盖率 1,005.73 就是这么丢的）。
        // 只在"数字,三位数字"这种位置删逗号，不会误伤中文顿号或列分隔。
        rest = Regex.Replace(rest, @"(?<=\d),(?=\d{3}(?!\d))", "");

        // **优先取带小数点的数字，整数只作备选**。
        // 起因是 pdftotext 输出里标签后面常紧跟脚注编号：
        //     成本收入比 5      29.30   29.90 …     ← 5 是脚注号，29.30 才是值
        //     不良貸款率 6      1.28    1.31  …
        // 直接取"第一个数字"会把 5、6 当成指标值（实测交通银行存进了 nim=4.00、
        // npl_ratio=6.00 这种连续整数，一眼假）。这些比率指标实际值几乎都带小数，
        // 而脚注号一定是整数，靠这个能干净地分开；万一真有整数值，备选逻辑仍会兜住。
        Match? firstInteger = null;
        foreach (Match m in Regex.Matches(rest, @"-?\d+(?:\.\d+)?"))
        {
            // 日期不是指标值。表头行"资本充足率指标(%)(高级法) 6月30日 12月31日"就是靠这条排除的
            // ——否则"资本充足率"会取到 6.00（来自"6月30日"）而不是真正的 18.33。
            // **紧跟在 ≥/≤ 后面的是监管标准值，不是当期值**——记下来当参考值，然后跳过。
            // 不能像早先那样"先摘掉标准值再从后面找"：那假设标准值排在值前面（银行表确实是
            //     流动性比例 ≥25 62.36 …
            // ），可券商表把标准值甩在行尾：
            //     及其衍生品/净 1.89% 11.92% 下降10.03个百分点 ≤80% ≤100%
            // 于是"摘掉≤80之后再找"就取到了 100，把监管上限当成了自营敞口（实测国海证券
            // 被存成 100.00 / 500.00）。改成逐个数字判断前缀，两种排法就都对了。
            int before = m.Index - 1;
            while (before >= 0 && rest[before] == ' ') before--;
            if (before >= 0 && (rest[before] == '≥' || rest[before] == '≤'))
            {
                std ??= rest[before].ToString() + m.Value;
                continue;
            }

            int end = m.Index + m.Length;
            if (end < rest.Length && "年月日".Contains(rest[end])) continue;

            // **变化量不是值**。财报注释里满是"较上年末减少73.42个百分点"这种句子，第二遍宽松
            // 扫描很容易把变化幅度当成指标本身——实测东北证券的流动性覆盖率就被存成了 73.42%
            // （低于监管红线 100%，一眼假），真实值那一格在表格里是空的。
            if (rest[end..].TrimStart().StartsWith("个百分点", StringComparison.Ordinal)) continue;

            // 数据行是"标签 值"，中间顶多夹个单位或注释角标；隔得太远说明这行根本不是数据行。
            if (m.Index > maxGap) break;

            if (m.Value.Contains('.')) return (m.Value, std);   // 带小数点，就是它
            firstInteger ??= m;                                  // 整数先记着，找不到小数时才用
        }
        return (firstInteger?.Value, std);
    }

    /// <summary>标签末尾到当期值之间允许的最大字符距离，见上面的说明。</summary>
    private const int MaxLabelToValueGap = 8;

    /// <summary>
    /// 标签**前面**挂着限定词、说明这个数不是当期值（2026-08-30 新增）。
    ///
    /// 真实踩到的例子：人保 2026 中报 p17 同一页上有两句——
    ///     …；综合成本率94.5%，同比下降0.8个百分点…      ← 当期值，标签在行尾、值换到下一行
    ///     人保财险三年平均综合成本率5为97.9%，…          ← 三年平均，标签就在行首附近
    /// 后者因为标签靠前，第一遍扫描先命中，把 97.9% 存成了当期综合成本率——差了 3.4 个百分点，
    /// 而且是**口径错**，比小数点认错危害大得多（体检表会据此判"承保接近盈亏平衡"）。
    ///
    /// 判据是标签前 8 个字里出现这些词。表格行的标签在行首（前面什么都没有），不受影响。
    /// 另外单独看紧邻的那一个字是不是"均"——"平均"被排版拆到上一行时，这一行开头就只剩
    /// "均综合成本率"（上例中正是如此）。中文里没有别的指标名前面正常挂个"均"字。
    /// </summary>
    private static bool IsQualifiedByPrefix(string text, int labelIndex)
    {
        if (labelIndex == 0) return false;
        if (text[labelIndex - 1] == '均') return true;
        var prefix = text[Math.Max(0, labelIndex - 8)..labelIndex];
        foreach (var w in QualifierWords)
            if (prefix.Contains(w, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// 让"标签后面那个数"不再是**当期、整体口径**的限定词。分两类：
    /// ① 时间口径（平均/上年/同期…）——不是本期的数；
    /// ② **险种口径**——财险年报按险种逐个披露综合成本率，"机动车辆险综合成本率93.5%"
    ///    "意外伤害及健康险业务综合成本率99.0%" 全都以"综合成本率"结尾，混进来就把某个
    ///    单险种的数当成了公司整体的（实测人保 2026 中报 p18 取到了车险的 93.5%，
    ///    公司整体是 94.5%）。这里列的是排版里真实出现过的险种名，宁可漏拦不误拦。
    ///    注意**不拦"人保财险"这种主体前缀**：集团的综合成本率本来就等于财险子公司的，
    ///    那个数是对的。
    /// </summary>
    private static readonly string[] QualifierWords =
    [
        "平均", "三年", "累计", "上年", "去年", "同期", "预计", "目标",
        "车辆险", "车险", "责任险", "财产险", "意外伤害", "健康险", "农业保险",
        "信用保证", "货运险", "工程险", "船舶险", "其他险",
    ];

    /// <summary>
    /// 把 PDF 还原成"行"：按基线 Y 聚类、行内按 X 排序。只保留出现过
    /// <see cref="SectionHints"/> 字样的页——招行 309 页里真正有用的就那几页，
    /// 全篇扫既慢又容易在叙述段落里误命中。
    /// </summary>
    private static List<PdfLine> ExtractLines(string pdfPath, (string Label, string Key)[] labels)
    {
        var lines = ExtractWithPdfPig(pdfPath, labels);
        if (lines.Count > 0) return lines;

        // ── PdfPig 一无所获时，退到外部 pdftotext ────────────────────────────────
        // 触发场景是 PdfPig 读不动的那些字体（Adobe-CNS1/GB1 的 CMap 缺失、TrueType 缺 head 表）。
        // 这些 PDF **不是扫描件**——查内部结构，交行有 338 个字体定义、人保 24 个，文本层都在，
        // 只是 PdfPig 解不开。同样几份 pdftotext 一转就出来了（交行 17 万中文字）。
        // 找不到 pdftotext 就返回空，行为跟以前一样（记为 no_text），不会因为缺它而更糟。
        var alt = PdfTextExtractor.TryExtractLines(pdfPath);
        if (alt == null) return lines;

        // pdftotext -layout 的输出本身就是"标签 值 值 值"的行，不需要再做坐标聚类；
        // 但页面筛选还是要做，避免在正文叙述里误命中。
        foreach (var pageGroup in alt.GroupBy(x => x.Page))
        {
            var pageText = Normalize(string.Join('\n', pageGroup.Select(x => x.Text)));
            int hits = labels.Count(l => pageText.Contains(l.Label, StringComparison.Ordinal));
            if (hits < MinLabelHits(labels)
                && !SectionHints.Any(h => pageText.Contains(h, StringComparison.Ordinal))) continue;
            foreach (var x in pageGroup) lines.Add(new PdfLine(x.Page, x.Text));
        }
        return lines;
    }

    /// <summary>用 PdfPig 提取；整份读不出来时返回空列表（由调用方决定要不要走兜底）。</summary>
    private static List<PdfLine> ExtractWithPdfPig(string pdfPath, (string Label, string Key)[] labels)
    {
        var lines = new List<PdfLine>();
        PdfDocument doc;
        // Open 本身就可能因为字体表损坏而抛（"The head table is required"），
        // 这种也要能落到 pdftotext 兜底，所以整个包起来。
        try { doc = PdfDocument.Open(pdfPath); }
        catch { return lines; }
        using var _ = doc;

        // ⚠ **逐页取、每页单独兜异常**，不能用 foreach (var page in doc.GetPages())。
        // doc.GetPages() 是惰性的：某一页的字体坏了，异常会从 foreach 里冒出来，
        // 整份 PDF 就此报废。实测 26 份失败报告里绝大多数是这么丢的——
        //   "Could not find the referenced CMap: Adobe-CNS1-7 / Adobe-GB1-6"
        //     PdfPig 不自带这些 CJK CMap 资源（0.1.2 也没有 SkipMissingFonts 选项可关）
        //   "The head table is required"
        //     内嵌 TrueType 字体缺 head 表
        // 这些都是**个别页**的字体问题，而我们要的指标表往往在别的页上，完全能读出来。
        // 改成按页号取 + 单页 try/catch 之后，坏页跳过、好页照常解析。
        for (int pageNo = 1; pageNo <= doc.NumberOfPages; pageNo++)
        {
            UglyToad.PdfPig.Content.Page page;
            string pageText;
            try
            {
                page = doc.GetPage(pageNo);
                pageText = page.Text;
            }
            catch { continue; }                      // 这一页读不了，换下一页
            if (string.IsNullOrWhiteSpace(pageText)) continue;
            // 页面筛选：**主判据是"这一页出现了几个我们要的指标名"**，章节标题只作补充。
            // 原来只按标题匹配，实测漏得厉害——各行的表格标题五花八门（"资本状况""流动性"
            // "贷款迁徙率""主要监管指标"…），张家港行装着不良率/拨备/迁徙率/成本收入比的那一整页
            // 因为标题对不上被整页跳过，青农商行更是一个指标都没解析出来（no_match）。
            // 指标表页通常一页就有 5~10 个标签，正文叙述页很少同时出现两个以上。
            var normalizedPageText = Normalize(pageText);
            int labelHits = labels.Count(l => normalizedPageText.Contains(l.Label, StringComparison.Ordinal));
            bool looksLikeTable = labelHits >= MinLabelHits(labels)
                                  || SectionHints.Any(h => normalizedPageText.Contains(h, StringComparison.Ordinal));
            if (!looksLikeTable) continue;

            // GetWords() 会重新走一遍字形解析，可能抛出跟 page.Text 不同的异常，
            // 所以这里也得单独兜住。
            List<UglyToad.PdfPig.Content.Word> words;
            try
            {
                words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            }
            catch { continue; }
            if (words.Count == 0) continue;

            // 按基线聚成行：**降序扫过去，跟当前行基线差在容差内就并进来**，超出就另起一行。
            //
            // 原来用的是 GroupBy(Round(Bottom / 容差)) 那种"分桶"写法，有个致命的边界问题——
            // 财报表格里多行单元格的**值和标签基线只差 2**（正常行距是 16），但分桶会按绝对
            // 位置切，两个只差 2 的基线照样可能落进相邻两个桶。实测山西证券：
            //     Bottom≈412  "193.08% 195.89% 下降2.81个百分点"   ← 值
            //     Bottom≈410  "净稳定资金率"                        ← 标签
            // 被切成两行后，标签那行没有数字、值那行没有标签，这个指标就永远取不到。
            // 改成相邻聚类后两者合并、行内再按 X 排序（标签在左、值在右），自然拼成
            // "净稳定资金率 193.08% 195.89% …"。跨行截断的标签（"自营权益类证券及证券衍生"
            // ＋ "品/净资本"）也一并被这个改动救回来了。
            var groups = new List<List<UglyToad.PdfPig.Content.Word>>();
            foreach (var w in words.OrderByDescending(w => w.BoundingBox.Bottom))
            {
                if (groups.Count == 0
                    || Math.Abs(groups[^1][0].BoundingBox.Bottom - w.BoundingBox.Bottom) > LineTolerance)
                    groups.Add([]);
                groups[^1].Add(w);
            }

            foreach (var group in groups)
            {
                // ⚠ 财报 PDF 里**中文是一个字一个 word** 存的（"不 良 贷 款 率" 是 5 个 word）。
                // 无脑用空格拼会得到"不 良 贷 款 率"，标签就永远匹配不上；完全不加空格又会把
                // 相邻两列的数字粘成"0.940.95"。所以按**字间距**判断：中文字之间几乎贴着
                // （间距接近 0），表格列之间有明显空白，超过字高的三成才补一个空格。
                // 阈值按**字宽**算，不能用 BoundingBox.Height——这份 PDF 里 Height 恒为 0
                // （字形没带高度信息），拿它当基准会让阈值变成 0、每个字之间都插空格。
                // 实测：相邻中文字的间距约 0.2pt、字宽 8pt，而表格列之间的空白有 20pt 以上，
                // 取两侧字宽较小者的三成（≈2.4pt）能干净地分开列、又不拆散词。
                var sb = new StringBuilder();
                double prevRight = double.NaN, prevWidth = 0;
                foreach (var w in group.OrderBy(w => w.BoundingBox.Left))
                {
                    double width = w.BoundingBox.Width;
                    if (!double.IsNaN(prevRight))
                    {
                        double gap = w.BoundingBox.Left - prevRight;
                        double threshold = Math.Min(prevWidth, width) * 0.3;
                        if (gap > threshold) sb.Append(' ');
                    }
                    sb.Append(w.Text);
                    prevRight = w.BoundingBox.Right;
                    prevWidth = width;
                }
                var text = sb.ToString().Trim();
                if (text.Length > 0) lines.Add(new PdfLine(page.Number, text));
            }
        }
        return lines;
    }
}
