using System;
using System.Collections.Generic;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Mobile.Services;

/// <summary>一个可调参数的定义（跟桌面版各方法可调项对应）。</summary>
public record MethodParam(string Label, double Default, double Min, double Max, double Increment = 1);

/// <summary>一个可在手机上跑的选股方法：显示名 + 可调参数列表 + "按参数给一批股票逐只判定"的工厂。
/// 参数值按 Params 顺序传给 Factory。所有方法复用桌面版分析引擎。</summary>
public record ScreeningMethod(
    string Name,
    IReadOnlyList<MethodParam> Params,
    Func<DataService, IReadOnlyList<double>, Func<string, string, StockScreenResult>> Factory)
{
    public override string ToString() => Name;
}

public static class ScreeningMethods
{
    private static MethodParam P(string l, double def, double min, double max, double inc = 1) => new(l, def, min, max, inc);

    public static readonly IReadOnlyList<ScreeningMethod> All = new List<ScreeningMethod>
    {
        new("三角收敛",
            new[] { P("形态窗口(天)", 90, 30, 250, 5), P("摆动点窗口(±天)", 3, 2, 10), P("R²下限", 0.45, 0.1, 0.9, 0.05) },
            (d, p) => { var e = new TriangleConvergenceAnalysisEngine(d.BarRepository); return (c, n) => e.Analyze(c, n, (int)p[0], (int)p[1], p[2]); }),

        new("峰哥法(近N天涨停)",
            new[] { P("近N天涨停", 7, 1, 30) },
            (d, p) => { var e = new FoundationAnalysisEngine(d.BarRepository); return (c, n) => e.Analyze(c, n, (int)p[0]); }),

        new("耀哥法",
            new[] { P("DIF阈值", 0, 0, 1, 0.05) },
            (d, p) => { var e = new BottomReboundAnalysisEngine(d.BarRepository, d.NetInflowRepository); return (c, n) => { var r = e.Analyze(c, p[0]); r.Name = n; return r; }; }),

        new("彬哥法",
            Array.Empty<MethodParam>(),
            (d, p) => { var e = new MidCapPullbackAnalysisEngine(d.BarRepository, d.FundamentalRepository); return (c, n) => e.Analyze(c, n); }),

        new("金叉法",
            Array.Empty<MethodParam>(),
            (d, p) => { var e = new GoldenCrossAnalysisEngine(d.BarRepository); return (c, n) => { var r = e.Analyze(c); r.Name = n; return r; }; }),

        new("短线法",
            new[] { P("放量倍数", 1.5, 1, 5, 0.1), P("当日涨幅上限%", 7, 1, 20, 0.5), P("流通市值下限(亿)", 30, 0, 2000, 10), P("流通市值上限(亿)", 300, 0, 5000, 10) },
            (d, p) => { var e = new ShortTermAnalysisEngine(d.BarRepository, d.NetInflowRepository, d.FundamentalRepository); return (c, n) => e.Analyze(c, n, p[0], p[1], p[2], p[3]); }),

        new("RisingLows",
            Array.Empty<MethodParam>(),
            (d, p) => { var e = new RisingLowsAnalysisEngine(d.BarRepository); return (c, n) => e.Analyze(c, n); }),
    };
}
