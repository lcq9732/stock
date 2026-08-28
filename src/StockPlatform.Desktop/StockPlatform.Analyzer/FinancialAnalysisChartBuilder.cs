using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer;

/// <summary>
/// 财务分析窗口右侧那几张趋势小图（2026-08-27 新增）。
///
/// 刻意做得比 <see cref="DossierChartBuilder"/> 简单：不要十字光标、不要联动、不要双轴——
/// 这些图的作用只是回答"这个指标是在变好还是变坏"，一眼扫过去就够了，点开看具体数值有左边的表格。
///
/// 两个约定跟项目其它图一致：
///   · 金额类画柱状，**正红负绿**（A股习惯）
///   · 比率类画折线，配色避开纯红/纯绿（那两个色留给正负）
///
/// 横轴用点的下标 + LabelFormatter 映射回报告期，不用 DateTimeAxis——报告期是离散的季度点，
/// DateTimeAxis 会按真实日期间距留出视觉空位，8 个点会挤在一起（理由同 DossierChartBuilder）。
/// </summary>
public static class FinancialAnalysisChartBuilder
{
    private static readonly OxyColor Positive = OxyColor.FromRgb(0xD6, 0x27, 0x28); // 正=红
    private static readonly OxyColor Negative = OxyColor.FromRgb(0x2C, 0xA0, 0x2C); // 负=绿
    private static readonly OxyColor LineColor = OxyColor.FromRgb(0x1F, 0x77, 0xB4); // 蓝

    /// <summary>比率类序列——画折线而不是柱状。</summary>
    private static bool IsRatio(TrendSeries s) => s.Unit is "%" or "";

    /// <summary>非末图的底部留白（只够画轴线，不放标签）。</summary>
    public const double BottomMarginCompact = 4;
    /// <summary>末图的底部留白（要放报告期标签）。
    /// 30 而不是 22：22 装不下 10 号字的 "19/06" 加刻度线，实测标签会被图的下边界裁掉半截。</summary>
    public const double BottomMarginWithLabels = 30;

