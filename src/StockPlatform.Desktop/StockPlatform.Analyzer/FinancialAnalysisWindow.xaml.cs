using System.Text;
using System.Windows;
using System.Windows.Media;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Desktop.Shared.Theme;

namespace StockPlatform.Analyzer;

/// <summary>绑给界面的一行。颜色和标记在这里预先算好，XAML 那边就不用写 converter。</summary>
public class AnalysisLineVm
{
    public string Mark { get; init; } = "";
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Change { get; init; } = "";
    public string Note { get; init; } = "";
    public Brush Brush { get; init; } = ThemeBrushes.Foreground;
    public FontWeight Weight { get; init; } = FontWeights.Normal;

    /// <summary>参考值/正常范围（2026-08-29，银行体检表用）——像体检报告那样"一列值、一列正常范围"。</summary>
    public string Reference { get; init; } = "";
    /// <summary>条目编号文本（"01"~"12"）；非体检表为空。</summary>
    public string Clause { get; init; } = "";
    public Visibility ReferenceVisibility =>
        Reference.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ClauseVisibility =>
        Clause.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// 绑给界面的一张趋势图。之所以要包一层而不是直接绑 PlotModel：几张图上下叠放时**共用横坐标**
/// （只有末图显示报告期标签，见 FinancialAnalysisChartBuilder），末图因此要比其它图高一点。
/// </summary>
public class TrendChartVm
{
    public OxyPlot.PlotModel Model { get; init; } = new();
    public double Height { get; init; }
}

/// <summary>绑给界面的一节。</summary>
public class AnalysisSectionVm
{
    public string Title { get; init; } = "";
    public List<AnalysisLineVm> Lines { get; init; } = new();
    public string Conclusion { get; init; } = "";
    public Visibility ConclusionVisibility =>
        Conclusion.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// 财务分析窗口（2026-08-27 新增）——展示 <see cref="FinancialAnalyzer"/> 的结果。
///
/// 这个功能的出发点：52 个财务科目摆在那里，光看数字看不出问题。所以每行都带"这意味着什么"、
/// 每节末尾给结论、最后汇总异常项，右边配同期趋势图回答"在变好还是变坏"。
///
/// 窗口本身不做任何计算——全部在 Logic 层的 FinancialAnalyzer 里，这样那套逻辑可以脱离 UI
/// 单独验证（2026-08-27 就是用真实数据端到端跑通后才做界面的）。
/// </summary>
public partial class FinancialAnalysisWindow : Window
{
    private readonly FinancialAnalysisReport _report;

    /// <summary>资金面诊断（2026-09-16，第①列下半部分）。在 <see cref="Open"/> 里装进来，
    /// 跟观察项一样**在构造之后**——没有财务数据的票照样要看得到它。</summary>
    private CapitalDiagnosis? _diagnosis;

