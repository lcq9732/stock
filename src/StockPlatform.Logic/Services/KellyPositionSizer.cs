namespace StockPlatform.Logic.Services;

/// <summary>
/// 一次仓位决策的输入——"这只票我认为有 <see cref="WinProbability"/> 的概率涨 <see cref="GainPct"/>，
/// 否则我会在跌 <see cref="LossPct"/> 时止损"。买入前后用的是同一套输入：判断该买多少 和 该留多少，
/// 在凯利框架里是同一个问题（成本价是沉没成本，跟"现在最优仓位是多少"无关）。
/// </summary>
public class PositionSizingInput
{
    /// <summary>上涨概率，0~1（界面上填百分数）。</summary>
    public double WinProbability { get; set; }

    /// <summary>判断对了预计能涨多少，正数百分点（如 20 表示 +20%）。</summary>
    public double GainPct { get; set; }

    /// <summary>判断错了在哪里止损，正数百分点（如 10 表示 -10%）。</summary>
    public double LossPct { get; set; }

    /// <summary>可投资总资金（元）——分母是"这笔钱的全部"，不是某只票已投入的金额。</summary>
    public double Capital { get; set; }

    /// <summary>凯利折扣系数：1=满凯利、0.5=半凯利、0.25=四分之一凯利。默认半凯利，理由见
    /// <see cref="KellyPositionSizer"/> 的类注释。</summary>
    public double KellyFraction { get; set; } = 0.5;

    /// <summary>单只票的仓位硬上限（0~1）——凯利假设"输了还能再来"，但个股有停牌/暴雷这类
    /// 不可逆风险，所以不管公式算出多少都压在这条线以内。</summary>
    public double MaxWeight { get; set; } = 0.25;
}

/// <summary>
/// 凯利公式（Kelly criterion）算出来的建议仓位。三个仓位数字是逐级收紧的：
/// <see cref="FullKelly"/>（理论最优）→ <see cref="ScaledKelly"/>（打折）→ <see cref="FinalWeight"/>
/// （再受单票上限约束），界面上三个都显示，让人看得见每一步被砍掉了多少、为什么。
/// </summary>
public class PositionSizingResult
{
    /// <summary>盈亏比 b = 目标涨幅 ÷ 止损幅度。</summary>
    public double Odds { get; init; }

    /// <summary>每投入 1 元的期望收益率（%）= p×涨幅 − q×跌幅。这是"有没有正期望"的判据：
    /// 它 ≤0 时不管仓位怎么调都是负期望，凯利也会给出 0。</summary>
    public double ExpectedReturnPct { get; init; }

    /// <summary>满凯利仓位（占总资金比例）。可能为负——那表示这笔交易本身就不值得做。</summary>
    public double FullKelly { get; init; }

    /// <summary>按 <see cref="PositionSizingInput.KellyFraction"/> 打折后的仓位。</summary>
    public double ScaledKelly { get; init; }

    /// <summary>最终建议仓位——打折后再截到 [0, 单票上限]。</summary>
    public double FinalWeight { get; init; }

    /// <summary>最终建议金额（元）。</summary>
    public double Amount { get; init; }

    /// <summary>最终仓位是被什么定下来的：单票上限截断了，还是打折后的凯利本身，还是负期望归零。</summary>
    public SizingBinding Binding { get; init; }

    /// <summary>按这个仓位，一次止损会亏掉总资金的百分之几——凯利的数字通常比直觉大，
    /// 配上这个"错一次要付多少钱"才好判断自己扛不扛得住。</summary>
    public double DrawdownPctOfCapital { get; init; }

    /// <summary>保本概率：低于这个上涨概率，这笔交易就是负期望（q/p 恰好等于赔率时的 p）。
    /// = 止损幅度 ÷（目标涨幅＋止损幅度）。</summary>
    public double BreakEvenProbability { get; init; }

