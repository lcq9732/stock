using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer;

/// <summary>行情详情两个副图可选的指标类型——跟"条件详情"那几个窗口不一样，这里的副图内容不是
/// 固定的，用户在界面上手工切换，所以每种指标都要能独立算出一份够用的 PlotModel + 悬浮信息文字，
/// 不依赖是哪个方法选中了这只股票。
///
/// 枚举的顺序必须跟 QuoteDetailWindow.xaml 里两个下拉框 ComboBoxItem 的顺序**严格一一对应**——
/// 界面是靠 SelectedIndex 强转成这个枚举的（见 QuoteDetailWindow.RebuildChart），加/改项时两边
/// 必须同步。前4个（成交量/MACD/KDJ/RSI）位置不要动，构造函数按名字设默认值（成交量+MACD）。
/// 2026-07-10 一次性补齐了常用指标全套（成交额/换手率/BOLL/EMA/SAR/DMI/BIAS/CCI/WR/MTM/ROC/
/// TRIX/DMA/OBV/VR/MFI/EMV/PSY/ARBR/ASI），算法都在 TechnicalIndicators。</summary>
public enum QuoteSubIndicator
{
    Volume, Macd, Kdj, Rsi,
    Amount, Turnover, Boll, Ema, Sar, Dmi, Bias, Cci, Wr, Mtm, Roc, Trix, Dma, Obv, Vr, Mfi, Emv, Psy, Arbr, Asi
}

/// <summary>
/// Everything QuoteDetailWindow needs——主图(K线+MA5/MA10/MA20，固定) + 两个可独立切换指标类型的
/// 副图（默认成交量+MACD，用户可各自切换成KDJ/RSI/另一个）。三个面板共用同一套日期轴，横向
/// 拖动/缩放联动，切换副图指标或者切换K线粒度（日K/周K/月K）都会用新的 bars 整体重新 Build 一次
/// ——不做增量更新，简单、不容易漏状态。
/// </summary>
public class QuoteChartResult
{
    public PlotModel Main { get; init; } = new();
    public LinearAxis MainDateAxis { get; init; } = null!;
    public LineAnnotation MainCrosshair { get; init; } = null!;

    public PlotModel Sub1 { get; init; } = new();
    public LinearAxis Sub1DateAxis { get; init; } = null!;
    public LineAnnotation Sub1Crosshair { get; init; } = null!;
    /// <summary>给定bar下标，返回这个副图当前指标该显示的悬浮文字——窗口不需要知道副图具体是
    /// 哪种指标，只管调用这个函数。</summary>
    public Func<int, string> Sub1FormatInfo { get; init; } = _ => "";

    public PlotModel Sub2 { get; init; } = new();
    public LinearAxis Sub2DateAxis { get; init; } = null!;
    public LineAnnotation Sub2Crosshair { get; init; } = null!;
    public Func<int, string> Sub2FormatInfo { get; init; } = _ => "";

    public Action<double> UpdatePlotWidth { get; init; } = _ => { };
    public List<Bar> Bars { get; init; } = new();
    public double[] Ma5 { get; init; } = Array.Empty<double>();
    public double[] Ma10 { get; init; } = Array.Empty<double>();
    public double[] Ma20 { get; init; } = Array.Empty<double>();
    public double[] Ma30 { get; init; } = Array.Empty<double>();
}

public static class QuoteChartBuilder
{
    // 暴露成公开常量，方便 QuoteDetailWindow.xaml.cs 给"MA5:158.49↑"这类文字上色时跟图上的线对上，
    // 不用在两个文件里各写一遍颜色。
    public static readonly OxyColor Ma5Color = OxyColors.Blue;
    public static readonly OxyColor Ma10Color = OxyColors.Orange;
    public static readonly OxyColor Ma20Color = OxyColors.Purple;
    public static readonly OxyColor Ma30Color = OxyColor.FromRgb(139, 0, 0); // DarkRed

