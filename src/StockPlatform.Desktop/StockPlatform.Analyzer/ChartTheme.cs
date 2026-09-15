using System.Runtime.CompilerServices;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using StockPlatform.Desktop.Shared.Theme;

namespace StockPlatform.Analyzer;

/// <summary>
/// 让 OxyPlot 图表跟着界面主题走（2026-08-29 新增）。
///
/// 图表跟别的控件不一样：颜色不是 WPF 画刷、拿不到 DynamicResource，全是 PlotModel/Axis/Series 上的
/// OxyColor 属性，换主题时得逐个改。PlotView 控件本身的底色靠各 XAML 上的
/// <c>{DynamicResource Theme.Chart.Background}</c> 跟随，这里管的是画布**里面**——坐标轴文字、
/// 网格线、图上直接标的数值、零轴/十字线这些标注，以及个别深到在黑底上看不见的线（MACD 零轴是纯黑）。
///
/// ⚠ **新加 PlotView 时别忘了那句 Background**：漏了它画布就是 OxyPlot 的默认白底，而这里已经把
/// 里面的文字刷成了浅灰——白底浅灰字，等于看不见。六个判断依据窗口从做主题起就一直漏着，
/// 2026-09-15 才补上（表现是"这些图没随主题"，很容易误判成 ChartTheme 没生效）。
///
/// 怎么挂上去的：建图的地方（各 ChartBuilder）把 model 交给 <see cref="Track"/> 登记，这里订阅
/// <see cref="PlotModel.Updated"/>——OxyPlot 每次绘制前都会 Update 一遍，那一刻坐标轴和序列都已经
/// 加齐，正好上色。所以 builder 只需在 <c>new PlotModel</c> 处包一层，不用管"什么时候算建完"。
///
/// **别改回用 PlotView 的 Loaded 类处理器**（2026-08-29 踩过）：
/// <c>EventManager.RegisterClassHandler(typeof(PlotView), FrameworkElement.LoadedEvent, …)</c> 看着
/// 一处安装全局生效，实际一次都不会触发——WPF 的 Loaded/Unloaded 是 BroadcastEventHelper 广播出来
/// 的，不走类处理器。当时表现是"图的底色变深了、里面的字还是黑的"，很容易误判成配色没调好。
///
/// 恢复得回去：首次改之前把原来的颜色整份快照进 <see cref="Snapshots"/>（挂在 PlotModel 上的弱表，
/// model 被回收快照自动消失）。切回浅色时按快照还原，而不是"再猜一套浅色值"——所以浅色下图表跟
/// 做主题之前完全一样，包括各 builder 自己调过的特殊色。
///
/// 行情详情窗（QuoteChartBuilder）不在管辖范围：那套图本来就是通达信风格的黑底专业看盘图、
/// 自带一整套配色，它**故意不调用 Track**，所以这里碰不到它。
/// </summary>
internal static class ChartTheme
{
    // ===== 涨跌配色（全程序唯一一份）=====
    //   通达信口径：涨红、跌青。跌**不用绿**——绿在黑底上跟"MA20 线""摆动低点"这些非涨跌语义的绿
    //   撞色，青才是看盘软件的惯例。原来只有行情详情图是红/青、其余判断依据图是红/绿，两边对不上
    //   （2026-09-15 按用户要求统一）。
    //   只给"涨跌"这一种语义用：MA 均线、标记点那些绿色是别的意思，不要往这儿并。
    public static readonly OxyColor Up = OxyColors.Red;
    public static readonly OxyColor Down = OxyColor.FromRgb(0, 210, 210);

    private static readonly List<WeakReference<PlotModel>> Models = new();
    private static readonly ConditionalWeakTable<PlotModel, Snapshot> Snapshots = new();

    /// <summary>App 启动时调一次：主题一变，把所有还活着的图重画一遍。</summary>
    public static void Install()
    {
        ThemeManager.ThemeChanged += (_, _) => ReapplyAll();
    }