    /// <summary>把上涨概率下调 10 个百分点后的最终仓位——用来看"我要是把胜率估高了 10 个点，
    /// 正确的仓位会缩到多少"。凯利对 p 的高估极其敏感，这一栏就是给这件事一个数。</summary>
    public double FinalWeightIfOverconfident { get; init; }
}

/// <summary>最终仓位是被哪一条约束定下来的。</summary>
public enum SizingBinding
{
    /// <summary>正常——打折后的凯利仓位本身就在上限以内。</summary>
    Kelly,

    /// <summary>被单票上限截断了（说明这笔机会在你的估计下"很好"，但集中度风险不允许押这么多）。</summary>
    MaxWeightCap,

    /// <summary>负期望，建议 0 仓位（不该买；已持仓则该清）。</summary>
    NoEdge,
}

/// <summary>
/// 凯利公式仓位计算器（2026-08-17新增，用于"仓位计算器"窗口）。
///
/// f* = (p·b − q) / b，其中 p=上涨概率、q=1−p、b=盈亏比（目标涨幅÷止损幅度），f* 是该品种
/// 占**可投资总资金**的比例。它最大化的是长期对数收益（几何增长率），不是单次期望收益——这正是
/// "别把话说满"的数学理由：满仓押正期望的赌局，长期几乎必然破产。
///
/// 三个刻意的保守处理，都在 <see cref="Calculate"/> 里：
/// ① 默认半凯利。凯利假设 p 是准的，而这里的 p 是人估出来的；p 高估会让实际增长率掉到负数，
///    而半凯利只损失约 1/4 的理论增长率，却把波动砍掉一半——估计有误差时这笔买卖非常划算。
/// ② 单票硬上限（默认25%）。凯利的推导里"输了还能再来"，个股的停牌/退市/暴雷不满足这个前提。
/// ③ 负期望直接归零，不做空——本程序只处理做多。
/// </summary>
public static class KellyPositionSizer
{
    public static PositionSizingResult Calculate(PositionSizingInput input)
    {
        double p = Math.Clamp(input.WinProbability, 0, 1);
        double q = 1 - p;
        double gain = Math.Max(input.GainPct, 0);
        double loss = Math.Max(input.LossPct, 0);

        // 止损填 0 时 b 无穷大（"不可能亏钱"），凯利会给出满仓——这不是一个有意义的输入，
        // 直接当成没有边际优势返回，界面上会提示把止损填上。
        if (loss <= 0 || gain <= 0)
            return new PositionSizingResult { Binding = SizingBinding.NoEdge, BreakEvenProbability = 1 };

        double b = gain / loss;
        double expected = p * gain - q * loss;          // 每 1 元的期望收益率（%）
        double fullKelly = (p * b - q) / b;             // = p − q/b
        double scaled = fullKelly * Math.Max(input.KellyFraction, 0);
        double cap = Math.Clamp(input.MaxWeight, 0, 1);

        double final;
        SizingBinding binding;
        if (fullKelly <= 0)
        {
            final = 0;
            binding = SizingBinding.NoEdge;
        }
        else if (scaled > cap)
        {
            final = cap;
            binding = SizingBinding.MaxWeightCap;
        }
        else
        {
            final = scaled;
            binding = SizingBinding.Kelly;
        }

        // 敏感度：把胜率下调 10 个百分点重算一遍（同样的折扣和上限），看仓位缩到多少。
        double pLow = Math.Max(p - 0.10, 0);
        double kellyLow = (pLow * b - (1 - pLow)) / b;
        double finalLow = Math.Clamp(kellyLow * Math.Max(input.KellyFraction, 0), 0, cap);

        return new PositionSizingResult
        {
            Odds = b,
            ExpectedReturnPct = expected,
            FullKelly = fullKelly,
            ScaledKelly = scaled,
            FinalWeight = final,
            Amount = final * Math.Max(input.Capital, 0),
            Binding = binding,
            DrawdownPctOfCapital = final * loss,
            BreakEvenProbability = loss / (gain + loss),
            FinalWeightIfOverconfident = finalLow,
        };
    }
}
