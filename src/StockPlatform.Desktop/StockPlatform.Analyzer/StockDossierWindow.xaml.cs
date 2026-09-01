using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OxyPlot;
using StockPlatform.Logic.Models;
using StockPlatform.Desktop.Shared.Theme;

namespace StockPlatform.Analyzer;

/// <summary>
/// "其他数据"——把一只标的在本地库里除K线之外的所有数据摊开看（数据来自
/// <see cref="StockPlatform.Data.Sqlite.SqliteStockDossierReader"/>，入口是"行情详情"窗口右边那个
/// 按钮）。一节一个页签，页签标题带行数，空节保留并显示"（无）"，让"没抓这类数据"和"抓了但这只票
/// 没有"能区分开。
///
/// 时间序列的节上半部分是趋势图（<see cref="DossierChartBuilder"/>）、下半部分是明细表，中间可拖
/// 分隔条：图回答"在往哪个方向走"，表回答"具体某天是多少"，两个问题都常有。快照/名单类的节（所属
/// 板块、所属指数、龙虎榜、公告）只有表——本地没有历史，画不出趋势，硬画会造出假信息。
///
/// 表格列全部是字符串（读取时就格式化好了，见 <see cref="StockDossier"/>），所以**关掉列排序**
/// ——"12.34亿"/"1,234"这种带单位和千分位的文本按字典序排出来是错的。各节读取时已经按"最新在前"
/// 排好。表格只读、允许连表头复制（Ctrl+C），方便贴到 Excel 里再自己排。
/// </summary>
public partial class StockDossierWindow : Window
{
    /// <summary>图占该页签的高度比例——图看趋势、表查具体值，图稍小一点，剩下的给表。用户可拖。</summary>
    private const double ChartHeightStar = 1.0;
    private const double TableHeightStar = 1.4;

    public StockDossierWindow(StockDossier dossier)
    {
        InitializeComponent();

        Title = $"其他数据 — {dossier.Code} {dossier.Name}".TrimEnd();
        TitleText.Text = $"{dossier.Code} {dossier.Name}".TrimEnd();

        int withData = dossier.Sections.Count(s => s.Rows.Count > 0);
        int withChart = dossier.Sections.Count(s => s.Chart != null);
        SubtitleText.Text = $"本地库里除K线之外的全部数据，共 {dossier.Sections.Count} 类、其中 {withData} 类有数据、{withChart} 类带趋势图。" +
                            "图可滚轮缩放、左键拖动、双击复位，鼠标移上去看当期读数；表按\"最新在前\"排列，列已关闭排序（值是带单位的文本，字典序排出来是错的），需要排序请 Ctrl+C 复制到 Excel。";

        BuildTabs(dossier);
    }

    private void BuildTabs(StockDossier dossier)
    {
        foreach (var section in dossier.Sections)
        {
            var count = section.Error != null ? "!" : section.Rows.Count > 0 ? section.Rows.Count.ToString() : "无";
            SectionTabs.Items.Add(new TabItem
            {
                Header = $"{section.Title} ({count})",
                Content = BuildSectionContent(section),
            });
        }

        // 默认停在第一个有数据的页签上——第一节"基本信息"永远有数据，所以实际就是它，
        // 但万一将来第一节也可能为空，这样不会开在一个空表上。
        SectionTabs.SelectedIndex = Math.Max(0, dossier.Sections.FindIndex(s => s.Rows.Count > 0));
    }