    /// <summary>建图时把 model 交给主题管理。返回的就是传进来的那个 model，方便写成
    /// <c>var m = ChartTheme.Track(new PlotModel { … });</c>。</summary>
    public static PlotModel Track(PlotModel model)
    {
        for (int i = Models.Count - 1; i >= 0; i--)
        {
            if (!Models[i].TryGetTarget(out var existing)) Models.RemoveAt(i);   // 顺手清掉已关窗口的图
            else if (ReferenceEquals(existing, model)) return model;
        }
        Models.Add(new WeakReference<PlotModel>(model));
#pragma warning disable CS0618 // OxyPlot 把 Updated 标了 obsolete（issue #111，说 v4.0 可能移除），
        // 但在 2.1.2 里它是唯一能"每次绘制前、坐标轴和序列都已加齐的那一刻"介入的钩子。
        // 换成"builder return 前 apply 一次"也行，但那样 model 之后再被改就管不到了。真升到 v4 再换。
        model.Updated += OnModelUpdated;
#pragma warning restore CS0618
        return model;
    }

    // OxyPlot 每次绘制前都会 Update：这时坐标轴/序列/标注都齐了，上色最稳妥。
    // 这里只改颜色、不触发重绘，所以不会跟 Update 循环起来。
    private static void OnModelUpdated(object? sender, EventArgs e)
    {
        if (sender is PlotModel model) ApplyTo(model);
    }

    private static void ReapplyAll()
    {
        foreach (var reference in Models.ToList())
        {
            if (!reference.TryGetTarget(out var model)) continue;
            ApplyTo(model);
            model.InvalidatePlot(false);   // 只重画，不重算数据
        }
    }

    private static void ApplyTo(PlotModel model)
    {
        var snapshot = Snapshots.GetValue(model, Snapshot.Capture);
        if (ThemeManager.IsDark) ApplyDark(model);
        else snapshot.Restore(model);
    }

    // 深色画布上的取色：文字比正文稍暗一点（图里字多、太亮会糊成一片），网格线只要"看得出有条线"
    private static readonly OxyColor DarkText = OxyColor.FromRgb(0xC8, 0xC8, 0xC8);
    private static readonly OxyColor DarkTick = OxyColor.FromRgb(0x6E, 0x6E, 0x74);
    private static readonly OxyColor DarkGrid = OxyColor.FromRgb(0x33, 0x33, 0x38);
    private static readonly OxyColor DarkBorder = OxyColor.FromRgb(0x3F, 0x3F, 0x46);

    private static void ApplyDark(PlotModel model)
    {
        model.TextColor = DarkText;
        model.TitleColor = DarkText;
        model.SubtitleColor = DarkText;
        model.PlotAreaBorderColor = DarkBorder;

        foreach (var axis in model.Axes)
        {
            // Transparent 是 builder 故意藏掉的坐标轴（HideAxisVisually），别给它"点亮"
            if (!axis.TextColor.IsInvisible()) axis.TextColor = DarkText;
            if (!axis.TicklineColor.IsInvisible()) axis.TicklineColor = DarkTick;
            if (!axis.AxislineColor.IsInvisible()) axis.AxislineColor = DarkTick;
            if (!axis.MajorGridlineColor.IsInvisible()) axis.MajorGridlineColor = DarkGrid;
            if (!axis.MinorGridlineColor.IsInvisible()) axis.MinorGridlineColor = DarkGrid;
            axis.TitleColor = DarkText;
        }

        foreach (var legend in model.Legends)
        {
            legend.LegendTextColor = DarkText;
            legend.LegendTitleColor = DarkText;
        }

        foreach (var series in model.Series)
        {
            // 直接标在数据点上的数字（LabelFormatString——财务分析右侧那几张趋势图靠它显示每期数值）
            series.TextColor = DarkText;
            // 线条里那些深到贴近黑色的（MACD 零轴、十字线的 DarkSlateGray）在黑底上等于消失，
            // 按亮度提亮；本来就是红/绿/橙/蓝这些够亮的语义色不动。
            if (series is LineSeries line) line.Color = Lighten(line.Color);
        }

        foreach (var annotation in model.Annotations)
        {
            switch (annotation)
            {
                case LineAnnotation lineAnnotation:
                    lineAnnotation.Color = Lighten(lineAnnotation.Color);
                    lineAnnotation.TextColor = Lighten(lineAnnotation.TextColor);
                    break;
                case TextAnnotation text:
                    text.TextColor = Lighten(text.TextColor);
                    text.Stroke = Lighten(text.Stroke);
                    break;
            }
        }
    }

