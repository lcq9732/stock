using System.Windows;

namespace StockPlatform.Analyzer;

/// <summary>
/// 【底仓】页每行的【分红历史】——这只票的历年每股派息柱状图。图的构建和配色见
/// <see cref="CorePositionChartBuilder"/>（跟底仓法条件详情里那张是同一个）。
///
/// 持仓页没有"条件详情"（它不是筛选结果），但"这家公司分红是一贯的、还是最近几年才开始的"
/// 对底仓是核心判断——底仓没有价格止损，分红中断就是它唯一的退出信号。
/// </summary>
public partial class DividendHistoryWindow : Window
{
    public DividendHistoryWindow(string code, string name, IReadOnlyList<(int Year, double PerShare)> annualDividends)
    {
        InitializeComponent();
        Title = $"{name}（{code}）— 分红历史";
        int years = annualDividends?.Count ?? 0;
        double total = annualDividends?.Sum(x => x.PerShare) ?? 0;
        HeaderText.Text = years > 0
            ? $"{code} {name}　　有派息记录 {years} 年，累计每股派息 {total:F3} 元（税前）"
            : $"{code} {name}　　本地没有该股的分红记录";
        DividendPlot.Model = CorePositionChartBuilder.BuildDividendChart(
            annualDividends ?? new List<(int Year, double PerShare)>());
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
