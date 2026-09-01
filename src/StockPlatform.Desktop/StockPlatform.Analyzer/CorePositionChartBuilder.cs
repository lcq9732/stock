using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace StockPlatform.Analyzer;

/// <summary>
/// 底仓法【条件详情】里那两张柱状图（2026-08-20 新增）——历年每股派息、历年归母净利。
/// 【底仓】页每行的【分红历史】按钮复用前一张。
///
/// 为什么要画图：这两项原来是结果表里的两串文字（"波动｜22年0.510 23年0.430 24年0.790
/// 25年0.180 26年0.410"、"23年+29.52 24年+21.59 25年+21.66｜累计+72.77亿"），列宽不够、扫一眼
/// 也读不出重点。柱子高低一眼就能看出"哪年腰斩了""是一贯分红还是最近才开始"——这正是底仓最该
/// 判断的事。结果表那两列现在只留结论（递增/持平/波动/中断、3年累计），明细全交给这两张图，
/// 而且画的是**全部**历史，不只判定窗口那几年。
///
/// **为什么用两个 <see cref="LinearBarSeries"/> 而不是一个带 per-item 颜色的柱状图**：本项目用的
/// OxyPlot 2.1 里没有 ColumnSeries（只有水平方向的 BarSeries），垂直柱要用 LinearBarSeries，而它
/// 是按整个 series 设一个 FillColor 的。所以按颜色拆成两个 series 各画各的——跟 ChartBuilder 里
/// MACD 柱"正值红、负值绿两个 StemSeries"是同一个套路。
/// </summary>
public static class CorePositionChartBuilder
{
    /// <summary>近几年用深色高亮——趋势结论（递增/持平/波动/中断）只看这几年，图上标出来，免得
    /// 用户拿十年前的柱子去对结论。跟 CorePositionAnalysisEngine.DividendLookbackYears 一致。</summary>
    private const int HighlightRecentYears = 5;

    /// <summary>柱顶数值标签最多标到多少根——再多就挤成一片糊，改成靠鼠标悬停看（TrackerFormatString）。</summary>
    private const int MaxValueLabels = 16;

    private static readonly OxyColor RecentColor = OxyColor.FromRgb(0x1F, 0x6F, 0xB2);   // 判定窗口内：深蓝
    private static readonly OxyColor OlderColor = OxyColor.FromRgb(0xA8, 0xC6, 0xE0);    // 更早：浅蓝
    private static readonly OxyColor ProfitColor = OxyColor.FromRgb(0xC0, 0x39, 0x2B);   // 盈利：红（国内习惯）
    private static readonly OxyColor LossColor = OxyColor.FromRgb(0x27, 0xAE, 0x60);     // 亏损：绿

