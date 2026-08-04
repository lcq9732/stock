using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer;

/// <summary>图例里的一项。<see cref="Swatches"/> 通常一个颜色；有正有负的柱状序列是两个
/// （上红下绿），所以是数组而不是单个颜色。</summary>
public record DossierLegendItem(string Label, OxyColor[] Swatches);

public class DossierChartResult
{
    public PlotModel Model { get; init; } = new();
    /// <summary>横轴（索引轴）——窗口靠它把鼠标位置反算成第几个点。</summary>
    public LinearAxis XAxis { get; init; } = null!;
    /// <summary>跟着鼠标走的竖线。</summary>
    public LineAnnotation Crosshair { get; init; } = null!;
    public List<DossierLegendItem> Legend { get; init; } = new();
    /// <summary>把"第几个点"格式化成一行读数（日期 + 各序列当期值），给图上方那行文字用。</summary>
    public Func<int, string> FormatInfo { get; init; } = _ => "";
    public int PointCount { get; init; }
}

/// <summary>
/// "其他数据"窗口里各节趋势图的构建（数据来自 <see cref="DossierChart"/>）。
///
/// 横轴是**点的下标**配一个把下标映射回日期串的 LabelFormatter，不是 DateTimeAxis 也不是
/// CategoryAxis——理由跟 <see cref="ChartBuilder"/> 那边一样：DateTimeAxis 会给周末/停牌/没披露的
/// 季度留出视觉空位，CategoryAxis 又想给每个类别都画标签（几千个交易日会糊成一团）。这里只是刻度
/// 稀疏程度按点数自适应，比 K线那套双层轴(日/月两 tier)简单，因为这些图不需要跟别的面板联动。
///
/// 量纲差几个数量级的序列（余额是亿元、占比是百分比）放右轴，见 <see cref="DossierSeries.OnRightAxis"/>。
/// 折线颜色刻意避开纯红/纯绿——那两个色在这个程序里专门表示涨跌/正负，留给柱状序列用。
/// </summary>
public static class DossierChartBuilder
{
    /// <summary>折线配色。避开纯红/纯绿（那是柱状序列表示正负用的），在白底上都能分清。</summary>
    private static readonly OxyColor[] LinePalette =
    {
        OxyColor.FromRgb(0x1F, 0x77, 0xB4), // 蓝
        OxyColor.FromRgb(0x94, 0x67, 0xBD), // 紫
        OxyColor.FromRgb(0xFF, 0x7F, 0x0E), // 橙
        OxyColor.FromRgb(0x17, 0xA2, 0xB8), // 青
        OxyColor.FromRgb(0x8C, 0x56, 0x4B), // 棕
        OxyColor.FromRgb(0xE3, 0x77, 0xC2), // 粉
    };

    private static readonly OxyColor PositiveBar = OxyColor.FromRgb(0xD6, 0x27, 0x28); // 正=红
    private static readonly OxyColor NegativeBar = OxyColor.FromRgb(0x2C, 0xA0, 0x2C); // 负=绿

    private const string LeftAxisKey = "left";
    private const string RightAxisKey = "right";

    public static DossierChartResult Build(DossierChart chart)
    {
        int count = chart.XLabels.Count;
        var model = new PlotModel { PlotMargins = new OxyThickness(64, 6, 64, 24) };

        var xAxis = new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Minimum = -0.5,
            Maximum = count - 0.5,
            // 目标约 10 个刻度——点数少时每点一个标签，几千点时自动稀疏。
            MajorStep = Math.Max(1, (int)Math.Ceiling(count / 10.0)),
            MinorStep = Math.Max(1, (int)Math.Ceiling(count / 10.0)),
            LabelFormatter = position => LabelAt(chart.XLabels, position),
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromRgb(0xE0, 0xE0, 0xE0),
            IsPanEnabled = true,
            IsZoomEnabled = true,
        };
        model.Axes.Add(xAxis);

        model.Axes.Add(new LinearAxis
        {
            Key = LeftAxisKey,
            Position = AxisPosition.Left,
            Title = chart.LeftAxisTitle,
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromRgb(0xE0, 0xE0, 0xE0),
            IsPanEnabled = true,
            IsZoomEnabled = true,
        });

        // 右轴只在真有序列用它时才建——凭空多一条轴会白占宽度。
        bool needsRight = chart.RightAxisTitle != null && chart.Series.Any(s => s.OnRightAxis);
        if (needsRight)
            model.Axes.Add(new LinearAxis
            {
                Key = RightAxisKey,
                Position = AxisPosition.Right,
                Title = chart.RightAxisTitle,
                MajorGridlineStyle = LineStyle.None,
                IsPanEnabled = true,
                IsZoomEnabled = true,
            });