    public FinancialAnalysisWindow(FinancialAnalysisReport report)
    {
        InitializeComponent();
        _report = report;

        // 标题跟列表页那个按钮的名字对齐（2026-09-14 用户要求）：按钮早就从【财务分析】
        // 改叫【分析详情】了，窗口标题还写着"财务分析"，两处对不上。窗口里确实不止财务——
        // 右边还有观察项和事件，"分析详情"更诚实。
        Title = $"分析详情 — {report.Code} {report.Name}".TrimEnd();

        if (report.Error != null)
        {
            HeaderText.Text = $"{report.Code} {report.Name}".TrimEnd();
            PeriodTitleText.Text = "财报分析";
            HeadlineText.Text = report.Error;
            // SetResourceReference 等于代码里的 DynamicResource：换主题时这块底色会自己跟着变
            HeadlineBorder.SetResourceReference(BackgroundProperty, "Theme.Danger.Background");
            FooterText.Text = "";
            // 没有财务数据时第②列只有一句提示，但**第①列的事件和资金面诊断照常有内容**
            // （2026-09-14 起如此；2026-09-16 三列重排后更是如此），所以不能缩成半屏——
            // 那会把事件叙述和六个维度挤成一团。给 0.92，跟正常情况一样。
            TextFitter.SizeToScreen(this);
            return;
        }

        // 总体判断并进标题行（2026-08-27 用户要求）——省掉一整行黄底框，而且那句话本来就是
        // 标题的一部分。数值在 FinancialAnalyzer.BuildHeadline 里已经并进去了。
        HeadlineBorder.Visibility = Visibility.Collapsed;
        HeaderText.Inlines.Add(new System.Windows.Documents.Run(
            $"{report.Code} {report.Name}".TrimEnd()) { FontWeight = FontWeights.Bold });
        // 紧跟名字的是**算估值用的那个价**（2026-09-14 用户要求）。日期一定要带：这个价是本地库里
        // 最新一根日K，而本地未必抓到了今天——PE/PB/股息率算的是那一天的估值，不标日期会被当成现价。
        // 尤其 PE 的另一个输入（总股本）走的是日更，两个输入的日期可能差着几天。
        if (report.Price is > 0)
        {
            var priceText = report.PriceDate is { } pd
                ? $"　·　{report.Price.Value:F2} 元（{pd:M-d} 收盘）"
                : $"　·　{report.Price.Value:F2} 元";
            var priceRun = new System.Windows.Documents.Run(priceText) { FontSize = 14 };
            priceRun.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Theme.Foreground.Muted");
            HeaderText.Inlines.Add(priceRun);
        }
        // 第②列的列标题 = 报告期 + 总体判断（2026-09-16）。
        // 两样都从通栏标题挪过来：那一列讲的就是这一期的财报，期别写在列头比混在通栏更好找；
        // 总体判断同理，而且通栏右边被「复制全文」占着、给不出多少横向空间，这一列标题右边
        // 本来就空着一大片（用户指着那片空白说"放这儿"）。
        PeriodTitleText.Inlines.Add(new System.Windows.Documents.Run(
            report.ReportDate != default ? report.PeriodName : "财报分析"));
        if (report.Headline.Length > 0)
        {
            var headlineRun = new System.Windows.Documents.Run($"　·　{report.Headline}")
            {
                FontSize = 13,
                FontWeight = FontWeights.Normal,
            };
            headlineRun.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Theme.Accent.Warm");
            PeriodTitleText.Inlines.Add(headlineRun);
        }
        // 机构类型标注（2026-08-29 细分）：三类金融机构各有一张体检表——银行看资产质量与资本，
        // 券商看净资本与自营敞口，保险看偿付能力与承保盈利，指标体系互不通用。
        // 认不出类型的（老数据缺 v4 特征科目）退回通用简版。
        string? kindTag = report.Kind switch
        {
            FinancialInstitutionKind.Bank => "　·　银行（十二条体检表）",
            FinancialInstitutionKind.Broker => "　·　券商（体检表）",
            FinancialInstitutionKind.Insurer => "　·　保险（体检表）",
            FinancialInstitutionKind.OtherFinancial => "　·　金融机构（简版指标）",
            _ => null,
        };
        if (kindTag != null)
        {
            var kindRun = new System.Windows.Documents.Run(kindTag) { FontSize = 13 };
            kindRun.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Theme.Foreground.Muted");
            HeaderText.Inlines.Add(kindRun);
        }

        SectionList.ItemsSource = report.Sections.Select(ToVm).ToList();

        // 共用横坐标：只有最后一张图画报告期标签，其它图省掉底部那 22px
        var trends = report.Trends;
        TrendList.ItemsSource = trends.Select((t, i) =>
        {
            bool last = i == trends.Count - 1;
            return new TrendChartVm
            {
                Model = FinancialAnalysisChartBuilder.Build(t, showXLabels: last),
                // 末图要多容纳 30px 的标签区（BottomMarginWithLabels），所以比其它图高一截
                Height = last ? 165 : 128,
            };
        }).ToList();

        // 口径说明进标题的 Tooltip（2026-09-16 用户要求），不再常驻占行——它解释的是
        // "为什么只取同月份的报告期"，看两次就不用再看了，而在这一列里它要占掉四五行。
        // 跟资金面诊断六个维度的做法一致。"报告期不足"不是口径而是**本次的实际状态**，
        // 所以那种情况仍然写在标题上（同样的理由见 CapitalDiagnosis 的告警 vs Tooltip 之分）。
        if (report.Trends.Count > 0)
        {
            TrendTitleText.ToolTip = new System.Windows.Controls.TextBlock
            {
                Text = "只取跟本期同月份的报告期（A股报表是年内累计口径，混着比会画成锯齿）。"
                       + "几张图共用最下面那条横坐标，同一列必是同一期。",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
            };
        }
        else
        {
            TrendTitleText.Text = "趋势 —— 报告期不足，画不出";
            TrendTitleText.ToolTip = null;
        }

        if (report.Alerts.Count > 0)
        {
            AlertBorder.Visibility = Visibility.Visible;
            AlertTitle.Text = $"需要留意的 {report.Alerts.Count} 项";
            AlertList.ItemsSource = report.Alerts;
        }

        FooterText.Text = report.PriorYearDate.HasValue
            ? $"同比基准：{report.PriorYearDate.Value:yyyy-MM-dd}　·　阈值写在 FinancialAnalyzer 里"
            : "没有去年同期数据，同比一栏为空";

        // 默认最大化（2026-08-27 用户要求）。先 SizeToScreen 定下**还原尺寸**——用户双击标题栏
        // 退出最大化时会回到屏幕九成大，而不是 XAML 里那个 900x500 的下限值。
        //
        // 为什么在 Loaded 里设而不是 XAML 里写 WindowState="Maximized"：WPF 要先完成一次布局
        // 才能可靠地进入最大化，直接在构造或 XAML 里设有时不生效（同 MainWindow/QuoteDetailWindow
        // 的做法，那边注释也记了这一点）。
        TextFitter.SizeToScreen(this);
        Loaded += (_, _) => WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// 装资金面诊断（第①列下半部分）。跟观察项一样由 <see cref="Open"/> 在构造之后调用：
    /// 构造函数在 report.Error 时会提前 return，写在里面的话"没有财务数据"的票就看不到诊断了
    /// ——而那种票恰恰更需要从资金面看它在发生什么。
    /// </summary>
    public void LoadDiagnosis(CapitalDiagnosis diagnosis)
    {
        _diagnosis = diagnosis;
        if (diagnosis.Error != null)
        {
            DiagnosisNoteText.Text = diagnosis.Error;
            return;
        }
        DiagnosisPanel.Load(diagnosis);
        DiagnosisNoteText.Text = diagnosis.GlobalNote;
    }

    private static AnalysisSectionVm ToVm(AnalysisSection sec) => new()
    {
        Title = sec.Title,
        Conclusion = sec.Conclusion,
        Lines = sec.Lines.Select(l => new AnalysisLineVm
        {
            Mark = MarkOf(l.Verdict),
            Label = l.Label,
            Value = l.Value,
            Change = l.Change,
            Note = l.Note,
            Brush = BrushOf(l.Verdict),
            // 明确负面的加粗——扫一眼就能定位到问题所在
            Weight = l.Verdict == Verdict.Bad ? FontWeights.Bold : FontWeights.Normal,
            Reference = l.Reference,
            Clause = l.ClauseNo > 0 ? l.ClauseNo.ToString("D2") : "",
        }).ToList(),
    };

    private static string MarkOf(Verdict v) => v switch
    {
        Verdict.Good => "✓",
        Verdict.Warn => "!",
        Verdict.Bad => "✗",
        Verdict.Missing => "—",
        _ => "",
    };

    /// <summary>判定 → 颜色。负面用红（跟"跌"同色系是有意的，A股语境下红色也表示下跌那一侧的坏消息
    /// 会引起歧义，所以这里的红取深一点的暗红，跟行情涨跌的亮红区分开）。</summary>
    private static Brush BrushOf(Verdict v) => v switch
    {
        Verdict.Good => ThemeBrushes.Ok,
        Verdict.Warn => ThemeBrushes.Warn,
        Verdict.Bad => ThemeBrushes.Danger,
        Verdict.Missing => ThemeBrushes.Gray,
        // 无判定的普通行用正文色——深色主题下这里原来写死的近黑色会整行看不见
        _ => ThemeBrushes.Foreground,
    };

    /// <summary>整份分析导成纯文本——方便贴进【分析笔记】。</summary>
    private string ToPlainText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{_report.Code} {_report.Name}　分析详情　{_report.PeriodName}".TrimEnd());
        if (_report.Error != null) { sb.AppendLine(_report.Error); return sb.ToString(); }
        sb.AppendLine($"【总体】{_report.Headline}");
        sb.AppendLine();
        foreach (var sec in _report.Sections)
        {
            sb.AppendLine(sec.Title);
            foreach (var l in sec.Lines)
                sb.AppendLine($"  {MarkOf(l.Verdict)} {l.Label}  {l.Value}"
                              + (l.Change.Length > 0 ? $"  {l.Change}" : "")
                              + (l.Note.Length > 0 ? $"   — {l.Note}" : ""));
            if (sec.Conclusion.Length > 0) sb.AppendLine($"  → {sec.Conclusion}");
            sb.AppendLine();
        }
        if (_report.Alerts.Count > 0)
        {
            sb.AppendLine($"需要留意的 {_report.Alerts.Count} 项：");
            foreach (var a in _report.Alerts) sb.AppendLine($"  ✗ {a}");
            sb.AppendLine();
        }
        foreach (var t in _report.Trends)
            sb.AppendLine($"{t.Name}（{t.Unit}）：" + string.Join("  ",
                t.Points.Select(p => $"{p.Period:yy/MM}={p.Value:F2}")));

        // 资金面诊断（2026-09-16 用户要求一并带上）。只导**结论**不导证据表：
        // 贴进分析笔记的是判断，几十行区间对照数字贴过去没人会再读一遍，
        // 要查证据回窗口里看即可。告警照导——它决定结论可不可信。
        if (_diagnosis is { Error: null } d)
        {
            sb.AppendLine();
            sb.AppendLine($"【资金面诊断】锚点 {d.Windows.AnchorDate:yyyy-MM-dd} @ {d.AnchorClose:F2}"
                          + $"　截止 {d.AsOf:yyyy-MM-dd}　累计 {d.AnchorChangePct:+0.00;-0.00}%");
            foreach (var dim in d.Dimensions)
            {
                sb.AppendLine($"{dim.Index}. {dim.Title}");
                foreach (var w in dim.Warnings) sb.AppendLine($"   ⚠ {w}");
                if (dim.Unavailable != null) sb.AppendLine($"   （不可用）{dim.Unavailable}");
                foreach (var c in dim.Conclusions) sb.AppendLine($"   {c}");
            }
            sb.AppendLine(d.GlobalNote);
        }
        return sb.ToString();
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ToPlainText()); }
        catch (Exception) { /* 剪贴板被占用，静默 */ }
    }