    /// <summary>太暗的颜色往白里提，保持原来的色相；够亮的原样返回。</summary>
    private static OxyColor Lighten(OxyColor color)
    {
        if (color.IsUndefined() || color.IsInvisible()) return color;

        double luma = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B);
        if (luma >= 90) return color;

        // 目标亮度 190：跟 DarkText 差不多，够读又不刺眼。纯黑没有色相，直接给中性亮灰。
        double scale = luma < 1 ? 0 : 190 / luma;
        byte Mix(byte c) => luma < 1 ? (byte)0xC8 : (byte)Math.Min(255, Math.Round(c * scale));
        return OxyColor.FromAColor(color.A, OxyColor.FromRgb(Mix(color.R), Mix(color.G), Mix(color.B)));
    }

    /// <summary>改动之前的原始配色，切回浅色时照着还原。</summary>
    private sealed class Snapshot
    {
        private OxyColor _text, _title, _subtitle, _plotAreaBorder;
        private readonly List<(Axis Axis, OxyColor Text, OxyColor Tick, OxyColor Axisline, OxyColor Major, OxyColor Minor, OxyColor Title)> _axes = new();
        private readonly List<(LegendBase Legend, OxyColor Text, OxyColor Title)> _legends = new();
        private readonly List<(Series Series, OxyColor TextColor, OxyColor Color)> _series = new();
        private readonly List<(Annotation Annotation, OxyColor First, OxyColor Second)> _annotations = new();

        public static Snapshot Capture(PlotModel model)
        {
            var snapshot = new Snapshot
            {
                _text = model.TextColor,
                _title = model.TitleColor,
                _subtitle = model.SubtitleColor,
                _plotAreaBorder = model.PlotAreaBorderColor,
            };
            foreach (var axis in model.Axes)
            {
                snapshot._axes.Add((axis, axis.TextColor, axis.TicklineColor, axis.AxislineColor,
                                    axis.MajorGridlineColor, axis.MinorGridlineColor, axis.TitleColor));
            }
            foreach (var legend in model.Legends)
            {
                snapshot._legends.Add((legend, legend.LegendTextColor, legend.LegendTitleColor));
            }
            foreach (var series in model.Series)
            {
                snapshot._series.Add((series, series.TextColor,
                                      series is LineSeries line ? line.Color : OxyColors.Undefined));
            }
            foreach (var annotation in model.Annotations)
            {
                switch (annotation)
                {
                    case LineAnnotation lineAnnotation:
                        snapshot._annotations.Add((lineAnnotation, lineAnnotation.Color, lineAnnotation.TextColor));
                        break;
                    case TextAnnotation text:
                        snapshot._annotations.Add((text, text.TextColor, text.Stroke));
                        break;
                }
            }
            return snapshot;
        }

        public void Restore(PlotModel model)
        {
            model.TextColor = _text;
            model.TitleColor = _title;
            model.SubtitleColor = _subtitle;
            model.PlotAreaBorderColor = _plotAreaBorder;

            foreach (var (axis, text, tick, axisline, major, minor, title) in _axes)
            {
                axis.TextColor = text;
                axis.TicklineColor = tick;
                axis.AxislineColor = axisline;
                axis.MajorGridlineColor = major;
                axis.MinorGridlineColor = minor;
                axis.TitleColor = title;
            }
            foreach (var (legend, text, title) in _legends)
            {
                legend.LegendTextColor = text;
                legend.LegendTitleColor = title;
            }
            foreach (var (series, textColor, color) in _series)
            {
                series.TextColor = textColor;
                if (series is LineSeries line) line.Color = color;
            }
            foreach (var (annotation, first, second) in _annotations)
            {
                switch (annotation)
                {
                    case LineAnnotation lineAnnotation:
                        lineAnnotation.Color = first;
                        lineAnnotation.TextColor = second;
                        break;
                    case TextAnnotation text:
                        text.TextColor = first;
                        text.Stroke = second;
                        break;
                }
            }
        }
    }
}
