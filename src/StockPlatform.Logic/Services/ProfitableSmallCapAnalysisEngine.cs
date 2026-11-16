using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// "陈峰法"（2026-11-16）——类名按算法命名（盈利小盘 + 低位 + 缩量），不随人名而变，同彬哥法的惯例。
///
/// 来历：库里十大流通股东"陈峰"的持仓风格——流通 5~50 亿的小盘、盈利、在一年低位附近、缩量时分批买、
/// 放量时卖。回测（scratchpad cfbt_run3~6，结论在记忆 project_chenfeng_rule_backtest）：
/// · 只靠选股（取位置最低的 30 只月调）年化 −4.1%，**挑低位本身不赚钱**；
/// · 收益来自卖出纪律——"放量就减"叠加价格版加减仓后年化 +9.3%、最大回撤 −31%
///   （同池盈利股等权基准 +6.4% / −53%）。所以这一页只是"建仓名单"，卖出纪律在每日晨检里
///   （<see cref="VolumeExitDiscipline.EvaluateHolding"/>），两半要一起用。
/// · 用户加的"股价≥15元"在回测里是负贡献（同池等权 +6.4% → +3.5%），按用户要求保留。
///
/// 八条全部满足才入选：
///   1 非北交所、非ST　2 流通市值 5~50 亿　3 上市满 1 年（StockMeta.list_date，缺了退回第一根K线）
///   4 TTM 归母净利 &gt; 0　5 TTM 经营现金流 &gt; 0　6 股价 ≥ 15 元
///   7 250 日位置 &lt; 30%　8 近 5 个交易日（含当天）至少 1 天缩量（成交额 &lt; 前20日均×0.6）
///
/// 性能：全市场 5500 只逐只跑。先判不用查库的 1、2（市值逐只查一次 FundamentalMetric），不过就直接返回；
/// 过了才读日K判 3、6、7、8；还过了才逐只读财报算 TTM——财报那张表大，只给剩下的几十~几百只读。
/// 所以**没通过的结果 Criteria 是不完整的**（后面的条件没算），结果表本来也只列通过的。
/// </summary>
public class ProfitableSmallCapAnalysisEngine
{
    public double MinMarketCap { get; init; } = 5e8;
    public double MaxMarketCap { get; init; } = 50e8;
    public double MinPrice { get; init; } = 15;
    public int PositionDays { get; init; } = 250;
    public double MaxPosition { get; init; } = 0.30;
    /// <summary>"前几天或当天缩量"的窗口（含当天）。</summary>
    public int ShrinkWindowDays { get; init; } = 5;

    private readonly IBarRepository _barRepository;
    private readonly IFundamentalMetricRepository _fundamentalRepository;
    private readonly IFinancialRepository _financialRepository;
    private readonly IReadOnlyDictionary<string, DateTime> _listDates;

    /// <param name="listDates">StockMeta.list_date（【新股发行】写的），没有的票不在字典里。</param>
    public ProfitableSmallCapAnalysisEngine(IBarRepository barRepository, IFundamentalMetricRepository fundamentalRepository,
        IFinancialRepository financialRepository, IReadOnlyDictionary<string, DateTime> listDates)
    {
        _barRepository = barRepository;
        _fundamentalRepository = fundamentalRepository;
        _financialRepository = financialRepository;
        _listDates = listDates;
    }

    public StockScreenResult Analyze(string code, string name)
    {
        var caps = _fundamentalRepository.Query(code, MetricKeys.CirculatingMarketCap);
        double? cap = caps.Count > 0 ? caps[^1].Value : null;
        return Evaluate(code, name, cap,
            _listDates.TryGetValue(code, out var ld) ? ld : null,
            () => _barRepository.Query(code, Granularity.Day),
            () => _financialRepository.GetAllByCode(code));
    }