    /// <summary>
    /// 打开某只票的财务分析——**唯一入口**，列表页的按钮和【行情详情】窗口里的按钮都走这里。
    ///
    /// 数据全部来自本地库（不联网），单只票几十个报告期是毫秒级查询，所以同步跑，
    /// 不值得为它转异步。库里没数据时 FinancialAnalyzer 会返回带 Error 的报告，
    /// 窗口自己显示"请先拉取财务报表"，不需要调用方判断。
    /// </summary>
    public static void Open(Window owner, ViewModels.MainViewModel vm, string code, string name)
    {
        try
        {
            var history = vm.FinancialRepository.GetAllByCode(code);

            // 现价和每股股息用于估值/股息率；取不到就让分析器跳过那几行
            double? price = null;
            DateTime? priceDate = null;
            var bars = vm.BarRepository.Query(code, Granularity.Day);
            if (bars.Count > 0) { price = bars[^1].Close; priceDate = bars[^1].PeriodStart; }

            double? dps = null;
            var trailing = vm.DividendRepository.GetTrailingCashDividendPerShare(DateTime.Today.AddYears(-1));
            if (trailing.TryGetValue(code, out var d) && d > 0) dps = d;

            // 银行才需要行业分位（体检表的"参考值"列）。先用最新一期判一下类型，不是银行就
            // 别去扫全市场——那一趟不便宜。
            StockPlatform.Logic.Models.BankPeerStats? peers = null;
            List<StockPlatform.Logic.Models.BankRegulatoryMetric>? regulatory = null;
            var kind = history.Count > 0
                ? StockPlatform.Logic.Services.BankHealthCheckBuilder.ClassifyInstitution(history[0])
                : StockPlatform.Logic.Models.FinancialInstitutionKind.NonFinancial;
            if (kind is StockPlatform.Logic.Models.FinancialInstitutionKind.Bank
                     or StockPlatform.Logic.Models.FinancialInstitutionKind.Broker
                     or StockPlatform.Logic.Models.FinancialInstitutionKind.Insurer)
            {
                // 行业分位只有银行用得上——券商/保险的参考值是监管红线，不需要分位。
                if (kind == StockPlatform.Logic.Models.FinancialInstitutionKind.Bank)
                    peers = LoadBankPeers(vm);
                // 从财报 PDF 解析出的监管指标（不良率/拨备覆盖率/核心一级/客户集中度）。
                // 没跑过 Fetcher 的【银行监管指标】时这里是空的，体检表会如实显示"待接入"。
                try
                {
                    regulatory = new StockPlatform.Data.Sqlite.SqliteBankRegulatoryRepository(
                        vm.CurrentDbPath).GetByCode(code);
                }
                catch { /* 这张表是可选增强，取不到不该拦住整份财务分析 */ }
            }

            // 总股本（PE/PB 的股数）。取不到就传 null，分析器会回退用报表实收资本并在界面上标识
            // ——这两个口径只有面值 1.00 元才相等，详见 MetricKeys.TotalShares。
            double? totalShares = LoadTotalShares(vm, code);

            // 行业 PE 分位（2026-09-14）。没有行业归属的票（退市股、个别新股，实测 41 只）
            // 拿不到，PE 行就只显示全市场那半句——跟这个功能上线前的行为一致。
            var industryPe = LoadIndustryPe(vm, code);

            var report = new StockPlatform.Logic.Services.FinancialAnalyzer(peers)
                .Analyze(code, name, history, price, dps, regulatory, null, totalShares, priceDate, industryPe);

            var w = new FinancialAnalysisWindow(report) { Owner = owner };
            // 第①列的事件。**在构造之后单独装**：构造函数在 report.Error 时会提前 return，
            // 写在里面的话"没有财务数据"的票就看不到事件了——而那恰恰是最该看事件的时候。
            w.WatchPanel.Load(vm.WatchService, vm.NoteStore, code, name);

            // 第①列的资金面诊断。跟财务分析不同，这个**必须推到后台线程**：
            // MarginDetail / BlockTrade / Lhb 的主键最左列都不是股票代码，按代码查是全表扫；
            // 同业中位数还要扫同行业几百只票在三个区间的首尾K线。在几 GB 的库上合计可能一两秒，
            // 卡在 UI 线程上会让窗口先白一片再出来（"其他数据"按钮当初就是为此推的后台）。
            // 窗口先开，诊断算完再填——用户可以先看财报，不用干等。
            _ = LoadDiagnosisAsync(w, vm.CurrentDbPath, code, name);

            w.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"财务分析失败：{ex.Message}", "财务分析",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 后台读库 + 算诊断，回到 UI 线程填进窗口。
    ///
    /// 失败不弹框、只在那块位置写一行原因：资金面诊断是这个窗口的**附加**内容，
    /// 库里缺哪张表（老版本库没有 BlockTrade / NetInflowDetail）都不该拦住财务分析。
    /// </summary>
    private static async Task LoadDiagnosisAsync(
        FinancialAnalysisWindow w, string dbPath, string code, string name)
    {
        try
        {
            var diagnosis = await Task.Run(() =>
            {
                var reader = new StockPlatform.Data.Sqlite.SqliteCapitalDiagnosisReader(dbPath);
                var (input, windows) = reader.Read(code, name);
                return new StockPlatform.Logic.Services.CapitalDiagnosisAnalyzer().Analyze(input, windows);
            });
            w.LoadDiagnosis(diagnosis);
        }
        catch (Exception ex)
        {
            w.DiagnosisNoteText.Text = $"资金面诊断读取失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 取这只票最新的总股本（<c>MetricKeys.TotalShares</c>，Fetcher 的【总股本】日更）。
    ///
    /// 取不到就返回 null —— 没跑过那一项、或这只票接口里没有（退市/停牌居多）。调用方据此
    /// 回退报表实收资本并标识，**不在这里替它猜**：偏大那一类（H 股会计口径，中国移动
    /// 4703.59 亿元 vs 216.91 亿股）没有任何本地判据能发现。
    ///
    /// 不缓存：一次查询按主键走索引，比银行分位那种全市场扫描便宜得多；而且缓存了就得处理
    /// "用户中途跑完抓取"的失效问题，不值当。
    /// </summary>
    private static double? LoadTotalShares(ViewModels.MainViewModel vm, string code)
    {
        try
        {
            var rows = new StockPlatform.Data.Sqlite.SqliteFundamentalMetricRepository(vm.CurrentDbPath)
                .Query(code, StockPlatform.Logic.Models.MetricKeys.TotalShares);
            var latest = rows.OrderByDescending(r => r.AsOfDate).FirstOrDefault();
            return latest is { Value: > 0 } ? latest.Value : null;
        }
        catch { return null; }   // 这一项是可选增强，取不到不该拦住整份财务分析
    }

    // ── 行业 PE 分位的会话内缓存 ──────────────────────────────────────────────
    // 算一次要扫全市场（3900 多只票的 TTM 净利 + 总股本 + 最新收盘），四条 SQL 约 1~2 秒。
    // 连着看好几只票时不该每次都重算——这个分布一天之内基本不动。缓存策略跟银行分位一致。
    private static Dictionary<string, StockPlatform.Logic.Models.IndustryPeStats>? _cachedIndustryPe;
    private static DateTime _cachedIndustryPeAt;

    /// <summary>
    /// 实算这只票所属行业的 PE 分位。返回 null 表示这只票没有行业归属、或所属行业样本太少
    /// （二级 &lt;10 只会退到一级，一级也不够才是 null）——那时 PE 行只显示全市场那半句。
    ///
    /// ⚠ 不走内置快照那条路：行业分位有 31+127 组，硬编码进代码既丑又会过期；而且
    /// **一次扫描把所有行业一起算出来，成本和只算一个全市场中位完全一样**。
    /// </summary>
    private static StockPlatform.Logic.Models.IndustryPeStats? LoadIndustryPe(
        ViewModels.MainViewModel vm, string code)
    {
        try
        {
            if (_cachedIndustryPe == null || DateTime.Now - _cachedIndustryPeAt > TimeSpan.FromMinutes(30))
            {
                var (pes, industries) = new StockPlatform.Data.Sqlite.SqliteMarketPeSource(vm.CurrentDbPath).Read();
                _cachedIndustryPe = StockPlatform.Logic.Services.IndustryPeStatsBuilder.Build(pes, industries);
                _cachedIndustryPeAt = DateTime.Now;
            }
            return _cachedIndustryPe.GetValueOrDefault(code);
        }
        catch
        {
            // 跟银行分位同样的态度：参考值那一列算不出来，不该让整个财务分析打不开。
            return null;
        }
    }

    // ── 银行行业分位的会话内缓存 ──────────────────────────────────────────────
    // 算一次要扫一遍全市场最新快照 + 逐只取 42 家银行的历史，几秒级。连着看好几只银行时不该
    // 每次都重算——分位是季度级别的量，一个会话里根本不会变。30 分钟够覆盖一次连续查看，
    // 又不至于在用户中途跑完抓取后还拿着旧的。
    private static StockPlatform.Logic.Models.BankPeerStats? _cachedPeers;
    private static DateTime _cachedPeersAt;

    /// <summary>
    /// 从本地库实算银行行业分位。算不出来（数据不足）就返回 null，由 FinancialAnalyzer 退回
    /// <see cref="StockPlatform.Logic.Models.BankPeerStats.Builtin"/> 那份带日期的内置基准。
    ///
    /// 银行还没按新科目集重抓时，库里没有 interest_net，<c>ClassifyInstitution</c> 一家银行都
    /// 认不出来 → 样本为空 → 用内置基准。这是有意的安全降级，不是 bug。
    /// </summary>
    private static StockPlatform.Logic.Models.BankPeerStats? LoadBankPeers(ViewModels.MainViewModel vm)
    {
        if (_cachedPeers != null && DateTime.Now - _cachedPeersAt < TimeSpan.FromMinutes(30))
            return _cachedPeers;
        try
        {
            var latest = vm.FinancialRepository.GetLatestSnapshotByCode();
            var bankCodes = latest
                .Where(kv => StockPlatform.Logic.Services.BankHealthCheckBuilder
                                 .ClassifyInstitution(kv.Value)
                             == StockPlatform.Logic.Models.FinancialInstitutionKind.Bank)
                .Select(kv => kv.Key)
                .ToList();

            var samples = new List<(FinancialSnapshot Cur, FinancialSnapshot? Prev)>();
            foreach (var c in bankCodes)
            {
                // 逐只取（约 42 次）——每次走 (code, report_date) 主键前缀，很快。
                // ROE/ROA 的分母要期初期末均值，所以必须拿到上一期，批量快照接口只给最新一期。
                var h = vm.FinancialRepository.GetAllByCode(c);
                if (h.Count > 0) samples.Add((h[0], h.Count > 1 ? h[1] : null));
            }

            _cachedPeers = StockPlatform.Logic.Services.BankPeerStatsBuilder.Build(samples);
            _cachedPeersAt = DateTime.Now;
            return _cachedPeers;
        }
        catch
        {
            // 分位只是"参考值"那一列，算不出来不该让整个财务分析打不开。
            return null;
        }
    }
}
