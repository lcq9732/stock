using System.Text;
using System.Windows;
using System.Windows.Media;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Analyzer;

/// <summary>绑给界面的一行。颜色和标记在这里预先算好，XAML 那边就不用写 converter。</summary>
public class AnalysisLineVm
{
    public string Mark { get; init; } = "";
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Change { get; init; } = "";
    public string Note { get; init; } = "";
    public Brush Brush { get; init; } = Brushes.Black;
    public FontWeight Weight { get; init; } = FontWeights.Normal;
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

    public FinancialAnalysisWindow(FinancialAnalysisReport report)
    {
        InitializeComponent();
        _report = report;

        Title = $"财务分析 — {report.Code} {report.Name}".TrimEnd();

        if (report.Error != null)
        {
            HeaderText.Text = $"{report.Code} {report.Name}".TrimEnd();
            HeadlineText.Text = report.Error;
            HeadlineBorder.Background = new SolidColorBrush(Color.FromRgb(0xFD, 0xF2, 0xF2));
            FooterText.Text = "";
            // 出错时只是一句提示，不最大化——一屏小窗口就够，最大化反而突兀
            TextFitter.SizeToScreen(this, 0.5);
            return;
        }

        // 总体判断并进标题行（2026-08-27 用户要求）——省掉一整行黄底框，而且那句话本来就是
        // 标题的一部分。数值在 FinancialAnalyzer.BuildHeadline 里已经并进去了。
        HeadlineBorder.Visibility = Visibility.Collapsed;
        HeaderText.Inlines.Add(new System.Windows.Documents.Run(
            $"{report.Code} {report.Name}".TrimEnd()) { FontWeight = FontWeights.Bold });
        if (report.ReportDate != default)
            HeaderText.Inlines.Add(new System.Windows.Documents.Run($"　·　{report.PeriodName}")
                { FontSize = 14 });
        if (report.IsFinancialInstitution)
            HeaderText.Inlines.Add(new System.Windows.Documents.Run("　·　金融机构（简版指标）")
                { FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)) });
        if (report.Headline.Length > 0)
            HeaderText.Inlines.Add(new System.Windows.Documents.Run($"　·　{report.Headline}")
            {
                FontSize = 13,
                FontWeight = FontWeights.Normal,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x45, 0x13)),
            });

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

        TrendHintText.Text = report.Trends.Count > 0
            ? "只取跟本期同月份的报告期（A股报表是年内累计口径，混着比会画成锯齿）。几张图共用最下面" +
              "那条横坐标，同一列必是同一期。"
            : "报告期不足，画不出趋势。";

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
        Verdict.Good => new SolidColorBrush(Color.FromRgb(0x1E, 0x7A, 0x33)),
        Verdict.Warn => new SolidColorBrush(Color.FromRgb(0xB8, 0x6E, 0x00)),
        Verdict.Bad => new SolidColorBrush(Color.FromRgb(0xB0, 0x20, 0x20)),
        Verdict.Missing => new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
        _ => new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
    };

    /// <summary>整份分析导成纯文本——方便贴进【分析笔记】。</summary>
    private string ToPlainText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{_report.Code} {_report.Name}　财务分析　{_report.PeriodName}".TrimEnd());
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
        return sb.ToString();
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ToPlainText()); }
        catch (Exception) { /* 剪贴板被占用，静默 */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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
            var bars = vm.BarRepository.Query(code, Granularity.Day);
            if (bars.Count > 0) price = bars[^1].Close;

            double? dps = null;
            var trailing = vm.DividendRepository.GetTrailingCashDividendPerShare(DateTime.Today.AddYears(-1));
            if (trailing.TryGetValue(code, out var d) && d > 0) dps = d;

            var report = new StockPlatform.Logic.Services.FinancialAnalyzer()
                .Analyze(code, name, history, price, dps);
            new FinancialAnalysisWindow(report) { Owner = owner }.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"财务分析失败：{ex.Message}", "财务分析",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
