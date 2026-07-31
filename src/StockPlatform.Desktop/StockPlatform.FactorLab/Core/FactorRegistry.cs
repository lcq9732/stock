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
        // 资金
        new MarginChange20(),  // 阴性对照
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
