using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;
using Avalonia.Interactivity;
using Avalonia.Media;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Mobile.Controls;

/// <summary>
/// 自绘 K线图（价格蜡烛 + MA5/10/20 + 成交量副图 + 十字光标 + 手指平移/缩放），纯 Avalonia DrawingContext，
/// 不依赖图表库，Android 由 SkiaSharp 渲染。交互：单指拖动=平移，双指捏合/滚轮=缩放，触摸/悬停=十字光标读数。
/// </summary>
public class CandleChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<Bar>?> BarsProperty =
        AvaloniaProperty.Register<CandleChart, IReadOnlyList<Bar>?>(nameof(Bars));

    public IReadOnlyList<Bar>? Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    private int _start;          // 可见段起始下标
    private int _count = 120;    // 可见段根数
    private int _cross = -1;     // 十字光标所在下标（-1=不显示）
    private double _crossY = double.NaN;
    private bool _dragging;
    private double _dragStartX;
    private int _dragStartStart;
    private int _pinchStartCount;

    private const int UpArgb = unchecked((int)0xFFFF4040);
    private static readonly Color Up = Color.FromRgb(255, 64, 64);
    private static readonly Color Down = Color.FromRgb(0, 210, 210);
    private static readonly Color Grid = Color.FromRgb(45, 45, 45);
    private static readonly Color Axis = Color.FromRgb(170, 170, 170);
    private static readonly Color Cross = Color.FromRgb(200, 200, 200);
    private static readonly Color Ma5C = Color.FromRgb(235, 235, 235);
    private static readonly Color Ma10C = Color.FromRgb(255, 215, 0);
    private static readonly Color Ma20C = Color.FromRgb(255, 0, 255);

    static CandleChart() => AffectsRender<CandleChart>(BarsProperty);

    public CandleChart()
    {
        ClipToBounds = true;
        GestureRecognizers.Add(new PinchGestureRecognizer());
        AddHandler(Gestures.PinchEvent, OnPinch);
        AddHandler(Gestures.PinchEndedEvent, (_, _) => _pinchStartCount = 0);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BarsProperty)
        {
            var n = Bars?.Count ?? 0;
            _count = Math.Min(120, Math.Max(10, n));
            _start = Math.Max(0, n - _count);
            _cross = -1;
            InvalidateVisual();
        }
    }

    // ── 交互 ──
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        _dragStartX = e.GetPosition(this).X;
        _dragStartStart = _start;
        UpdateCross(e.GetPosition(this));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_dragging && Bars is { Count: > 0 })
        {
            double plotW = PlotWidth();
            double barW = plotW / _count;
            if (barW > 0)
            {
                int moved = (int)Math.Round((p.X - _dragStartX) / barW);
                _start = Clamp(_dragStartStart - moved, 0, Math.Max(0, Bars.Count - _count));
            }
        }
        UpdateCross(p);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Zoom(e.Delta.Y > 0 ? 0.85 : 1.18, e.GetPosition(this).X);
    }

    private void OnPinch(object? sender, PinchEventArgs e)
    {
        if (Bars is not { Count: > 0 }) return;
        if (_pinchStartCount == 0) _pinchStartCount = _count;
        int newCount = Clamp((int)Math.Round(_pinchStartCount / Math.Max(0.2, e.Scale)), 10, Bars.Count);
        SetCount(newCount, PlotLeft() + PlotWidth() / 2);
    }

    private void Zoom(double factor, double centerX)
    {
        if (Bars is not { Count: > 0 }) return;
        int newCount = Clamp((int)Math.Round(_count * factor), 10, Bars.Count);
        SetCount(newCount, centerX);
    }

    private void SetCount(int newCount, double centerX)
    {
        if (Bars is not { Count: > 0 } || newCount == _count) { _count = newCount; InvalidateVisual(); return; }
        double plotW = PlotWidth();
        double frac = plotW > 0 ? (centerX - PlotLeft()) / plotW : 0.5;
        double anchor = _start + frac * _count;                 // 锚定光标处那根
        _count = newCount;
        _start = Clamp((int)Math.Round(anchor - frac * _count), 0, Math.Max(0, Bars.Count - _count));
        InvalidateVisual();
    }

    private void UpdateCross(Point p)
    {
        if (Bars is not { Count: > 0 }) return;
        double plotW = PlotWidth();
        if (p.X < PlotLeft() || p.X > PlotLeft() + plotW) { _cross = -1; }
        else
        {
            int idx = _start + (int)((p.X - PlotLeft()) / plotW * _count);
            _cross = Clamp(idx, _start, Math.Min(_start + _count - 1, Bars.Count - 1));
            _crossY = p.Y;
        }
        InvalidateVisual();
    }

    private const double LeftPad = 4, RightPad = 54, TopPad = 6, BottomPad = 18;
    private double PlotLeft() => LeftPad;
    private double PlotWidth() => Math.Max(1, Bounds.Width - LeftPad - RightPad);

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.FillRectangle(new SolidColorBrush(Colors.Black), new Rect(0, 0, w, h));
        var bars = Bars;
        if (bars == null || bars.Count == 0 || w < 60 || h < 80) return;

        int n = bars.Count;
        _count = Clamp(_count, 10, n);
        _start = Clamp(_start, 0, Math.Max(0, n - _count));
        int end = Math.Min(n - 1, _start + _count - 1);

        double plotLeft = LeftPad, plotW = PlotWidth();
        double plotTop = TopPad, plotBottom = h - BottomPad;
        double priceBottom = plotTop + (plotBottom - plotTop) * 0.72;
        double volTop = priceBottom + 8, volBottom = plotBottom;

        // 价格范围。
        double min = double.MaxValue, max = double.MinValue, volMax = 0;
        for (int i = _start; i <= end; i++)
        {
            min = Math.Min(min, bars[i].Low); max = Math.Max(max, bars[i].High);
            volMax = Math.Max(volMax, bars[i].Volume);
        }
        if (max <= min) max = min + 1;
        double pad = (max - min) * 0.05; min -= pad; max += pad;
        if (volMax <= 0) volMax = 1;

        double X(int idx) => plotLeft + (idx - _start + 0.5) / _count * plotW;
        double Yp(double price) => plotTop + (max - price) / (max - min) * (priceBottom - plotTop);
        double Yv(double vol) => volBottom - vol / volMax * (volBottom - volTop);

        var gridPen = new Pen(new SolidColorBrush(Grid), 1);
        var axisBrush = new SolidColorBrush(Axis);
        var tf = new Typeface(FontFamily.Default);

        // 价格网格 + 右侧刻度。
        for (int g = 0; g <= 4; g++)
        {
            double price = min + (max - min) * g / 4.0;
            double y = Yp(price);
            ctx.DrawLine(gridPen, new Point(plotLeft, y), new Point(plotLeft + plotW, y));
            var t = new FormattedText(price.ToString("F2"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 11, axisBrush);
            ctx.DrawText(t, new Point(plotLeft + plotW + 4, y - t.Height / 2));
        }

        double candleW = Math.Max(1.0, plotW / _count * 0.6);
        for (int i = _start; i <= end; i++)
        {
            var b = bars[i];
            var brush = new SolidColorBrush(b.Close >= b.Open ? Up : Down);
            var pen = new Pen(brush, 1);
            double x = X(i);
            ctx.DrawLine(pen, new Point(x, Yp(b.High)), new Point(x, Yp(b.Low)));
            double yo = Yp(b.Open), yc = Yp(b.Close);
            double top = Math.Min(yo, yc), bot = Math.Max(yo, yc);
            if (bot - top < 1) bot = top + 1;
            ctx.FillRectangle(brush, new Rect(x - candleW / 2, top, candleW, bot - top));
            // 成交量柱。
            var vbrush = new SolidColorBrush(b.Close >= b.Open ? Up : Down);
            ctx.FillRectangle(vbrush, new Rect(x - candleW / 2, Yv(b.Volume), candleW, volBottom - Yv(b.Volume)));
        }

        var closes = bars.Select(bb => bb.Close).ToList();
        DrawMa(ctx, closes, 5, Ma5C, end, X, Yp);
        DrawMa(ctx, closes, 10, Ma10C, end, X, Yp);
        DrawMa(ctx, closes, 20, Ma20C, end, X, Yp);

        // 日期（起止）。
        DrawText(ctx, bars[_start].PeriodStart.ToString("yyyy-MM-dd"), plotLeft, plotBottom + 3, tf, axisBrush);
        var d1 = new FormattedText(bars[end].PeriodStart.ToString("yyyy-MM-dd"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 11, axisBrush);
        ctx.DrawText(d1, new Point(plotLeft + plotW - d1.Width, plotBottom + 3));

        // 十字光标 + 读数。
        if (_cross >= _start && _cross <= end)
        {
            var cpen = new Pen(new SolidColorBrush(Cross), 1, new DashStyle(new double[] { 3, 3 }, 0));
            double cx = X(_cross);
            ctx.DrawLine(cpen, new Point(cx, plotTop), new Point(cx, volBottom));
            if (_crossY >= plotTop && _crossY <= priceBottom)
                ctx.DrawLine(cpen, new Point(plotLeft, _crossY), new Point(plotLeft + plotW, _crossY));

            var b = bars[_cross];
            // 涨跌幅现算（Bar 不再存 pct，见桌面版 §9.5：派生值读取时算）。
            double pct = _cross > 0 && bars[_cross - 1].Close > 0 ? (b.Close - bars[_cross - 1].Close) / bars[_cross - 1].Close * 100 : 0;
            string info = $"{b.PeriodStart:yyyy-MM-dd}  开{b.Open:F2} 高{b.High:F2} 低{b.Low:F2} 收{b.Close:F2}  {(pct >= 0 ? "+" : "")}{pct:F2}%";
            var it = new FormattedText(info, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 12,
                new SolidColorBrush(pct >= 0 ? Up : Down));
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), new Rect(plotLeft, plotTop, Math.Min(it.Width + 8, plotW), it.Height + 4));
            ctx.DrawText(it, new Point(plotLeft + 4, plotTop + 2));
        }
    }

    private static void DrawText(DrawingContext ctx, string s, double x, double y, Typeface tf, IBrush brush)
        => ctx.DrawText(new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 11, brush), new Point(x, y));

    private void DrawMa(DrawingContext ctx, List<double> closes, int period, Color color, int end, Func<int, double> X, Func<double, double> Y)
    {
        var ma = TechnicalIndicators.SMA(closes, period);
        var pen = new Pen(new SolidColorBrush(color), 1.2);
        Point? prev = null;
        for (int i = _start; i <= end; i++)
        {
            if (double.IsNaN(ma[i])) { prev = null; continue; }
            var p = new Point(X(i), Y(ma[i]));
            if (prev.HasValue) ctx.DrawLine(pen, prev.Value, p);
            prev = p;
        }
    }
}
