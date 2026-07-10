using System.Windows;
using Microsoft.Win32;
using StockPlatform.Analyzer.ViewModels;

namespace StockPlatform.Analyzer.Export;

/// <summary>
/// "导出Excel" for the five analysis-result grids and the watchlist grid. Builds the same columns the
/// grid shows (see MainWindow.xaml), prompts for a save location, and writes a real .xlsx via
/// XlsxWriter. Lives here (not per-tab) so all six tabs export consistently; each tab's ExportCommand
/// just calls the matching method with its own collection.
/// </summary>
public static class GridExporter
{
    /// <summary>Analysis-result grids. <paramref name="includeScore"/> adds a column bound to
    /// SortScore (三角收敛 "收敛质量"、短线法 "近15日涨停"，header via <paramref name="scoreHeader"/>);
    /// <paramref name="includeTotalCount"/> adds 金叉法's "共几条" column.</summary>
    public static void ExportResults(string methodName, IReadOnlyList<ResultRowViewModel> rows,
        bool includeScore = false, bool includeTotalCount = false, string scoreHeader = "收敛质量")
    {
        if (rows.Count == 0)
        {
            MessageBox.Show("当前没有可导出的结果，请先运行分析。", "导出Excel", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var headers = new List<string> { "代码", "名称", "板块" };
        if (includeScore) headers.Add(scoreHeader);
        headers.Add("满足数");
        if (includeTotalCount) headers.Add("共几条");

        var data = new List<IReadOnlyList<string>>();
        foreach (var r in rows)
        {
            var cells = new List<string> { r.Code, r.Name, r.Board };
            if (includeScore) cells.Add(r.ConvergenceQualityText);
            cells.Add(r.SatisfiedCount.ToString());
            if (includeTotalCount) cells.Add(r.TotalCount.ToString());
            data.Add(cells);
        }

        SaveWithDialog(methodName, headers, data);
    }

    /// <summary>自选股 grid — matches the columns in MainWindow.xaml's WatchlistGrid.</summary>
    public static void ExportWatchlist(IReadOnlyList<WatchlistRowViewModel> rows)
    {
        if (rows.Count == 0)
        {
            MessageBox.Show("自选股列表为空，没有可导出的数据。", "导出Excel", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var headers = new List<string> { "代码", "名称", "方法", "数据日期", "当时价格", "最新收盘", "选中后涨跌幅", "满足数", "加入时间" };
        var data = rows.Select(r => (IReadOnlyList<string>)new List<string>
        {
            r.Code, r.Name, r.Method, r.DataDate, r.PriceAtPick.ToString("F2"),
            r.LatestCloseText, r.ChangeText, r.SatisfiedText, r.AddedAt,
        }).ToList();

        SaveWithDialog("自选股", headers, data);
    }

    private static void SaveWithDialog(string methodName, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> data)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出Excel",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            FileName = $"{methodName}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            XlsxWriter.Write(dialog.FileName, methodName, headers, data);
            MessageBox.Show($"已导出 {data.Count} 行到：\n{dialog.FileName}", "导出Excel", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}\n（如果文件已在 Excel 中打开，请先关闭再导出）", "导出Excel", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
