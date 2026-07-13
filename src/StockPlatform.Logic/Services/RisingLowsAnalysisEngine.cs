using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 用户口述形态"阶梯低点法"（2026-07-10）——一段"起点低→拉升到阶段顶→回落在更高的低点金叉→反弹到
/// P→回踩V(又一个更高的低点)→今日重新向上"的上升结构。核心是**三个依次抬高的低点**(起点低 &lt;
/// 金叉低 &lt; 回踩V低) + MACD 的位置/次序 + 量能配合(回踩缩量、今日放量)。C1~C8 全部满足才算命中
/// （AND，日线）。锚点定位交给 <see cref="RisingLowsDetector"/>（图上也用同一批点画依据）。
///
/// **阈值/天数都是拿两只样本(000977 浪潮、000066 长城)的真实数据校准出来的**（见
/// doc/analysis-app-design.md 讨论记录），故意放松到能同时框住这两只：金叉≈今天前14交易日、阶段顶≈
/// 金叉前27交易日、"略高"≤10%、回踩≤17%。这些跟最初口述的"约20天/0-1%/≤1%"不同——按口述的紧阈值
/// 会把样本自己排除掉，已与用户确认取"能框住样本"的宽口径(方案A)。所有"约N天"= 交易日，容差
/// ±<see cref="TolDays"/>。阈值常量集中在下方，方便调。
///
/// **2026-07-13 按回测数据修改 C8**：原来还要求"今日放量≥1.2×前5日均量"，86个历史时间点
/// (2024-02~2026-06 每月上/中/下旬)回测显示这条是负贡献——赢家信号日量比中位1.21、输家反而1.39，
/// 放巨量更像追高陷阱；删掉后信号数96→235提升的同时胜率52.3%→53.7%(配合市场环境过滤到60%)。
/// C8 现在只要求回踩V日缩量。配套的市场环境出手条件(宽度>50%、热度>1)在
/// <see cref="MarketEnvironmentCalculator"/>——它是市场级判断，不属于单只股票的 C1~C8。
/// 完整回测依据见 doc/analysis-app-design.md 3.2.7 的 2026-07-13 变更记录。
///
/// **2026-07-13 新增 C9**：今日收盘距阶段顶最高价至少保留 3% 空间（用户观察：已突破前高的命中
/// 属于追高，如 000977 于 07-09 破顶后仍命中；历史276笔按位置分桶验证——贴顶-3%~0胜率仅50%、
/// 破顶后53.6%，顶下3%~15%是最优区间；加 C9(3%) 后整体 60.1%→65.1% 胜率，分年都稳定）。
/// </summary>
public class RisingLowsAnalysisEngine
{
    private const int TolDays = 5;
    private const int StageHighBeforeCross = 27;      // 阶段顶应在金叉前约27交易日(判定用)
    private const double SlightlyHigherMaxPct = 10.0; // ③"略高"上限(样本 +3.3% / +9.3%)
    private const double RetraceMaxPct = 17.0;        // ⑤回踩幅度上限(样本 8.9% / 16.4%)
    private const int VolumeAvgDays = 5;              // ⑧量能对比的"前N日均量"窗口
    private const double MinGapBelowStageHighPct = 3.0; // ⑨今日收盘距阶段顶最高价至少留的空间(%)

    private readonly IBarRepository _barRepository;
    public RisingLowsAnalysisEngine(IBarRepository barRepository) => _barRepository = barRepository;