    /// <summary>
    /// <paramref name="showXLabels"/>=false 时隐藏横轴标签——几张图上下叠放时**只有最下面那张
    /// 显示报告期**（2026-08-27 用户要求，省空间）。所有图的横轴范围和刻度完全一致，所以
    /// 上面几张图的柱子跟最下面的标签是对齐的。
    /// </summary>
    public static PlotModel Build(TrendSeries series, bool showXLabels = true)
    {
        var model = new PlotModel
        {
            Title = series.Unit.Length > 0 ? $"{series.Name}（{series.Unit}）" : series.Name,
            TitleFontSize = 12,
            TitleFont = "Microsoft YaHei",
            PlotAreaBorderColor = OxyColor.FromRgb(0xDD, 0xDD, 0xDD),
            Padding = new OxyThickness(0),
            PlotMargins = new OxyThickness(46, 0, 10,
                showXLabels ? BottomMarginWithLabels : BottomMarginCompact),
            DefaultFont = "Microsoft YaHei",
        };

        var labels = series.Points.Select(p => p.Period.ToString("yy/MM")).ToList();
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Minimum = -0.6,
            Maximum = series.Points.Count - 0.4,
            MajorStep = 1,
            MinorStep = 1,
            MajorGridlineStyle = LineStyle.None,
            MinorTickSize = 0,
            FontSize = 10,
            // 共用横坐标：上面几张图不画标签也不画刻度线，只有末图画（见 showXLabels）
            TickStyle = showXLabels ? TickStyle.Outside : TickStyle.None,
            // 下标 → 报告期。非整数刻度不给标签，避免 0.5 之类冒出来。
            LabelFormatter = v =>
            {
                if (!showXLabels) return "";
                int i = (int)Math.Round(v);
                return Math.Abs(v - i) < 1e-6 && i >= 0 && i < labels.Count ? labels[i] : "";
            },
            // 这些图只是给人扫一眼看趋势，不需要缩放/拖动（用户 2026-08-27 明确说不用）。
            // 关掉之后误滚滚轮也不会把图搞乱。
            IsZoomEnabled = false,
            IsPanEnabled = false,
        });
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromRgb(0xEE, 0xEE, 0xEE),
            FontSize = 10,
            StringFormat = "0.##",
            // 图只有一百多像素高，OxyPlot 默认的刻度间隔算法会退化到只画一个 "0"（实测营业收入
            // 那张就是）。给一个刻度数下限 + 上下留白，保证至少能看出量级。
            MinimumMajorStep = 0,
            IntervalLength = 26,
            // 数值标在柱子/点的外侧，所以上下要多留一点，否则标签会贴边被裁掉
            MaximumPadding = 0.20,
            MinimumPadding = 0.14,
            IsZoomEnabled = false,
            IsPanEnabled = false,
        });

        if (IsRatio(series))
        {
            var line = new LineSeries
            {
                Color = LineColor,
                StrokeThickness = 2,
                MarkerType = MarkerType.Circle,
                MarkerSize = 3.5,
                MarkerFill = LineColor,
                TrackerFormatString = "{2:0.##}",
                // 数值直接标在点上（用户 2026-08-27 要求）——省得为了看具体值去悬停
                LabelFormatString = "{1:0.##}",
                LabelMargin = 3,
                FontSize = 9,
            };
            for (int i = 0; i < series.Points.Count; i++)
            {
                // NaN 表示该期算不出来（比如净利为负、比率没有意义）。OxyPlot 遇到 NaN 会断线，
                // 正好是想要的效果：**空着但不错位**，不会把缺口两侧连成一条假的连续线。
                double v = series.Points[i].Value;
                line.Points.Add(double.IsNaN(v) ? DataPoint.Undefined : new DataPoint(i, v));
            }
            model.Series.Add(line);

            // 有正有负时补一条零轴——比率跨零（比如现金流/净利变负）是关键信号，不画线看不出来
            if (series.Points.Any(p => p.Value < 0) && series.Points.Any(p => p.Value > 0)
                && series.Points.Any(p => !double.IsNaN(p.Value)))
                model.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
                {
                    Type = OxyPlot.Annotations.LineAnnotationType.Horizontal,
                    Y = 0,
                    Color = OxyColor.FromRgb(0x99, 0x99, 0x99),
                    StrokeThickness = 1,
                    LineStyle = LineStyle.Solid,
                });
        }
        else
        {
            // 金额类：一根柱一期，正红负绿。用 ScatterSeries 画不出柱子，这里用 RectangleBarSeries
            // 逐根加——OxyPlot 的 BarSeries 需要 CategoryAxis，而横轴已经是 LinearAxis 了。
            var bars = new RectangleBarSeries { StrokeThickness = 0 };
            for (int i = 0; i < series.Points.Count; i++)
            {
                double v = series.Points[i].Value;
                if (double.IsNaN(v)) continue;   // 该期没有值——留空位，不画柱
                bars.Items.Add(new RectangleBarItem(i - 0.32, 0, i + 0.32, v)
                {
                    Color = v >= 0 ? Positive : Negative,
                });
            }
            model.Series.Add(bars);
            model.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
            {
                Type = OxyPlot.Annotations.LineAnnotationType.Horizontal,
                Y = 0,
                Color = OxyColor.FromRgb(0x99, 0x99, 0x99),
                StrokeThickness = 1,
            });

            // 数值标在柱子外侧（正数标上方、负数标下方）。用 TextAnnotation 而不是
            // RectangleBarItem.Title：后者把字画在矩形正中，柱子矮的时候会糊成一团、看不清。
            for (int i = 0; i < series.Points.Count; i++)
            {
                double v = series.Points[i].Value;
                if (double.IsNaN(v)) continue;
                model.Annotations.Add(new OxyPlot.Annotations.TextAnnotation
                {
                    Text = FormatBarValue(v),
                    TextPosition = new DataPoint(i, v),
                    TextVerticalAlignment = v >= 0 ? VerticalAlignment.Bottom : VerticalAlignment.Top,
                    TextHorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 9,
                    TextColor = OxyColor.FromRgb(0x33, 0x33, 0x33),
                    StrokeThickness = 0,
                    Background = OxyColors.Undefined,
                });
            }
        }

        return model;
    }

    /// <summary>柱子上的数值文本。金额动辄三四位数，全画出来会互相挤，所以按量级降精度。</summary>
    private static string FormatBarValue(double v)
    {
        double abs = Math.Abs(v);
        return abs >= 1000 ? v.ToString("0")
            : abs >= 100 ? v.ToString("0")
            : abs >= 10 ? v.ToString("0.#")
            : v.ToString("0.##");
    }
}
