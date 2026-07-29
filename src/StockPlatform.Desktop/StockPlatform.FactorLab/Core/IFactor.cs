namespace StockPlatform.FactorLab.Core;

/// <summary>因子在框架中的角色：对照因子用来校准框架本身。</summary>
public enum FactorRole
{
    /// <summary>正式候选因子。</summary>
    Candidate,
    /// <summary>阳性对照：文献/经验上应有信号，跑不出信号说明框架有bug。</summary>
    PositiveControl,
    /// <summary>阴性对照：已验证过基本无效，跑出显著信号说明框架有前视偏差。</summary>
    NegativeControl,
    /// <summary>多因子合成：由候选因子按样本内统计加权而成，不参与去重。</summary>
    Composite,
}

/// <summary>
/// 截面因子。约定：因子值已按"预期越大越好"取向构造（取向来自文献先验，写死在定义里，不根据回测调整）；
/// Compute 只允许使用 ≤ 当日收盘的信息（评估协议为 T 收盘算因子、T+1 开盘建仓）。
/// 元数据（说明/公式/逻辑）是一等公民：因子手册和后续 Analyzer 界面都从这里读，保持单一事实来源。
/// </summary>
public interface IFactor
{
    /// <summary>中文名，如"动量20日"。</summary>
    string Name { get; }
    /// <summary>类别：趋势/反转/量价/波动/规模/资金/筹码。</summary>
    string Category { get; }
    /// <summary>构造公式（人可读）。</summary>
    string Formula { get; }
    /// <summary>作用说明：这个因子捕捉什么现象、为什么可能有效（经济学/行为逻辑）。</summary>
    string Description { get; }
    /// <summary>因子值越大代表什么（已取向后的含义）。</summary>
    string Direction { get; }
    FactorRole Role { get; }
    /// <summary>因子值矩阵 [股票][交易日]，无法计算处为 NaN。</summary>
    double[][] Compute(MarketData md);
}
