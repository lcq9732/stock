using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 阶梯低点法"出手条件"用到的两个市场级指标（2026-07-13 回测引入，计算口径见
/// doc/analysis-app-design.md 3.2.7 变更记录）。两者都只用当天及之前的数据，收盘后即可计算：
///
/// - <see cref="BreadthPct"/> **市场宽度** = 本地库全部股票中，最新交易日"收盘价 &gt; 自身20日
///   收盘均线(MA20，含当日)"的占比。只统计当天有K线且历史够20根的股票（停牌股不进分母）。
/// - <see cref="HeatRatio"/> **市场热度** = 最新交易日全市场 Σ(成交量×收盘价) ÷ 之前20个交易日
///   同一合计值的平均（不含当日）。用 量×收盘价 代理成交额，因为库里 Bar.Amount 未填(全为0)。
///
/// 回测结论(86个时间点, 2024-02~2026-06)：宽度≤50%时信号胜率36.5%、热度≤1时44.2%，两条都满足时
/// 60%——所以两条都过才算"适合出手"。阈值是在同一批历史数据上选的，有过拟合成分，只作提示、
/// 不强行过滤结果。
/// </summary>
public class MarketEnvironmentResult
{
    /// <summary>指标对应的交易日（=库里最新一天）。</summary>
    public DateTime Date { get; init; }
    /// <summary>进入宽度统计的股票数（当天有K线且≥20根历史）。</summary>
    public int SampleCount { get; init; }
    /// <summary>市场宽度（%）：收盘站上自身MA20的股票占比。</summary>
    public double BreadthPct { get; init; }
    /// <summary>市场热度：当日全市场量额 / 前20交易日均值；历史不足20天时为 null。</summary>
    public double? HeatRatio { get; init; }

    public bool BreadthOk => BreadthPct > MarketEnvironmentCalculator.BreadthMinPct;
    public bool HeatOk => HeatRatio.HasValue && HeatRatio.Value > MarketEnvironmentCalculator.HeatMinRatio;
    /// <summary>宽度、热度双双达标才算"适合出手"。</summary>
    public bool Passed => BreadthOk && HeatOk;

    public string Summary =>
        $"市场环境({Date:yyyy-MM-dd}，样本{SampleCount}只)：" +
        $"宽度{BreadthPct:F0}%（需>{MarketEnvironmentCalculator.BreadthMinPct:F0}%{(BreadthOk ? "✓" : "✗")}）  " +
        $"热度{(HeatRatio.HasValue ? HeatRatio.Value.ToString("F2") : "无法计算")}（需>{MarketEnvironmentCalculator.HeatMinRatio:F1}{(HeatOk ? "✓" : "✗")}）" +
        $" → {(Passed ? "满足出手条件" : "不满足，按回测规则今日信号仅观察")}";
}

public static class MarketEnvironmentCalculator
{
    public const double BreadthMinPct = 50.0;  // 宽度>50%才出手
    public const double HeatMinRatio = 1.0;    // 热度>1才出手
    private const int MaDays = 20;             // 宽度的均线窗口
    private const int HeatAvgDays = 20;        // 热度的"前N交易日均值"窗口

    /// <summary>扫全库算最新交易日的宽度/热度；库为空时返回 null。每只股票只取最近约3个月的
    /// 日线（够 MA20 + 热度20日均值即可），比完整分析扫描轻得多。</summary>
    public static MarketEnvironmentResult? Compute(IBarRepository repository, Action<string>? progress = null)
    {
        var latest = repository.GetOverallLatestPeriodStart(Granularity.Day);
        if (latest == null) return null;
        var start = latest.Value.AddDays(-90); // 90自然日≈60交易日，含长假也够 21 天热度窗口

        var codes = repository.GetAllCodes();
        int above = 0, sampled = 0;
        var amountByDate = new Dictionary<DateTime, double>();
        for (int i = 0; i < codes.Count; i++)
        {
            if (progress != null && i % 500 == 0) progress($"正在计算市场环境 {i}/{codes.Count}");
            var bars = repository.Query(codes[i], Granularity.Day, start, latest);
            foreach (var b in bars)
            {
                var d = b.PeriodStart.Date;
                amountByDate[d] = amountByDate.GetValueOrDefault(d) + b.Volume * b.Close;
            }
            if (bars.Count >= MaDays && bars[^1].PeriodStart.Date == latest.Value.Date)
            {
                double ma = 0;
                for (int j = bars.Count - MaDays; j < bars.Count; j++) ma += bars[j].Close;
                ma /= MaDays;
                sampled++;
                if (bars[^1].Close > ma) above++;
            }
        }
        if (sampled == 0) return null;

        double? heat = null;
        var days = amountByDate.Keys.OrderBy(d => d).ToList();
        int todayIdx = days.IndexOf(latest.Value.Date);
        if (todayIdx >= HeatAvgDays)
        {
            double sum = 0;
            for (int j = todayIdx - HeatAvgDays; j < todayIdx; j++) sum += amountByDate[days[j]];
            double avg = sum / HeatAvgDays;
            if (avg > 0) heat = amountByDate[days[todayIdx]] / avg;
        }

        return new MarketEnvironmentResult
        {
            Date = latest.Value.Date,
            SampleCount = sampled,
            BreadthPct = 100.0 * above / sampled,
            HeatRatio = heat,
        };
    }
}
