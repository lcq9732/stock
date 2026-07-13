using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 定位"阶梯低点法"用到的所有锚点（阶段顶 / 金叉 / 金叉附近低 / 前50日低 / 反弹高点P / 回踩V底），
/// 由 <see cref="RisingLowsAnalysisEngine"/>（做条件判定）和 RisingLowsChartBuilder（在图上把这些点
/// 标出来）共用——跟 TriangleConvergenceDetector 一样的分工：本类只"找点"，不做达标判断，这样图上
/// 画的点永远跟规则实际用的点是同一批，不会各算各的。找点用的窗口常量在本类；达标阈值(略高%/回踩%/
/// 放量倍数)在 engine。
/// </summary>
public static class RisingLowsDetector
{
    public const int MinBarsRequired = 120;
    private const int TolDays = 5;
    private const int CrossDaysAgo = 14;       // 金叉≈今天前14交易日，在 [今天-19, 今天-9] 里找
    private const int StageHighLookback = 60;  // 阶段顶=金叉前这么多交易日内的最高
    private const int Prior50Days = 50;        // "略高"参照：金叉附近低点往前约50交易日的最低
    private const int RetraceWindow = 10;      // 回踩V=最近这么多交易日内的最低收盘

    /// <summary>各锚点在 bars 里的下标；<see cref="CrossFound"/>=false 时 Cross/NearLow/StageHigh/
    /// Prior50Low/P 都是 -1（没找到金叉，后续依赖金叉的锚点无从谈起）。Dif/Dea 是整条序列。</summary>
    public readonly struct Anchors
    {
        public int Today { get; init; }
        public bool CrossFound { get; init; }
        public int CrossIdx { get; init; }
        public int NearLowIdx { get; init; }
        public int StageHighIdx { get; init; }
        public int Prior50LowIdx { get; init; }
        public int PIdx { get; init; }
        public int VIdx { get; init; }
        public double[] Dif { get; init; }
        public double[] Dea { get; init; }
    }

    public static Anchors? Find(IReadOnlyList<Bar> bars)
    {
        if (bars.Count < MinBarsRequired) return null;
        int today = bars.Count - 1;
        var closes = bars.Select(b => b.Close).ToList();
        var (dif, dea) = TechnicalIndicators.MACD(closes);

        int LowMinIdx(int a, int b) { a = Math.Max(0, a); b = Math.Min(today, b); int x = a; for (int i = a; i <= b; i++) if (bars[i].Low < bars[x].Low) x = i; return x; }
        int HighMaxIdx(int a, int b) { a = Math.Max(0, a); b = Math.Min(today, b); int x = a; for (int i = a; i <= b; i++) if (bars[i].High > bars[x].High) x = i; return x; }
        int CloseMinIdx(int a, int b) { a = Math.Max(0, a); b = Math.Min(today, b); int x = a; for (int i = a; i <= b; i++) if (closes[i] < closes[x]) x = i; return x; }
        int CloseMaxIdx(int a, int b) { a = Math.Max(0, a); b = Math.Min(today, b); int x = a; for (int i = a; i <= b; i++) if (closes[i] > closes[x]) x = i; return x; }

        // 金叉 G：在 [今天-19, 今天-9] 里找 DIF 上穿 DEA，取离"今天-14"最近的一个。
        int g = -1;
        for (int i = Math.Max(1, today - CrossDaysAgo - TolDays); i <= today - CrossDaysAgo + TolDays; i++)
        {
            if (double.IsNaN(dif[i]) || double.IsNaN(dea[i]) || double.IsNaN(dif[i - 1]) || double.IsNaN(dea[i - 1])) continue;
            if (dif[i - 1] <= dea[i - 1] && dif[i] > dea[i])
                if (g == -1 || Math.Abs(i - (today - CrossDaysAgo)) < Math.Abs(g - (today - CrossDaysAgo))) g = i;
        }
        bool crossFound = g != -1;

        int nlIdx = crossFound ? LowMinIdx(g - TolDays, g + TolDays) : -1;
        int hIdx = crossFound ? HighMaxIdx(g - StageHighLookback, g) : -1;
        int p50Idx = crossFound ? LowMinIdx(nlIdx - Prior50Days, nlIdx - 6) : -1;

        // 回踩V：最近约10交易日内的最低收盘；P：金叉到V之间的最高收盘（没金叉就退一步取V前10日）。
        int vIdx = CloseMinIdx(today - RetraceWindow, today - 1);
        int pIdx = crossFound ? CloseMaxIdx(g, vIdx) : CloseMaxIdx(Math.Max(0, vIdx - RetraceWindow), vIdx);

        return new Anchors
        {
            Today = today,
            CrossFound = crossFound,
            CrossIdx = g,
            NearLowIdx = nlIdx,
            StageHighIdx = hIdx,
            Prior50LowIdx = p50Idx,
            PIdx = pIdx,
            VIdx = vIdx,
            Dif = dif,
            Dea = dea,
        };
    }
}
