using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using OxyPlot;
using StockPlatform.Data.Sqlite;
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

    /// <summary>本地库路径——点股东名称时反查他的其他持仓要用（<see cref="SqliteHolderPortfolioReader"/>）。
    /// 为 null 时股东名称照样显示成链接，点了提示不可用。</summary>
    private readonly string? _dbPath;

    /// <summary>在股东持仓窗口里双击某只票时打开它的行情详情（参数：owner 窗口、代码、名称）。</summary>
    private readonly Action<Window, string, string>? _openQuote;

    public StockDossierWindow(StockDossier dossier, string? dbPath = null, Action<Window, string, string>? openQuote = null)
    {
        InitializeComponent();
        _dbPath = dbPath;
        _openQuote = openQuote;

        Title = $"其他数据 — {dossier.Code} {dossier.Name}".TrimEnd();
        TitleText.Text = $"{dossier.Code} {dossier.Name}".TrimEnd();

        int withData = dossier.Sections.Count(s => s.Rows.Count > 0);
        int withChart = dossier.Sections.Count(s => s.Chart != null);
        SubtitleText.Text = $"本地库里除K线之外的全部数据，共 {dossier.Sections.Count} 类、其中 {withData} 类有数据、{withChart} 类带趋势图。" +
                            "图可滚轮缩放、左键拖动、双击复位，鼠标移上去看当期读数；表按\"最新在前\"排列，列已关闭排序（值是带单位的文本，字典序排出来是错的），需要排序请 Ctrl+C 复制到 Excel。";

        BuildTabs(dossier);
    }

    /// <summary>
    /// 打开某只票的"其他数据"——**唯一入口**：【行情详情】和【分析详情】两个窗口的按钮都走这里。
    /// 读库放后台（十几张表、附 25GB 库），读完再 ShowDialog。股东持仓窗口里双击某只票时
    /// 用 <paramref name="openQuote"/> 打开它的行情详情（由调用方给，因为要用到仓储和笔记存储）。
    /// </summary>
    public static async Task OpenAsync(Window owner, string dbPath, string code, string name,
        Action<Window, string, string>? openQuote)
    {
        var previousCursor = owner.Cursor;
        owner.Cursor = Cursors.Wait;
        try
        {
            var dossier = await Task.Run(() => new SqliteStockDossierReader(dbPath).Read(code, name));
            owner.Cursor = previousCursor;
            new StockDossierWindow(dossier, dbPath, openQuote) { Owner = owner }.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"读取其他数据失败：{ex.Message}", "其他数据", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            owner.Cursor = previousCursor;
        }
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

        // 默认停在第一个有数据的页签上——第一节是十大流通股东（2026-11-16 起），ETF 这类没有
        // 流通股东数据的就顺延到下一个有数据的节，不开在一个空表上。
        SectionTabs.SelectedIndex = Math.Max(0, dossier.Sections.FindIndex(s => s.Rows.Count > 0));
    }

    private FrameworkElement BuildSectionContent(DossierSection section)
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

    private DataGrid BuildDataGrid(DossierSection section)
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
            var width = col.Width > 0 ? new DataGridLength(col.Width) : new DataGridLength(1, DataGridLengthUnitType.Star);
            if (section.LinkColumn == i)
            {
                dataGrid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = col.Header,
                    Width = width,
                    CellTemplate = LinkCellTemplate(i),
                    ClipboardContentBinding = new Binding($"[{i}]"),   // Ctrl+C 照样复制出名称
                });
                continue;
            }

            var column = new DataGridTextColumn
            {
                Header = col.Header,
                // 行是 string[]，用索引器绑定取第 i 个单元格。
                Binding = new Binding($"[{i}]"),
                Width = width,
            };
            if (col.RightAlign)
            {
                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
                column.ElementStyle = style;
            }
            dataGrid.Columns.Add(column);
        }

        if (section.GroupColumn is int groupColumn && section.Rows.Count > 0)
        {
            // 按某列分组：行已按"最新在前"排好，组的顺序就是行首次出现的顺序，所以第一组=最新一期，
            // 只把它默认展开（其余折叠，一眼看到最新名单，旧的要看再点开）。
            var view = new ListCollectionView(section.Rows);
            view.GroupDescriptions.Add(new ColumnGroupDescription(groupColumn, section.GroupHeaders, section.Rows[0][groupColumn]));
            dataGrid.ItemsSource = view;
            dataGrid.GroupStyle.Add(new GroupStyle { ContainerStyle = (Style)FindResource("DossierGroupContainer") });
            VirtualizingPanel.SetIsVirtualizingWhenGrouping(dataGrid, true);
        }

        if (section.LinkColumn != null)
        {
            var tag = section.LinkTag ?? TopShareholderRow.KindFloat;
            dataGrid.AddHandler(Hyperlink.ClickEvent, new RoutedEventHandler(async (_, e) =>
            {
                if (e.OriginalSource is Hyperlink { DataContext: string[] row } && section.LinkColumn is int c)
                    await OpenHolderAsync(row[c], tag);
            }));
        }
        return dataGrid;
    }

    /// <summary>可点击单元格的模板。列下标要写进绑定路径，所以用 XAML 字符串现拼。</summary>
    private static DataTemplate LinkCellTemplate(int column) => (DataTemplate)XamlReader.Parse($$"""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <TextBlock TextTrimming="CharacterEllipsis">
                <Hyperlink ToolTip="查看这个股东还持有哪些股票" Foreground="{DynamicResource Theme.Accent.Link}">
                    <Run Text="{Binding [{{column}}], Mode=OneWay}" />
                </Hyperlink>
            </TextBlock>
        </DataTemplate>
        """);

    /// <summary>反查这个股东的其他持仓并打开窗口。读库放后台线程（附 25GB 库，没索引的老库要扫一秒多）。</summary>
    private async Task OpenHolderAsync(string holderName, string kind)
    {
        if (string.IsNullOrWhiteSpace(holderName) || holderName == "—") return;
        if (_dbPath == null)
        {
            MessageBox.Show(this, "这里没有拿到本地库路径，无法反查股东持仓。", "股东持仓", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var previousCursor = Cursor;
        Cursor = Cursors.Wait;
        try
        {
            var dbPath = _dbPath;
            var portfolio = await Task.Run(() => new SqliteHolderPortfolioReader(dbPath).Read(holderName, kind));
            Cursor = previousCursor;
            new HolderPortfolioWindow(portfolio, _openQuote) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"读取股东持仓失败：{ex.Message}", "股东持仓", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Cursor = previousCursor;
        }
    }

    /// <summary>组键：标题文字 + 是否默认展开。record 的值相等保证同一期的行落进同一组。</summary>
    private sealed record GroupKey(string Header, bool Expanded);

    /// <summary>按 string[] 行的第 N 列分组（PropertyGroupDescription 认不了数组下标）。</summary>
    private sealed class ColumnGroupDescription(int column, Dictionary<string, string> headers, string expandedKey) : GroupDescription
    {
        public override object GroupNameFromItem(object item, int level, CultureInfo culture)
        {
            var key = ((string[])item)[column];
            return new GroupKey(headers.GetValueOrDefault(key) ?? key, key == expandedKey);
        }
    }
}
