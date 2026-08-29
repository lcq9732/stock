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
    /// <summary>同一行的基线 Y 容差（PDF 单位，约等于 1pt）。表格行距通常 10 以上，2.0 足够分开。</summary>
    private const double LineTolerance = 2.0;

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
        var pattern = string.Join(@"\s*", label.Select(ch => Regex.Escape(ch.ToString())));
        re = new Regex(pattern, RegexOptions.Compiled);
        lock (LabelRegex) LabelRegex[label] = re;
        return re;
    }

    /// <summary>
    /// 每个指标的合理区间（%）。取值宽松——目的是挡掉页码、年份、金额这类明显不是指标的数，
    /// 不是替代人工判断。边界参照 42 家上市银行的实际分布再往外放一档。
    /// 迁徙率可以超过 100%（次级类转下迁的比例常年 100%+），单独给大区间。
    /// </summary>
    private static readonly Dictionary<string, (double Min, double Max)> Ranges = new()
    {
        [BankRegulatoryKeys.NplRatio] = (0.05, 15),
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
        [BrokerRegulatoryKeys.NetCapitalToNetAssets] = (10, 100),
        [BrokerRegulatoryKeys.NetCapitalToLiabilities] = (4, 200),
        [BrokerRegulatoryKeys.EquityPropToNetCapital] = (0, 200),
        [BrokerRegulatoryKeys.NonEquityPropToNetCapital] = (0, 800),

        // 保险：偿付能力充足率监管下限 50%/100%，实际值多在 150%~400%。
        [InsurerRegulatoryKeys.CoreSolvency] = (30, 1000),
        [InsurerRegulatoryKeys.ComprehensiveSolvency] = (50, 1000),
        [InsurerRegulatoryKeys.CombinedRatio] = (60, 160),
        [InsurerRegulatoryKeys.AutoCombinedRatio] = (60, 160),
    };

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
    /// 解析一份财报 PDF。<paramref name="reportDate"/> 是这份报告的报告期（决定指标归属哪一期）。
    /// 抛异常由调用方记进 BankReportFetchState，不在这里吞掉。
    /// </summary>
    public static List<BankRegulatoryMetric> Parse(string pdfPath, string code, DateTime reportDate,
        FinancialInstitutionKind kind = FinancialInstitutionKind.Bank)
    {
        var labels = LabelsFor(kind);
        if (labels.Length == 0) return [];      // 非金融/未细分：没有统一披露的监管指标可解析

        var lines = ExtractLines(pdfPath, labels);
        if (lines.Count == 0)
            throw new InvalidOperationException("PDF 没有可提取的文本层（可能是扫描件，需要 OCR）");

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

        if (kind == FinancialInstitutionKind.Bank) DropInconsistentCapital(result);
        if (kind == FinancialInstitutionKind.Insurer) DropCrossPageSolvency(result);
        return result;

        void ScanLines(int maxLabelIndex)
        {
        foreach (var line in lines)
        {
            var text = line.Text;

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

                var rest = text[(m.Index + m.Length)..];
                var (value, std) = FirstNumberAfterStandard(rest);
                if (value == null) break;

                bool isCapital = key is BankRegulatoryKeys.CoreTier1Car
                                     or BankRegulatoryKeys.Tier1Car
                                     or BankRegulatoryKeys.TotalCar;
                AddIfNew(key, isCapital ? capitalBasis : "", value, std, line.Page);
                break;   // 一行只认一个指标，命中即止（Labels 已按长度排序）
            }
        }
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
    private static (string? Value, string? Standard) FirstNumberAfterStandard(string rest)
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
        var stdMatch = Regex.Match(rest, @"[≥≤]\s*[\d.]+");
        if (stdMatch.Success)
        {
            std = stdMatch.Value.Replace(" ", "");
            rest = rest[(stdMatch.Index + stdMatch.Length)..];
        }

        foreach (Match m in Regex.Matches(rest, @"-?\d+(?:\.\d+)?"))
        {
            // 日期不是指标值。表头行"资本充足率指标(%)(高级法) 6月30日 12月31日"就是靠这条排除的
            // ——否则"资本充足率"会取到 6.00（来自"6月30日"）而不是真正的 18.33。
            int end = m.Index + m.Length;
            if (end < rest.Length && "年月日".Contains(rest[end])) continue;

            // 数据行是"标签 值"，中间顶多夹个单位或注释角标；隔得太远说明这行根本不是数据行。
            if (m.Index > MaxLabelToValueGap) return (null, std);
            return (m.Value, std);
        }
        return (null, std);
    }

    /// <summary>标签末尾到当期值之间允许的最大字符距离，见上面的说明。</summary>
    private const int MaxLabelToValueGap = 8;

    /// <summary>
    /// 把 PDF 还原成"行"：按基线 Y 聚类、行内按 X 排序。只保留出现过
    /// <see cref="SectionHints"/> 字样的页——招行 309 页里真正有用的就那几页，
    /// 全篇扫既慢又容易在叙述段落里误命中。
    /// </summary>
    private static List<PdfLine> ExtractLines(string pdfPath, (string Label, string Key)[] labels)
    {
        var lines = new List<PdfLine>();
        using var doc = PdfDocument.Open(pdfPath);

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
            int labelHits = labels.Count(l => pageText.Contains(l.Label, StringComparison.Ordinal));
            bool looksLikeTable = labelHits >= 2
                                  || SectionHints.Any(h => pageText.Contains(h, StringComparison.Ordinal));
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

            // 按基线聚类成行。PDF 的 Y 轴向上，所以降序遍历才是从上往下读。
            foreach (var group in words
                         .GroupBy(w => Math.Round(w.BoundingBox.Bottom / LineTolerance))
                         .OrderByDescending(g => g.Key))
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