    /// <summary>历年每股现金派息（元/股，税前）。<paramref name="data"/> 需按年份升序。</summary>
    public static PlotModel BuildDividendChart(IReadOnlyList<(int Year, double PerShare)> data)
    {
        var model = NewModel("历年每股派息（元/股，税前）");
        if (data == null || data.Count == 0)
        {
            model.Subtitle = "本地没有该股的分红记录";
            return model;
        }

        int cutoff = data[^1].Year - HighlightRecentYears + 1;
        var recent = data.Where(d => d.Year >= cutoff).ToList();
        var older = data.Where(d => d.Year < cutoff).ToList();

        model.Axes.Add(YearAxis(data.Select(d => d.Year)));
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            AbsoluteMinimum = 0,
            Minimum = 0,
            MaximumPadding = 0.2,       // 给柱顶的数值标签留位置
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromRgb(0xE0, 0xE0, 0xE0),
        });

        if (older.Count > 0) model.Series.Add(Bars(older.Select(d => (d.Year, d.PerShare)), OlderColor, "更早", "F3"));
        model.Series.Add(Bars(recent.Select(d => (d.Year, d.PerShare)), RecentColor,
            $"近{HighlightRecentYears}年", "F3"));

        AddValueLabels(model, data.Select(d => (d.Year, d.PerShare)), "F3", above: true);

        model.Subtitle = older.Count > 0
            ? $"共 {data.Count} 年有派息；深色为近{HighlightRecentYears}年（趋势结论只看这几年）"
            : $"共 {data.Count} 年有派息";
        return model;
    }

    /// <summary>历年归母净利（图上换算成亿元）。<paramref name="data"/> 需按年份升序。</summary>
    public static PlotModel BuildProfitChart(IReadOnlyList<(int Year, double NetProfit)> data)
    {
        var model = NewModel("历年归母净利（亿元）");
        if (data == null || data.Count == 0)
        {
            model.Subtitle = "本地没有该股的年报数据";
            return model;
        }

        var scaled = data.Select(d => (d.Year, Value: d.NetProfit / 1e8)).ToList();
        var profit = scaled.Where(d => d.Value >= 0).ToList();
        var loss = scaled.Where(d => d.Value < 0).ToList();

        model.Axes.Add(YearAxis(data.Select(d => d.Year)));
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            MinimumPadding = 0.2,
            MaximumPadding = 0.2,
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromRgb(0xE0, 0xE0, 0xE0),
            // 有亏损年份时零轴必须画出来，否则红绿柱子谁在零线下看不出来
            ExtraGridlines = new[] { 0.0 },
            ExtraGridlineColor = OxyColors.Gray,
            ExtraGridlineStyle = LineStyle.Solid,
            ExtraGridlineThickness = 1.2,
        });

        if (profit.Count > 0) model.Series.Add(Bars(profit, ProfitColor, "盈利", "F2"));
        if (loss.Count > 0) model.Series.Add(Bars(loss, LossColor, "亏损", "F2"));

        AddValueLabels(model, scaled, "F1", above: null);

        model.Subtitle = loss.Count > 0
            ? $"共 {data.Count} 年年报，其中 {loss.Count} 年亏损（绿柱）"
            : $"共 {data.Count} 年年报，全部盈利";
        return model;
    }

    private static LinearBarSeries Bars(
        IEnumerable<(int Year, double Value)> points, OxyColor color, string title, string format)
    {
        var series = new LinearBarSeries
        {
            Title = title,
            FillColor = color,
            StrokeColor = OxyColors.Transparent,
            StrokeThickness = 0,
            BarWidth = 18,
            TrackerFormatString = "{0}\n{2:0} 年：{4:" + format + "}",
        };
        foreach (var (year, value) in points) series.Points.Add(new DataPoint(year, value));
        return series;
    }

    /// <summary>柱顶（或柱底，负值时）的数值标签。柱子太多就不标了，见 <see cref="MaxValueLabels"/>。
    /// <paramref name="above"/> 传 null 表示按正负自动决定标在上还是标在下。</summary>
    private static void AddValueLabels(
        PlotModel model, IEnumerable<(int Year, double Value)> points, string format, bool? above)
    {
        var list = points.ToList();
        if (list.Count > MaxValueLabels) return;

        foreach (var (year, value) in list)
        {
            bool up = above ?? value >= 0;
            model.Annotations.Add(new TextAnnotation
            {
                Text = value.ToString(format),
                TextPosition = new DataPoint(year, value),
                TextVerticalAlignment = up ? VerticalAlignment.Bottom : VerticalAlignment.Top,
                TextHorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 11,
                TextColor = OxyColors.Black,
                StrokeThickness = 0,
                Background = OxyColors.Transparent,
                Offset = new ScreenVector(0, up ? -3 : 3),
            });
        }
    }

    private static PlotModel NewModel(string title) => ChartTheme.Track(new()
    {
        Title = title,
        TitleFontSize = 14,
        SubtitleFontSize = 11,
        SubtitleColor = OxyColors.Gray,
        PlotAreaBorderColor = OxyColor.FromRgb(0xDD, 0xDD, 0xDD),
        DefaultFontSize = 12,
        IsLegendVisible = false,
    });

    /// <summary>年份轴。用 <see cref="LinearAxis"/> 而不是 CategoryAxis——柱子画的是
    /// <see cref="LinearBarSeries"/>（数值X），两者必须配数值轴。年数多时放稀刻度，
    /// 否则二十几个四位数年份横排会互相压掉。</summary>
    private static LinearAxis YearAxis(IEnumerable<int> years)
    {
        var list = years.ToList();
        int first = list.Min(), last = list.Max();
        int span = last - first + 1;
        return new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Minimum = first - 0.7,
            Maximum = last + 0.7,
            MajorStep = span > 20 ? 4 : span > 12 ? 2 : 1,
            MinorStep = 1,
            MinorTickSize = 0,
            IsZoomEnabled = false,
            IsPanEnabled = false,
            StringFormat = "0",
            MajorGridlineStyle = LineStyle.None,
        };
    }
}