    public static string IndicatorLabel(QuoteSubIndicator kind) => kind switch
    {
        QuoteSubIndicator.Volume => "成交量",
        QuoteSubIndicator.Macd => "MACD",
        QuoteSubIndicator.Kdj => "KDJ",
        QuoteSubIndicator.Rsi => "RSI",
        QuoteSubIndicator.Amount => "成交额",
        QuoteSubIndicator.Turnover => "换手率",
        QuoteSubIndicator.Boll => "BOLL",
        QuoteSubIndicator.Ema => "EMA",
        QuoteSubIndicator.Sar => "SAR",
        QuoteSubIndicator.Dmi => "DMI",
        QuoteSubIndicator.Bias => "BIAS",
        QuoteSubIndicator.Cci => "CCI",
        QuoteSubIndicator.Wr => "WR",
        QuoteSubIndicator.Mtm => "MTM",
        QuoteSubIndicator.Roc => "ROC",
        QuoteSubIndicator.Trix => "TRIX",
        QuoteSubIndicator.Dma => "DMA",
        QuoteSubIndicator.Obv => "OBV",
        QuoteSubIndicator.Vr => "VR",
        QuoteSubIndicator.Mfi => "MFI",
        QuoteSubIndicator.Emv => "EMV",
        QuoteSubIndicator.Psy => "PSY",
        QuoteSubIndicator.Arbr => "ARBR",
        QuoteSubIndicator.Asi => "ASI",
        _ => kind.ToString(),
    };

    // 副图里各条线用到的颜色，集中定义方便图例(LegendFor)跟画线(BuildSub)对上，不用两头各写一遍。
    private static readonly OxyColor C1 = OxyColors.Blue;
    private static readonly OxyColor C2 = OxyColors.Orange;
    private static readonly OxyColor C3 = OxyColors.Purple;
    private static readonly OxyColor C4 = OxyColor.FromRgb(0, 150, 136);   // Teal
    private static readonly OxyColor C5 = OxyColor.FromRgb(139, 0, 0);     // DarkRed
    private static readonly OxyColor UpColor = OxyColors.Red;
    private static readonly OxyColor DownColor = OxyColors.Green;

    /// <summary>每种指标的图例——(文字, 颜色)，跟 BuildSub 里画的线/柱颜色手动对应，供窗口表头
    /// 动态生成图例用（这两个副图内容会变，没法像其它窗口那样把图例写死在XAML里）。</summary>
    public static (string Label, OxyColor Color)[] LegendFor(QuoteSubIndicator kind) => kind switch
    {
        QuoteSubIndicator.Volume => new[] { ("成交量(涨)", UpColor), ("成交量(跌)", DownColor) },
        QuoteSubIndicator.Macd => new[] { ("MACD柱(正)", UpColor), ("MACD柱(负)", DownColor), ("DIF（快线）", C1), ("DEA（慢线）", C2) },
        QuoteSubIndicator.Kdj => new[] { ("K", C1), ("D", C2) },
        QuoteSubIndicator.Rsi => new[] { ("RSI", C3) },
        QuoteSubIndicator.Amount => new[] { ("成交额(涨)", UpColor), ("成交额(跌)", DownColor) },
        QuoteSubIndicator.Turnover => new[] { ("换手率(%)", C3) },
        QuoteSubIndicator.Boll => new[] { ("中轨", C2), ("上轨", C5), ("下轨", C4), ("收盘", C1) },
        QuoteSubIndicator.Ema => new[] { ("EMA12", C1), ("EMA26", C2), ("收盘", OxyColors.Gray) },
        QuoteSubIndicator.Sar => new[] { ("收盘", C1), ("SAR", UpColor) },
        QuoteSubIndicator.Dmi => new[] { ("+DI", UpColor), ("-DI", DownColor), ("ADX", C1), ("ADXR", C3) },
        QuoteSubIndicator.Bias => new[] { ("BIAS6", C1), ("BIAS12", C2), ("BIAS24", C3) },
        QuoteSubIndicator.Cci => new[] { ("CCI", C1) },
        QuoteSubIndicator.Wr => new[] { ("WR10", C1), ("WR6", C2) },
        QuoteSubIndicator.Mtm => new[] { ("MTM", C1), ("MTMMA", C2) },
        QuoteSubIndicator.Roc => new[] { ("ROC", C1), ("ROCMA", C2) },
        QuoteSubIndicator.Trix => new[] { ("TRIX", C1), ("TRIXMA", C2) },
        QuoteSubIndicator.Dma => new[] { ("DMA", C1), ("AMA", C2) },
        QuoteSubIndicator.Obv => new[] { ("OBV", C1), ("OBVMA", C2) },
        QuoteSubIndicator.Vr => new[] { ("VR", C1), ("VRMA", C2) },
        QuoteSubIndicator.Mfi => new[] { ("MFI", C1) },
        QuoteSubIndicator.Emv => new[] { ("EMV", C1), ("EMVMA", C2) },
        QuoteSubIndicator.Psy => new[] { ("PSY", C1), ("PSYMA", C2) },
        QuoteSubIndicator.Arbr => new[] { ("AR", C1), ("BR", C2) },
        QuoteSubIndicator.Asi => new[] { ("ASI", C1), ("ASIMA", C2) },
        _ => Array.Empty<(string, OxyColor)>(),
    };

