using StockPlatform.FactorLab.Factors;

namespace StockPlatform.FactorLab.Core;

/// <summary>因子注册表——控制台(Program)和 Analyzer 因子Tab共用同一份清单，保证两边看到的因子一致。</summary>
public static class FactorRegistry
{
    public static List<IFactor> BuildAll() =>
    [
        // 趋势
        new Momentum(20),
        new Momentum(60, skip: 5),
        new Momentum(120),
        new NewHighDistance60(),
        // 反转
        new Reversal(5),   // 阳性对照
        new Reversal(10),
        new Reversal(20),
        new MaDeviation(20),
        new MaDeviation(60),
        // 波动
        new LowVolatility20(),
        new LowAmplitude20(),
        new LowMax20(),
        new AmplitudeShrink(),
        // 量价
        new LowTurnover20(),
        new VolumeShrink(5, 60),
        new VolumeShrink(20, 120),
        new PriceVolumeDiverge(20),
        new PriceVolumeDiverge(60),
        new StableTurnover20(),
        new Amihud20(),
        new OvernightReversal20(),
        new IntradayMomentum20(),
        new LowUpperShadow20(),
        // 规模
        new SmallSize(),
        // 基本面（M4，2026-07-31：量价因子只会排雷、顶部无区分度，正向选股靠这批不相关的信息源。
        // 数据来自 Fetcher"拉取财务报表"，没抓过时这些因子无值、在报告里显示为 "-"）
        new RoeTtm(),
        new ProfitYoy(),
        new RevenueYoy(),
        new GrossMarginDelta(),
        new CashflowQuality(),
        new LowLeverage(),
        new EarningsYield(),
        new BookToPrice(),
        // 分红（2026-08-03，Dividend 表新到位——"高股息"是 A股 2021~2024 最强风格之一，此前无法测）
        new DividendYield(),
        new DividendConsistency(),
        // 资金流（2026-08-03，NetInflow 补齐十年，此前只有3个月做不了）
        new MoneyFlow20(),
        new FlowPriceDiverge20(),
        // 筹码：十大流通股东（库里躺了545万行从没用过）
        new HolderConcentration(),
        new HolderConcentrationChange(),
        // 北向（陆股通）——数据来自十大流通股东里的"香港中央结算有限公司"，季度级、截断观测
        new NorthboundHolding(),
        new NorthboundChange(),
        // 资金
        new MarginChange20(),  // 阴性对照
        new LowMarginRatio(),  // 融资占比（水平量，与上面的"变化量"是两回事）
        new LhbCold20(),
        // 筹码
        new HolderShrink(),    // 阴性对照
    ];

    public static string RoleLabel(FactorRole role) => role switch
    {
        FactorRole.PositiveControl => "阳性对照",
        FactorRole.NegativeControl => "阴性对照",
        FactorRole.Composite => "合成",
        _ => "候选",
    };
}
