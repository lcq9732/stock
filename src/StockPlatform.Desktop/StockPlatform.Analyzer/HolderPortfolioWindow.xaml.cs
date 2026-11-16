using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer;

/// <summary>
/// "股东持仓"——某个股东（按名称）在本地库里还出现在哪些票的十大（流通）股东名单里。入口是"其他数据"
/// 窗口里点股东名称；数据来自 <see cref="StockPlatform.Data.Sqlite.SqliteHolderPortfolioReader"/>。
///
/// 一只票一行，默认只列**仍在前十**的（这只票最新一期名单里还有他），按占比倒序；勾"含已退出"
/// 把掉出前十的也列出来（排在后面）。每行可展开看这只票上的明细（按日期倒序）：各期名单里的持仓
/// 变动 + 增减持公告。双击一行打开那只票的行情详情。
/// </summary>
public partial class HolderPortfolioWindow : Window
{
    private readonly HolderPortfolio _portfolio;
    private readonly Action<Window, string, string>? _openQuote;
    private readonly ListCollectionView _view;

    public HolderPortfolioWindow(HolderPortfolio portfolio, Action<Window, string, string>? openQuote)
    {
        InitializeComponent();
        _portfolio = portfolio;
        _openQuote = openQuote;

        var kindLabel = portfolio.Kind == TopShareholderRow.KindTotal ? "十大股东" : "十大流通股东";
        Title = $"股东持仓 — {portfolio.HolderName}";
        TitleText.Text = portfolio.HolderName;

        int current = portfolio.Positions.Count(p => !p.Exited);
        SubtitleText.Text =
            $"本地库{kindLabel}名单里出现过这个名字的股票共 {portfolio.Positions.Count} 只，其中 {current} 只最新一期仍在前十。" +
            $"占比是占{(portfolio.Kind == TopShareholderRow.KindTotal ? "总股本" : "流通股")}。" +
            "点左侧 + 展开该票明细（前十名单逐期变动 + 增减持公告，日期倒序）；双击一行打开那只票的行情详情。" +
            "只看得到进了前十的持仓——没进前十的仓位本地没有数据。";
        NaturalPersonBanner.Visibility = portfolio.IsNaturalPerson ? Visibility.Visible : Visibility.Collapsed;

        // 一只都还在前十的时候默认就把已退出的也显示出来，不然是个空表
        IncludeExitedCheck.IsChecked = current == 0;

        _view = new ListCollectionView(portfolio.Positions);
        ApplyFilter();
        PositionsGrid.ItemsSource = _view;
    }

    private void ApplyFilter()
    {
        bool includeExited = IncludeExitedCheck.IsChecked == true;
        _view.Filter = includeExited ? null : o => !((HolderPosition)o).Exited;
        CountText.Text = $"显示 {_view.Count} / {_portfolio.Positions.Count} 只";
    }

    private void IncludeExitedCheck_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    private void PositionsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_openQuote == null || e.OriginalSource is not DependencyObject source) return;
        // 双击发生在展开的明细表里（内层 DataGrid）就不算——那是在看明细，不是要打开这只票
        if (FindAncestor<DataGrid>(source) != PositionsGrid) return;
        if (FindAncestor<ToggleButton>(source) != null) return;
        if (FindAncestor<DataGridRow>(source)?.Item is not HolderPosition position) return;
        _openQuote(this, position.Code, position.Name);
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}

/// <summary>Visibility ⇄ bool（展开按钮绑行的 DetailsVisibility 用）。</summary>
public sealed class VisibilityToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
}
