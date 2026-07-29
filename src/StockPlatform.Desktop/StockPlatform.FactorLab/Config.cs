namespace StockPlatform.FactorLab;

/// <summary>评估协议参数（doc/factorlab-design.md 第3节）。改动切分点/成本后样本内外结论不可比，需整体重跑。</summary>
public static class Config
{
    /// <summary>持有期（交易日）：T日收盘算因子，T+1开盘买入，T+1+HoldDays开盘结算。</summary>
    public const int HoldDays = 5;

    /// <summary>因子预热期（交易日）——最长回看窗口(120)留余量，评估从此下标之后开始。</summary>
    public const int WarmupDays = 126;

    /// <summary>上市不足该交易日数的新股剔除（次新股波动异常）。</summary>
    public const int MinListedDays = 60;

    /// <summary>样本内/样本外切分日：此日期(含)之后的调仓期为样本外，只做验证不做筛选。</summary>
    public static readonly DateOnly SplitDate = new(2025, 7, 1);

    /// <summary>双边交易成本合计（佣金+印花税+滑点），按Top组换手率扣除。</summary>
    public const double RoundTripCost = 0.003;

    /// <summary>分组数（十分组）。</summary>
    public const int Deciles = 10;

    /// <summary>股东户数报告基准日→可用日的估计披露滞后（交易日）。库里无公告日，此为保守估计。</summary>
    public const int HolderLagDays = 10;

    /// <summary>单期截面有效样本低于该数则跳过该期（防止早期数据不全时的噪声IC）。</summary>
    public const int MinCrossSection = 300;

    /// <summary>年化用的每年交易日数。</summary>
    public const double TradingDaysPerYear = 242.0;

    // ---- M3 合成与组合回测 ----

    /// <summary>进入合成因子的门槛：去重保留的候选中，样本内|ICIR|≥此值（入选/方向/权重均只用样本内）。</summary>
    public const double CompositeIcirMin = 0.3;

    /// <summary>组合回测每期持有的股票数（按合成因子取Top）。</summary>
    public const int TopN = 50;

    /// <summary>指数择时均线窗口（上证指数收盘 < MA60 → 空仓）。</summary>
    public const int TimingMaWindow = 60;
}