        var legend = new List<DossierLegendItem>();
        int lineColorIndex = 0;

        foreach (var series in chart.Series)
        {
            // 右轴序列在右轴没建起来时（数据里没标 RightAxisTitle）退回左轴，不能引用不存在的轴 Key。
            var yAxisKey = series.OnRightAxis && needsRight ? RightAxisKey : LeftAxisKey;

            if (series.AsBars)
            {
                bool hasNegative = series.Values.Any(v => !double.IsNaN(v) && v < 0);
                if (hasNegative)
                {
                    AddStems(model, xAxis.Key, yAxisKey, series.Values, PositiveBar, v => v >= 0);
                    AddStems(model, xAxis.Key, yAxisKey, series.Values, NegativeBar, v => v < 0);
                    legend.Add(new DossierLegendItem(series.Label, new[] { PositiveBar, NegativeBar }));
                }
                else
                {
                    var color = LinePalette[lineColorIndex++ % LinePalette.Length];
                    AddStems(model, xAxis.Key, yAxisKey, series.Values, color, _ => true);
                    legend.Add(new DossierLegendItem(series.Label, new[] { color }));
                }
                continue;
            }

            var lineColor = LinePalette[lineColorIndex++ % LinePalette.Length];
            var line = new LineSeries
            {
                XAxisKey = xAxis.Key,
                YAxisKey = yAxisKey,
                Color = lineColor,
                StrokeThickness = 1.5,
                // 单点序列（中间全是 NaN）折线画不出来，加个小点标记才看得见。
                MarkerType = count <= 60 ? MarkerType.Circle : MarkerType.None,
                MarkerSize = 2.5,
                MarkerFill = lineColor,
            };
            for (int i = 0; i < series.Values.Length; i++)
                // NaN 直接塞进去：OxyPlot 把无效点当断线处理，正好是"这期没数据、不要连过去"的语义。
                line.Points.Add(new DataPoint(i, series.Values[i]));
            model.Series.Add(line);
            legend.Add(new DossierLegendItem(series.Label, new[] { lineColor }));
        }

        var crosshair = new LineAnnotation
        {
            Type = LineAnnotationType.Vertical,
            XAxisKey = xAxis.Key,
            YAxisKey = LeftAxisKey,
            X = count - 1,
            Color = OxyColor.FromRgb(0x88, 0x88, 0x88),
            LineStyle = LineStyle.Dash,
            StrokeThickness = 1,
        };
        model.Annotations.Add(crosshair);

        return new DossierChartResult
        {
            Model = model,
            XAxis = xAxis,
            Crosshair = crosshair,
            Legend = legend,
            PointCount = count,
            FormatInfo = idx => FormatInfo(chart, idx),
        };
    }

    private static void AddStems(PlotModel model, string xAxisKey, string yAxisKey, double[] values, OxyColor color, Func<double, bool> include)
    {
        var stems = new StemSeries { XAxisKey = xAxisKey, YAxisKey = yAxisKey, Color = color, StrokeThickness = 2 };
        for (int i = 0; i < values.Length; i++)
            if (!double.IsNaN(values[i]) && include(values[i]))
                stems.Points.Add(new DataPoint(i, values[i]));
        model.Series.Add(stems);
    }

    /// <summary>刻度标签：下标 → 那个点的日期串。位置可能落在数据范围外（用户拖出去了）或半格上，
    /// 四舍五入后越界就不给标签。</summary>
    private static string LabelAt(List<string> labels, double position)
    {
        int idx = (int)Math.Round(position);
        return idx >= 0 && idx < labels.Count ? labels[idx] : "";
    }

    /// <summary>图上方那行读数："2026-07-29  融资余额:104.77  融券余额:0.00  占流通市值:1.28"。</summary>
    private static string FormatInfo(DossierChart chart, int idx)
    {
        if (idx < 0 || idx >= chart.XLabels.Count) return "";
        var parts = new List<string> { chart.XLabels[idx] };
        foreach (var s in chart.Series)
        {
            var value = idx < s.Values.Length ? s.Values[idx] : double.NaN;
            parts.Add($"{StripUnit(s.Label)}:{(double.IsNaN(value) ? "—" : value.ToString("N2"))}");
        }
        return string.Join("   ", parts);
    }

    /// <summary>读数行里去掉图例上那串括号说明（"融资余额（亿元）" → "融资余额"）——单位在轴标题上
    /// 已经写了，读数行要短。</summary>
    private static string StripUnit(string label)
    {
        int paren = label.IndexOf('（');
        return paren > 0 ? label[..paren] : label;
    }
}