    public static QuoteChartResult Build(List<Bar> bars, QuoteSubIndicator sub1Kind, QuoteSubIndicator sub2Kind)
    {
        var closes = bars.Select(b => b.Close).ToList();
        var ma5 = TechnicalIndicators.SMA(closes, 5);
        var ma10 = TechnicalIndicators.SMA(closes, 10);
        var ma20 = TechnicalIndicators.SMA(closes, 20);
        var ma30 = TechnicalIndicators.SMA(closes, 30);
        int last = bars.Count - 1;

        int visibleCount = Math.Min(ChartBuilder.DefaultVisibleBars, bars.Count);
        double visibleStart = bars.Count - visibleCount;
        double visibleEnd = last + 0.6;
        var (dayStep, monthStep) = ChartAxisSync.ComputeSteps(
            visibleCount, ChartBuilder.InitialPlotWidthGuess, ChartBuilder.PxPerDayLabel, ChartBuilder.PxPerMonthLabel, ChartBuilder.TradingDaysPerMonth);

        var (mainDay, mainMonth) = ChartBuilder.BuildDateAxes(bars, "QuoteMainDay", visibleStart, visibleEnd, dayStep, monthStep);

        // 主K线图不画横纵坐标（价格靠最高/最低价标注和表头，日期靠表头），两个副图保留坐标轴。
        ChartBuilder.HideAxisVisually(mainDay);
        ChartBuilder.HideAxisVisually(mainMonth);

        var highs = bars.Select(b => b.High).ToList();
        var lows = bars.Select(b => b.Low).ToList();

        var main = new PlotModel { PlotMargins = new OxyThickness(ChartBuilder.FixedLeftMargin, double.NaN, ChartBuilder.FixedRightMargin, double.NaN) };
        main.Axes.Add(mainDay);
        main.Axes.Add(mainMonth);
        var mainYAxis = new LinearAxis { Position = AxisPosition.Left, IsPanEnabled = true, IsZoomEnabled = true };
        ChartBuilder.HideAxisVisually(mainYAxis);
        main.Axes.Add(mainYAxis);

        // 国内看盘习惯：涨红跌绿。
        var candles = new CandleStickSeries
        {
            Title = "K线",
            XAxisKey = mainDay.Key,
            IncreasingColor = OxyColors.Red,
            DecreasingColor = OxyColors.Green,
            CandleWidth = 0.5,
            TrackerFormatString = "日期: {2}\n开盘: {3:F2}\n最高: {4:F2}\n最低: {5:F2}\n收盘: {6:F2}",
        };
        for (int i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            candles.Items.Add(new HighLowItem(i, b.High, b.Low, b.Open, b.Close));
        }
        main.Series.Add(candles);
        ChartBuilder.AddLine(main, mainDay.Key, ma5, "MA5", Ma5Color);
        ChartBuilder.AddLine(main, mainDay.Key, ma10, "MA10", Ma10Color);
        ChartBuilder.AddLine(main, mainDay.Key, ma20, "MA20", Ma20Color);
        ChartBuilder.AddLine(main, mainDay.Key, ma30, "MA30", Ma30Color);

        var mainCrosshair = NewCrosshair(mainDay.Key, last);
        main.Annotations.Add(mainCrosshair);
        ChartBuilder.AddHighLowAnnotations(main, mainDay.Key, bars, (int)Math.Round(visibleStart), last);

        var sub1 = BuildSub(bars, sub1Kind, "QuoteSub1Day", visibleStart, visibleEnd, dayStep, monthStep, last);
        var sub2 = BuildSub(bars, sub2Kind, "QuoteSub2Day", visibleStart, visibleEnd, dayStep, monthStep, last);

        // KDJ/RSI(如果被选中)不参与Y轴自适应——它们的Y轴是固定0~100语义区间，见BuildSub。
        var yRanges = new List<(LinearAxis, Func<int, int, (double, double)?>)> { (mainYAxis, ChartBuilder.YRangeFn(highs, lows, ma5, ma10, ma20, ma30)) };
        if (sub1.RangeFn != null) yRanges.Add((sub1.YAxis, sub1.RangeFn));
        if (sub2.RangeFn != null) yRanges.Add((sub2.YAxis, sub2.RangeFn));

        var updateWidth = ChartAxisSync.Wire(
            new[] { main, sub1.Model, sub2.Model },
            new[] { mainDay, sub1.DateAxis, sub2.DateAxis },
            new[] { mainMonth, sub1.MonthAxis, sub2.MonthAxis },
            visibleStart, visibleEnd, ChartBuilder.InitialPlotWidthGuess, ChartBuilder.PxPerDayLabel, ChartBuilder.PxPerMonthLabel, ChartBuilder.TradingDaysPerMonth,
            candleSeries: new[] { candles },
            yAxisRanges: yRanges);

        return new QuoteChartResult
        {
            Main = main,
            MainDateAxis = mainDay,
            MainCrosshair = mainCrosshair,
            Sub1 = sub1.Model,
            Sub1DateAxis = sub1.DateAxis,
            Sub1Crosshair = sub1.Crosshair,
            Sub1FormatInfo = sub1.FormatInfo,
            Sub2 = sub2.Model,
            Sub2DateAxis = sub2.DateAxis,
            Sub2Crosshair = sub2.Crosshair,
            Sub2FormatInfo = sub2.FormatInfo,
            UpdatePlotWidth = updateWidth,
            Bars = bars,
            Ma5 = ma5,
            Ma10 = ma10,
            Ma20 = ma20,
            Ma30 = ma30,
        };
    }