    /// <summary>判定本体——数据由调用方喂进来（K线和财报用委托，前面的条件不过就不读），单测直接调它。</summary>
    /// <param name="financials">全部报告期，最新在前（同 <see cref="IFinancialRepository.GetAllByCode"/>）。</param>
    public StockScreenResult Evaluate(string code, string name, double? marketCap, DateTime? listDate,
        Func<IReadOnlyList<Bar>> loadBars, Func<List<FinancialSnapshot>> financials)
    {
        var result = new StockScreenResult { Code = code, Name = name, Granularity = Granularity.Day };
        var c = result.Criteria;

        var board = MarketClassifier.Classify(code);
        bool notBjSt = board != MarketBoard.Beijing && !name.Contains("ST");
        c.Add(new() { Name = "1 非北交所、非ST", Satisfied = notBjSt, Basis = $"板块={MarketClassifier.DisplayName(board)}；简称={name}" });

        bool capOk = marketCap is { } mc && mc >= MinMarketCap && mc <= MaxMarketCap;
        c.Add(new()
        {
            Name = $"2 流通市值 {MinMarketCap / 1e8:F0}~{MaxMarketCap / 1e8:F0} 亿",
            Satisfied = capOk,
            Basis = marketCap is { } v ? $"流通市值={v / 1e8:F1}亿" : "缺少流通市值数据（退市/长期停牌的票常见），按不满足处理",
        });
        if (!notBjSt || !capOk) return result;

        var bars = loadBars();
        if (bars.Count == 0) { result.Error = "没有日K数据"; return result; }
        int i = bars.Count - 1;
        var last = bars[i];
        result.DataDate = last.PeriodStart;
        result.LastClose = last.Close;

        // 3 上市满 1 年
        var listed = listDate ?? bars[0].PeriodStart;
        bool listOk = listed <= last.PeriodStart.AddYears(-1);
        c.Add(new()
        {
            Name = "3 上市满 1 年",
            Satisfied = listOk,
            Basis = $"上市日={listed:yyyy-MM-dd}" + (listDate == null ? "（名册里没有上市日，用第一根日K代替）" : ""),
        });

        // 6 股价 ≥ 15 元——前复权的最新收盘就是实际价
        bool priceOk = last.Close >= MinPrice;
        c.Add(new() { Name = $"6 股价 ≥ {MinPrice:F0} 元", Satisfied = priceOk, Basis = $"最新收盘={last.Close:F2}（{last.PeriodStart:MM-dd}）" });

        // 7 一年低位
        var pos = VolumeExitDiscipline.Position(bars, PositionDays);
        bool posOk = pos < MaxPosition;
        result.SortScore = pos * 100;
        c.Add(new()
        {
            Name = $"7 近一年处于低位（{PositionDays}日位置 < {MaxPosition * 100:F0}%）",
            Satisfied = posOk,
            Basis = pos is { } p ? $"{PositionDays}日位置={p * 100:F0}%（0%=一年最低收盘，100%=一年最高收盘）" : "K线不足，算不出位置",
        });

        // 8 近 N 日内有缩量
        var shrinkDays = new List<DateTime>();
        int judged = 0;
        bool todayShrink = false;
        for (int t = Math.Max(0, i - ShrinkWindowDays + 1); t <= i; t++)
        {
            var r = VolumeExitDiscipline.AmountRatio(bars, t);
            if (r == null) continue;
            judged++;
            if (r < VolumeExitDiscipline.ShrinkRatio)
            {
                shrinkDays.Add(bars[t].PeriodStart);
                if (t == i) todayShrink = true;
            }
        }
        var todayRatio = VolumeExitDiscipline.AmountRatio(bars, i);
        bool shrinkMissing = judged == 0;
        c.Add(new()
        {
            Name = $"8 近{ShrinkWindowDays}个交易日（含当天）有缩量（成交额 < 前{VolumeExitDiscipline.AvgDays}日均×{VolumeExitDiscipline.ShrinkRatio}）",
            Satisfied = shrinkDays.Count > 0,
            DataMissing = shrinkMissing,
            Basis = shrinkMissing
                ? $"近{ShrinkWindowDays}天成交额缺失或之前有效天数不足{VolumeExitDiscipline.MinValidDays}天，这条跳过"
                : $"缩量{shrinkDays.Count}天" + (shrinkDays.Count > 0 ? $"（{string.Join("、", shrinkDays.Select(d => d.ToString("MM-dd")))}）" : "") +
                  $"；今天成交额为20日均×{todayRatio:F2}",
        });
        result.PatternNote = shrinkMissing
            ? "成交额缺失"
            : $"缩量{shrinkDays.Count}天" +
              (shrinkDays.Count > 0 ? $"（{string.Join("、", shrinkDays.Select(d => d.ToString("MM-dd")))}）" : "") +
              (todayShrink ? "｜今天缩量" : "") +
              (todayRatio is { } tr ? $"｜今日量比{tr:F2}" : "");

        if (!listOk || !priceOk || !posOk || !(shrinkDays.Count > 0 || shrinkMissing)) return result;

        // 4、5 TTM
        var fins = financials();
        var (np, npBasis) = Ttm(fins, FinancialKeys.NetProfitParent);
        var (ocf, ocfBasis) = Ttm(fins, FinancialKeys.Ocf);
        c.Insert(3, new() { Name = "4 近四季（TTM）归母净利 > 0", Satisfied = np > 0, Basis = npBasis });
        c.Insert(4, new() { Name = "5 近四季（TTM）经营现金流 > 0", Satisfied = ocf > 0, Basis = ocfBasis });

        result.Passed = c.AllSatisfiedIgnoringMissingData();
        return result;
    }

    /// <summary>TTM = 年报直接用，中间期 = 上年年报 − 上年同期 + 本期（口径同 <see cref="FinancialAnalyzer.TtmFromCumulative"/>）。
    /// 缺上年数据时退回最新一期累计值，依据里写明。</summary>
    public static (double? Value, string Basis) Ttm(List<FinancialSnapshot> desc, string key)
    {
        var cur = desc.FirstOrDefault(s => s.Get(key) != null);
        if (cur == null) return (null, "没有财报数据");
        var lastAnnual = desc.FirstOrDefault(h => h.ReportDate.Month == 12 && h.ReportDate.Year == cur.ReportDate.Year - 1);
        var prior = desc.FirstOrDefault(h => h.ReportDate.Month == cur.ReportDate.Month && h.ReportDate.Year == cur.ReportDate.Year - 1);
        var ttm = FinancialAnalyzer.TtmFromCumulative(cur.ReportDate, cur.Get(key), lastAnnual?.Get(key), prior?.Get(key));
        if (ttm is { } t)
            return (t, $"TTM={t / 1e8:+0.00;-0.00}亿（截至{cur.ReportDate:yyyy-MM-dd}）");
        var v = cur.Get(key)!.Value;
        return (v, $"缺上年同期，退回最新一期累计={v / 1e8:+0.00;-0.00}亿（{cur.ReportDate:yyyy-MM-dd}）");
    }
}