    private static FrameworkElement BuildSectionContent(DossierSection section)
    {
        var grid = new Grid { Margin = new Thickness(6) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 节说明
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 图（有的话）
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 分隔条
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TableHeightStar, GridUnitType.Star) });

        var note = new TextBlock
        {
            Text = section.Note,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrushes.Gray,
            Margin = new Thickness(0, 0, 0, 6),
        };
        Grid.SetRow(note, 0);
        grid.Children.Add(note);

        FrameworkElement body = section.Error != null
            ? Message($"读取失败：{section.Error}\n（这一节读不到不影响其它节；常见原因是本地数据库是旧版本、还没有这张表或这个字段）", ThemeBrushes.Firebrick)
            : section.Rows.Count == 0
                ? Message("本地库里这只标的没有这类数据。", ThemeBrushes.Gray)
                : BuildDataGrid(section);
        Grid.SetRow(body, 3);
        grid.Children.Add(body);

        if (section.Chart != null && section.Error == null)
        {
            var chart = BuildChartPanel(section.Chart);
            grid.RowDefinitions[1].Height = new GridLength(ChartHeightStar, GridUnitType.Star);
            Grid.SetRow(chart, 1);
            grid.Children.Add(chart);

            var splitter = new GridSplitter
            {
                Height = 6,
                // 全限定：这个类继承自 Window(→FrameworkElement)，简单名 VerticalAlignment 会先解析
                // 成继承来的那个**属性**而不是枚举类型（QuoteDetailWindow 里同样这么写）。
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ToolTip = "上下拖动，调整图和表格的高度比例",
            };
            // 这一屏是代码拼出来的，拿不到 XAML 的 DynamicResource；SetResourceReference 是它的
            // 代码写法，换主题时同样会自己变色。
            splitter.SetResourceReference(BackgroundProperty, "Theme.Border");
            Grid.SetRow(splitter, 2);
            grid.Children.Add(splitter);
        }

        return grid;
    }

    /// <summary>一张趋势图的整块：图例 + 悬浮读数 + PlotView。读数和十字线的联动跟"行情详情"窗口
    /// 一个做法（鼠标横向位置反算成第几个点），见 <see cref="DossierChartBuilder"/>。</summary>
    private static FrameworkElement BuildChartPanel(DossierChart chart)
    {
        var built = DossierChartBuilder.Build(chart);

        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 图例
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 读数
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 图的口径提醒
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var legend = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var item in built.Legend)
        {
            foreach (var swatch in item.Swatches)
                legend.Children.Add(new Rectangle
                {
                    Width = 14,
                    Height = 3,
                    Fill = new SolidColorBrush(Color.FromRgb(swatch.R, swatch.G, swatch.B)),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 3, 0),
                });
            legend.Children.Add(new TextBlock
            {
                Text = item.Label,
                FontSize = 11,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new Thickness(1, 0, 14, 0),
            });
        }
        Grid.SetRow(legend, 0);
        panel.Children.Add(legend);

        var readout = new TextBlock
        {
            Text = built.FormatInfo(built.PointCount - 1),   // 默认显示最新一期，不用等鼠标移上去
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetRow(readout, 1);
        panel.Children.Add(readout);

        if (!string.IsNullOrEmpty(chart.Note))
        {
            var chartNote = new TextBlock
            {
                Text = chart.Note,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = ThemeBrushes.Gray,
                Margin = new Thickness(0, 2, 0, 2),
            };
            Grid.SetRow(chartNote, 2);
            panel.Children.Add(chartNote);
        }

        var plotView = new OxyPlot.Wpf.PlotView
        {
            Model = built.Model,
            Controller = CreatePlotController(),
        };
        plotView.MouseMove += (_, e) =>
        {
            if (built.PointCount == 0) return;
            var x = built.XAxis.InverseTransform(e.GetPosition(plotView).X);
            int idx = Math.Clamp((int)Math.Round(x), 0, built.PointCount - 1);
            built.Crosshair.X = idx;
            built.Model.InvalidatePlot(false);
            readout.Text = built.FormatInfo(idx);
        };
        Grid.SetRow(plotView, 3);
        panel.Children.Add(plotView);

        return panel;
    }

    /// <summary>跟"行情详情"里的图一样的操作方式：左键拖动平移、滚轮缩放、右键框选缩放、双击复位。</summary>
    private static PlotController CreatePlotController()
    {
        var controller = new PlotController();
        controller.UnbindAll();
        controller.BindMouseDown(OxyMouseButton.Left, PlotCommands.PanAt);
        controller.BindMouseWheel(PlotCommands.ZoomWheel);
        controller.BindMouseDown(OxyMouseButton.Right, PlotCommands.ZoomRectangle);
        controller.BindMouseDown(OxyMouseButton.Left, OxyModifierKeys.None, 2, PlotCommands.ResetAt);
        return controller;
    }

    private static TextBlock Message(string text, Brush foreground) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = foreground,
        VerticalAlignment = System.Windows.VerticalAlignment.Top,
    };

    private static DataGrid BuildDataGrid(DossierSection section)
    {
        var dataGrid = new DataGrid
        {
            ItemsSource = section.Rows,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserSortColumns = false,   // 见类注释：字符串列的字典序排序会骗人
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
            EnableRowVirtualization = true,   // 净流入/融资融券可能上千行
        };
        dataGrid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "Theme.Grid.Row.Alternate");

        for (int i = 0; i < section.Columns.Count; i++)
        {
            var col = section.Columns[i];
            var column = new DataGridTextColumn
            {
                Header = col.Header,
                // 行是 string[]，用索引器绑定取第 i 个单元格。
                Binding = new Binding($"[{i}]"),
                Width = col.Width > 0 ? new DataGridLength(col.Width) : new DataGridLength(1, DataGridLengthUnitType.Star),
            };
            if (col.RightAlign)
            {
                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
                column.ElementStyle = style;
            }
            dataGrid.Columns.Add(column);
        }
        return dataGrid;
    }
}