    private record SubBuildResult(
        PlotModel Model, LinearAxis DateAxis, LinearAxis MonthAxis, LineAnnotation Crosshair, Func<int, string> FormatInfo,
        LinearAxis YAxis, Func<int, int, (double, double)?>? RangeFn);

    private static SubBuildResult BuildSub(
        List<Bar> bars, QuoteSubIndicator kind, string keyPrefix,
        double visibleStart, double visibleEnd, double dayStep, double monthStep, int last)
    {
        var (dayAxis, monthAxis) = ChartBuilder.BuildDateAxes(bars, keyPrefix, visibleStart, visibleEnd, dayStep, monthStep);
        // 副图不画横纵坐标，跟主K线图一样——数值靠表头悬浮信息。
        ChartBuilder.HideAxisVisually(dayAxis);
        ChartBuilder.HideAxisVisually(monthAxis);
        var model = new PlotModel { PlotMargins = new OxyThickness(ChartBuilder.FixedLeftMargin, double.NaN, ChartBuilder.FixedRightMargin, double.NaN) };
        model.Axes.Add(dayAxis);
        model.Axes.Add(monthAxis);

        // Y轴统一在这里建、隐藏、加入——固定0~100语义区间的指标（KDJ/RSI/WR/MFI/PSY）额外设
        // Min/Max，且把 rangeFn 留 null（不参与"按可见K线自适应"，见 Build 里的判断）。
        var yAxis = new LinearAxis { Position = AxisPosition.Left, IsPanEnabled = true, IsZoomEnabled = true };
        ChartBuilder.HideAxisVisually(yAxis);
        model.Axes.Add(yAxis);

        string key = dayAxis.Key;
        string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("F2");
        string Fmt0(double v) => double.IsNaN(v) ? "—" : v.ToString("F0");
        Func<int, string> formatInfo;
        Func<int, int, (double, double)?>? rangeFn = null;

        var closes = bars.Select(b => b.Close).ToList();
        var highs = bars.Select(b => b.High).ToList();
        var lows = bars.Select(b => b.Low).ToList();
        var opens = bars.Select(b => b.Open).ToList();
        var volumes = bars.Select(b => b.Volume).ToList();

        // 成交量式的红涨绿跌柱状（成交量/成交额共用）。
        void AddUpDownStems(string upTitle, string downTitle, IReadOnlyList<double> values)
        {
            var up = new StemSeries { Title = upTitle, Color = UpColor, StrokeThickness = 3, XAxisKey = key };
            var down = new StemSeries { Title = downTitle, Color = DownColor, StrokeThickness = 3, XAxisKey = key };
            for (int i = 0; i < bars.Count; i++)
                (bars[i].Close >= bars[i].Open ? up : down).Points.Add(new DataPoint(i, values[i]));
            model.Series.Add(up);
            model.Series.Add(down);
        }
        void ZeroLine() => model.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Horizontal, XAxisKey = key, Y = 0, Color = OxyColors.Black, LineStyle = LineStyle.Solid });
        void HLine(double y) => model.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Horizontal, XAxisKey = key, Y = y, Color = OxyColors.LightGray, LineStyle = LineStyle.Dash });

        switch (kind)
        {
            case QuoteSubIndicator.Volume:
                AddUpDownStems("成交量(涨)", "成交量(跌)", volumes);
                formatInfo = idx => $"成交量:{Fmt0(volumes[idx])}";
                rangeFn = ChartBuilder.YRangeFn(volumes);
                break;

            case QuoteSubIndicator.Amount:
            {
                var amounts = bars.Select(b => b.Amount).ToList();
                AddUpDownStems("成交额(涨)", "成交额(跌)", amounts);
                formatInfo = idx => $"成交额:{FormatYi(amounts[idx])}";
                rangeFn = ChartBuilder.YRangeFn(amounts);
                break;
            }

            case QuoteSubIndicator.Turnover:
            {
                var turnovers = bars.Select(b => b.Turnover).ToList();
                ChartBuilder.AddLine(model, key, turnovers.ToArray(), "换手率", C3);
                formatInfo = idx => $"换手率:{Fmt(turnovers[idx])}%";
                rangeFn = ChartBuilder.YRangeFn(turnovers);
                break;
            }

            case QuoteSubIndicator.Macd:
            {
                var (dif, dea) = TechnicalIndicators.MACD(closes);
                var macdHist = new double[bars.Count];
                for (int i = 0; i < bars.Count; i++)
                    macdHist[i] = double.IsNaN(dif[i]) || double.IsNaN(dea[i]) ? double.NaN : (dif[i] - dea[i]) * 2;
                ChartBuilder.AddHistogram(model, key, macdHist);
                ChartBuilder.AddLine(model, key, dif, "DIF（快线）", C1);
                ChartBuilder.AddLine(model, key, dea, "DEA（慢线）", C2);
                ZeroLine();
                formatInfo = idx => $"DIF:{Fmt(dif[idx])}  DEA:{Fmt(dea[idx])}  MACD柱:{Fmt(macdHist[idx])}";
                rangeFn = ChartBuilder.YRangeFn(dif, dea, macdHist);
                break;
            }

            case QuoteSubIndicator.Kdj:
            {
                var (k, d, _) = TechnicalIndicators.KDJ(closes, highs, lows);
                yAxis.Minimum = 0; yAxis.Maximum = 100;
                ChartBuilder.AddLine(model, key, k, "K", C1);
                ChartBuilder.AddLine(model, key, d, "D", C2);
                formatInfo = idx => $"K:{Fmt(k[idx])}  D:{Fmt(d[idx])}";
                break;
            }

            case QuoteSubIndicator.Rsi:
            {
                var rsi = TechnicalIndicators.RSI(closes);
                yAxis.Minimum = 0; yAxis.Maximum = 100;
                ChartBuilder.AddLine(model, key, rsi, "RSI", C3);
                formatInfo = idx => $"RSI:{Fmt(rsi[idx])}";
                break;
            }

            case QuoteSubIndicator.Boll:
            {
                var (mid, upper, lower) = TechnicalIndicators.BOLL(closes);
                ChartBuilder.AddLine(model, key, closes.ToArray(), "收盘", C1);
                ChartBuilder.AddLine(model, key, mid, "中轨", C2);
                ChartBuilder.AddLine(model, key, upper, "上轨", C5);
                ChartBuilder.AddLine(model, key, lower, "下轨", C4);
                formatInfo = idx => $"中轨:{Fmt(mid[idx])}  上轨:{Fmt(upper[idx])}  下轨:{Fmt(lower[idx])}";
                rangeFn = ChartBuilder.YRangeFn(closes, upper, lower);
                break;
            }

            case QuoteSubIndicator.Ema:
            {
                var ema12 = TechnicalIndicators.EMA(closes, 12);
                var ema26 = TechnicalIndicators.EMA(closes, 26);
                ChartBuilder.AddLine(model, key, closes.ToArray(), "收盘", OxyColors.Gray);
                ChartBuilder.AddLine(model, key, ema12, "EMA12", C1);
                ChartBuilder.AddLine(model, key, ema26, "EMA26", C2);
                formatInfo = idx => $"EMA12:{Fmt(ema12[idx])}  EMA26:{Fmt(ema26[idx])}";
                rangeFn = ChartBuilder.YRangeFn(closes, ema12, ema26);
                break;
            }

            case QuoteSubIndicator.Sar:
            {
                var sar = TechnicalIndicators.SAR(highs, lows);
                ChartBuilder.AddLine(model, key, closes.ToArray(), "收盘", C1);
                var dots = new ScatterSeries { Title = "SAR", MarkerType = MarkerType.Circle, MarkerSize = 2, MarkerFill = UpColor, XAxisKey = key };
                for (int i = 0; i < sar.Length; i++)
                    if (!double.IsNaN(sar[i])) dots.Points.Add(new ScatterPoint(i, sar[i]));
                model.Series.Add(dots);
                formatInfo = idx => $"SAR:{Fmt(sar[idx])}";
                rangeFn = ChartBuilder.YRangeFn(closes, sar);
                break;
            }

            case QuoteSubIndicator.Dmi:
            {
                var (pdi, mdi, adx, adxr) = TechnicalIndicators.DMI(highs, lows, closes);
                ChartBuilder.AddLine(model, key, pdi, "+DI", UpColor);
                ChartBuilder.AddLine(model, key, mdi, "-DI", DownColor);
                ChartBuilder.AddLine(model, key, adx, "ADX", C1);
                ChartBuilder.AddLine(model, key, adxr, "ADXR", C3);
                formatInfo = idx => $"+DI:{Fmt(pdi[idx])}  -DI:{Fmt(mdi[idx])}  ADX:{Fmt(adx[idx])}  ADXR:{Fmt(adxr[idx])}";
                rangeFn = ChartBuilder.YRangeFn(pdi, mdi, adx, adxr);
                break;
            }

            case QuoteSubIndicator.Bias:
            {
                var b6 = TechnicalIndicators.BIAS(closes, 6);
                var b12 = TechnicalIndicators.BIAS(closes, 12);
                var b24 = TechnicalIndicators.BIAS(closes, 24);
                ChartBuilder.AddLine(model, key, b6, "BIAS6", C1);
                ChartBuilder.AddLine(model, key, b12, "BIAS12", C2);
                ChartBuilder.AddLine(model, key, b24, "BIAS24", C3);
                ZeroLine();
                formatInfo = idx => $"BIAS6:{Fmt(b6[idx])}  BIAS12:{Fmt(b12[idx])}  BIAS24:{Fmt(b24[idx])}";
                rangeFn = ChartBuilder.YRangeFn(b6, b12, b24);
                break;
            }

            case QuoteSubIndicator.Cci:
            {
                var cci = TechnicalIndicators.CCI(highs, lows, closes);
                ChartBuilder.AddLine(model, key, cci, "CCI", C1);
                HLine(100); HLine(-100);
                formatInfo = idx => $"CCI:{Fmt(cci[idx])}";
                rangeFn = ChartBuilder.YRangeFn(cci);
                break;
            }

            case QuoteSubIndicator.Wr:
            {
                var wr10 = TechnicalIndicators.WR(highs, lows, closes, 10);
                var wr6 = TechnicalIndicators.WR(highs, lows, closes, 6);
                yAxis.Minimum = 0; yAxis.Maximum = 100;
                ChartBuilder.AddLine(model, key, wr10, "WR10", C1);
                ChartBuilder.AddLine(model, key, wr6, "WR6", C2);
                formatInfo = idx => $"WR10:{Fmt(wr10[idx])}  WR6:{Fmt(wr6[idx])}";
                break;
            }

            case QuoteSubIndicator.Mtm:
            {
                var (mtm, mtmMa) = TechnicalIndicators.MTM(closes);
                ChartBuilder.AddLine(model, key, mtm, "MTM", C1);
                ChartBuilder.AddLine(model, key, mtmMa, "MTMMA", C2);
                ZeroLine();
                formatInfo = idx => $"MTM:{Fmt(mtm[idx])}  MTMMA:{Fmt(mtmMa[idx])}";
                rangeFn = ChartBuilder.YRangeFn(mtm, mtmMa);
                break;
            }

            case QuoteSubIndicator.Roc:
            {
                var (roc, rocMa) = TechnicalIndicators.ROC(closes);
                ChartBuilder.AddLine(model, key, roc, "ROC", C1);
                ChartBuilder.AddLine(model, key, rocMa, "ROCMA", C2);
                ZeroLine();
                formatInfo = idx => $"ROC:{Fmt(roc[idx])}  ROCMA:{Fmt(rocMa[idx])}";
                rangeFn = ChartBuilder.YRangeFn(roc, rocMa);
                break;
            }

            case QuoteSubIndicator.Trix:
            {
                var (trix, trixMa) = TechnicalIndicators.TRIX(closes);
                ChartBuilder.AddLine(model, key, trix, "TRIX", C1);
                ChartBuilder.AddLine(model, key, trixMa, "TRIXMA", C2);
                ZeroLine();
                formatInfo = idx => $"TRIX:{Fmt(trix[idx])}  TRIXMA:{Fmt(trixMa[idx])}";
                rangeFn = ChartBuilder.YRangeFn(trix, trixMa);
                break;
            }

            case QuoteSubIndicator.Dma:
            {
                var (dma, ama) = TechnicalIndicators.DMA(closes);
                ChartBuilder.AddLine(model, key, dma, "DMA", C1);
                ChartBuilder.AddLine(model, key, ama, "AMA", C2);
                ZeroLine();
                formatInfo = idx => $"DMA:{Fmt(dma[idx])}  AMA:{Fmt(ama[idx])}";
                rangeFn = ChartBuilder.YRangeFn(dma, ama);
                break;
            }

            case QuoteSubIndicator.Obv:
            {
                var (obv, obvMa) = TechnicalIndicators.OBV(closes, volumes);
                ChartBuilder.AddLine(model, key, obv, "OBV", C1);
                ChartBuilder.AddLine(model, key, obvMa, "OBVMA", C2);
                formatInfo = idx => $"OBV:{Fmt0(obv[idx])}  OBVMA:{Fmt0(obvMa[idx])}";
                rangeFn = ChartBuilder.YRangeFn(obv, obvMa);
                break;
            }

            case QuoteSubIndicator.Vr:
            {
                var (vr, vrMa) = TechnicalIndicators.VR(closes, volumes);
                ChartBuilder.AddLine(model, key, vr, "VR", C1);
                ChartBuilder.AddLine(model, key, vrMa, "VRMA", C2);
                formatInfo = idx => $"VR:{Fmt(vr[idx])}  VRMA:{Fmt(vrMa[idx])}";
                rangeFn = ChartBuilder.YRangeFn(vr, vrMa);
                break;
            }

            case QuoteSubIndicator.Mfi:
            {
                var mfi = TechnicalIndicators.MFI(highs, lows, closes, volumes);
                yAxis.Minimum = 0; yAxis.Maximum = 100;
                ChartBuilder.AddLine(model, key, mfi, "MFI", C1);
                formatInfo = idx => $"MFI:{Fmt(mfi[idx])}";
                break;
            }

            case QuoteSubIndicator.Emv:
            {
                var (emv, emvMa) = TechnicalIndicators.EMV(highs, lows, volumes);
                ChartBuilder.AddLine(model, key, emv, "EMV", C1);
                ChartBuilder.AddLine(model, key, emvMa, "EMVMA", C2);
                ZeroLine();
                formatInfo = idx => $"EMV:{Fmt(emv[idx])}  EMVMA:{Fmt(emvMa[idx])}";
                rangeFn = ChartBuilder.YRangeFn(emv, emvMa);
                break;
            }

            case QuoteSubIndicator.Psy:
            {
                var (psy, psyMa) = TechnicalIndicators.PSY(closes);
                yAxis.Minimum = 0; yAxis.Maximum = 100;
                ChartBuilder.AddLine(model, key, psy, "PSY", C1);
                ChartBuilder.AddLine(model, key, psyMa, "PSYMA", C2);
                formatInfo = idx => $"PSY:{Fmt(psy[idx])}  PSYMA:{Fmt(psyMa[idx])}";
                break;
            }

            case QuoteSubIndicator.Arbr:
            {
                var (ar, br) = TechnicalIndicators.ARBR(opens, highs, lows, closes);
                ChartBuilder.AddLine(model, key, ar, "AR", C1);
                ChartBuilder.AddLine(model, key, br, "BR", C2);
                formatInfo = idx => $"AR:{Fmt(ar[idx])}  BR:{Fmt(br[idx])}";
                rangeFn = ChartBuilder.YRangeFn(ar, br);
                break;
            }

            default: // Asi
            {
                var (asi, asiMa) = TechnicalIndicators.ASI(opens, highs, lows, closes);
                ChartBuilder.AddLine(model, key, asi, "ASI", C1);
                ChartBuilder.AddLine(model, key, asiMa, "ASIMA", C2);
                ZeroLine();
                formatInfo = idx => $"ASI:{Fmt(asi[idx])}  ASIMA:{Fmt(asiMa[idx])}";
                rangeFn = ChartBuilder.YRangeFn(asi, asiMa);
                break;
            }
        }

        var crosshair = NewCrosshair(dayAxis.Key, last);
        model.Annotations.Add(crosshair);
        return new SubBuildResult(model, dayAxis, monthAxis, crosshair, formatInfo, yAxis, rangeFn);
    }

    /// <summary>成交额悬浮文字用的亿/万格式化，跟表头 FormatLargeNumber 口径一致（值单位是元）。</summary>
    private static string FormatYi(double v) => double.IsNaN(v) ? "—" : v switch
    {
        >= 1e8 => $"{v / 1e8:F2}亿",
        >= 1e4 => $"{v / 1e4:F2}万",
        _ => v.ToString("F0"),
    };

    private static LineAnnotation NewCrosshair(string xAxisKey, int last) => new()
    {
        Type = LineAnnotationType.Vertical,
        XAxisKey = xAxisKey,
        X = last,
        Color = OxyColors.DarkSlateGray,
        LineStyle = LineStyle.Dash,
        StrokeThickness = 1,
    };
}