    public StockScreenResult Analyze(string code, string name)
    {
        var bars = _barRepository.Query(code, Granularity.Day);
        if (bars.Count < RisingLowsDetector.MinBarsRequired)
            return new StockScreenResult
            {
                Code = code,
                Name = name,
                Granularity = Granularity.Day,
                Error = $"日线历史数据不足（仅 {bars.Count} 条），至少需要 {RisingLowsDetector.MinBarsRequired} 条",
            };

        var a = RisingLowsDetector.Find(bars)!.Value;
        int today = a.Today;
        var closes = bars.Select(b => b.Close).ToList();
        var volumes = bars.Select(b => b.Volume).ToList();
        var dif = a.Dif; var dea = a.Dea;
        bool crossFound = a.CrossFound;

        double AvgVolBefore(int idx, int n) { int s0 = Math.Max(0, idx - n); if (idx - s0 <= 0) return double.NaN; double s = 0; for (int i = s0; i < idx; i++) s += volumes[i]; return s / (idx - s0); }

        double nearLow = crossFound ? bars[a.NearLowIdx].Low : double.NaN;

        // ── C2 金叉：约14交易日前 DIF 上穿 DEA，且金叉在零轴下方 ──
        bool crossBelowZero = crossFound && dif[a.CrossIdx] < 0;
        bool c2 = crossFound && crossBelowZero;
        string c2Basis = crossFound
            ? $"金叉 {bars[a.CrossIdx].PeriodStart:yyyy-MM-dd}（今天前{today - a.CrossIdx}交易日）：DIF{dif[a.CrossIdx - 1]:F3}上穿DEA{dea[a.CrossIdx - 1]:F3}→{dif[a.CrossIdx]:F3}/{dea[a.CrossIdx]:F3}；金叉时DIF在零轴{(crossBelowZero ? "下方✓" : "上方✗")}"
            : $"约今天前14±{TolDays}交易日内没找到MACD金叉";

        // ── C1 阶段顶：金叉前约27交易日出现"金叉前60交易日内最高点" ──
        bool c1 = false; string c1Basis;
        if (crossFound)
        {
            int gap = a.CrossIdx - a.StageHighIdx;
            c1 = gap >= StageHighBeforeCross - TolDays && gap <= StageHighBeforeCross + TolDays;
            c1Basis = $"阶段顶 {bars[a.StageHighIdx].High:F2}（{bars[a.StageHighIdx].PeriodStart:yyyy-MM-dd}），在金叉前{gap}交易日（需约{StageHighBeforeCross}±{TolDays}）";
        }
        else c1Basis = "未找到金叉，无法定位阶段顶";

        // ── C3 更高的低点①：金叉附近最低 略高于 其前约50交易日最低（0%~10%）──
        bool c3 = false; string c3Basis;
        if (crossFound)
        {
            double prior50 = bars[a.Prior50LowIdx].Low;
            double diffPct = prior50 > 0 ? (nearLow - prior50) / prior50 * 100 : 0;
            c3 = a.NearLowIdx - 50 >= 0 && nearLow > prior50 && diffPct <= SlightlyHigherMaxPct;
            c3Basis = $"金叉附近最低{nearLow:F2}（{bars[a.NearLowIdx].PeriodStart:yyyy-MM-dd}），前约50交易日最低{prior50:F2}，高出{diffPct:+0.00;-0.00}%（需>0且≤{SlightlyHigherMaxPct:F0}%）";
        }
        else c3Basis = "未找到金叉，无法判断略高";

        double vLow = bars[a.VIdx].Low;
        double retracePct = closes[a.PIdx] > 0 ? (closes[a.PIdx] - closes[a.VIdx]) / closes[a.PIdx] * 100 : 0;

        // ── C4 金叉后反弹到 P ──
        bool c4 = crossFound && a.PIdx > a.CrossIdx && a.PIdx < a.VIdx;
        string c4Basis = crossFound
            ? $"金叉后反弹高点P 收{closes[a.PIdx]:F2}（{bars[a.PIdx].PeriodStart:yyyy-MM-dd}），在金叉之后、回踩V之前：{(c4 ? "是" : "否")}"
            : "未找到金叉，无法判断金叉后的反弹高点";

        // ── C5 回踩V：自P回落≤17%，且V低点高于金叉低点（更高的低点②）──
        bool c5 = false; string c5Basis;
        if (crossFound)
        {
            bool depthOk = retracePct > 0 && retracePct <= RetraceMaxPct;
            bool higherLow = vLow > nearLow;
            c5 = depthOk && higherLow;
            c5Basis = $"回踩V底 低{vLow:F2}/收{closes[a.VIdx]:F2}（{bars[a.VIdx].PeriodStart:yyyy-MM-dd}），自P回落{retracePct:F2}%（需≤{RetraceMaxPct:F0}%）；V低{vLow:F2}{(higherLow ? ">" : "≤")}金叉低{nearLow:F2}";
        }
        else c5Basis = "未找到金叉，无法判断回踩与更高低点";

        // ── C6 MACD：金叉后DIF始终不下穿DEA；回踩时DIF小回后重新抬头；今日DIF/DEA双双在零轴上方 ──
        bool c6 = false; string c6Basis;
        if (crossFound)
        {
            bool noDeathCross = true;
            for (int i = a.CrossIdx; i <= today; i++) if (dif[i] < dea[i]) { noDeathCross = false; break; }
            bool difDipThenRise = dif[today] > dif[a.VIdx];
            bool bothAboveZeroToday = dif[today] > 0 && dea[today] > 0;
            c6 = noDeathCross && difDipThenRise && bothAboveZeroToday;
            c6Basis = $"金叉后DIF未下穿DEA：{(noDeathCross ? "是" : "否")}；DIF回踩到{dif[a.VIdx]:F3}后回升到{dif[today]:F3}：{(difDipThenRise ? "是" : "否")}；今日DIF{dif[today]:F3}/DEA{dea[today]:F3}均在零轴上：{(bothAboveZeroToday ? "是" : "否")}";
        }
        else c6Basis = "未找到金叉，无法判断MACD配合";

        // ── C7 今日向上确认：今收 > 昨收，且今收已高于回踩V底 ──
        bool c7 = closes[today] > closes[today - 1] && closes[today] > closes[a.VIdx];
        string c7Basis = $"今收{closes[today]:F2} vs 昨收{closes[today - 1]:F2}、V底收{closes[a.VIdx]:F2}（需今收同时高于两者）";

        // ── C8 量能：回踩V日缩量（<前5日均量）。今日放量的旧要求已按回测删除（见类注释），
        // 今日量比只在依据文字里展示供参考，不参与判定 ──
        double volAvgBeforeV = AvgVolBefore(a.VIdx, VolumeAvgDays);
        double volAvgBeforeToday = AvgVolBefore(today, VolumeAvgDays);
        double vVolRatio = volAvgBeforeV > 0 ? volumes[a.VIdx] / volAvgBeforeV : double.NaN;
        double todayVolRatio = volAvgBeforeToday > 0 ? volumes[today] / volAvgBeforeToday : double.NaN;
        bool c8 = !double.IsNaN(vVolRatio) && vVolRatio < 1.0;
        string c8Basis = $"回踩V日量比{vVolRatio:F2}（需<1.00，缩量）；今日量比{todayVolRatio:F2}（仅参考，回测显示>2属追高风险）";

        // ── C9 距前高留空间：今日收盘 ≤ 阶段顶最高价×(1-3%)，破顶/贴顶=追高不选 ──
        bool c9 = false; string c9Basis;
        if (crossFound)
        {
            double topHigh = bars[a.StageHighIdx].High;
            double posPct = topHigh > 0 ? (closes[today] - topHigh) / topHigh * 100 : double.NaN;
            c9 = !double.IsNaN(posPct) && posPct <= -MinGapBelowStageHighPct;
            c9Basis = $"今收{closes[today]:F2} vs 阶段顶最高{topHigh:F2}，位置{posPct:+0.00;-0.00}%（需≤-{MinGapBelowStageHighPct:F0}%，即距前高留足空间；破顶/贴顶=追高）";
        }
        else c9Basis = "未找到金叉，无法定位阶段顶";

        var result = new StockScreenResult
        {
            Code = code,
            Name = name,
            Granularity = Granularity.Day,
            DataDate = bars[today].PeriodStart,
            LastClose = closes[today],
            Criteria = new List<CriterionResult>
            {
                new() { Name = "C1 金叉前约27交易日出现阶段顶(近60交易日最高)", Satisfied = c1, Basis = c1Basis },
                new() { Name = "C2 约14交易日前出现MACD金叉，且金叉在零轴下方", Satisfied = c2, Basis = c2Basis },
                new() { Name = "C3 金叉附近最低 略高于 其前约50交易日最低(0~10%)", Satisfied = c3, Basis = c3Basis },
                new() { Name = "C4 金叉后反弹到一个高点P", Satisfied = c4, Basis = c4Basis },
                new() { Name = "C5 回踩V(自P回落≤17%)，且V低点高于金叉低点", Satisfied = c5, Basis = c5Basis },
                new() { Name = "C6 MACD：金叉后不死叉、回踩后DIF回升、今日双线上零轴", Satisfied = c6, Basis = c6Basis },
                new() { Name = "C7 今日向上确认(今收高于昨收且高于V底)", Satisfied = c7, Basis = c7Basis },
                new() { Name = "C8 量能：回踩V日缩量(<前5日均量)", Satisfied = c8, Basis = c8Basis },
                new() { Name = "C9 今日收盘距阶段顶最高价≥3%(未破顶/未贴顶)", Satisfied = c9, Basis = c9Basis },
            },
        };
        result.Passed = result.Criteria.AllSatisfiedIgnoringMissingData();
        return result;
    }
}
