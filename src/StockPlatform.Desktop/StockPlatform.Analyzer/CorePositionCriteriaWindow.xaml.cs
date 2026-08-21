using System.Windows;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer;

/// <summary>
/// 底仓法【条件详情】——左边两张柱状图上下排（历年每股派息 / 历年归母净利），右边条件文字。
/// 图的构建见 <see cref="CorePositionChartBuilder"/>，那里也写了为什么这两项要画成图。
/// 窗口大小和字号自适应见 <see cref="TextFitter"/>（用户 2026-08-20 要求"开大、不要出滚动条"）。
/// </summary>
public partial class CorePositionCriteriaWindow : Window
{
    /// <summary>用户手动点过 +/− 之后就停止自动适配——既然自己定了大小，跟着窗口变化去覆盖会很烦。</summary>
    private bool _fontManuallySet;

    public CorePositionCriteriaWindow(StockScreenResult result)
    {
        InitializeComponent();

        HeaderText.Text =
            $"{result.Code} {result.Name}　　数据日期 {result.DataDate:yyyy-MM-dd}　收盘 {result.LastClose:F2}\n" +
            $"股息率 {(result.DividendYield ?? 0) * 100:F2}%（结果表按这个降序排）" +
            (result.AvgDividendYield is > 0 ? $"　近5年平均 {result.AvgDividendYield.Value * 100:F2}%" : "") +
            (result.ConsecutiveDividendYears is > 0 ? $"　连续分红 {result.ConsecutiveDividendYears} 年" : "") +
            (string.IsNullOrEmpty(result.DividendTrend) ? "" : $"　派息趋势 {result.DividendTrend}");

        DividendPlot.Model = CorePositionChartBuilder.BuildDividendChart(
            result.AnnualDividends ?? new List<(int Year, double PerShare)>());
        ProfitPlot.Model = CorePositionChartBuilder.BuildProfitChart(
            result.AnnualProfits ?? new List<(int Year, double NetProfit)>());

        BodyText.Text = string.Join("\n\n", result.Criteria.Select(c =>
            $"{(c.DataMissing ? "⚠" : c.Satisfied ? "✓" : "✗")} {c.Name}\n    {c.Basis}"));

        TextFitter.SizeToScreen(this);
        Loaded += (_, _) => AutoFit();
        SizeChanged += (_, _) => AutoFit();
    }

    private void AutoFit()
    {
        if (!_fontManuallySet) TextFitter.Fit(BodyText);
    }

    private void SetFontSize(double size)
    {
        _fontManuallySet = true;
        BodyText.FontSize = Math.Clamp(size, TextFitter.MinFontSize, TextFitter.MaxFontSize);
    }

    private void SmallerFont_Click(object sender, RoutedEventArgs e) => SetFontSize(BodyText.FontSize - 1);

    private void LargerFont_Click(object sender, RoutedEventArgs e) => SetFontSize(BodyText.FontSize + 1);

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BodyText.Text);
        }
        catch (Exception)
        {
            // 剪贴板被别的进程占用时会抛——静默算了，用户还能手动拖选复制。
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
