using System;
using System.Collections.Generic;
using StockPlatform.Logic.Abstractions;
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

    /// <summary>上证收盘是否站在 MA60 上方——短线法用它选入场时机分支（照搬桌面版
    /// ShortTermTabViewModel.ComputeMarketState）。本地缺上证日线时按"上方"处理，那是较保守的分支。</summary>
    private static bool MarketAboveMa60(IBarRepository bar)
    {
        var bars = bar.Query("sh000001", Granularity.Day);
        if (bars.Count < 60) return true;
        int i = bars.Count - 1;
        double ma60 = 0;
        for (int t = i - 59; t <= i; t++) ma60 += bars[t].Close;
        return bars[i].Close > ma60 / 60;
    }

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

        // 彬哥法后来加了第11条(股东户数环比降)、第12条(融资余额增长)，所以要多带两个仓库；
        // 缺这两张表的数据时引擎自己会跳过那两条，不会报错。
        new("彬哥法",
            Array.Empty<MethodParam>(),
            (d, p) => { var e = new MidCapPullbackAnalysisEngine(d.BarRepository, d.FundamentalRepository, d.ShareholderRepository, d.MarginRepository); return (c, n) => e.Analyze(c, n); }),

        new("金叉法",
            Array.Empty<MethodParam>(),
            (d, p) => { var e = new GoldenCrossAnalysisEngine(d.BarRepository); return (c, n) => { var r = e.Analyze(c); r.Name = n; return r; }; }),

        // 短线法的阈值后来内置进引擎了（回测定死的那套），所以这里不再有可调参数；换来的是引擎需要
        // 预先算好的三样东西：财务快照、近3年年报净利、以及大盘是否站在MA60上方（决定用哪套入场
        // 条件分支）。工厂是在后台线程里调的（见 MainViewModel.RunScreenAsync），可以放心做这些加载。
        new("短线法",
            Array.Empty<MethodParam>(),
            (d, p) =>
            {
                var financials = d.FinancialRepository.GetLatestSnapshotByCode();
                var annualProfits = d.FinancialRepository.GetRecentAnnualNetProfitByCode(3);
                var e = new ShortTermAnalysisEngine(d.BarRepository, financials, annualProfits, MarketAboveMa60(d.BarRepository));
                return (c, n) => e.Analyze(c, n);
            }),

        new("RisingLows",
            Array.Empty<MethodParam>(),
            (d, p) => { var e = new RisingLowsAnalysisEngine(d.BarRepository); return (c, n) => e.Analyze(c, n); }),
    };
}
